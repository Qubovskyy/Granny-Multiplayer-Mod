using System;
using System.Linq;
using UnityEngine;

namespace GrannyMP
{
    /// <summary>
    /// Host-side Granny AI.
    /// Target = the nearest player Granny can actually SEE (line of sight from her eye, within range/angle);
    /// while she is chasing, the target does not change.
    ///
    /// Granny's code (EnemyAIGranny.FixedUpdate, EnemyEye.Update, grannySeePlayerTrigger) works only with the
    /// real player object (GetComponent on it, "Player" tag checks, linecasts to its head). So when the target is
    /// a remote player, the host's real player is moved to that player's position for the duration of those calls
    /// and moved back right after ("spoofing"). Granny then uses her original sight/chase logic on remote players.
    /// </summary>
    internal static class AiControl
    {
        public static byte Target = 0;
        /// <summary>Host: the player Granny is (or last was) chasing – only that player hears the chase music.</summary>
        public static byte ChaseOwner = 255;
        static float _nextPick;

        static bool[] _hideSave = new bool[11];
        static bool _hideOverridden;

        public static void Reset() { Target = 0; _nextPick = 0; _hideOverridden = false; _spoofDepth = 0; _spoofHid = false; _lostSince = 0; ChaseOwner = 255; _remoteCatchUntil = 0; _holdUntil = 0; _holdRemote = false; _holdAnim = null; _bedReset = false; _lastHidden.Clear(); _lastSeen.Clear(); _seekId = 255; _spotWaitSince = 0; _pvpHostCatchUntil = 0; }

        /// <summary>Granny is chasing somebody right now (and is not knocked out).</summary>
        public static bool ChasingNow { get { var g = GameRefs.Granny; return U.Alive(g) && Chasing(g) && !Stunned(g); } }

        static bool Chasing(EnemyAIGranny g)
        {
            try { return g.huntPlayer || g.attackingPlayer || g.grannyIsFollow || g.seePlayer; } catch { return false; }
        }

        /// <summary>A player Granny can target: in the game, alive, not a ghost and NOT hiding (host included).</summary>
        internal static bool Valid(byte id)
        {
            if (!Session.Players.TryGetValue(id, out var p) || p.Ghost) return false;
            if (id == Session.LocalId) return U.Alive(GameRefs.PlayerRoot) && !(_hideOverridden ? HideSaved() : GameRefs.LocalHiding);
            if (!p.SceneReady || !PlayerSync.TryGetAvatar(id, out var a)) return false;
            if (PlayerSync.TryGetState(id, out var s) && (s.Flags & PlayerSync.F_Hiding) != 0) return false;
            return Time.unscaledTime - a.LastUpdate < 3f;
        }

        static bool HideSaved() { foreach (var b in _hideSave) if (b) return true; return false; }

        /// <summary>The player holds the teddy bear (Granny always knows where it is and hunts that player).</summary>
        static bool HasTeddy(byte id)
        {
            if (id == Session.LocalId) { try { return U.Alive(GameRefs.Inventory) && GameRefs.Inventory.haveteddy; } catch { return false; } }
            return PlayerSync.TryGetState(id, out var s) && (s.Flags & PlayerSync.F_Teddy) != 0;
        }

        // when each remote player was last seen hiding – a catch right after that is refused (the hiding flag
        // reaches the host with a delay and can flicker while the player gets into / out of the hiding spot)
        static readonly System.Collections.Generic.Dictionary<byte, float> _lastHidden = new();
        const float HiddenGrace = 1.5f;
        static bool HiddenRecently(byte id) => _lastHidden.TryGetValue(id, out var t) && Time.unscaledTime - t < HiddenGrace;

        internal static bool PosOf(byte id, out Vector3 pos)
        {
            pos = default;
            if (id == Session.LocalId) { if (!U.Alive(GameRefs.PlayerRoot)) return false; pos = GameRefs.PlayerRoot.position; return true; }
            if (!PlayerSync.TryGetAvatar(id, out var a)) return false;
            pos = a.TPos; return true;
        }

        /// <summary>Eye/head position of a player (world).</summary>
        internal static bool HeadOf(byte id, out Vector3 head)
        {
            head = default;
            if (id == Session.LocalId)
            {
                if (U.Alive(GameRefs.PlayerCam)) { head = GameRefs.PlayerCam.position; return true; }
                if (U.Alive(GameRefs.PlayerRoot)) { head = GameRefs.PlayerRoot.position + Vector3.up * 0.6f; return true; }
                return false;
            }
            if (!PlayerSync.TryGetAvatar(id, out var a)) return false;
            head = a.TPos + Vector3.up * a.CamY;
            return true;
        }

        static EnemyEye _eye;
        /// <summary>Granny's eyes (world).</summary>
        public static Vector3 HeadPoint(EnemyAIGranny g) => EyeTransform(g).position;

        static Transform EyeTransform(EnemyAIGranny g)
        {
            try
            {
                if (!U.Alive(_eye)) _eye = GameRefs.Eye;
                if (U.Alive(_eye) && U.Alive(_eye.myTransform)) return _eye.myTransform;
                if (U.Alive(g.grannyEye)) return g.grannyEye;
            }
            catch { }
            return g.transform;
        }

        /// <summary>Own line-of-sight test (used only to choose WHICH player Granny should look at).</summary>
        static bool CanSee(EnemyAIGranny g, byte id, out float dist)
        {
            dist = float.MaxValue;
            if (!HeadOf(id, out var head)) return false;
            var eye = EyeTransform(g);
            var from = eye.position;
            var dir = head - from;
            dist = dir.magnitude;
            float range = 30f, angle = 70f;
            try { if (U.Alive(_eye)) { if (_eye.seeRange > 1f) range = _eye.seeRange; if (_eye.SeeAngle > 5f) angle = _eye.SeeAngle; } } catch { }
            if (dist > range) return false;
            if (Vector3.Angle(g.transform.forward, dir) > angle) return false;
            // blocked by walls? ignore the target player's own colliders
            if (Physics.Linecast(from, head, out var hit))
            {
                var t = hit.collider != null ? hit.collider.transform : null;
                if (t != null)
                {
                    if (id == Session.LocalId && U.IsUnder(t, GameRefs.PlayerRoot)) return true;
                    if (U.IsUnder(t, g.transform)) return true; // Granny's own collider
                    if ((hit.point - head).sqrMagnitude < 0.36f) return true;
                    return false;
                }
            }
            return true;
        }

        // ---------------- walls ----------------
        /// <summary>
        /// Nothing solid (a wall, a closed door, a floor) between <paramref name="from"/> and <paramref name="to"/>.
        /// Players, their avatars, loose items, triggers and everything under <paramref name="self"/> do not block.
        /// </summary>
        public static bool ClearLine(Vector3 from, Vector3 to, Transform self)
        {
            var dir = to - from; float len = dir.magnitude;
            if (len < 0.05f) return true;
            dir /= len;
            for (int i = 0; i < 8; i++)
            {
                if (!Physics.Linecast(from, to, out var hit, ~0, QueryTriggerInteraction.Ignore)) return true;
                var c = hit.collider;
                if (c == null || (hit.point - to).sqrMagnitude < 0.25f) return true;
                var t = c.transform;
                var rb = c.attachedRigidbody;
                bool pass = (U.Alive(self) && U.IsUnder(t, self)) || (U.Alive(GameRefs.PlayerRoot) && U.IsUnder(t, GameRefs.PlayerRoot))
                            || PlayerSync.IsAvatarPart(t) || (rb != null && !rb.isKinematic);
                if (!pass) return false;
                from = hit.point + dir * 0.05f;
                if (Vector3.Dot(to - from, dir) <= 0f) return true;
            }
            return true;
        }

        static float _wallLogAt;
        /// <summary>Granny can reach the player: no wall between them (head to head or chest to chest).</summary>
        public static bool NoWallBetween(EnemyAIGranny g, byte pid)
        {
            try
            {
                if (!U.Alive(g) || !HeadOf(pid, out var head) || !PosOf(pid, out var root)) return true;
                var eye = EyeTransform(g).position;
                var gChest = Vector3.Lerp(g.transform.position, eye, 0.55f);
                var pChest = Vector3.Lerp(root, head, 0.5f);
                if (ClearLine(eye, head, g.transform) || ClearLine(gChest, pChest, g.transform)) return true;
                if (Time.unscaledTime >= _wallLogAt) { _wallLogAt = Time.unscaledTime + 2f; Log.Debug($"catch of {Session.NameOf(pid)} refused – a wall between Granny and the player"); }
                return false;
            }
            catch (Exception e) { Log.Debug("wall check: " + e.Message); return true; }
        }

        // ---------------- choosing the target ----------------
        // Every 0.25 s all players are checked:
        //  * a player Granny can SEE (line of sight from her eye, range, view angle) beats one she cannot see;
        //  * among the visible ones the NEAREST wins – also during a chase, when another visible player is
        //    clearly closer (1.5 m) or the chased player has been out of sight for 2 s;
        //  * hidden players (bed, wardrobe, coffin, car) and ghosts are never targets – a hidden player
        //    does not make Granny blind to the others;
        //  * when nobody is visible Granny "listens" to the nearest player (her own sight/hearing checks that one).
        static float _lostSince;
        const float SwitchMargin = 1.5f, LostTime = 2f;

        static void Pick(EnemyAIGranny g)
        {
            var gpos = g.transform.position;
            float now = Time.unscaledTime;
            byte bestVis = 255, bestAny = 255; float bestVisD = float.MaxValue, bestAnyD = float.MaxValue;
            bool curVisible = false; float curD = float.MaxValue;
            var sb = new System.Text.StringBuilder();
            byte teddy = 255; float teddyD = float.MaxValue;
            foreach (var id in Session.Players.Keys)
            {
                if (id != Session.LocalId && IsHiddenRemote(id)) _lastHidden[id] = now;
                bool valid = Valid(id);
                if (!valid || !PosOf(id, out var pos)) { sb.Append($" | {Session.NameOf(id)}: {WhyInvalid(id)}"); continue; }
                float d = Vector3.Distance(gpos, pos);
                if (d < bestAnyD) { bestAny = id; bestAnyD = d; }
                if (HasTeddy(id) && d < teddyD) { teddy = id; teddyD = d; }
                bool seen = CanSee(g, id, out _);
                if (seen) _lastSeen[id] = now;
                sb.Append($" | {Session.NameOf(id)}: {d:F1}m {(seen ? "SEEN" : "unseen")}");
                if (!seen) continue;
                if (d < bestVisD) { bestVis = id; bestVisD = d; }
                if (id == Target) { curVisible = true; curD = d; }
            }

            bool targetValid = Valid(Target);
            Status = $"target={Session.NameOf(Target)} owner={(ChaseOwner == 255 ? "-" : Session.NameOf(ChaseOwner))} {GrannyFlags(g)}{(teddy != 255 ? " teddy=" + Session.NameOf(teddy) : "")}{sb}";
            if (Cfg.DebugLog.Value && now >= _nextStatusLog) { _nextStatusLog = now + 2f; Log.Debug("AI " + Status); }
            // the chased player hid (bed, wardrobe, coffin, car) – like in single player: when Granny was chasing that
            // player (or saw them a moment ago) she goes to the hiding spot, waits, looks in and catches them; a player
            // who hid unseen is safe. Another player she can see right now takes over.
            bool targetHidden = Target == Session.LocalId ? LocalHidden() : IsHiddenRemote(Target);
            if (!targetValid && targetHidden)
            {
                bool hunted = _seekId == Target || Chasing(g) || (_lastSeen.TryGetValue(Target, out var ls) && now - ls < 2f);
                if (hunted)
                {
                    if (bestVis != 255) { EndSeek(); SetTarget(bestVis, "seen while the chased player hides"); return; }
                    // the host hid: Granny's own single-player code walks to the bed and catches (EndDayUnderBed)
                    // only real hiding spots (beds, coffins, car) can be searched, like in single player
                    if (Target != Session.LocalId && _seekId != Target)
                    {
                        if (SpotOf(Target) is >= 1 and <= 6) BeginSeek(g, Target);
                        else if (Chasing(g)) { Log.Debug($"{Session.NameOf(Target)} hid (no searchable spot) – Granny stops the chase"); ResetChase(); }
                    }
                    return;
                }
                if (Target != Session.LocalId && Chasing(g)) { Log.Debug($"{Session.NameOf(Target)} hid unseen – Granny stops the chase"); ResetChase(); return; }
            }
            if (_seekId != 255 && _seekId != Target) EndSeek();
            // the teddy bear: like in single player Granny always knows where it is and goes for that player
            if (teddy != 255)
            {
                if (Target != teddy) SetTarget(teddy, "has the teddy");
                return;
            }
            if (targetValid && Chasing(g))
            {
                if (curVisible) _lostSince = 0; else if (_lostSince == 0) _lostSince = now;
                if (bestVis != 255 && bestVis != Target)
                {
                    if (curVisible ? bestVisD + SwitchMargin < curD : now - _lostSince > LostTime) SetTarget(bestVis, "closer / visible");
                }
                return;
            }
            _lostSince = 0;
            if (bestVis != 255) { SetTarget(bestVis, "seen"); return; }
            // she heard someone recently (noise forwarded by a client) – keep walking towards that player
            if (targetValid && now < _heardUntil) return;
            if (bestAny != 255) { SetTarget(bestAny, null); return; }
            Target = Session.LocalId; // everybody hidden / ghost: Granny's original behaviour
        }

        /// <summary>Last AI decision (for the debug panel and the log).</summary>
        public static string Status = "";
        static float _nextStatusLog;

        static readonly System.Collections.Generic.Dictionary<byte, float> _lastSeen = new();

        static bool LocalHidden()
        {
            if (!U.Alive(GameRefs.PlayerRoot) || (Session.Local?.Ghost ?? false)) return false;
            return _hideOverridden ? HideSaved() : GameRefs.LocalHiding;
        }

        static string WhyInvalid(byte id)
        {
            if (!Session.Players.TryGetValue(id, out var p)) return "gone";
            if (p.Ghost) return "spectator";
            if (id == Session.LocalId) return U.Alive(GameRefs.PlayerRoot) ? "hidden" : "no player";
            if (!p.SceneReady) return "loading";
            if (!PlayerSync.TryGetAvatar(id, out var a)) return "no avatar";
            if (PlayerSync.TryGetState(id, out var s) && (s.Flags & PlayerSync.F_Hiding) != 0) return "hidden";
            return $"stale {Time.unscaledTime - a.LastUpdate:F1}s";
        }

        static string GrannyFlags(EnemyAIGranny g)
        {
            try
            {
                var f = GameRefs.Fps;
                return $"[hunt={B(g.huntPlayer)} see={B(g.seePlayer)} follow={B(g.grannyIsFollow)} attack={B(g.attackingPlayer)} caught={B(g.playerGetCaught)} search={B(g.GrannySearching)} hostCaught={B(U.Alive(f) && f.playerCaught)}]";
            }
            catch { return ""; }
        }
        static string B(bool b) => b ? "1" : "0";
        static float _heardUntil;

        /// <summary>A noise trigger (creaky floor, falling item…) forwarded by a client: Granny investigates that player.</summary>
        public static void OnRemoteHeard(byte id)
        {
            var g = GameRefs.Granny;
            if (!U.Alive(g) || !Valid(id)) return;
            if (Chasing(g) && Valid(Target) && Target != id) return;
            SetTarget(id, "heard");
            _heardUntil = Time.unscaledTime + 10f;
        }
        public static bool IsHiddenRemote(byte id) =>
            Session.Players.TryGetValue(id, out var p) && !p.Ghost && PlayerSync.TryGetAvatar(id, out _)
            && PlayerSync.TryGetState(id, out var st) && (st.Flags & PlayerSync.F_Hiding) != 0;

        static void SetTarget(byte id, string why)
        {
            if (Target == id) return;
            Target = id; _lostSince = 0;
            if (why != null) Log.Debug($"Granny target -> {Session.NameOf(id)} ({why})");
        }

        // ---------------- spoofing the host player ----------------
        static int _spoofDepth;
        static Vector3 _savePos, _savePPos; static Quaternion _saveRot;
        static Transform _spoofPP;

        /// <summary>Move the host's real player to the position of remote player <paramref name="id"/>.</summary>
        public static bool BeginSpoof(byte id)
        {
            if (id == Session.LocalId || !PlayerSync.TryGetAvatar(id, out var av)) return false;
            var root = GameRefs.PlayerRoot;
            if (!U.Alive(root)) return false;
            if (_spoofDepth++ > 0) return true;
            _savePos = root.position; _saveRot = root.rotation;
            var delta = av.TPos - _savePos;
            root.position = av.TPos;
            root.rotation = Quaternion.Euler(0, av.TYaw, 0);
            // hiding under a bed DEACTIVATES the host's player object – Granny's sight/trigger code cannot see an
            // inactive object, so for the duration of the spoof it is switched on (and off again in EndSpoof)
            _spoofActivated = false;
            if (!root.gameObject.activeSelf) { root.gameObject.SetActive(true); _spoofActivated = true; }
            if (U.Alive(GameRefs.Granny) && !_hideOverridden) { OverrideHide(GameRefs.Granny); _spoofHid = true; }
            _spoofPP = GameRefs.OrigGrannyPlayerPos;
            if (U.Alive(_spoofPP) && !U.IsUnder(_spoofPP, root)) { _savePPos = _spoofPP.position; _spoofPP.position = _savePPos + delta; } else _spoofPP = null;
            SpoofEyeTarget(av);
            // the teddy bear: Granny's code reads her own playerHaveTeddy flag (set by the host's pick-up only)
            _teddySet = false;
            var g0 = GameRefs.Granny;
            if (U.Alive(g0)) { try { _teddySave = g0.playerHaveTeddy; bool t = HasTeddy(id); if (t != _teddySave) { g0.playerHaveTeddy = t; _teddySet = true; } } catch { } }
            try { Physics.SyncTransforms(); } catch { }
            return true;
        }
        static bool _teddySave, _teddySet;

        // Granny's eye (EnemyEye) linecasts to its target – the host's Main Camera – and sees a player only when the ray
        // hits exactly that object. After the host died the camera is elsewhere (death / crawl animation, spectator view)
        // or its collider is off, so Granny went blind for everybody "sometimes" (depending on how the host died).
        // For the spoof the camera is put at the remote player's head with its collider on.
        static Transform _eyeTgt; static Vector3 _eyeTgtPos; static Quaternion _eyeTgtRot;
        static Collider _eyeTgtCol; static bool _eyeTgtColWas;
        static readonly System.Collections.Generic.List<GameObject> _eyeActivated = new();

        static void SpoofEyeTarget(Avatar av)
        {
            _eyeTgt = null;
            try
            {
                var e = U.Alive(_eye) ? _eye : GameRefs.Eye;
                var t = U.Alive(e) ? e.target : null;
                if (!U.Alive(t)) return;
                _eyeTgt = t; _eyeTgtPos = t.localPosition; _eyeTgtRot = t.localRotation;
                _eyeActivated.Clear();
                for (var x = t; x != null && !x.gameObject.activeInHierarchy; x = x.parent)
                    if (!x.gameObject.activeSelf) { x.gameObject.SetActive(true); _eyeActivated.Add(x.gameObject); }
                t.position = av.TPos + Vector3.up * av.CamY;
                t.rotation = Quaternion.Euler(av.TPitch, av.TYaw, 0);
                _eyeTgtCol = t.GetComponent<Collider>();
                if (U.Alive(_eyeTgtCol)) { _eyeTgtColWas = _eyeTgtCol.enabled; if (!_eyeTgtColWas) _eyeTgtCol.enabled = true; }
            }
            catch (Exception ex) { Log.Debug("eye target: " + ex.Message); }
        }

        static void RestoreEyeTarget()
        {
            if (!U.Alive(_eyeTgt)) { _eyeTgt = null; return; }
            try
            {
                if (U.Alive(_eyeTgtCol) && !_eyeTgtColWas) _eyeTgtCol.enabled = false;
                _eyeTgt.localPosition = _eyeTgtPos; _eyeTgt.localRotation = _eyeTgtRot;
                for (int i = _eyeActivated.Count - 1; i >= 0; i--) if (U.Alive(_eyeActivated[i])) _eyeActivated[i].SetActive(false);
            }
            catch { }
            _eyeActivated.Clear(); _eyeTgt = null; _eyeTgtCol = null;
        }

        public static void EndSpoof()
        {
            if (_spoofDepth <= 0) return;
            if (--_spoofDepth > 0) return;
            RestoreEyeTarget();
            var root = GameRefs.PlayerRoot;
            if (U.Alive(root))
            {
                root.position = _savePos; root.rotation = _saveRot;
                if (_spoofActivated) { _spoofActivated = false; root.gameObject.SetActive(false); }
            }
            if (U.Alive(_spoofPP)) _spoofPP.position = _savePPos;
            if (_teddySet) { _teddySet = false; try { if (U.Alive(GameRefs.Granny)) GameRefs.Granny.playerHaveTeddy = _teddySave; } catch { } }
            if (_spoofHid) { _spoofHid = false; if (_hideOverridden && U.Alive(GameRefs.Granny)) RestoreHide(GameRefs.Granny); }
            try { Physics.SyncTransforms(); } catch { }
        }
        static bool _spoofHid, _spoofActivated;

        public static void EnsureRestored()
        {
            if (_spoofDepth > 0) { _spoofDepth = 1; EndSpoof(); }
            if (_hideOverridden && U.Alive(GameRefs.Granny)) RestoreHide(GameRefs.Granny);
        }

        // ---------------- EnemyAIGranny.FixedUpdate ----------------
        /// <summary>Host, before Granny's FixedUpdate. Returns false when her FixedUpdate must be skipped this step.</summary>
        public static bool PreFixedUpdate(EnemyAIGranny g)
        {
            if (!Session.Active || !Session.IsHost || !U.Alive(g)) return true;
            if (UpdateHold(g, out bool runGranny)) { if (Pvp.Active) Pvp.Release(g); return runGranny; } // a catch animation is playing
            // a spectating host must never look "caught" to Granny – her code reads the host player's state even
            // while she is chasing a remote player (that is how the host's own catch blinded her for everybody)
            if (Session.Local?.Ghost ?? false) { var f = GameRefs.Fps; try { if (U.Alive(f) && f.playerCaught) f.playerCaught = false; } catch { } }
            // PvP: a player is Granny – no AI, she walks where that player walks (Pvp)
            if (Pvp.Active) return Pvp.HostFixed(g);
            Pvp.Release(g);
            // the teddy bear: Granny's own code (her red eyes, the teddy music, going for the player) reads her
            // playerHaveTeddy flag, which only the host's own pick-up sets – any player's teddy counts
            try { bool anyTeddy = Session.Players.Keys.Any(id => Valid(id) && HasTeddy(id)); if (g.playerHaveTeddy != anyTeddy) g.playerHaveTeddy = anyTeddy; } catch { }
            if (Time.unscaledTime >= _nextPick) { _nextPick = Time.unscaledTime + 0.25f; Pick(g); }
            if (_seekId != 255) return UpdateSeek(g);
            if (SpotWatchdog(g)) return false;
            if (Chasing(g)) ChaseOwner = Target;
            if (Target == Session.LocalId || !PlayerSync.TryGetAvatar(Target, out var av)) return true;
            if (IsHiddenRemote(Target)) return true; // never aim Granny at a hidden player

            // catching a remote player
            float dist = (av.TPos - g.transform.position).magnitude;
            float reach = 1.3f; try { reach = Mathf.Max(1.2f, g.attackDistance + 0.2f); } catch { }
            if (dist < reach && Chasing(g) && !Stunned(g) && !GameFlow.InTransition && !HiddenRecently(Target)) { if (RemoteCaught(Target, "Granny")) return false; }

            BeginSpoof(Target);
            return true;
        }

        // ---------------- catch animation ----------------
        // Like in single player Granny stops and plays her grab animation ("Hit") when she catches somebody.
        // Remote victim: her AI is paused for the animation (the victim's own game plays the jumpscare).
        // Host victim: her own catch sequence runs; our target choosing waits until it is over.
        static float _holdUntil; static byte _holdVictim = 255; static bool _holdRemote, _bedReset; static string _holdAnim;
        static Vector3? _holdFace; // where Granny looks during the catch (the bed she looks under)
        public static bool HoldingCatch => _holdUntil > 0 && Time.unscaledTime < _holdUntil;

        static void BeginRemoteHold(EnemyAIGranny g, byte pid, string anim = "Hit")
        {
            _holdUntil = Time.unscaledTime + (anim == "Hit" ? 2.6f : 3.2f); _holdVictim = pid; _holdRemote = true; _holdAnim = anim;
            EndSpoof();
            try
            {
                var nav = g.navComponent;
                if (U.Alive(nav) && nav.enabled) { nav.isStopped = true; nav.velocity = Vector3.zero; }
                FaceVictim(g);
                if (anim == "lookBed")
                {
                    // like in single player: her head-follow is switched off so the look under the bed plays as
                    // animated, and her under-bed state is reset when it is over
                    SetHeadFollow(g, false);
                    _bedReset = true;
                }
                var an = U.Alive(g.animationHolder) ? g.animationHolder.GetComponent<Animation>() : null;
                if (U.Alive(an)) an.CrossFade(anim);
            }
            catch (Exception e) { Log.Debug("catch hold: " + e.Message); }
        }

        static void BeginHostHold()
        {
            _holdUntil = Time.unscaledTime + 3.5f; _holdVictim = Session.LocalId; _holdRemote = false;
        }

        /// <summary>
        /// Host, "last survivor": Granny caught the host under a bed (her single-player look under the bed) but the
        /// day goes on – the bed jumpscare plays, then Granny's under-bed state is reset and she continues.
        /// </summary>
        public static void AfterHostBedCatch()
        {
            _holdUntil = Time.unscaledTime + 2.5f; _holdVictim = Session.LocalId; _holdRemote = true; _bedReset = true; _holdAnim = null;
            EndSeek();
        }

        public static void SetHeadFollow(EnemyAIGranny g, bool on)
        {
            try
            {
                if (!U.Alive(g.boneController)) return;
                foreach (var o in g.boneController.GetComponents(Il2CppInterop.Runtime.Il2CppType.Of<Behaviour>()))
                {
                    var c = o?.TryCast<Behaviour>();
                    if (c != null && c.enabled != on && U.Il2CppTypeName(c) == "BoneController") c.enabled = on;
                }
            }
            catch (Exception e) { Log.Debug("head follow: " + e.Message); }
        }

        static void FaceVictim(EnemyAIGranny g)
        {
            Vector3 p;
            if (_holdFace.HasValue) p = _holdFace.Value;
            else if (!PosOf(_holdVictim, out p)) return;
            var d = p - g.transform.position; d.y = 0;
            if (d.sqrMagnitude > 0.01f) g.transform.rotation = Quaternion.Slerp(g.transform.rotation, Quaternion.LookRotation(d), 0.25f);
        }

        static bool UpdateHold(EnemyAIGranny g, out bool runGranny)
        {
            runGranny = true;
            if (_holdUntil <= 0) return false;
            if (Time.unscaledTime < _holdUntil)
            {
                if (_holdRemote) { runGranny = false; if (_holdAnim != null) try { FaceVictim(g); } catch { } }
                return true;
            }
            _holdUntil = 0; _holdRemote = false; _holdVictim = 255; _holdAnim = null; _holdFace = null;
            if (_bedReset) { _bedReset = false; ResetBedLook(g); }
            try { var nav = g.navComponent; if (U.Alive(nav) && nav.enabled) nav.isStopped = false; } catch { }
            ResetChase();
            return false;
        }

        public static void PostFixedUpdate(EnemyAIGranny g)
        {
            if (_spoofDepth > 0) EndSpoof();
            if (_hideOverridden) RestoreHide(g);
        }

        // ---------------- EnemyEye.Update (Granny's sight) ----------------
        static bool _eyeSpoof;
        public static void PreEye(EnemyEye e)
        {
            _eyeSpoof = false;
            _eye = e;
            if (!Session.Active || !Session.IsHost) return;
            if (HoldingCatch) return;
            var g = GameRefs.Granny;
            if (U.Alive(g) && Time.unscaledTime >= _nextPick) { _nextPick = Time.unscaledTime + 0.25f; Pick(g); }
            if (Target != Session.LocalId && !IsHiddenRemote(Target)) _eyeSpoof = BeginSpoof(Target);
        }

        public static void PostEye(EnemyEye e)
        {
            if (_eyeSpoof && Cfg.DebugLog.Value && Time.unscaledTime >= _nextEyeLog)
            {
                _nextEyeLog = Time.unscaledTime + 2f;
                try
                {
                    var t = e.target;
                    PlayerSync.TryGetAvatar(Target, out var av);
                    var head = av != null ? av.TPos + Vector3.up * av.CamY : Vector3.zero;
                    Log.Debug($"eye -> {Session.NameOf(Target)}: target '{(U.Alive(t) ? t.name : "-")}' {(U.Alive(t) ? (t.position - head).magnitude : -1):F2} m from head, sees {B(e.seePlayer)}/{B(e.seePlayerR)}/{B(e.seePlayerL)}, host {(Session.Local?.Ghost ?? false ? "spectating" : "alive")}");
                }
                catch { }
            }
            if (_eyeSpoof) { _eyeSpoof = false; EndSpoof(); }
        }
        static float _nextEyeLog;

        /// <summary>A sight trigger forwarded by a client: make that player the target if Granny is free.</summary>
        public static void OnRemoteSeen(byte id)
        {
            var g = GameRefs.Granny;
            if (!U.Alive(g) || !Valid(id)) return;
            if (Chasing(g) && Valid(Target) && Target != id) return; // Pick() decides about switching during a chase
            SetTarget(id, "sight trigger");
        }

        // ---------------- hiding flags ----------------
        static void OverrideHide(EnemyAIGranny g)
        {
            try
            {
                _hideSave[0] = g.playerHiding; _hideSave[1] = g.playerHidingUnderBed; _hideSave[2] = g.playerHidingInCoffin; _hideSave[3] = g.playerHidingInCoffinBackyard;
                _hideSave[4] = g.playerHidingInCar; _hideSave[5] = g.hidingUnderBed1; _hideSave[6] = g.hidingUnderBed2; _hideSave[7] = g.hidingUnderBed3;
                _hideSave[8] = g.hidingInCoffin4; _hideSave[9] = g.hidingInCoffinBY; _hideSave[10] = g.hidingInCar;
                g.playerHiding = false; g.playerHidingUnderBed = false; g.playerHidingInCoffin = false; g.playerHidingInCoffinBackyard = false; g.playerHidingInCar = false;
                g.hidingUnderBed1 = false; g.hidingUnderBed2 = false; g.hidingUnderBed3 = false; g.hidingInCoffin4 = false; g.hidingInCoffinBY = false; g.hidingInCar = false;
                _hideOverridden = true;
            }
            catch { }
        }

        static void RestoreHide(EnemyAIGranny g)
        {
            _hideOverridden = false;
            try
            {
                g.playerHiding = _hideSave[0]; g.playerHidingUnderBed = _hideSave[1]; g.playerHidingInCoffin = _hideSave[2]; g.playerHidingInCoffinBackyard = _hideSave[3];
                g.playerHidingInCar = _hideSave[4]; g.hidingUnderBed1 = _hideSave[5]; g.hidingUnderBed2 = _hideSave[6]; g.hidingUnderBed3 = _hideSave[7];
                g.hidingInCoffin4 = _hideSave[8]; g.hidingInCoffinBY = _hideSave[9]; g.hidingInCar = _hideSave[10];
            }
            catch { }
        }

        // ---------------- catches ----------------
        static float _remoteCatchUntil;

        /// <summary>
        /// Granny is knocked out or frozen (crossbow, shotgun, car, pepper spray, freeze trap): her chase flags stay
        /// set and she lies next to the player, but she must not catch anybody until she gets up.
        /// </summary>
        public static bool Stunned(EnemyAIGranny g)
        {
            if (!U.Alive(g)) return false;
            try { return g.hitByArrow || g.hitByGun || g.hitByCar || g.hitByPepper || g.hitByPepperStart || g.freeze; }
            catch { return false; }
        }

        /// <summary>Which knock-out flags are on (for the log).</summary>
        public static string StunReason(EnemyAIGranny g)
        {
            if (!U.Alive(g)) return "-";
            try
            {
                var l = new System.Collections.Generic.List<string>();
                if (g.hitByArrow) l.Add("crossbow"); if (g.hitByGun) l.Add("shotgun"); if (g.hitByCar) l.Add("car");
                if (g.hitByPepper || g.hitByPepperStart) l.Add("pepper spray"); if (g.freeze) l.Add("freeze");
                return l.Count > 0 ? string.Join(", ", l) : "none";
            }
            catch { return "?"; }
        }

        /// <summary>PvP: Granny gets up (a knock-out flag stayed on too long).</summary>
        public static void ClearStun(EnemyAIGranny g)
        {
            try { g.hitByArrow = false; g.hitByGun = false; g.hitByCar = false; g.hitByPepper = false; g.hitByPepperStart = false; g.freeze = false; }
            catch (Exception e) { Log.Debug("clear stun: " + e.Message); }
        }

        static bool RemoteCaught(byte pid, string by, string anim = "Hit", bool found = false)
        {
            // found = caught in the hiding spot (Granny looked in)
            if (HoldingCatch) return false;
            if (Stunned(GameRefs.Granny)) { Log.Debug($"catch of {Session.NameOf(pid)} refused – Granny is stunned"); return false; }
            if (!found && (!Valid(pid) || HiddenRecently(pid))) return false;
            if (!GameFlow.CanCatch(pid)) return false;
            if (!found && !NoWallBetween(GameRefs.Granny, pid)) return false; // never through a wall
            // Granny's own catch sequence may already be running against the (spoofed) host player –
            // for a few seconds it must not catch the host instead (in "last survivor" the host's day would end)
            _remoteCatchUntil = Time.unscaledTime + 6f;
            if (!found) _holdFace = null;
            var g = GameRefs.Granny;
            int spot = found ? SpotOf(pid) : 0;
            if (U.Alive(g)) BeginRemoteHold(g, pid, anim);
            GameFlow.PlayerCaught(pid, by);
            // (host option) everybody hiding in the same spot is found together
            if (found) GameFlow.CatchOthersInSpot(spot, pid, by);
            _nextPick = 0;
            return true;
        }

        static float _pvpHostCatchUntil;

        /// <summary>
        /// Host, PvP: the Granny player grabs <paramref name="victim"/>. The victim must play (not a ghost, not Granny),
        /// stand within <paramref name="reach"/> of Granny – a hiding victim within <paramref name="spotReach"/> of the
        /// hiding spot – and Granny must be free. The catch is the normal one: Granny's grab or her look under the bed,
        /// the jumpscare, then the victim spectates.
        /// </summary>
        public static bool PvpCatch(byte victim, float reach, float spotReach)
        {
            var g = GameRefs.Granny;
            if (!U.Alive(g) || HoldingCatch || Stunned(g) || Pvp.IsGranny(victim)) return false;
            if (!Session.Players.TryGetValue(victim, out var p) || p.Ghost || !GameFlow.CanCatch(victim)) return false;
            if (!PosOf(victim, out var vp)) return false;
            var gp = g.transform.position;
            bool hidden = victim == Session.LocalId ? LocalHidden() : IsHiddenRemote(victim);
            int spot = 0;
            if (hidden)
            {
                spot = victim == Session.LocalId ? GameRefs.LocalSpot : SpotOf(victim);
                if (spot < 1) return false;
                var cam = spot <= 6 ? GameRefs.SpotCamera(spot, vp) : null;
                var sp = U.Alive(cam) ? cam.position : vp;
                if (U.FlatDist(gp, sp) > spotReach && (spot > 6 || U.FlatDist(gp, SpotPos(g, spot, vp)) > spotReach)) return false;
                _holdFace = sp;
            }
            else
            {
                if (U.FlatDist(gp, vp) > reach || Mathf.Abs(gp.y - vp.y) > 2.2f) return false;
                if (victim != Session.LocalId && !Valid(victim)) return false;
                if (!NoWallBetween(g, victim)) return false;
                _holdFace = null;
            }
            Pvp.Release(g);
            bool bed = spot >= 1 && spot <= 3;
            if (victim != Session.LocalId) return RemoteCaught(victim, bed ? "bed" : "Granny", bed ? "lookBed" : "Hit", hidden);
            if (hidden)
            {
                _remoteCatchUntil = 0;
                BeginRemoteHold(g, victim, bed ? "lookBed" : "Hit");
                GameFlow.HostFoundInSpot(spot);
                return true;
            }
            // the host: Granny's own catch sequence (her grab and the jumpscare) – ShouldSkipHostCatch lets this one through
            Target = Session.LocalId; _pvpHostCatchUntil = Time.unscaledTime + 1.5f;
            try { g.StartCoroutine(g.Playercaught()); }
            catch (Exception e) { Log.Warn("PvP catch: " + e.Message); _pvpHostCatchUntil = 0; GameFlow.PlayerCaught(victim, "Granny"); }
            return true;
        }

        /// <summary>Host: a remote player was caught a moment ago (Granny's own catch logic may still run against the host).</summary>
        public static bool RecentRemoteCatch => Time.unscaledTime < _remoteCatchUntil;

        // ---------------- a chased remote player hid ----------------
        static byte _seekId = 255; static int _seekSpot; static Vector3 _seekPos;
        static float _seekStart, _seekArrived, _nextDest; static bool _seekWalk;
        public static bool Seeking => _seekId != 255;

        /// <summary>Where Granny goes to look into hiding spot <paramref name="spot"/> (PlayerSync.Spot*).</summary>
        static Vector3 SpotPos(EnemyAIGranny g, int spot, Vector3 fallback)
        {
            try
            {
                Transform t = spot switch
                {
                    1 => g.bedtargetTemp1, 2 => g.bedtargetTemp2, 3 => g.bedtargetTemp3,
                    4 => g.coffintargetTemp4, 5 => g.coffintargetTempBY, 6 => g.cartargetTemp, _ => null
                };
                if (U.Alive(t) && (t.position - fallback).sqrMagnitude < 36f) return t.position;
            }
            catch { }
            return fallback;
        }

        static int SpotOf(byte id) => PlayerSync.TryGetState(id, out var st) ? st.Spot : 0;
        static readonly System.Collections.Generic.Dictionary<byte, float> _seekLogged = new();

        static void BeginSeek(EnemyAIGranny g, byte id)
        {
            if (!PlayerSync.TryGetAvatar(id, out var a)) return;
            _seekId = id; _seekStart = Time.unscaledTime; _seekArrived = 0; _nextDest = 0; _seekWalk = false; _seekVisibleSince = 0; _seekStillSince = 0;
            _seekSpot = SpotOf(id);
            _seekPos = SpotPos(g, _seekSpot, a.TPos);
            ChaseOwner = id;
            string msg = $"{Session.NameOf(id)} hid in front of Granny (spot {_seekSpot}) – she goes to look";
            if (!_seekLogged.TryGetValue(id, out var t) || Time.unscaledTime - t > 15f) { _seekLogged[id] = Time.unscaledTime; Log.Info(msg); }
            else Log.Debug(msg);
        }
        static float _seekVisibleSince, _seekStillSince, _seekLogAt;

        static void EndSeek()
        {
            if (_seekId == 255) return;
            _seekId = 255;
            var g = GameRefs.Granny;
            try { if (U.Alive(g) && U.Alive(g.navComponent)) g.navComponent.isStopped = false; } catch { }
        }

        static Animation GrannyAnim(EnemyAIGranny g) => U.Alive(g.animationHolder) ? g.animationHolder.GetComponent<Animation>() : null;

        /// <summary>Granny walks to the hiding spot, waits 3 s like in single player, then looks in and catches the player.</summary>
        static bool UpdateSeek(EnemyAIGranny g)
        {
            float now = Time.unscaledTime;
            byte id = _seekId;
            if (!Session.Players.ContainsKey(id) || GameFlow.InTransition) { EndSeek(); _nextPick = 0; return true; }
            // the player came out of the hiding spot (the hiding flag can flicker for a moment – wait 1 s)
            if (!IsHiddenRemote(id))
            {
                if (_seekVisibleSince == 0) _seekVisibleSince = now;
                if (now - _seekVisibleSince > 1f) { Log.Debug("seek: the player left the hiding spot"); EndSeek(); _nextPick = 0; return true; }
            }
            else _seekVisibleSince = 0;
            if (now - _seekStart > 25f) { Log.Debug("seek: hiding spot not reached – Granny gives up"); EndSeek(); ResetChase(); return true; }
            try
            {
                var nav = g.navComponent;
                var an = GrannyAnim(g);
                var gp = g.transform.position;
                float flat = new Vector2(gp.x - _seekPos.x, gp.z - _seekPos.z).magnitude;
                // arrived: close to the spot, or the agent cannot get any closer (in single player 4 m from the bed is enough)
                bool navDone = U.Alive(nav) && !nav.pathPending && nav.remainingDistance < 0.6f;
                bool still = U.Alive(nav) && nav.velocity.sqrMagnitude < 0.04f;
                if (still && now - _seekStart > 1f) { if (_seekStillSince == 0) _seekStillSince = now; } else _seekStillSince = 0;
                bool arrived = flat < 2f || (now - _seekStart > 1f && flat < 4.5f && (navDone || (_seekStillSince > 0 && now - _seekStillSince > 0.8f)))
                               || (flat < 7f && _seekStillSince > 0 && now - _seekStillSince > 2.5f);
                if (Cfg.DebugLog.Value && now >= _seekLogAt) { _seekLogAt = now + 2f; Log.Debug($"seek: {flat:F1} m from the spot, path left {(U.Alive(nav) ? nav.remainingDistance : -1):F1} m, still {(_seekStillSince > 0 ? now - _seekStillSince : 0):F1} s"); }
                if (_seekArrived == 0 && !arrived)
                {
                    if (U.Alive(nav))
                    {
                        nav.isStopped = false;
                        try { nav.speed = Mathf.Max(1f, g.grannysFollowSpeed); } catch { }
                        if (now >= _nextDest) { _nextDest = now + 0.5f; nav.SetDestination(_seekPos); }
                    }
                    if (!_seekWalk && U.Alive(an))
                    {
                        _seekWalk = true;
                        an.CrossFade("Walk");
                        try { var w = an["Walk"]; if (w != null) w.speed = Mathf.Max(0.5f, g.grannysAnimFollowSpeed); } catch { }
                    }
                    return false;
                }
                if (_seekArrived == 0)
                {
                    _seekArrived = now;
                    if (U.Alive(nav)) { nav.isStopped = true; nav.velocity = Vector3.zero; }
                    if (U.Alive(an)) an.CrossFade("idle");
                }
                // like in single player (facePlayerBed): she turns to the hiding spot itself, not to where the
                // player stood before hiding
                var face = FacePoint(id, gp);
                {
                    var d = face - gp; d.y = 0;
                    float spd = 3f; try { spd = Mathf.Max(1f, g.speed); } catch { }
                    if (d.sqrMagnitude > 0.01f) g.transform.rotation = Quaternion.RotateTowards(g.transform.rotation, Quaternion.LookRotation(d), spd * 60f * Time.deltaTime);
                }
                if (now - _seekArrived < 3f) return false;
                bool bed = _seekSpot >= 1 && _seekSpot <= 3;
                _holdFace = face;
                EndSeek();
                if (!RemoteCaught(id, bed ? "bed" : "Granny", bed ? "lookBed" : "Hit", true)) { Log.Debug("seek: catch refused"); ResetChase(); }
            }
            catch (Exception e) { Log.Debug("seek: " + e.Message); EndSeek(); }
            return false;
        }

        // ---------------- Granny stands at a hiding spot and does not look in ----------------
        // Her own code (the host hiding under a bed) looks under the bed 3 s after she stops in front of it; in the
        // shared game she was seen standing there for more than 10 s (two players under one bed). When she stands
        // still at a hiding spot with a player she was after for SpotWaitMax seconds, the mod looks in for her.
        const float SpotWaitMax = 6f, SpotReach = 3.2f;
        static float _spotWaitSince, _spotWaitLog; static int _spotWaitSpot;

        /// <summary>Host, every FixedUpdate. Returns true when the catch was started now (Granny's own step is skipped).</summary>
        static bool SpotWatchdog(EnemyAIGranny g)
        {
            float now = Time.unscaledTime;
            int spot = 0; float best = SpotReach; bool still = false;
            try
            {
                if (HoldingCatch || _seekId != 255 || Stunned(g) || GameFlow.InTransition || g.grannyLookUnderBed || g.playerGetCaught) { _spotWaitSince = 0; return false; }
                var nav = g.navComponent;
                still = !U.Alive(nav) || nav.velocity.sqrMagnitude < 0.09f;
                var gp = g.transform.position;
                foreach (var p in Session.Players.Values)
                {
                    if (p.Ghost) continue;
                    int s = p.IsLocal ? (LocalHidden() ? GameRefs.LocalSpot : 0) : (IsHiddenRemote(p.Id) ? SpotOf(p.Id) : 0);
                    if (s < 1 || s > 6 || !PosOf(p.Id, out var pp)) continue;
                    // a player hiding unseen is safe – only the one she chased (or saw a moment ago); once she waits at
                    // the spot she keeps waiting even when her chase flags run out
                    bool wanted = (_spotWaitSince > 0 && s == _spotWaitSpot) || (p.Id == Target && Chasing(g)) || (_lastSeen.TryGetValue(p.Id, out var ls) && now - ls < 4f);
                    if (!wanted) continue;
                    var cam = GameRefs.SpotCamera(s, pp);
                    float d = Mathf.Min(U.FlatDist(gp, U.Alive(cam) ? cam.position : pp), U.FlatDist(gp, SpotPos(g, s, pp)));
                    if (d < best) { best = d; spot = s; }
                }
            }
            catch { spot = 0; }
            if (spot == 0 || !still) { _spotWaitSince = 0; return false; }
            if (_spotWaitSince == 0 || _spotWaitSpot != spot) { _spotWaitSince = now; _spotWaitSpot = spot; return false; }
            if (Cfg.DebugLog.Value && now >= _spotWaitLog)
            {
                _spotWaitLog = now + 2f;
                try { Log.Debug($"spot watchdog: Granny {best:F1} m from spot {spot} for {now - _spotWaitSince:F1} s [timerBed={g.timerBed:F1} waypoint={g.distanceWaypoint:F1} follow={B(g.grannyIsFollow)} hunt={B(g.huntPlayer)} underBed={B(g.playerHidingUnderBed)}]"); } catch { }
            }
            if (now - _spotWaitSince < SpotWaitMax) return false;
            _spotWaitSince = 0;
            return LookIntoSpot(g, spot);
        }

        /// <summary>Granny looks into hiding spot <paramref name="spot"/> and catches who hides there (her chased player first).</summary>
        static bool LookIntoSpot(EnemyAIGranny g, int spot)
        {
            bool bed = spot >= 1 && spot <= 3;
            var inSpot = Session.Players.Values.Where(p => !p.Ghost && (p.IsLocal ? LocalHidden() && GameRefs.LocalSpot == spot : IsHiddenRemote(p.Id) && SpotOf(p.Id) == spot))
                                         .Select(p => p.Id).ToList();
            if (inSpot.Count == 0) return false;
            byte victim = inSpot.Contains(Target) ? Target : inSpot.Contains(Session.LocalId) ? Session.LocalId : inSpot[0];
            PosOf(victim, out var vp);
            var cam = GameRefs.SpotCamera(spot, vp);
            _holdFace = U.Alive(cam) ? cam.position : vp;
            Log.Info($"Granny stood at hiding spot {spot} without looking in – she looks in now ({Session.NameOf(victim)})");
            EndSeek();
            if (victim != Session.LocalId) return RemoteCaught(victim, bed ? "bed" : "Granny", bed ? "lookBed" : "Hit", true);
            if (!GameFlow.CanCatch(victim)) return false;
            _remoteCatchUntil = 0;
            BeginRemoteHold(g, victim, bed ? "lookBed" : "Hit");
            GameFlow.HostFoundInSpot(spot);
            _nextPick = 0;
            return true;
        }

        /// <summary>The point Granny faces at a hiding spot: the spot's target, or its camera when she stands on the target.</summary>
        static Vector3 FacePoint(byte id, Vector3 gp)
        {
            var p = _seekPos;
            var fd = p - gp; fd.y = 0;
            if (fd.sqrMagnitude < 0.25f)
            {
                var cam = GameRefs.SpotCamera(_seekSpot, _seekPos);
                if (U.Alive(cam)) p = cam.position;
                else if (PlayerSync.TryGetAvatar(id, out var a)) p = a.TPos;
            }
            return p;
        }

        /// <summary>Resets Granny's "looking under the bed" state (her own EndDayUnderBed normally does that).</summary>
        static void ResetBedLook(EnemyAIGranny g)
        {
            try
            {
                g.grannyLookUnderBed = false; g.timerBed = 0f; g.soundPlaying = false; g.playerGetCaught = false; g.attackingPlayer = false; g.huntPlayer = false; g.grannyIsFollow = false;
                if (U.Alive(g.boneController))
                {
                    foreach (var o in g.boneController.GetComponents(Il2CppInterop.Runtime.Il2CppType.Of<Behaviour>()))
                    {
                        var c = o?.TryCast<Behaviour>();
                        if (c != null && !c.enabled && U.Il2CppTypeName(c) == "BoneController") c.enabled = true;
                    }
                }
                var an = GrannyAnim(g); if (U.Alive(an)) an.CrossFade("idle");
            }
            catch (Exception e) { Log.Debug("bed reset: " + e.Message); }
        }

        /// <summary>Granny stops chasing and returns to patrol (e.g. after catching a ghost).</summary>
        public static void ResetChase()
        {
            var g = GameRefs.Granny;
            if (!U.Alive(g)) return;
            try
            {
                g.huntPlayer = false; g.attackingPlayer = false; g.seePlayer = false; g.grannyIsFollow = false; g.playerGetCaught = false;
                g.startAttack = false; g.GrannySearching = false;
                var f = GameRefs.Fps; if (U.Alive(f)) f.playerCaught = false;
                // (PvP: her navigation agent is off while the Granny player walks her)
                if (!U.Alive(g.navComponent) || g.navComponent.enabled) g.newNav();
            }
            catch (Exception e) { Log.Debug("ResetChase: " + e.Message); }
            Target = Session.LocalId; _nextPick = 0;
        }

        /// <summary>
        /// Host: Granny starts her catch sequence (Playercaught). Returns true when we skip it:
        /// the target was remote (handled by us), or the host is / becomes a ghost in LastSurvivor mode.
        /// </summary>
        public static bool ShouldSkipHostCatch()
        {
            if (!Session.Active || !Session.IsHost) return false;
            if (Session.GodMode) { ResetChase(); return true; } // debug panel: nobody is caught
            if (Pvp.Active)
            {
                // only the Granny player catches (PvpCatch) – never Granny's own code
                if (Time.unscaledTime >= _pvpHostCatchUntil) return true;
                _pvpHostCatchUntil = 0;
                // the game's own catch (Granny's grab + the jumpscare) plays like in single player; with survivors
                // still in the game the end of the day that follows is held back (GameFlow), the host spectates
                BeginHostHold();
                if (Session.Players.Values.Any(p => !p.IsLocal && !p.Ghost && p.SceneReady && !Pvp.IsGranny(p.Id))) GameFlow.PlayerCaught(Session.LocalId, "Granny");
                return false;
            }
            if (HoldingCatch) return true; // a catch animation is already playing
            if (GameFlow.InTransition) { Log.Debug("catch skipped – the day is changing"); ResetChase(); return true; }
            if (Stunned(GameRefs.Granny)) { Log.Debug("catch skipped – Granny is stunned"); return true; }
            // Granny's catch started inside a sensor trigger forwarded by a client – it belongs to that client
            if (Events.ActionBy != 255 && Events.ActionBy != Session.LocalId)
            {
                if (!RemoteCaught(Events.ActionBy, "Granny")) ResetChase();
                return true;
            }
            if (Time.unscaledTime < _remoteCatchUntil) { ResetChase(); return true; } // this catch belonged to a remote player
            if (Target != Session.LocalId && Session.Players.ContainsKey(Target))
            {
                // Granny's catch was aimed at a remote player (spoofed position) – never at the host
                if (!RemoteCaught(Target, "Granny")) ResetChase();
                return true;
            }
            var me = Session.Local;
            if (me != null && me.Ghost) { ResetChase(); return true; }
            if (Session.GhostMode)
            {
                int othersAlive = Session.Players.Values.Count(p => !p.IsLocal && !p.Ghost && p.SceneReady && !Pvp.IsGranny(p.Id));
                if (othersAlive > 0)
                {
                    // the game's own catch (Granny's grab + the jumpscare) plays like in single player;
                    // the end of the day that follows it is held back (GameFlow), the host spectates afterwards
                    BeginHostHold();
                    GameFlow.PlayerCaught(Session.LocalId, "Granny");
                    return false;
                }
            }
            return false;
        }
    }
}
