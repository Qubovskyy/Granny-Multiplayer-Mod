using System;
using UnityEngine;

namespace GrannyMP
{
    /// <summary>
    /// The sauna lever (openDoors.bastuSpak). Pulling it, the game plays the lever's animation, sets the lever's
    /// collider tag (bastuspakOn / bastuspakOff), switches the steam's emission (openDoors.bastuSteam) and sets
    /// Granny's bastuswitchOn / bastuTimer – only in the game where it was pulled. The tag is synced (TagSync) and
    /// Granny's fields go to the host (ClientAiFields); here every game follows the tag: the steam and the lever.
    /// </summary>
    internal static class SaunaSync
    {
        const string TagOn = "bastuspakOn", TagOff = "bastuspakOff";
        static openDoors _od; static GameObject _coll; static ParticleSystem _steam; static Animation _anim;
        static float _nextFind, _next; static int _state = -1; // -1 unknown, 0 off, 1 on

        public static void OnSceneChanged() { _od = null; _coll = null; _steam = null; _anim = null; _nextFind = 0; _state = -1; }

        static bool Find()
        {
            if (U.Alive(_od) && U.Alive(_coll)) return true;
            if (Time.unscaledTime < _nextFind) return false;
            _nextFind = Time.unscaledTime + 3f;
            try
            {
                _od = UnityEngine.Object.FindObjectOfType(Il2CppInterop.Runtime.Il2CppType.Of<openDoors>(), true)?.TryCast<openDoors>();
                if (!U.Alive(_od) || !U.Alive(_od.bastuSpak)) return false;
                var lever = _od.bastuSpak.transform;
                _anim = lever.GetComponent<Animation>();
                if (U.Alive(_od.bastuSteam)) _steam = _od.bastuSteam.GetComponent<ParticleSystem>();
                var t = FindName(lever.parent != null ? lever.parent : lever, "BastuSpakColl");
                _coll = t != null ? t.gameObject : lever.gameObject;
                Log.Debug($"sauna: lever {lever.name}, tag on {_coll.name}, steam {(_steam != null ? _steam.name : "-")}");
                return true;
            }
            catch (Exception e) { Log.Debug("sauna: " + e.Message); return false; }
        }

        public static void Update()
        {
            if (!Session.Active || !GameRefs.InGameScene || Time.unscaledTime < _next) return;
            _next = Time.unscaledTime + 0.1f;
            if (!Find()) return;
            int s;
            try { var tag = _coll.tag; s = tag == TagOn ? 1 : tag == TagOff ? 0 : -1; } catch { return; }
            if (s < 0 || s == _state) return;
            bool first = _state < 0;
            _state = s;
            if (first && s == 0) return; // as the scene starts
            Apply(s == 1, !first);
        }

        static void Apply(bool on, bool lever)
        {
            try { if (U.Alive(_steam)) _steam.enableEmission = on; } catch (Exception e) { Log.Debug("sauna steam: " + e.Message); }
            if (lever && U.Alive(_anim))
            {
                // the game that pulled it is already playing this clip; the others play it now
                var clip = on ? "BastuSpakOn" : "BastuSpakOff";
                try { if (!_anim.IsPlaying(clip) && _anim.GetClip(clip) != null) _anim.Play(clip); } catch { }
            }
            Log.Debug($"sauna steam {(on ? "on" : "off")}");
        }

        static Transform FindName(Transform t, string name)
        {
            if (t.name == name) return t;
            for (int i = 0; i < t.childCount; i++) { var r = FindName(t.GetChild(i), name); if (r != null) return r; }
            return null;
        }
    }
}
