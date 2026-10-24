using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.AI;

namespace GrannyMP
{
    /// <summary>
    /// PvP mode: one random player plays Granny, the others are survivors ("last survivor" rules: a caught survivor
    /// spectates until the end of the day, the day is lost when all survivors are caught).
    ///  * The host's Granny has no AI – the host puts her where the Granny player walks every frame and everybody sees
    ///    her through the usual AI pose sync. The Granny player looks through Granny's eyes: their own Granny is
    ///    invisible, their player body is invisible to the others.
    ///  * LMB grabs a survivor in front of Granny (also one hiding in a bed / coffin / car right next to her); the host
    ///    checks the distance and runs the normal catch (Granny's grab, the jumpscare, spectating). A miss is a swing.
    ///  * RMB: Granny says a line, LaughKey: she laughs, TrapKey: she puts a bear trap down (her own code).
    ///  * Shot, sprayed, hit by the car: Granny is knocked out by her own code – the Granny player cannot move until
    ///    she gets up. The Granny player cannot pick up or use items, hide, or spring traps.
    /// </summary>
    internal static class Pvp
    {
        /// <summary>PvP is the chosen mode (Granny may still be the AI: one player only, or the Granny player left).</summary>
        public static bool Mode => Session.Active && Session.PvpOn;
        /// <summary>A player plays Granny right now.</summary>
        public static bool Active => Mode && Session.GrannyId != 255 && Session.Players.ContainsKey(Session.GrannyId);
        public static bool IsGranny(byte id) => Active && id == Session.GrannyId;
        public static bool LocalIsGranny => Active && Session.GrannyId == Session.LocalId;
        /// <summary>The local player plays Granny in Granny's house (items, hiding and traps are blocked).</summary>
        public static bool LocalGrannyInGame => LocalIsGranny && GameRefs.InGameScene;

        const float LinkDist = 3.5f, HostSlack = 1.2f, CatchReach = 2.1f, SpotReach = 2.6f;
        const float GrabCooldown = 1f, VoiceCooldown = 1.5f, TrapCooldown = 30f;
        /// <summary>At the start of every day the Granny player waits this long (black screen) – a head start for the survivors.</summary>
        const float RespawnDelay = 30f;

        public static void Reset()
        {
            _taken = false; _anim = null; _speed = 0; _busyUntil = 0; _animResetAt = 0; _nextGrab = 0; _nextVoice = 0; _nextTrap = 0; _pivot = 0; _pivotKnown = false;
            _frozen = false; _stunned = false; _stateAt = 0; _sentFlags = 255; _farSince = 0; _localNextGrab = 0; _localNextVoice = 0; _localNextTrap = 0;
            _hidden.Clear(); _hiddenSet.Clear(); _nextHideScan = 0; _ignored.Clear(); _ignoreCc = IntPtr.Zero; _nextIgnore = 0; _speedFps = IntPtr.Zero; _wasFrozen = false;
            _woke = false; _controlSince = 0; _nightLight = null; _stunSince = 0; _stunIgnored = false; _nextStatus = 0; _frozenWhy = "";
            _respawnAt = 0; _chaseSpeed = 0; _appliedSpeed = -1; _loggedSpeed = -1; _hiddenUi.Clear(); _nextUiScan = 0;
            _head = null; _eyes = null; _eyeOffset = -1f; _camSet = false; _nextBodyScan = 0;
            _tableHidden.Clear(); _tableLights.Clear(); _nextTableScan = 0;
        }

        // ================= choosing the Granny player (host) =================
        static readonly System.Random _rng = new();

        /// <summary>Host, the game starts: a random player becomes Granny (two players or more).</summary>
        public static void PickGranny()
        {
            if (!Session.IsHost) return;
            var ids = Session.Players.Keys.OrderBy(x => x).ToList();
            // the host's choice (server configuration), otherwise a random player
            byte want = Session.GrannyChoice;
            Session.GrannyId = ids.Count < 2 ? (byte)255 : want != 255 && Session.Players.ContainsKey(want) ? want : ids[_rng.Next(ids.Count)];
            Session.PushSettings();
            if (Session.GrannyId == 255)
                Session.Notify2("PvP needs at least two players – Granny is played by the computer.", "PvP wymaga co najmniej dwóch graczy – Granny steruje komputer.", true);
            else
            {
                var n = Session.NameOf(Session.GrannyId);
                Session.Notify2($"{n} plays Granny! The others: escape from the house.", $"{n} gra jako Granny! Reszta: uciekajcie z domu.", true);
            }
            Log.Info("PvP: Granny = " + (Session.GrannyId == 255 ? "AI" : Session.NameOf(Session.GrannyId)));
        }

        /// <summary>Host: the game is over (menu, escape) – the next game draws Granny again.</summary>
        public static void OnGameEnded()
        {
            if (!Session.IsHost || Session.GrannyId == 255) return;
            Session.GrannyId = 255;
            Session.PushSettings();
        }

        /// <summary>Host: a player left (already removed from the player list).</summary>
        public static void OnPlayerLeft(NetPlayer p)
        {
            if (Session.IsHost && p != null && p.Id == Session.GrannyChoice) { Session.GrannyChoice = 255; Session.PushSettings(); }
            if (!Session.IsHost || p == null || p.Id != Session.GrannyId) return;
            Session.GrannyId = 255;
            Session.PushSettings();
            if (Session.HostInGame)
                Session.Notify2("The Granny player left – the computer plays Granny now.", "Gracz-Granny wyszedł – Granny steruje teraz komputer.", true);
        }

        // ================= network =================
        // Msg.Pvp, first byte = kind:
        //   0 H->*  Granny's state: byte flags (1 = she cannot move: knocked out / grabbing / busy, 2 = knocked out),
        //           float pivot (how high her transform is above the floor), float chase speed (her speed for the difficulty)
        //   1 G->H  grab: byte target (255 = nobody in reach – Granny swings at the air)
        //   2 G->H  voice: byte 0 = a line, 1 = laugh
        //   3 G->H  put a bear trap down
        public static void RegisterHandlers() => Session.On(Msg.Pvp, Handle);

        static void Handle(NetPlayer from, NetReader r)
        {
            byte kind = r.Byte();
            if (kind == 0)
            {
                if (!Session.IsClient) return;
                byte f = r.Byte(); _pivot = r.Float(); _chaseSpeed = r.Float();
                bool fr = (f & 1) != 0;
                if (fr != _frozen && LocalIsGranny) Log.Info(fr ? $"PvP: Granny cannot move ({((f & 2) != 0 ? "knocked out" : "catching / busy")})" : "PvP: Granny can move again");
                _frozen = fr; _stunned = (f & 2) != 0; _stateAt = Time.unscaledTime;
                return;
            }
            if (!Session.IsHost || from == null || !IsGranny(from.Id) || !GameRefs.InGameScene) return;
            switch (kind)
            {
                case 1: HostGrab(r.Byte()); break;
                case 2: HostVoice(r.Byte() == 1); break;
                case 3: HostTrap(); break;
            }
        }

        static void Request(byte kind, byte arg)
        {
            if (Session.IsHost)
            {
                if (kind == 1) HostGrab(arg); else if (kind == 2) HostVoice(arg == 1); else if (kind == 3) HostTrap();
                return;
            }
            var w = Session.W(Msg.Pvp); w.Byte(kind); w.Byte(arg); Session.SendToHost(w, true);
        }

        // ================= host: Granny follows the Granny player =================
        static bool _taken, _pivotKnown, _wasFrozen;
        static string _anim;
        static float _speed, _busyUntil, _nextGrab, _nextVoice, _nextTrap, _pivot, _animResetAt;
        static byte _sentFlags = 255; static float _nextState; static string _frozenWhy = "";

        /// <summary>Host: Granny's chase speed – set by her own code for the chosen difficulty (easy 2.5, normal 3.8, hard 5, extreme 8.5 m/s).</summary>
        static float HostChaseSpeed()
        {
            try { var g = GameRefs.Granny; return U.Alive(g) ? g.grannysFollowSpeed : 0f; } catch { return 0f; }
        }

        static bool HostFrozen(EnemyAIGranny g) => StunnedNow(g) || AiControl.HoldingCatch || Time.unscaledTime < _busyUntil;

        // knocked out (shotgun, crossbow, pepper, car, freeze trap): her own code lets her fall and get up. A flag that
        // stays on (seen: the Granny player could not move from the start of the day) must not hold the player forever.
        const float MaxStun = 40f;
        static float _stunSince; static bool _stunIgnored;

        static bool StunnedNow(EnemyAIGranny g)
        {
            if (!AiControl.Stunned(g)) { _stunSince = 0; _stunIgnored = false; return false; }
            float now = Time.unscaledTime;
            if (_stunSince == 0) { _stunSince = now; Log.Info("PvP: Granny is knocked out – " + AiControl.StunReason(g)); }
            if (!_stunIgnored && now - _stunSince > MaxStun)
            {
                _stunIgnored = true;
                Log.Warn($"PvP: Granny has been knocked out for {MaxStun:F0} s ({AiControl.StunReason(g)}) – she gets up, the Granny player takes her back");
                AiControl.ClearStun(g);
            }
            return !_stunIgnored;
        }

        /// <summary>
        /// Host, before Granny's FixedUpdate (after the catch hold). Returns whether her own FixedUpdate runs:
        /// only while she is knocked out (her own code lets her fall and get up again).
        /// </summary>
        public static bool HostFixed(EnemyAIGranny g)
        {
            if (StunnedNow(g)) { Release(g); return true; }
            if (Time.unscaledTime < _busyUntil) return false; // swinging / putting a trap down
            Take(g);
            ClearAi(g);
            return false;
        }

        /// <summary>Host: the AI no longer needs to give way (the catch hold, a knock-out, PvP is over) – her navigation agent back on.</summary>
        public static void Release(EnemyAIGranny g)
        {
            if (!_taken || !U.Alive(g)) { _taken = false; return; }
            _taken = false;
            try
            {
                var nav = g.navComponent;
                if (U.Alive(nav) && !nav.enabled) { nav.enabled = true; nav.Warp(g.transform.position); }
            }
            catch (Exception e) { Log.Debug("PvP release: " + e.Message); }
            if (!Active) AiControl.SetHeadFollow(g, true);
        }

        static void Take(EnemyAIGranny g)
        {
            var nav = g.navComponent;
            if (!_taken)
            {
                _taken = true; _anim = null;
                try
                {
                    // how high her transform stands above the navigation mesh (the floor) – the same everywhere
                    if (!_pivotKnown && U.Alive(nav) && nav.enabled && NavMesh.SamplePosition(g.transform.position, out var hit, 2f, NavMesh.AllAreas))
                    {
                        _pivot = Mathf.Clamp(g.transform.position.y - hit.position.y, -0.5f, 1.5f);
                        _pivotKnown = true;
                        Log.Info($"PvP: Granny is played by {Session.NameOf(Session.GrannyId)} (pivot {_pivot:F2} m)");
                    }
                }
                catch (Exception e) { Log.Debug("PvP pivot: " + e.Message); }
                // her head would keep turning to the host's player
                AiControl.SetHeadFollow(g, false);
            }
            try { if (U.Alive(nav) && nav.enabled) nav.enabled = false; } catch { }
        }

        static void ClearAi(EnemyAIGranny g)
        {
            try
            {
                if (g.huntPlayer) g.huntPlayer = false;
                if (g.attackingPlayer) g.attackingPlayer = false;
                if (g.seePlayer) g.seePlayer = false;
                if (g.grannyIsFollow) g.grannyIsFollow = false;
                if (g.startAttack) g.startAttack = false;
                if (g.GrannySearching) g.GrannySearching = false;
                if (g.grannyHearObject) g.grannyHearObject = false; // a noise – only her AI would go there and reset it
                if (g.seePlayerTimer) g.seePlayerTimer = false;
            }
            catch { }
        }

        /// <summary>The Granny player's feet and the direction they look in.</summary>
        static bool GrannyPlayerFeet(out Vector3 feet, out float yaw)
        {
            feet = default; yaw = 0;
            byte id = Session.GrannyId;
            if (id == Session.LocalId)
            {
                var root = GameRefs.PlayerRoot;
                if (!U.Alive(root)) return false;
                feet = root.position + Vector3.up * LocalFeetOffset(); yaw = root.eulerAngles.y;
                return true;
            }
            if (!PlayerSync.TryGetAvatar(id, out var a) || !a.HasData || Time.unscaledTime - a.LastUpdate > 3f) return false;
            var t = a.Root.transform;
            feet = t.position + Vector3.up * (a.CCCenterY - a.CCHeight / 2f); yaw = t.eulerAngles.y;
            return true;
        }

        static float LocalFeetOffset()
        {
            var root = GameRefs.PlayerRoot; var cc = GameRefs.PlayerCC;
            float sc = 1f; try { sc = Mathf.Max(0.1f, root.lossyScale.y); } catch { }
            return U.Alive(cc) ? (cc.center.y - cc.height / 2f) * sc : -0.9f;
        }

        static void HostUpdate()
        {
            var g = GameRefs.Granny;
            if (!U.Alive(g)) return;
            bool frozen = HostFrozen(g);
            HostSendState(g, frozen);
            if (frozen) { _wasFrozen = true; return; }
            if (_wasFrozen) { _wasFrozen = false; _anim = null; } // the catch / knock-out played its own animations
            if (_animResetAt > 0 && Time.unscaledTime >= _animResetAt) { _animResetAt = 0; _anim = null; }
            if (!_taken || GameFlow.WaitingForPlayers || GameFlow.InTransition) return; // (at the end of the day the player is put in the bed)
            if (!GrannyPlayerFeet(out var feet, out var yaw)) { SetAnim(g, "idle", 1f); return; }
            var t = g.transform; var gp = t.position;
            var target = feet;
            try
            {
                // like the AI: on the navigation mesh (a little above the floor the player stands on)
                if (NavMesh.SamplePosition(feet + Vector3.up * 0.3f, out var hit, 1f, NavMesh.AllAreas) && Mathf.Abs(hit.position.y - feet.y) < 0.6f) target.y = hit.position.y;
            }
            catch { }
            target.y += _pivot;
            // the Granny player is somewhere else (start of the day, a teleport) – Granny waits, the player comes to her
            if (U.FlatDist(gp, target) > LinkDist + HostSlack || Mathf.Abs(gp.y - target.y) > 2.5f) { _speed = 0; SetAnim(g, "idle", 1f); return; }
            float dt = Mathf.Max(0.0001f, Time.unscaledDeltaTime);
            var mv = target - gp; mv.y = 0;
            _speed = Mathf.Lerp(_speed, Mathf.Min(mv.magnitude / dt, 12f), 1f - Mathf.Exp(-dt * 8f));
            t.position = target;
            t.rotation = Quaternion.Euler(0f, yaw, 0f);
            if (_speed > 0.4f) SetAnim(g, "Walk", Mathf.Clamp(_speed / WalkRef(g), 0.6f, 2.2f));
            else SetAnim(g, "idle", 1f);
        }

        /// <summary>How fast (m/s) Granny walks with her walk animation at normal speed.</summary>
        static float WalkRef(EnemyAIGranny g)
        {
            try
            {
                float s = g.grannysFollowSpeed / Mathf.Max(0.3f, g.grannysAnimFollowSpeed);
                if (s > 0.6f && s < 6f) return s;
            }
            catch { }
            return 1.6f;
        }

        static void SetAnim(EnemyAIGranny g, string clip, float speed)
        {
            try
            {
                var an = U.Alive(g.animationHolder) ? g.animationHolder.GetComponent<Animation>() : null;
                if (!U.Alive(an)) return;
                if (_anim != clip) { _anim = clip; an.CrossFade(clip, 0.2f); }
                var st = an[clip];
                if (st != null && Mathf.Abs(st.speed - speed) > 0.05f) st.speed = speed;
            }
            catch { }
        }

        static void HostSendState(EnemyAIGranny g, bool frozen)
        {
            float now = Time.unscaledTime;
            byte f = (byte)((frozen ? 1 : 0) | (StunnedNow(g) ? 2 : 0));
            _frozen = frozen; _stunned = (f & 2) != 0;
            if (f == _sentFlags && now < _nextState) return;
            bool changed = f != _sentFlags;
            if (changed && _sentFlags != 255)
            {
                _frozenWhy = !frozen ? "" : (f & 2) != 0 ? "knocked out" : AiControl.HoldingCatch ? "catching" : "busy";
                Log.Info(frozen ? $"PvP: Granny cannot move ({_frozenWhy})" : "PvP: Granny can move again");
            }
            _sentFlags = f; _nextState = now + 0.25f;
            _chaseSpeed = HostChaseSpeed();
            var w = Session.W(Msg.Pvp); w.Byte(0); w.Byte(f); w.Float(_pivot); w.Float(_chaseSpeed);
            Session.Broadcast(w, changed);
        }

        // ---------------- host: what the Granny player does ----------------
        static void HostGrab(byte target)
        {
            var g = GameRefs.Granny;
            float now = Time.unscaledTime;
            if (!U.Alive(g) || GameFlow.InTransition || HostFrozen(g) || now < _nextGrab) return;
            _nextGrab = now + GrabCooldown * 0.8f;
            if (target != 255 && AiControl.PvpCatch(target, CatchReach + HostSlack, SpotReach + HostSlack))
            {
                Log.Info($"PvP: Granny ({Session.NameOf(Session.GrannyId)}) caught {Session.NameOf(target)}");
                return;
            }
            // nobody within reach (on the host's picture): she swings at the air and stands still for a moment
            _busyUntil = now + 0.9f; _anim = null;
            try { var an = U.Alive(g.animationHolder) ? g.animationHolder.GetComponent<Animation>() : null; if (U.Alive(an)) an.CrossFade("Hit", 0.1f); } catch { }
        }

        static void HostVoice(bool laugh)
        {
            float now = Time.unscaledTime;
            if (now < _nextVoice) return;
            _nextVoice = now + VoiceCooldown * 0.8f;
            Events.GrannyTalk(laugh);
        }

        static void HostTrap()
        {
            var g = GameRefs.Granny;
            float now = Time.unscaledTime;
            if (!U.Alive(g) || GameFlow.InTransition || HostFrozen(g) || now < _nextTrap) return;
            _nextTrap = now + TrapCooldown - 2f;
            ClearAi(g);
            Release(g); // her own trap code uses the navigation agent
            _busyUntil = now + 2f;
            try
            {
                // her own code (dropBearTrap) first waits 10 s – in single player she walks on meanwhile – then stops,
                // and a second later puts the trap at her feet, unless she is chasing, hears something or is knocked
                // out. Its first two steps are run here, so the trap is put down at once.
                var en = g.dropBearTrap();
                en.MoveNext(); // → waits 10 s
                en.MoveNext(); // → stops, waits 1 s
                g.StartCoroutine(en); // → the trap, then 4 s later she walks again
                _animResetAt = now + 4.3f; // (that last step plays her walk / stand animation itself)
                Log.Info("PvP: Granny puts a bear trap down");
            }
            catch (Exception e) { Log.Warn("PvP trap: " + e.Message); _busyUntil = 0; }
        }

        // ================= the Granny player's own machine =================
        static bool _frozen, _stunned;
        static float _stateAt, _farSince, _localNextGrab, _localNextVoice, _localNextTrap;
        static readonly List<Renderer> _hidden = new();
        static readonly HashSet<IntPtr> _hiddenSet = new();
        static float _nextHideScan;
        static readonly List<Collider> _ignored = new();
        static IntPtr _ignoreCc, _speedFps;
        static float _walkSave, _runSave, _nextIgnore, _nextStatus;
        // the start of every day: the game wakes the player up in the bed (black screen, the camera getting up). The
        // Granny player is moved to Granny only when that is over and they can walk.
        static bool _woke; static float _controlSince, _respawnAt, _chaseSpeed, _appliedSpeed = -1;
        static Light _nightLight;
        static readonly List<Behaviour> _hiddenUi = new(); static float _nextUiScan;

        /// <summary>The Granny player waits for the day to start (black screen with the countdown).</summary>
        static bool Respawning => _respawnAt > 0 && Time.unscaledTime < _respawnAt;
        public static bool BlackScreen => LocalGrannyInGame && Session.HostInGame && !GameFlow.InTransition && (Respawning || !_woke);
        public static int RespawnSeconds => Mathf.Max(0, Mathf.CeilToInt(_respawnAt - Time.unscaledTime));
        /// <summary>Top-right icon of the Granny player: the bear trap and how long until the next one.</summary>
        public static bool ShowTrapIcon => LocalGrannyInGame && !BlackScreen;
        public static float TrapCooldownLeft => Mathf.Max(0f, _localNextTrap - Time.unscaledTime);
        public static float TrapCooldownTotal => TrapCooldown;

        /// <summary>Every frame (in the game scene).</summary>
        public static void Update()
        {
            if (!Session.Active || !GameRefs.InGameScene) { RestoreLocal(); return; }
            if (Session.IsHost)
            {
                var g = GameRefs.Granny;
                if (Active && Session.HostInGame) HostUpdate();
                else if (_taken && U.Alive(g)) Release(g);
            }
            if (LocalIsGranny) LocalUpdate(); else RestoreLocal();
        }

        static void LocalUpdate()
        {
            var g = GameRefs.Granny; var root = GameRefs.PlayerRoot;
            if (!U.Alive(g) || !U.Alive(root)) return;
            FindBodyParts(g);
            if (!Cfg.PvpOwnBody.Value) HideLocalGranny(g);
            IgnoreGrannyColliders(g);
            ApplySpeed();
            NightVision();
            NoOwnLights();
            HideTableLights();
            HideHidingButtons();
            float now = Time.unscaledTime;
            UpdateWake(now);
            LogStatus(g, root, now);
            // where Granny is decides where the Granny player is (the start of the day, a knock-out, a catch)
            bool ready = (Session.IsHost ? _pivotKnown : _stateAt > 0 && !GameFlow.WaitingForWorld) && _woke;
            if (ready && Session.HostInGame && !GameFlow.InTransition)
            {
                var gp = GrannyPos(g);
                float flat = U.FlatDist(root.position, gp);
                float dy = root.position.y + LocalFeetOffset() + _pivot - gp.y;
                if (LocalFrozen || Respawning)
                {
                    // Granny cannot move (knocked out, catching, a swing, a trap, waiting for the day to start): the
                    // player's speed is 0 (ApplySpeed); put back only when Granny herself was moved (her catch code)
                    _farSince = 0;
                    if (flat > 1.5f || Mathf.Abs(dy) > 1f) PlaceAt(g);
                }
                else if (flat > LinkDist || Mathf.Abs(dy) > 2.2f)
                {
                    if (_farSince == 0) _farSince = now;
                    else if (now - _farSince > 0.6f) { _farSince = 0; PlaceAt(g); }
                }
                else _farSince = 0;
            }
            else _farSince = 0;
            if (!InputFree() || LocalFrozen || BlackScreen || GameFlow.InTransition) return;
            if (In.MouseDown(0) && now >= _localNextGrab) { _localNextGrab = now + GrabCooldown; Request(1, FindTarget()); }
            // RMB: one of her lines or her laugh
            else if (In.MouseDown(1) && now >= _localNextVoice) { _localNextVoice = now + VoiceCooldown; Request(2, (byte)(_rng.Next(4) == 0 ? 1 : 0)); }
            else if (In.KeyDown(Cfg.PvpTrapKey.Value))
            {
                if (now < _localNextTrap) Session.Notify(L.T($"The next trap in {Mathf.CeilToInt(_localNextTrap - now)} s.", $"Następna pułapka za {Mathf.CeilToInt(_localNextTrap - now)} s."), false);
                else { _localNextTrap = now + TrapCooldown; Request(3, 0); }
            }
        }

        /// <summary>The local player can walk: the controller, its body and the camera are on.</summary>
        static bool HasControl()
        {
            try
            {
                var f = GameRefs.Fps; var cc = GameRefs.PlayerCC; var root = GameRefs.PlayerRoot;
                if (!U.Alive(f) || !f.enabled || !U.Alive(root) || !root.gameObject.activeInHierarchy) return false;
                if (U.Alive(cc) && !cc.enabled) return false;
                var cam = U.Alive(GameRefs.PlayerCam) ? GameRefs.PlayerCam.GetComponent<Camera>() : null;
                return cam == null || cam.enabled;
            }
            catch { return false; }
        }

        /// <summary>Woken up: in control for 1.5 s and walked (or 6 s in control) since the day started.</summary>
        static void UpdateWake(float now)
        {
            if (GameFlow.InTransition) { _woke = false; _controlSince = 0; _respawnAt = 0; return; }
            if (GameFlow.StartHolding) { _controlSince = 0; return; } // everybody is still loading
            // the day started: the countdown runs from now (also while the game still wakes the player up)
            if (_respawnAt == 0 && Session.HostInGame) { _respawnAt = now + RespawnDelay; Log.Info($"PvP: Granny comes back in {RespawnDelay:F0} s"); }
            if (_woke) return;
            if (!HasControl()) { _controlSince = 0; return; }
            if (_controlSince == 0) _controlSince = now;
            bool moved = false; try { moved = GameRefs.Fps.playerMoving; } catch { }
            if (now - _controlSince > 1.5f && (moved || now - _controlSince > 6f))
            {
                _woke = true;
                Log.Info("PvP: the Granny player is awake – going to Granny");
            }
        }

        static void LogStatus(EnemyAIGranny g, Transform root, float now)
        {
            if (!Cfg.DebugLog.Value || now < _nextStatus) return;
            _nextStatus = now + 5f;
            var gp = GrannyPos(g); var mp = root.position;
            Log.Debug($"PvP status: Granny ({gp.x:F1}, {gp.y:F1}, {gp.z:F1}), me ({mp.x:F1}, {mp.y:F1}, {mp.z:F1}) {U.FlatDist(gp, mp):F1} m · awake {_woke} · control {HasControl()} · frozen {LocalFrozen}{(_frozenWhy != "" ? " (" + _frozenWhy + ")" : "")} · state {(Session.IsHost ? "host" : $"{now - _stateAt:F1}s old")}");
        }

        /// <summary>Granny sees in the dark: a soft light around the Granny player's eyes, on their screen only.</summary>
        static void NightVision()
        {
            if (!Cfg.PvpNightVision.Value) { if (U.Alive(_nightLight)) { UnityEngine.Object.Destroy(_nightLight.gameObject); _nightLight = null; } return; }
            if (U.Alive(_nightLight)) return;
            var cam = GameRefs.PlayerCam;
            if (!U.Alive(cam)) return;
            try
            {
                var go = new GameObject("GrannyMP_GrannyEyes");
                go.transform.SetParent(cam, false);
                _nightLight = go.AddComponent<Light>();
                _nightLight.type = LightType.Point; _nightLight.range = 24f; _nightLight.intensity = 1.8f;
                _nightLight.color = new Color(1f, 0.94f, 0.88f); _nightLight.shadows = LightShadows.None;
                _nightLight.renderMode = LightRenderMode.ForcePixel;
            }
            catch (Exception e) { Log.Debug("PvP light: " + e.Message); }
        }

        /// <summary>Granny cannot move (knocked out, grabbing somebody, busy) – on a client as long as the host keeps saying so.</summary>
        static bool LocalFrozen => _frozen && (Session.IsHost || Time.unscaledTime - _stateAt < 1.5f);

        static bool InputFree()
        {
            if (LobbyUI.Open || LobbyUI.ChatOpen || LobbyUI.TypingInField) return false;
            try { var f = GameRefs.Fps; if (U.Alive(f) && f.menuEnabled) return false; } catch { }
            return true;
        }

        /// <summary>The survivor in front of the Granny player within reach (also one hiding right there), 255 = nobody.</summary>
        static byte FindTarget()
        {
            var root = GameRefs.PlayerRoot; var cam = GameRefs.PlayerCam;
            var fwd = U.Alive(cam) ? cam.forward : root.forward; fwd.y = 0;
            if (fwd.sqrMagnitude < 1e-4f) fwd = root.forward;
            byte best = 255; float bestD = float.MaxValue;
            foreach (var p in Session.Players.Values)
            {
                if (p.IsLocal || p.Ghost || !PlayerSync.TryGetAvatar(p.Id, out var a) || !a.HasData) continue;
                if ((a.Flags & PlayerSync.F_Ghost) != 0) continue;
                bool hiding = (a.Flags & PlayerSync.F_Hiding) != 0;
                var pos = hiding && a.Views.Count > 0 ? a.Views[a.Views.Count - 1].P : a.Root.transform.position;
                var d = pos - root.position; float dy = d.y; d.y = 0;
                float dist = d.magnitude;
                if (dist > (hiding ? SpotReach : CatchReach) || Mathf.Abs(dy) > 2f) continue;
                if (dist > 0.6f && Vector3.Angle(fwd, d) > (hiding ? 85f : 70f)) continue;
                if (dist < bestD) { bestD = dist; best = p.Id; }
            }
            return best;
        }

        /// <summary>Puts the local player (Granny) where Granny stands.</summary>
        static void PlaceAt(EnemyAIGranny g)
        {
            var gp = GrannyPos(g);
            var pos = new Vector3(gp.x, gp.y - _pivot - LocalFeetOffset() + 0.05f, gp.z);
            var from = GameRefs.LocalPos;
            GameRefs.TeleportLocal(pos);
            // the hold during a catch / knock-out moves the player a little every frame – only real moves are logged
            if ((from - pos).sqrMagnitude > 4f) Log.Info($"PvP: the Granny player was put where Granny is ({pos.x:F1}, {pos.y:F1}, {pos.z:F1}), {U.FlatDist(from, pos):F0} m from ({from.x:F1}, {from.y:F1}, {from.z:F1})");
        }

        /// <summary>
        /// Where Granny really is: the host moves her; on a client the network's position (her model there is put at
        /// the Granny player every frame – LateUpdate).
        /// </summary>
        static Vector3 GrannyPos(EnemyAIGranny g)
        {
            if (Session.IsClient && World.TryGetNetPos(g.transform, out var p)) return p;
            return g.transform.position;
        }

        // ---------------- the Granny player sees her own body ----------------
        // Her model stays visible on the Granny player's screen; only her head is folded away (its bone scaled to 0
        // after the animation, every frame) so it does not block the view, and the camera is at the height of her eyes
        // (the GrannyEyes object on her head, measured once).
        static Transform _head, _eyes; static Vector3 _headScale; static float _eyeOffset = -1f;
        static bool _camSet; static Vector3 _camBase, _camWritten; static float _nextBodyScan;

        static void FindBodyParts(EnemyAIGranny g)
        {
            if (U.Alive(_head) && _eyeOffset > 0f) return;
            if (Time.unscaledTime < _nextBodyScan) return;
            _nextBodyScan = Time.unscaledTime + 1f;
            try
            {
                foreach (var t in g.GetComponentsInChildren<Transform>(true))
                {
                    if (t == null) continue;
                    if (!U.Alive(_head) && t.name == "Bip01 Head") { _head = t; _headScale = t.localScale; }
                    else if (!U.Alive(_eyes) && t.name == "GrannyEyes") _eyes = t;
                }
                if (_eyeOffset < 0f && U.Alive(_eyes) && U.Alive(_head) && _head.localScale.sqrMagnitude > 0.01f)
                {
                    _eyeOffset = Mathf.Clamp(_eyes.position.y - g.transform.position.y, 1f, 8f);
                    Log.Info($"PvP: Granny's eyes are {_eyeOffset:F2} m above her feet – the Granny player's camera is put there");
                }
            }
            catch (Exception e) { Log.Debug("PvP body: " + e.Message); }
        }

        /// <summary>After the game's scripts and animations: Granny's body at the player, her head folded, the camera at her eyes.</summary>
        public static void LateUpdate()
        {
            if (!LocalGrannyInGame) { RestoreBodyView(); return; }
            var g = GameRefs.Granny; var root = GameRefs.PlayerRoot; var cam = GameRefs.PlayerCam;
            if (!U.Alive(g) || !U.Alive(root)) return;
            try
            {
                float feetY = root.position.y + LocalFeetOffset();
                // a client: the body exactly where the player is (the network position comes a little later)
                if (Session.IsClient && _woke && !BlackScreen && !LocalFrozen && !GameFlow.InTransition)
                {
                    g.transform.position = new Vector3(root.position.x, feetY + _pivot, root.position.z);
                    g.transform.rotation = Quaternion.Euler(0f, root.eulerAngles.y, 0f);
                }
                if (U.Alive(_head) && Cfg.PvpOwnBody.Value) _head.localScale = Vector3.zero;
                if (U.Alive(cam) && _eyeOffset > 0f && _woke && !GameFlow.InTransition)
                {
                    var lp = cam.localPosition;
                    if (_camSet && (lp - _camWritten).sqrMagnitude < 1e-8f) lp = _camBase; // the game did not move it since
                    _camBase = lp;
                    var parent = cam.parent;
                    float baseY = parent != null ? parent.TransformPoint(lp).y : lp.y;
                    float want = feetY + _pivot + _eyeOffset * Cfg.PvpCameraHeight.Value;
                    float dy = Mathf.Clamp(want - baseY, -3f, 3f);
                    var off = parent != null ? parent.InverseTransformVector(new Vector3(0f, dy, 0f)) : new Vector3(0f, dy, 0f);
                    _camWritten = lp + off; cam.localPosition = _camWritten; _camSet = true;
                }
                else ResetCamera();
            }
            catch (Exception e) { Log.Debug("PvP late: " + e.Message); }
        }

        static void ResetCamera()
        {
            if (!_camSet) return;
            _camSet = false;
            var cam = GameRefs.PlayerCam;
            try { if (U.Alive(cam) && (cam.localPosition - _camWritten).sqrMagnitude < 1e-6f) cam.localPosition = _camBase; } catch { }
        }

        static void RestoreBodyView()
        {
            ResetCamera();
            if (U.Alive(_head) && _head.localScale.sqrMagnitude < 1e-6f) try { _head.localScale = _headScale; } catch { }
        }

        /// <summary>
        /// (Old option, PvP Granny not seeing her own body:) Granny's model is not drawn on the Granny player's screen.
        /// forceRenderingOff, not "enabled": the enabled state of renderers is world state that the host sends to everybody.
        /// </summary>
        static void HideLocalGranny(EnemyAIGranny g)
        {
            float now = Time.unscaledTime;
            if (now >= _nextHideScan)
            {
                _nextHideScan = now + 2f;
                int before = _hidden.Count;
                try
                {
                    foreach (var r in g.GetComponentsInChildren<Renderer>(true))
                        if (r != null && _hiddenSet.Add(r.Pointer)) _hidden.Add(r);
                }
                catch { }
                if (before == 0 && _hidden.Count > 0) Log.Info($"PvP: you play Granny – her model is hidden on your screen ({_hidden.Count} parts)");
            }
            foreach (var r in _hidden) if (U.Alive(r) && !r.forceRenderingOff) r.forceRenderingOff = true;
        }

        /// <summary>
        /// Granny stands where the Granny player stands – her colliders must not push the player's controller. Unity
        /// forgets an ignored pair when a collider is switched off and on (the game does that to Granny at the start of
        /// every day), so it is set again twice a second; without it the player was stuck in her body.
        /// </summary>
        static void IgnoreGrannyColliders(EnemyAIGranny g)
        {
            var cc = GameRefs.PlayerCC;
            if (!U.Alive(cc)) return;
            float now = Time.unscaledTime;
            if (cc.Pointer == _ignoreCc && now < _nextIgnore) return;
            _ignoreCc = cc.Pointer; _nextIgnore = now + 0.5f; _ignored.Clear();
            try
            {
                foreach (var c in g.GetComponentsInChildren<Collider>(true))
                {
                    if (c == null || c.isTrigger || !c.enabled || !c.gameObject.activeInHierarchy) continue;
                    Physics.IgnoreCollision(cc, c, true); _ignored.Add(c);
                }
            }
            catch (Exception e) { Log.Debug("PvP colliders: " + e.Message); }
        }

        /// <summary>
        /// Like Granny in single player: the Granny player moves at her chase speed for the chosen difficulty (the host
        /// sends it), times [PvP] GrannySpeed.
        /// </summary>
        static void ApplySpeed()
        {
            var f = GameRefs.Fps;
            if (!U.Alive(f)) return;
            try
            {
                if (f.Pointer != _speedFps) { _speedFps = f.Pointer; _walkSave = f.m_WalkSpeed; _runSave = f.m_RunSpeed; _appliedSpeed = -1; _loggedSpeed = -1; }
                float chase = Session.IsHost ? HostChaseSpeed() : _chaseSpeed, k = Cfg.PvpGrannySpeed.Value;
                float walk = chase > 0.5f ? chase * k : _walkSave * k, run = chase > 0.5f ? chase * k : _runSave * k;
                if (Mathf.Abs(walk - _loggedSpeed) > 0.01f)
                {
                    _loggedSpeed = walk;
                    Log.Info(chase > 0.5f ? $"PvP: the Granny player moves at {walk:F1} m/s (Granny's chase speed for this difficulty{(Mathf.Abs(k - 1f) > 0.01f ? $" x {k:F2}" : "")})"
                                          : $"PvP: Granny's speed unknown yet – {walk:F1} m/s");
                }
                // Granny cannot move now (knocked out, catching, a swing, a trap, the start of the day): she stands still
                if (LocalFrozen || Respawning || BlackScreen) { walk = 0f; run = 0f; }
                if (Mathf.Abs(walk - _appliedSpeed) < 0.01f) return;
                _appliedSpeed = walk;
                f.m_WalkSpeed = walk; f.m_RunSpeed = run;
            }
            catch (Exception e) { Log.Debug("PvP speed: " + e.Message); }
        }

        /// <summary>
        /// Granny does not use drawers, cupboards, the fridge, levers or the car – only real doors. openDoors acts on what
        /// its ray hits; the same ray is cast here and the door scripts above the hit decide.
        /// </summary>
        public static bool GrannyMayUse(Component doorRay)
        {
            if (!LocalGrannyInGame || !U.Alive(doorRay)) return true;
            try
            {
                var t = doorRay.transform;
                if (!Physics.Raycast(t.position, t.forward, out var hit, 6f, ~0, QueryTriggerInteraction.Collide) || hit.collider == null) return true;
                int depth = 0;
                for (var x = hit.collider.transform; x != null && depth < 5; x = x.parent, depth++)
                    foreach (var mb in x.GetComponents<MonoBehaviour>())
                    {
                        if (mb == null) continue;
                        var tn = U.Il2CppTypeName(mb);
                        if (GrannyDoors.Contains(tn)) return true;
                        if (tn.StartsWith("OpenClose", StringComparison.OrdinalIgnoreCase) || tn.StartsWith("openClose", StringComparison.Ordinal)) return false;
                    }
                // a lever, a switch, a lock… (no door script above it): openDoors only acts on tagged objects, which are
                // the interactive ones – Granny leaves those alone
                return string.IsNullOrEmpty(hit.collider.tag) || hit.collider.tag == "Untagged";
            }
            catch { return true; }
        }

        /// <summary>
        /// The door scripts Granny may open: house doors and the small doors (the cellar door under the stairs, the meat
        /// room, the attic) – not drawers, kitchen cupboards, the fridge, wardrobes, the glass cabinet or the car.
        /// </summary>
        static readonly HashSet<string> GrannyDoors = new()
        {
            "OpenCloseDoors", "OpenCloseSmallDoors", "OpenCloseOldDoor", "OpenCloseLeftOldDoor", "OpenCloseBackdoor", "OpenCloseDoorV", "OpenCloseDoorVind",
        };

        /// <summary>The flashlight and the lantern on their tables are not there for Granny (only on her screen).</summary>
        static void HideTableLights()
        {
            var nd = GameRefs.NewDay;
            if (!U.Alive(nd) || Time.unscaledTime < _nextTableScan) return;
            _nextTableScan = Time.unscaledTime + 1f;
            try
            {
                foreach (var go in new[] { nd.FlashligtTable, nd.LanternTable })
                {
                    if (!U.Alive(go)) continue;
                    foreach (var r in go.GetComponentsInChildren<Renderer>(true))
                        if (r != null && !r.forceRenderingOff) { r.forceRenderingOff = true; if (_tableHidden.TrueForAll(x => x.Pointer != r.Pointer)) _tableHidden.Add(r); }
                    foreach (var l in go.GetComponentsInChildren<Light>(true))
                        if (l != null && l.enabled) { l.enabled = false; _tableLights.Add(l); }
                }
            }
            catch (Exception e) { Log.Debug("PvP table lights: " + e.Message); }
        }
        static readonly List<Renderer> _tableHidden = new();
        static readonly List<Light> _tableLights = new();
        static float _nextTableScan, _loggedSpeed = -1;

        /// <summary>Granny has no flashlight or lantern (the light-sources option gives them to every player at the start).</summary>
        static void NoOwnLights()
        {
            try
            {
                var fc = PlayerSync.FlashController;
                if (U.Alive(fc))
                {
                    if (fc.playerHaveFlashlight) fc.playerHaveFlashlight = false;
                    if (U.Alive(fc.flashlightPlayer) && fc.flashlightPlayer.activeSelf) fc.flashlightPlayer.SetActive(false);
                }
                var lc = PlayerSync.LanternController;
                if (U.Alive(lc))
                {
                    if (lc.playerHaveLantern) lc.playerHaveLantern = false;
                    if (U.Alive(lc.lanternPlayer) && lc.lanternPlayer.activeSelf) lc.lanternPlayer.SetActive(false);
                }
            }
            catch (Exception e) { Log.Debug("PvP lights: " + e.Message); }
        }

        static readonly string[] HideButtonTypes = { "bedButton", "bedButton2", "bedButton3", "coffinButton", "coffinButtonBackyard", "insideCarButton" };

        /// <summary>Granny cannot hide – the "hide" icon of beds / coffins / the car is not shown to her.</summary>
        static void HideHidingButtons()
        {
            float now = Time.unscaledTime;
            if (now >= _nextUiScan)
            {
                _nextUiScan = now + 3f;
                try
                {
                    foreach (var tn in HideButtonTypes)
                    {
                        var t = U.GameType(tn);
                        if (t == null) continue;
                        var prop = t.GetProperty("button", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                        foreach (var o in UnityEngine.Object.FindObjectsOfType(Il2CppInterop.Runtime.Il2CppType.From(t), true))
                        {
                            var comp = o?.TryCast<Component>();
                            if (comp == null || prop == null) continue;
                            var img = prop.GetValue(U.Wrap(t, comp.Pointer)) as UnityEngine.UI.Image;
                            if (!U.Alive(img)) continue;
                            foreach (var im in img.GetComponentsInChildren<UnityEngine.UI.Image>(true))
                                if (im != null && !_hiddenUi.Exists(x => x.Pointer == im.Pointer)) _hiddenUi.Add(im);
                        }
                    }
                }
                catch (Exception e) { Log.Debug("PvP hide icons: " + e.Message); }
            }
            foreach (var b in _hiddenUi) if (U.Alive(b) && b.enabled) b.enabled = false;
        }

        static void RestoreLocal()
        {
            RestoreBodyView();
            if (_hidden.Count > 0) { foreach (var r in _hidden) if (U.Alive(r)) r.forceRenderingOff = false; _hidden.Clear(); _hiddenSet.Clear(); }
            _nextHideScan = 0;
            if (_ignored.Count > 0)
            {
                var cc = GameRefs.PlayerCC;
                if (U.Alive(cc) && cc.Pointer == _ignoreCc) foreach (var c in _ignored) if (U.Alive(c)) try { Physics.IgnoreCollision(cc, c, false); } catch { }
                _ignored.Clear();
            }
            _ignoreCc = IntPtr.Zero;
            if (U.Alive(_nightLight)) { try { UnityEngine.Object.Destroy(_nightLight.gameObject); } catch { } }
            _nightLight = null;
            if (_hiddenUi.Count > 0) { foreach (var b in _hiddenUi) if (U.Alive(b)) try { b.enabled = true; } catch { } _hiddenUi.Clear(); }
            if (_tableHidden.Count > 0) { foreach (var r in _tableHidden) if (U.Alive(r)) try { r.forceRenderingOff = false; } catch { } _tableHidden.Clear(); }
            if (_tableLights.Count > 0) { foreach (var l in _tableLights) if (U.Alive(l)) try { l.enabled = true; } catch { } _tableLights.Clear(); }
            _nextTableScan = 0;
            if (_speedFps != IntPtr.Zero)
            {
                var f = GameRefs.Fps;
                if (U.Alive(f) && f.Pointer == _speedFps) try { f.m_WalkSpeed = _walkSave; f.m_RunSpeed = _runSave; } catch { }
                _speedFps = IntPtr.Zero; _appliedSpeed = -1;
            }
        }

        /// <summary>The line the Granny player sees at the bottom of the screen (null = none).</summary>
        public static string HudText
        {
            get
            {
                if (!LocalGrannyInGame || BlackScreen) return null;
                if (_stunned && LocalFrozen) return L.T("Granny is knocked out – wait until she gets up...", "Granny jest ogłuszona – poczekaj, aż wstanie...");
                return L.T($"You are GRANNY · LMB: grab · RMB: talk / laugh · {Cfg.PvpTrapKey.Value}: bear trap",
                           $"Grasz jako GRANNY · LPM: złap · PPM: mów / śmiej się · {Cfg.PvpTrapKey.Value}: pułapka");
            }
        }
    }
}
