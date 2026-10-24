using System;
using UnityEngine;

namespace GrannyMP
{
    /// <summary>
    /// Pepper spray. The game's spray (sprayPepper on the spray button) switches its particle object on for a moment;
    /// PepperSprayHit on those particles writes Granny's hitByPepper / hitByPepperStart directly – on a client those
    /// fields go to the host as AI fields ([Advanced] ClientAiFields). Here:
    ///  * whether the local player is spraying (sent in the player state, PlayerSync.F2_Spray);
    ///  * a client's spray that did not hit Granny by particles while she stood right in front of it hits her too;
    ///  * the other players' copies of the held spray (avatars, the spectator's hand) show the spray cloud.
    /// </summary>
    internal static class SprayFx
    {
        static sprayPepper _script; static float _nextFind;
        static bool _was, _hitDone; static float _since;
        const float Reach = 3.5f, Cone = 28f;

        static sprayPepper Script
        {
            get
            {
                if (U.Alive(_script)) return _script;
                if (Time.unscaledTime < _nextFind || !GameRefs.InGameScene) return null;
                _nextFind = Time.unscaledTime + 2f;
                try { _script = UnityEngine.Object.FindObjectOfType(Il2CppInterop.Runtime.Il2CppType.Of<sprayPepper>(), true)?.TryCast<sprayPepper>(); } catch { }
                return _script;
            }
        }

        public static void OnSceneChanged() { _script = null; _nextFind = 0; _was = false; _remote = null; _remoteLampDone = false; _exit = null; _lamp1 = _lamp2 = false; }

        // ---- the remote control's lamp over the door ----
        // UseRemote turns its lamp green (material colour 0,1,0) only in the game where the remote was used; its
        // doorUnlocked flag is synced, so every machine turns the lamp green when it sees the door unlocked
        static UseRemote _remote; static bool _remoteLampDone; static float _remoteNext;
        static void RemoteLamp()
        {
            if (_remoteLampDone || Time.unscaledTime < _remoteNext) return;
            _remoteNext = Time.unscaledTime + 0.5f;
            try
            {
                if (!U.Alive(_remote)) _remote = UnityEngine.Object.FindObjectOfType(Il2CppInterop.Runtime.Il2CppType.Of<UseRemote>(), true)?.TryCast<UseRemote>();
                if (!U.Alive(_remote) || !_remote.doorUnlocked || !U.Alive(_remote.lamp)) return;
                var m = _remote.lamp.material;
                if (m == null) return;
                var green = new Color(0f, 1f, 0f, 1f);
                if (m.HasProperty("_Color")) m.SetColor("_Color", green);
                if (m.HasProperty("_EmissionColor")) m.SetColor("_EmissionColor", green);
                _remoteLampDone = true;
                Log.Debug("remote control: the lamp over the door is green");
            }
            catch (Exception e) { Log.Debug("remote lamp: " + e.Message); _remoteNext = Time.unscaledTime + 5f; }
        }

        public static bool LocalSpraying()
        {
            var s = Script;
            if (!U.Alive(s)) return false;
            try { return s.sprayTime && U.Alive(s.spray) && s.spray.activeInHierarchy; } catch { return false; }
        }

        /// <summary>Name of the spray's particle object (the same child in every copy of the held spray).</summary>
        static string SprayName()
        {
            var s = Script;
            try { return U.Alive(s) && U.Alive(s.spray) ? s.spray.name : "PepperSpray"; } catch { return "PepperSpray"; }
        }

        /// <summary>Client, every frame: a spray straight at Granny that her particles missed.</summary>
        // ---- the lamps at the main door: cutting the cables turns them green (material colour) in that game only;
        //      CheckExitDoor.lampa1ok / lampa2ok are synced, so every game turns the lamp green when it sees them ----
        static CheckExitDoor _exit; static bool _lamp1, _lamp2; static float _lampNext;
        static void DoorLamps()
        {
            if ((_lamp1 && _lamp2) || Time.unscaledTime < _lampNext) return;
            _lampNext = Time.unscaledTime + 0.5f;
            try
            {
                if (!U.Alive(_exit)) _exit = UnityEngine.Object.FindObjectOfType(Il2CppInterop.Runtime.Il2CppType.Of<CheckExitDoor>(), true)?.TryCast<CheckExitDoor>();
                var pu = GameRefs.PickUp;
                if (!U.Alive(_exit) || !U.Alive(pu)) return;
                if (!_lamp1 && _exit.lampa1ok) _lamp1 = Green(pu.LampaDoor1);
                if (!_lamp2 && _exit.lampa2ok) _lamp2 = Green(pu.LampaDoor2);
            }
            catch (Exception e) { Log.Debug("door lamps: " + e.Message); _lampNext = Time.unscaledTime + 5f; }
        }

        static bool Green(GameObject lamp)
        {
            if (!U.Alive(lamp)) return false;
            var r = lamp.GetComponent<Renderer>();
            if (r == null || r.material == null) return false;
            r.material.color = new Color(0f, 1f, 0f, 1f);
            Log.Debug($"door lamp {lamp.name} is green");
            return true;
        }

        public static void Update()
        {
            if (Session.Active && GameRefs.InGameScene) { RemoteLamp(); DoorLamps(); }
            if (!Session.Active || !Session.IsClient || !GameRefs.InGameScene || Pvp.LocalGrannyInGame) { _was = false; return; }
            bool on = LocalSpraying();
            float now = Time.unscaledTime;
            if (on && !_was) { _since = now; _hitDone = false; }
            _was = on;
            if (!on || _hitDone || now - _since < 0.3f) return;
            var g = GameRefs.Granny; var cam = GameRefs.PlayerCam;
            if (!U.Alive(g) || !U.Alive(cam)) return;
            try
            {
                if (g.hitByPepper) { _hitDone = true; return; } // the game's own particles hit her
                var head = AiControl.HeadPoint(g);
                var chest = Vector3.Lerp(g.transform.position, head, 0.6f);
                var from = cam.position; var d = chest - from;
                if (d.magnitude > Reach || Vector3.Angle(cam.forward, d) > Cone) return;
                if (!AiControl.ClearLine(from, chest, g.transform)) return;
                g.hitByPepper = true; g.hitByPepperStart = true; // sent to the host like the game's own hit
                _hitDone = true;
                Log.Debug("pepper spray: Granny right in front – hit");
            }
            catch (Exception e) { Log.Debug("pepper spray: " + e.Message); }
        }

        /// <summary>A copy of the held spray: its spray cloud switched off (a fresh copy may have been taken while spraying).</summary>
        public static void Reset(GameObject copy)
        {
            if (!U.Alive(copy)) return;
            try { var fx = Find(copy.transform, SprayName()); if (fx != null && fx.gameObject.activeSelf) fx.gameObject.SetActive(false); } catch { }
        }

        /// <summary>Another player's spray (avatar / spectator hand): the cloud on while they spray.</summary>
        public static void Show(GameObject copy, bool on, ref bool shown)
        {
            if (on == shown) return;
            shown = on;
            if (!U.Alive(copy)) return;
            try
            {
                var fx = Find(copy.transform, SprayName());
                if (fx != null && fx.gameObject.activeSelf != on) fx.gameObject.SetActive(on);
            }
            catch (Exception e) { Log.Debug("spray fx: " + e.Message); }
        }

        static Transform Find(Transform t, string name)
        {
            if (t.name == name) return t;
            for (int i = 0; i < t.childCount; i++) { var r = Find(t.GetChild(i), name); if (r != null) return r; }
            return null;
        }
    }
}
