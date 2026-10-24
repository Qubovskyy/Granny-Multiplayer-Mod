using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace GrannyMP
{
    internal sealed class Avatar
    {
        public byte Id;
        public GameObject Root;
        public Transform Body, Head;
        public CapsuleCollider Col;
        public Renderer[] Rends;
        public Vector3 TPos; public float TYaw, TPitch;
        public float CCCenterY, CCHeight = 1.8f, CamY = 0.7f;
        public byte Flags;
        public bool HasData;
        public float LastUpdate;
        public bool LastCrouch;
        public float LaidOutCamY = -99f;
        public byte HeldIdx = 255; public GameObject HeldVisual;
        public byte Flags2; public bool SprayShown;
        public float Shade = -1f; public Material[] OwnMats;
        public bool HeldRel; public Vector3 HeldRelPos; public Quaternion HeldRelRot = Quaternion.identity;
        public Light Lamp; public byte LampMode;
        public byte Spot; public int Pose; // Pose: 0 standing, 1 lying (under a bed, in a coffin)
        // the player model (man / woman) with its animations; null = the simple capsule avatar
        public PlayerModels.Instance Model; public string ModelKind; public float StandCamY; public Vector3 LastPos; public float Speed;
        public float GroundY; public bool HasGround; public float NextGround; public bool SizeLogged;
        // the model's body faces BodyYaw, the head turns from there to where the player looks
        public float BodyYaw, LookPitch, LookYaw; public bool HasBodyYaw, Turning;
        public Renderer[] SimpleRends;
        // what the player's screen shows (camera position, rotation, field of view) in the sender's clock – the
        // spectator replays it between two received states
        public readonly List<ViewSnap> Views = new();
        public float ClockOff, AvgDt = 0.05f; public bool HasClock;
    }

    internal struct ViewSnap { public float T; public Vector3 P; public Quaternion R; public float Fov; }

    internal struct PState
    {
        public Vector3 Pos; public float Yaw, Pitch; public byte Flags; public float CCy, CCh, CamY; public byte Held, Spot; public float Time;
        public byte Flags2; // PlayerSync.F2_*
        public uint Seq;
        /// <summary>The camera the player sees through (also a hiding spot's camera) and the sender's clock.</summary>
        public Vector3 ViewPos; public Quaternion ViewRot; public float Fov, ViewT;
    }

    /// <summary>
    /// Players: a client sends its input + predicted position (PlayerInput), the host validates it and broadcasts
    /// authoritative states of all players (PlayerStates). Remote players are shown as avatars.
    /// </summary>
    internal static class PlayerSync
    {
        /// <summary>Hiding spot (PState.Spot): 1–3 bed, 4 coffin, 5 backyard coffin, 6 car, 7 other (wardrobe).</summary>
        public const byte SpotNone = 0;
        public const byte F_Crouch = 1, F_Moving = 2, F_Hiding = 4, F_Ghost = 8, F_Sprint = 16, F_Flashlight = 32, F_Lantern = 64, F_Teddy = 128;
        public const byte F2_Spray = 1; // spraying pepper spray right now

        static readonly Dictionary<byte, Avatar> _avatars = new();
        static readonly Dictionary<IntPtr, byte> _colliderOwner = new();
        static readonly Dictionary<byte, PState> _states = new();
        static readonly Dictionary<byte, int> _violations = new();
        static uint _seq;

        public static IEnumerable<Avatar> Avatars => _avatars.Values;
        public static bool TryGetAvatar(byte id, out Avatar a) => _avatars.TryGetValue(id, out a) && U.Alive(a.Root);

        /// <summary>The transform belongs to a remote player's avatar (model, held item, lamp).</summary>
        public static bool IsAvatarPart(Transform t)
        {
            if (t == null) return false;
            foreach (var a in _avatars.Values) if (U.Alive(a.Root) && U.IsUnder(t, a.Root.transform)) return true;
            return false;
        }

        public static bool TryGetPos(byte id, out Vector3 pos)
        {
            if (id == Session.LocalId && U.Alive(GameRefs.PlayerRoot)) { pos = GameRefs.PlayerRoot.position; return true; }
            if (_states.TryGetValue(id, out var s)) { pos = s.Pos; return true; }
            pos = default; return false;
        }

        public static bool TryGetState(byte id, out PState s) => _states.TryGetValue(id, out s);

        public static byte ProxyOwner(Collider c)
        {
            if (c == null) return 255;
            return _colliderOwner.TryGetValue(c.Pointer, out var id) ? id : (byte)255;
        }

        public static bool IsProxy(Collider c) => c != null && _colliderOwner.ContainsKey(c.Pointer);

        public static void RegisterHandlers()
        {
            Session.On(Msg.PlayerInput, HandleInput);
            Session.On(Msg.PlayerStates, HandleStates);
            Session.On(Msg.Correction, (p, r) =>
            {
                if (!Session.IsClient) return;
                var pos = r.Vec3();
                GameRefs.TeleportLocal(pos);
                Log.Info("Position corrected by the host");
            });
        }

        // ---------------- items held in hand (InventoryController: field X + haveX) ----------------
        static List<PropertyInfo> _heldGo, _heldFlag;
        static void EnsureHeldProps()
        {
            if (_heldGo != null) return;
            _heldGo = new List<PropertyInfo>(); _heldFlag = new List<PropertyInfo>();
            var t = typeof(InventoryController);
            foreach (var p in World.FieldProps(t).Where(p => p.PropertyType == typeof(GameObject)).OrderBy(p => p.Name, StringComparer.Ordinal))
            {
                var have = t.GetProperty("have" + p.Name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
                if (have == null || have.PropertyType != typeof(bool)) continue;
                _heldGo.Add(p); _heldFlag.Add(have);
                if (_heldGo.Count >= 250) break;
            }
        }

        static byte LocalHeld()
        {
            var inv = GameRefs.Inventory;
            if (!U.Alive(inv)) return 255;
            EnsureHeldProps();
            try
            {
                for (int i = 0; i < _heldGo.Count; i++)
                {
                    if (!(bool)_heldFlag[i].GetValue(inv)) continue;
                    var go = _heldGo[i].GetValue(inv) as GameObject;
                    if (U.Alive(go) && go.activeInHierarchy) return (byte)i;
                }
            }
            catch { }
            return 255;
        }

        public static GameObject HandModel(byte idx) => LocalHandModel(idx);

        static GameObject LocalHandModel(byte idx)
        {
            var inv = GameRefs.Inventory;
            if (!U.Alive(inv) || idx == 255) return null;
            EnsureHeldProps();
            if (idx >= _heldGo.Count) return null;
            try { return _heldGo[idx].GetValue(inv) as GameObject; } catch { return null; }
        }

        static byte LocalFlags()
        {
            byte f = 0;
            var fps = GameRefs.Fps;
            if (U.Alive(fps))
            {
                try { if (fps.playerCrouch) f |= F_Crouch; } catch { }
                try { if (fps.playerMoving) f |= F_Moving; } catch { }
                try { if (!fps.m_IsWalking) f |= F_Sprint; } catch { }
            }
            if (GameRefs.LocalHiding) f |= F_Hiding;
            if (Session.Local?.Ghost ?? false) f |= F_Ghost;
            else
            {
                f |= LocalLightFlags();
                // holding the teddy bear – Granny (on the host) goes for this player
                try { if (U.Alive(GameRefs.Inventory) && GameRefs.Inventory.haveteddy) f |= F_Teddy; } catch { }
            }
            return f;
        }

        // ---------------- flashlight / lantern ----------------
        static flashlightController _flashCtrl; static lanternController _lanternCtrl; static float _nextCtrlFind;
        public static Light FlashTemplate, LanternTemplate;

        public static flashlightController FlashController { get { FindLightControllers(); return _flashCtrl; } }
        public static lanternController LanternController { get { FindLightControllers(); return _lanternCtrl; } }

        static void FindLightControllers()
        {
            if ((U.Alive(_flashCtrl) && U.Alive(_lanternCtrl)) || Time.unscaledTime < _nextCtrlFind) return;
            _nextCtrlFind = Time.unscaledTime + 3f;
            try
            {
                if (!U.Alive(_flashCtrl))
                    foreach (var o in UnityEngine.Object.FindObjectsOfType(Il2CppInterop.Runtime.Il2CppType.Of<flashlightController>(), true)) { _flashCtrl = o.TryCast<flashlightController>(); break; }
                if (!U.Alive(_lanternCtrl))
                    foreach (var o in UnityEngine.Object.FindObjectsOfType(Il2CppInterop.Runtime.Il2CppType.Of<lanternController>(), true)) { _lanternCtrl = o.TryCast<lanternController>(); break; }
                if (!U.Alive(FlashTemplate) && U.Alive(_flashCtrl) && U.Alive(_flashCtrl.flashlightPlayer)) FlashTemplate = FirstLight(_flashCtrl.flashlightPlayer, true);
                if (!U.Alive(LanternTemplate) && U.Alive(_lanternCtrl) && U.Alive(_lanternCtrl.lanternPlayer)) LanternTemplate = FirstLight(_lanternCtrl.lanternPlayer, true);
            }
            catch (Exception e) { Log.Debug("light controllers: " + e.Message); }
        }

        static Light FirstLight(GameObject g, bool includeInactive)
        {
            foreach (var c in g.GetComponentsInChildren(Il2CppInterop.Runtime.Il2CppType.Of<Light>(), includeInactive))
            {
                var l = c.TryCast<Light>();
                if (l != null && (includeInactive || l.enabled)) return l;
            }
            return null;
        }

        /// <summary>Is the local player's flashlight / lantern in hand and switched on.</summary>
        static byte LocalLightFlags()
        {
            FindLightControllers();
            byte f = 0;
            try
            {
                if (U.Alive(_flashCtrl) && _flashCtrl.playerHaveFlashlight && U.Alive(_flashCtrl.flashlightPlayer) && _flashCtrl.flashlightPlayer.activeInHierarchy
                    && FirstLight(_flashCtrl.flashlightPlayer, false) != null) f |= F_Flashlight;
                if (U.Alive(_lanternCtrl) && _lanternCtrl.playerHaveLantern && U.Alive(_lanternCtrl.lanternPlayer) && _lanternCtrl.lanternPlayer.activeInHierarchy
                    && FirstLight(_lanternCtrl.lanternPlayer, false) != null) f |= F_Lantern;
            }
            catch { }
            return f;
        }

        /// <summary>Light of the watched player (flashlight = spot light, lantern = point light), copied from the game's own lights – for the spectator only.</summary>
        static void UpdateLamp(Avatar a, bool ghost)
        {
            // only the spectator looking through this player's eyes gets the light – the others play with their own lights
            bool watched = a.Id == Spectator.Watching;
            byte mode = ghost || !watched ? (byte)0 : (a.Flags & F_Flashlight) != 0 ? (byte)1 : (a.Flags & F_Lantern) != 0 ? (byte)2 : (byte)0;
            if (mode == a.LampMode && (mode == 0 || U.Alive(a.Lamp))) return;
            a.LampMode = mode;
            if (mode == 0) { if (U.Alive(a.Lamp)) a.Lamp.enabled = false; return; }
            FindLightControllers();
            if (!U.Alive(a.Lamp))
            {
                var go = new GameObject("Lamp");
                go.transform.SetParent(U.Alive(a.Head) ? a.Head : a.Root.transform, false);
                go.transform.localPosition = new Vector3(0.12f, -0.12f, 0.25f);
                a.Lamp = go.AddComponent<Light>();
            }
            CopyLight(a.Lamp, mode == 1 ? FlashTemplate : LanternTemplate, mode == 1);
            a.Lamp.enabled = true;
        }

        public static void CopyLight(Light dst, Light src, bool spot)
        {
            if (U.Alive(src))
            {
                dst.type = src.type; dst.range = src.range; dst.spotAngle = src.spotAngle; dst.intensity = src.intensity; dst.color = src.color;
            }
            else if (spot) { dst.type = LightType.Spot; dst.range = 20f; dst.spotAngle = 55f; dst.intensity = 1.6f; dst.color = new Color(1f, 0.96f, 0.85f); }
            else { dst.type = LightType.Point; dst.range = 8f; dst.intensity = 1.3f; dst.color = new Color(1f, 0.78f, 0.5f); }
            dst.shadows = LightShadows.None;
            dst.renderMode = LightRenderMode.ForcePixel; // "Important" – no blocky per-vertex lighting artefacts
        }

        static PState CaptureLocal()
        {
            var root = GameRefs.PlayerRoot;
            var s = new PState { Pos = root.position, Yaw = root.eulerAngles.y, Flags = LocalFlags(), Time = Time.unscaledTime, CCh = 1.8f, CamY = 0.7f, Held = LocalHeld(), Spot = GameRefs.LocalSpot, Flags2 = (byte)(SprayFx.LocalSpraying() ? F2_Spray : 0) };
            if (U.Alive(GameRefs.PlayerCam))
            {
                var x = GameRefs.PlayerCam.localEulerAngles.x;
                s.Pitch = x > 180 ? x - 360 : x;
                s.Yaw = GameRefs.PlayerCam.eulerAngles.y;
                s.CamY = GameRefs.PlayerCam.position.y - root.position.y; // real eye height above the player root
            }
            // hiding: the player looks through the hiding spot's camera (bed, coffin, car) – send where it looks
            if (s.Spot >= 1 && s.Spot <= 6)
            {
                var hc = GameRefs.SpotCamera(s.Spot, root.position);
                if (U.Alive(hc))
                {
                    var e = hc.eulerAngles;
                    s.Yaw = e.y; s.Pitch = e.x > 180 ? e.x - 360 : e.x;
                }
            }
            // exactly what the player's screen shows: the camera drawing the picture (player camera, hiding spot,
            // catch animation) with its field of view
            var vc = GameRefs.RenderCamera();
            if (U.Alive(vc)) { s.ViewPos = vc.transform.position; s.ViewRot = vc.transform.rotation; s.Fov = vc.fieldOfView; }
            else { s.ViewPos = root.position + Vector3.up * s.CamY; s.ViewRot = Quaternion.Euler(s.Pitch, s.Yaw, 0f); s.Fov = 65f; }
            s.ViewT = Time.unscaledTime;
            // the player object is scaled (about 1.55) – the controller's size in world units
            float sc = 1f; try { sc = Mathf.Max(0.1f, root.lossyScale.y); } catch { }
            if (U.Alive(GameRefs.PlayerCC)) { s.CCy = GameRefs.PlayerCC.center.y * sc; s.CCh = GameRefs.PlayerCC.height * sc; }
            return s;
        }

        public static void Tick()
        {
            if (!Session.Active || !GameRefs.InGameScene || !U.Alive(GameRefs.PlayerRoot)) return;
            var local = CaptureLocal();
            if (Session.IsClient)
            {
                var w = Session.W(Msg.PlayerInput);
                w.UInt(++_seq); WriteState(w, local);
                Vector2 inp = Vector2.zero;
                try { inp = GameRefs.Fps.m_Input; } catch { }
                w.Float(inp.x); w.Float(inp.y);
                Session.SendToHost(w, false);
                _states[Session.LocalId] = local;
            }
            else
            {
                _states[0] = local;
                var w = Session.W(Msg.PlayerStates);
                int n = 0; var pos = w.Len; w.Byte(0);
                foreach (var p in Session.Players.Values)
                {
                    if (!_states.TryGetValue(p.Id, out var s)) continue;
                    if (p.Id != 0 && !p.SceneReady) continue;
                    w.Byte(p.Id); WriteState(w, s); n++;
                }
                w.Buf[pos] = (byte)n;
                Session.Broadcast(w, false);
            }
        }

        static void WriteState(NetWriter w, PState s)
        {
            w.Vec3(s.Pos); w.Float(s.Yaw); w.Float(s.Pitch); w.Byte(s.Flags); w.Float(s.CCy); w.Float(s.CCh); w.Float(s.CamY); w.Byte(s.Held); w.Byte(s.Spot);
            w.Vec3(s.ViewPos); w.Quat(s.ViewRot); w.Float(s.Fov); w.Float(s.ViewT); w.Byte(s.Flags2);
        }

        static PState ReadState(NetReader r) =>
            new PState { Pos = r.Vec3(), Yaw = r.Float(), Pitch = r.Float(), Flags = r.Byte(), CCy = r.Float(), CCh = r.Float(), CamY = r.Float(), Held = r.Byte(), Spot = r.Byte(),
                         ViewPos = r.Vec3(), ViewRot = r.Quat(), Fov = r.Float(), ViewT = r.Float(), Flags2 = r.Byte(), Time = Time.unscaledTime };

        static void HandleInput(NetPlayer from, NetReader r)
        {
            if (!Session.IsHost || from == null) return;
            var seq = r.UInt();
            var s = ReadState(r);
            r.Float(); r.Float();
            s.Seq = seq;
            if (_states.TryGetValue(from.Id, out var prev))
            {
                if (seq <= prev.Seq && prev.Seq - seq < 1000) return;
                if (Cfg.SpeedCheck.Value && !GameFlow.InTransition && !from.Ghost)
                {
                    float dt = Mathf.Max(0.02f, Time.unscaledTime - prev.Time);
                    float allowed = Cfg.MaxSpeed.Value * dt * 1.3f + 0.75f;
                    float d = U.FlatDist(prev.Pos, s.Pos);
                    if (d > allowed)
                    {
                        _violations.TryGetValue(from.Id, out var v);
                        _violations[from.Id] = ++v;
                        if (v >= 3)
                        {
                            _violations[from.Id] = 0;
                            var cw = Session.W(Msg.Correction); cw.Vec3(prev.Pos);
                            Session.SendTo(from.Id, cw, true);
                            Log.Warn($"Movement validation: {from.Name} moved {d:F1} m in {dt:F2} s – corrected");
                            return;
                        }
                    }
                    else _violations[from.Id] = 0;
                }
            }
            _states[from.Id] = s;
            ApplyToAvatar(from.Id, s);
        }

        static void HandleStates(NetPlayer from, NetReader r)
        {
            if (!Session.IsClient) return;
            int n = r.Byte();
            for (int i = 0; i < n; i++)
            {
                var id = r.Byte();
                var s = ReadState(r);
                if (id == Session.LocalId) continue;
                _states[id] = s;
                ApplyToAvatar(id, s);
            }
        }

        static void ApplyToAvatar(byte id, PState s)
        {
            if (!GameRefs.InGameScene) return;
            if (!Session.Players.ContainsKey(id)) return;
            var a = GetOrCreate(id);
            if (a == null) return;
            if (!a.HasData || (a.Root.transform.position - s.Pos).sqrMagnitude > 25f) a.Root.transform.position = s.Pos;
            a.TPos = s.Pos; a.TYaw = s.Yaw; a.TPitch = s.Pitch; a.Flags = s.Flags; a.Flags2 = s.Flags2; a.HasData = true; a.LastUpdate = Time.unscaledTime;
            a.CCHeight = Mathf.Clamp(s.CCh, 0.5f, 3f); a.CCCenterY = s.CCy;
            a.CamY = Mathf.Clamp(s.CamY, 0.1f, 4f);
            a.Spot = s.Spot;
            if (Mathf.Abs(a.CamY - a.LaidOutCamY) > 0.04f) Layout(a);
            if (s.Held != a.HeldIdx) SetHeld(a, s.Held);
            AddView(a, s);
        }

        // ---------------- the watched player's screen, replayed for the spectator ----------------
        static void AddView(Avatar a, PState s)
        {
            var v = a.Views;
            if (v.Count > 0 && s.ViewT <= v[v.Count - 1].T) return; // the host repeats a client's state until a newer one arrives
            float now = Time.unscaledTime, off = now - s.ViewT;
            // the sender's clock: the fastest packet shows the real offset, it drifts slowly upwards otherwise
            if (!a.HasClock || off < a.ClockOff || off - a.ClockOff > 1f) { a.ClockOff = off; a.HasClock = true; }
            else a.ClockOff += (off - a.ClockOff) * 0.02f;
            if (v.Count > 0) { float dt = s.ViewT - v[v.Count - 1].T; if (dt > 0.005f && dt < 0.5f) a.AvgDt = Mathf.Lerp(a.AvgDt, dt, 0.1f); }
            v.Add(new ViewSnap { T = s.ViewT, P = s.ViewPos, R = s.ViewRot, Fov = s.Fov });
            if (v.Count > 16) v.RemoveRange(0, v.Count - 16);
        }

        /// <summary>
        /// The watched player's camera a moment ago (about two network ticks), interpolated between the two states
        /// around that time – the spectator sees the same picture, just slightly later.
        /// </summary>
        public static bool SampleView(Avatar a, out Vector3 pos, out Quaternion rot, out float fov)
        {
            pos = default; rot = Quaternion.identity; fov = 65f;
            var v = a.Views;
            int n = v.Count;
            if (n == 0 || !a.HasClock) return false;
            float t = Time.unscaledTime - a.ClockOff - Mathf.Clamp(a.AvgDt * 2.2f, 0.06f, 0.25f);
            ViewSnap x = v[n - 1], y = x; float f = 0f;
            if (t <= v[0].T) x = y = v[0];
            else
                for (int i = n - 2; i >= 0; i--)
                    if (v[i].T <= t)
                    {
                        x = v[i]; y = v[i + 1];
                        f = Mathf.Clamp01((t - x.T) / Mathf.Max(0.001f, y.T - x.T));
                        break;
                    }
            if ((y.P - x.P).sqrMagnitude > 9f) { pos = f < 0.5f ? x.P : y.P; rot = f < 0.5f ? x.R : y.R; fov = f < 0.5f ? x.Fov : y.Fov; } // a teleport / camera switch
            else { pos = Vector3.Lerp(x.P, y.P, f); rot = Quaternion.Slerp(x.R, y.R, f); fov = Mathf.Lerp(x.Fov, y.Fov, f); }
            if (fov < 5f || fov > 150f) fov = 65f;
            return true;
        }

        // ---------------- avatar model: body + head + eyes, sized from the real camera height ----------------
        static Avatar GetOrCreate(byte id)
        {
            if (_avatars.TryGetValue(id, out var a) && U.Alive(a.Root)) return a;
            try
            {
                a = new Avatar { Id = id };
                var root = new GameObject("GrannyMP_Avatar_" + id);
                a.Root = root;
                // the collider exists only for bookkeeping; it is disabled so it never blocks players, raycasts or triggers
                a.Col = root.AddComponent<CapsuleCollider>();
                a.Col.radius = 0.3f; a.Col.enabled = false;
                var color = Session.ColorOf(id);

                var body = GameObject.CreatePrimitive(PrimitiveType.Capsule);
                body.name = "Body"; StripCollider(body);
                body.transform.SetParent(root.transform, false);
                a.Body = body.transform;
                var head = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                head.name = "Head"; StripCollider(head);
                head.transform.SetParent(root.transform, false);
                head.transform.localScale = Vector3.one * 0.3f;
                a.Head = head.transform;
                var eyes = GameObject.CreatePrimitive(PrimitiveType.Cube);
                eyes.name = "Eyes"; StripCollider(eyes);
                eyes.transform.SetParent(head.transform, false);
                eyes.transform.localPosition = new Vector3(0, 0.08f, 0.42f);
                eyes.transform.localScale = new Vector3(0.75f, 0.18f, 0.25f);

                SetColor(body, color);
                SetColor(head, Color.Lerp(color, Color.white, 0.35f));
                SetColor(eyes, Color.black);
                a.Rends = a.SimpleRends = new[] { body.GetComponent<Renderer>(), head.GetComponent<Renderer>(), eyes.GetComponent<Renderer>() };
                EnsureModel(a);
                Layout(a);
                _avatars[id] = a;
                _colliderOwner[a.Col.Pointer] = id;
                Log.Info("Created avatar for " + Session.NameOf(id));
                return a;
            }
            catch (Exception e) { Log.Error("Avatar: " + e); return null; }
        }

        /// <summary>The player's chosen model (man / woman); the capsule stays as a fallback when the model files are missing.</summary>
        static void EnsureModel(Avatar a)
        {
            string kind = Session.Players.TryGetValue(a.Id, out var p) ? p.Model : PlayerModels.Default;
            if (kind == a.ModelKind) return;
            a.ModelKind = kind;
            if (a.Model != null && U.Alive(a.Model.Go)) UnityEngine.Object.Destroy(a.Model.Go);
            a.Model = PlayerModels.Build(kind, a.Root.transform);
            foreach (var r in a.SimpleRends) if (U.Alive(r)) r.enabled = a.Model == null;
            a.Rends = a.Model != null ? a.Model.Rends : a.SimpleRends;
            a.OwnMats = null; a.Shade = -1f;
            a.LaidOutCamY = -99f;
        }

        static void StripCollider(GameObject g)
        {
            var c = g.GetComponent<Collider>();
            if (c != null) UnityEngine.Object.Destroy(c);
        }

        static void SetColor(GameObject g, Color c)
        {
            try { var r = g.GetComponent<Renderer>(); r.material.color = c; r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On; } catch { }
        }

        /// <summary>
        /// Avatar pivot = remote player root. Feet = bottom of its CharacterController, eyes = its camera height,
        /// so the head is exactly where the remote player's camera is.
        /// </summary>
        static void Layout(Avatar a)
        {
            float feet = a.CCCenterY - a.CCHeight / 2f;
            float eye = Mathf.Max(a.CamY, feet + 0.5f);
            float headCenter = eye + 0.02f;
            float headTop = headCenter + 0.15f;
            float bodyTop = headCenter - 0.13f;
            float bodyH = Mathf.Max(0.4f, bodyTop - feet);
            if (U.Alive(a.Body))
            {
                a.Body.localScale = new Vector3(0.5f, bodyH / 2f, 0.5f);
                a.Body.localPosition = new Vector3(0, feet + bodyH / 2f, 0);
            }
            if (U.Alive(a.Head)) a.Head.localPosition = new Vector3(0, headCenter, 0);
            if (U.Alive(a.Body)) a.Body.localRotation = Quaternion.identity;
            if (a.Pose == 1 && U.Alive(a.Body) && U.Alive(a.Head))
            {
                // lying (under a bed, in a coffin): the pivot is the head (the hiding spot's camera), the body
                // stretches out behind it
                a.Head.localPosition = Vector3.zero;
                a.Body.localRotation = Quaternion.Euler(90f, 0f, 0f);
                a.Body.localScale = new Vector3(0.42f, bodyH / 2f, 0.42f);
                a.Body.localPosition = new Vector3(0, -0.12f, -(bodyH / 2f + 0.08f));
            }
            if (a.Model != null && U.Alive(a.Model.Go))
            {
                // sized from the standing eye height (crouching lowers the camera, the crouch animation does the rest)
                if ((a.Flags & F_Crouch) == 0 && a.Pose == 0) a.StandCamY = Mathf.Max(a.StandCamY * 0.98f, eye);
                float standEye = Mathf.Max(a.StandCamY, eye);
                float s = Mathf.Max(0.05f, (standEye - feet) * Cfg.ModelSize.Value / a.Model.D.Height); // (PlaceModel puts it on the floor)
                var mt = a.Model.Go.transform;
                mt.localScale = Vector3.one * s;
                if (a.Pose == 1)
                {
                    // lying on the belly, the head at the hiding spot's camera, the legs behind
                    mt.localRotation = Quaternion.Euler(90f, 0f, 0f);
                    mt.localPosition = new Vector3(0, -0.12f, -a.Model.D.Height * s * 0.96f);
                }
                else
                {
                    // standing: the body may face a little away from where the player looks (UpdateLook)
                    mt.localRotation = a.HasBodyYaw ? Quaternion.Euler(0f, Mathf.DeltaAngle(a.Root.transform.eulerAngles.y, a.BodyYaw), 0f) : Quaternion.identity;
                    mt.localPosition = new Vector3(0, feet, 0);
                }
            }
            a.Col.center = new Vector3(0, (feet + headTop) / 2f, 0);
            a.Col.height = headTop - feet;
            a.LaidOutCamY = a.CamY;
        }

        /// <summary>Show a copy of the item the remote player is holding.</summary>
        static void SetHeld(Avatar a, byte idx)
        {
            a.HeldIdx = idx;
            if (U.Alive(a.HeldVisual)) UnityEngine.Object.Destroy(a.HeldVisual);
            a.HeldVisual = null;
            var src = LocalHandModel(idx);
            if (!U.Alive(src)) return;
            try
            {
                var copyObj = UnityEngine.Object.Instantiate((UnityEngine.Object)src);
                var copy = copyObj?.TryCast<GameObject>();
                if (copy == null) return;
                copy.name = "GrannyMP_Held_" + a.Id;
                foreach (var mb in copy.GetComponentsInChildren(Il2CppInterop.Runtime.Il2CppType.Of<MonoBehaviour>(), true)) if (mb != null) UnityEngine.Object.Destroy(mb);
                foreach (var c in copy.GetComponentsInChildren(Il2CppInterop.Runtime.Il2CppType.Of<Collider>(), true)) if (c != null) UnityEngine.Object.Destroy(c);
                foreach (var rb in copy.GetComponentsInChildren(Il2CppInterop.Runtime.Il2CppType.Of<Rigidbody>(), true)) if (rb != null) UnityEngine.Object.Destroy(rb);
                foreach (var an in copy.GetComponentsInChildren(Il2CppInterop.Runtime.Il2CppType.Of<Animation>(), true)) if (an != null) UnityEngine.Object.Destroy(an);
                SetLayer(copy.transform, 0);
                var scale = src.transform.lossyScale;
                copy.transform.SetParent(a.Root.transform, false);
                copy.transform.localPosition = new Vector3(0.28f, a.CamY - 0.35f, 0.45f);
                copy.transform.localRotation = Quaternion.identity;
                // where the item sits in front of the eyes in the local game (HandHoldObjects follows the camera): the
                // copy is held the same way in front of the other player's eyes, turned where they look
                a.HeldRel = false;
                var cam = GameRefs.PlayerCam;
                if (U.Alive(cam))
                {
                    var rp = Quaternion.Inverse(cam.rotation) * (src.transform.position - cam.position);
                    if (rp.magnitude < 2f) { a.HeldRelPos = rp; a.HeldRelRot = Quaternion.Inverse(cam.rotation) * src.transform.rotation; a.HeldRel = true; }
                }
                copy.transform.localScale = scale;
                SprayFx.Reset(copy); a.SprayShown = false;
                copy.SetActive(true);
                a.HeldVisual = copy;
            }
            catch (Exception e) { Log.Debug("held visual: " + e.Message); }
        }

        /// <summary>
        /// The flashlight / lantern in the viewer's hand is a strong light: close up it washed the character out to white
        /// (the material is matte – it is the plain amount of light). The closer the viewer, the darker the colour.
        /// Each character gets its own copy of the material for that (Renderer.SetPropertyBlock is stripped).
        /// </summary>
        static void ShadeModel(Avatar a)
        {
            if (a.Model == null || a.Model.Rends == null) return;
            var cam = Spectator.Active ? Camera.main?.transform : GameRefs.PlayerCam;
            if (!U.Alive(cam)) return;
            float d = Vector3.Distance(cam.position, a.Root.transform.position + Vector3.up * (a.CamY * 0.7f));
            float shade = Mathf.Lerp(0.45f, 1f, Mathf.InverseLerp(0.8f, 3.5f, d));
            if (Mathf.Abs(shade - a.Shade) < 0.03f) return;
            a.Shade = shade;
            try
            {
                if (a.OwnMats == null)
                {
                    var list = new List<Material>();
                    foreach (var r in a.Model.Rends) if (U.Alive(r)) { var m = r.material; if (m != null) list.Add(m); } // .material = this renderer's own copy
                    a.OwnMats = list.ToArray();
                }
                var c = new Color(0.8f * shade, 0.8f * shade, 0.8f * shade, 1f);
                foreach (var m in a.OwnMats) if (m != null) m.color = c;
            }
            catch (Exception e) { Log.Debug("model shade: " + e.Message); }
        }

        static void SetLayer(Transform t, int layer)
        {
            t.gameObject.layer = layer;
            for (int i = 0; i < t.childCount; i++) SetLayer(t.GetChild(i), layer);
        }

        public static void FrameUpdate()
        {
            if (_avatars.Count == 0) return;
            float k = 1f - Mathf.Exp(-Time.unscaledDeltaTime * 14f);
            List<byte> stale = null;
            foreach (var a in _avatars.Values)
            {
                if (!U.Alive(a.Root) || !a.HasData) continue;
                if (Time.unscaledTime - a.LastUpdate > 5f) { (stale ??= new List<byte>()).Add(a.Id); continue; }
                var t = a.Root.transform;
                // hiding (bed, coffin, car): the body is where the player really is – at the hiding spot's camera,
                // not in front of the bed where the player stood
                var tpos = a.TPos; var trot = Quaternion.Euler(0, a.TYaw, 0); int pose = 0;
                bool hid = (a.Flags & F_Hiding) != 0 && a.Spot >= 1 && a.Spot <= 6 && a.Views.Count > 0;
                if (hid)
                {
                    var view = a.Views[a.Views.Count - 1];
                    if (a.Spot <= 5)
                    {
                        pose = 1; tpos = view.P;
                        var fw = view.R * Vector3.forward; fw.y = 0;
                        if (fw.sqrMagnitude > 1e-4f) trot = Quaternion.LookRotation(fw);
                    }
                    else tpos = view.P - Vector3.up * a.CamY; // sitting in the car: the head at the car's camera
                }
                if (pose != a.Pose) { a.Pose = pose; Layout(a); t.position = tpos; t.rotation = trot; a.LastPos = tpos; }
                if (a.ModelKind != (Session.Players.TryGetValue(a.Id, out var np) ? np.Model : PlayerModels.Default)) { EnsureModel(a); Layout(a); }
                // animation: standing / walking / crouching, from the synced movement and crouch flag
                if (a.Model != null)
                {
                    float dt = Mathf.Max(0.0001f, Time.unscaledDeltaTime);
                    var mv = tpos - a.LastPos; mv.y = 0; a.LastPos = tpos;
                    a.Speed = Mathf.Lerp(a.Speed, Mathf.Min(mv.magnitude / dt, 12f), 1f - Mathf.Exp(-dt * 8f));
                    string clip; float rate = 1f;
                    bool moving = a.Speed > 0.35f || ((a.Flags & F_Moving) != 0 && a.Speed > 0.1f);
                    if (pose == 1) clip = "idle";
                    else if ((a.Flags & F_Crouch) != 0) { if (moving) { clip = "crouchwalk"; rate = Mathf.Clamp(a.Speed / 1.2f, 0.6f, 1.8f); } else clip = "crouch"; }
                    else if (moving) { clip = "walk"; rate = Mathf.Clamp(a.Speed / 2.2f, 0.6f, 2.2f); }
                    else clip = "idle";
                    UpdateLook(a, pose, dt);
                    PlayerModels.Animate(a.Model, clip, rate, dt);
                    if (pose == 0) PlaceModel(a, dt);
                }
                t.position = Vector3.Lerp(t.position, tpos, k);
                t.rotation = Quaternion.Slerp(t.rotation, trot, k);
                if (U.Alive(a.Head)) a.Head.localRotation = Quaternion.Slerp(a.Head.localRotation, Quaternion.Euler(Mathf.Clamp(a.TPitch, -80, 80), 0, 0), k);
                bool ghost = (a.Flags & F_Ghost) != 0;
                // the spectator looks through this player's eyes; PvP: the Granny player is seen as Granny herself
                bool hideBody = ghost || a.Id == Spectator.Watching || Pvp.IsGranny(a.Id);
                foreach (var r in a.Rends) if (U.Alive(r) && r.enabled == hideBody) r.enabled = !hideBody;
                UpdateLamp(a, true); // the spectator's view has its own copy of the hand items and their lights (Spectator)
                ShadeModel(a);
                bool showHeld = !hideBody && (a.Flags & F_Hiding) == 0;
                if (U.Alive(a.HeldVisual) && a.HeldVisual.activeSelf != showHeld) a.HeldVisual.SetActive(showHeld);
                if (showHeld && a.HeldRel && U.Alive(a.HeldVisual))
                {
                    var look = t.rotation * Quaternion.Euler(Mathf.Clamp(a.TPitch, -80f, 80f), 0f, 0f);
                    var ht = a.HeldVisual.transform;
                    ht.position = t.position + Vector3.up * a.CamY + look * a.HeldRelPos;
                    ht.rotation = look * a.HeldRelRot;
                }
                SprayFx.Show(a.HeldVisual, showHeld && (a.Flags2 & F2_Spray) != 0, ref a.SprayShown);
            }
            if (stale != null) foreach (var id in stale) { var keep = _states.TryGetValue(id, out var s); RemoveAvatar(id); if (keep) _states[id] = s; }
        }

        /// <summary>
        /// Standing: the model stands ON the floor under the player – its lowest point (the feet in the current animation)
        /// at the floor, not at the bottom of the player's controller, which floats a little above it – and its eyes at the
        /// height of the player's camera.
        /// </summary>
        static void PlaceModel(Avatar a, float dt)
        {
            var m = a.Model; var mt = m.Go.transform; var t = a.Root.transform;
            float feet = a.CCCenterY - a.CCHeight / 2f, ground = a.HasGround ? a.GroundY : feet;
            float now = Time.unscaledTime;
            if (now >= a.NextGround)
            {
                a.NextGround = now + 0.05f;
                ground = feet;
                try
                {
                    var origin = t.position + Vector3.up * (feet + 0.5f);
                    float best = float.MaxValue;
                    foreach (var h in Physics.RaycastAll(origin, Vector3.down, 1.6f, ~0, QueryTriggerInteraction.Ignore))
                    {
                        var c = h.collider;
                        if (c == null || U.IsUnder(c.transform, t) || (U.Alive(GameRefs.PlayerRoot) && U.IsUnder(c.transform, GameRefs.PlayerRoot))) continue;
                        if (h.distance < best) best = h.distance;
                    }
                    if (best < float.MaxValue) ground = Mathf.Clamp(origin.y - best - t.position.y, feet - 0.5f, feet + 0.2f);
                }
                catch { }
                a.GroundY = a.HasGround ? Mathf.Lerp(a.GroundY, ground, 0.5f) : ground; a.HasGround = true;
            }
            float eye = Mathf.Max(a.CamY, feet + 0.5f), standEye = Mathf.Max(a.StandCamY, eye);
            // as tall as the player's camera is high: the top of the head at the camera
            float s = Mathf.Max(0.05f, (standEye - a.GroundY) * Cfg.ModelSize.Value / m.D.Height);
            if (!a.SizeLogged && a.HasGround) { a.SizeLogged = true; Log.Debug($"Model of {Session.NameOf(a.Id)}: camera {standEye - a.GroundY:F2} above the floor, model height {m.D.Height:F2} x {s:F2}"); }
            mt.localScale = Vector3.one * s;
            float low = float.IsNaN(m.MinY) ? 0f : m.MinY;
            var lp = mt.localPosition; lp.y = a.GroundY - low * s; mt.localPosition = lp;
        }

        const float MaxHeadYaw = 70f, TurnAt = 55f;

        /// <summary>
        /// The model's head follows where the player looks (up / down, left / right); the body turns after it – at once
        /// while walking, standing still only when the head is turned too far. Lying in a hiding spot the head is raised.
        /// </summary>
        static void UpdateLook(Avatar a, int pose, float dt)
        {
            var mt = a.Model.Go.transform;
            float rootYaw = a.Root.transform.eulerAngles.y;
            float k = 1f - Mathf.Exp(-dt * 10f);
            float pitch, yaw = 0f;
            if (pose == 1)
            {
                // lying on the belly (the model is turned face down): looking ahead means lifting the head
                pitch = Mathf.Clamp(a.TPitch - 90f, -45f, 10f);
                a.HasBodyYaw = false; a.Turning = false;
            }
            else
            {
                if (!a.HasBodyYaw) { a.BodyYaw = rootYaw; a.HasBodyYaw = true; }
                float diff = Mathf.DeltaAngle(a.BodyYaw, rootYaw);
                if (a.Speed > 0.35f) { a.Turning = false; a.BodyYaw = Mathf.MoveTowardsAngle(a.BodyYaw, rootYaw, 420f * dt); }
                else
                {
                    if (Mathf.Abs(diff) > TurnAt) a.Turning = true;
                    if (a.Turning)
                    {
                        a.BodyYaw = Mathf.MoveTowardsAngle(a.BodyYaw, rootYaw, 220f * dt);
                        if (Mathf.Abs(Mathf.DeltaAngle(a.BodyYaw, rootYaw)) < 4f) a.Turning = false;
                    }
                }
                // never further than the neck can turn (a fast turn of the mouse drags the body along)
                diff = Mathf.DeltaAngle(a.BodyYaw, rootYaw);
                if (Mathf.Abs(diff) > MaxHeadYaw) a.BodyYaw = rootYaw - Mathf.Sign(diff) * MaxHeadYaw;
                yaw = Mathf.DeltaAngle(a.BodyYaw, rootYaw);
                pitch = Mathf.Clamp(a.TPitch, -55f, 65f);
                mt.localRotation = Quaternion.Euler(0f, -yaw, 0f);
            }
            a.LookPitch = Mathf.Lerp(a.LookPitch, pitch, k);
            a.LookYaw = Mathf.Lerp(a.LookYaw, yaw, k);
            PlayerModels.SetLook(a.Model, a.LookPitch, a.LookYaw);
        }

        public static void RemoveAvatar(byte id)
        {
            _states.Remove(id);
            if (!_avatars.TryGetValue(id, out var a)) return;
            _avatars.Remove(id);
            if (U.Alive(a.Col)) _colliderOwner.Remove(a.Col.Pointer);
            if (U.Alive(a.Root)) UnityEngine.Object.Destroy(a.Root);
        }

        public static void ClearAll()
        {
            foreach (var id in new List<byte>(_avatars.Keys)) RemoveAvatar(id);
            _avatars.Clear(); _colliderOwner.Clear(); _states.Clear(); _violations.Clear();
        }

        /// <summary>Avatars are destroyed together with the scene – just drop references.</summary>
        public static void OnSceneChanged()
        {
            _avatars.Clear(); _colliderOwner.Clear(); _states.Clear();
        }
    }
}
