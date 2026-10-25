using System;
using System.Collections.Generic;
using UnityEngine;

namespace GrannyMP
{
    /// <summary>
    /// The game's on-screen notices about Granny (pictures under AllaTexter): "Granny is now gone for about 1 minute"
    /// (Granny Gone Texter, one per difficulty and weapon), "Granny placed an extra lock on the door", "Granny rewarded
    /// you with an extra day". Her AI shows them – on the host only – by setting GrannyGoneText.textOnOff; its Update
    /// then switches the picture on and off again after a while. The host sends which one started; the clients set the
    /// same flag on their copy.
    /// </summary>
    internal static class NoticeSync
    {
        static readonly string[] Singles = { "GrannyPlacedExtraLockOnDoor", "GrannyRewardedExtraDay" };
        static readonly List<GrannyGoneText> _notices = new();
        static readonly Dictionary<string, GrannyGoneText> _byName = new();
        static readonly HashSet<IntPtr> _wasOn = new();
        static bool _built; static float _next, _nextBuild;

        public static void RegisterHandlers() => Session.On(Msg.Notice, Handle);
        public static void OnSceneChanged() { _notices.Clear(); _byName.Clear(); _wasOn.Clear(); _built = false; _nextBuild = 0; }

        static void Build()
        {
            if (_built || Time.unscaledTime < _nextBuild) return;
            _nextBuild = Time.unscaledTime + 3f;
            var root = GameObject.Find("AllaTexter");
            if (root == null) return;
            _built = true;
            var gone = FindDeep(root.transform, "Granny Gone Texter");
            if (gone != null) for (int i = 0; i < gone.childCount; i++) Add(gone.GetChild(i));
            foreach (var n in Singles) { var t = FindDeep(root.transform, n); if (t != null) Add(t); }
            Log.Debug($"notices: {_notices.Count} synced (Granny gone, extra lock, extra day)");
        }

        static void Add(Transform t)
        {
            if (t == null || _byName.ContainsKey(t.name)) return;
            var g = t.GetComponent<GrannyGoneText>();
            if (g == null) return;
            _notices.Add(g); _byName[t.name] = g;
            try { if (g.textOnOff) _wasOn.Add(g.Pointer); } catch { }
        }

        static Transform FindDeep(Transform t, string name)
        {
            if (t.name == name) return t;
            for (int i = 0; i < t.childCount; i++) { var r = FindDeep(t.GetChild(i), name); if (r != null) return r; }
            return null;
        }

        /// <summary>Host: a notice started here goes to everybody.</summary>
        public static void Update()
        {
            if (!Session.Active || !Session.IsHost || !GameRefs.InGameScene || Time.unscaledTime < _next) return;
            _next = Time.unscaledTime + 0.05f;
            Build();
            foreach (var g in _notices)
            {
                if (!U.Alive(g)) continue;
                bool on; try { on = g.textOnOff; } catch { continue; }
                bool was = _wasOn.Contains(g.Pointer);
                if (on == was) continue;
                if (on) _wasOn.Add(g.Pointer); else _wasOn.Remove(g.Pointer);
                if (!on) continue;
                var w = Session.W(Msg.Notice); w.Str(g.name); Session.Broadcast(w, true);
                Log.Debug("notice sent: " + g.name);
            }
        }

        static void Handle(NetPlayer from, NetReader r)
        {
            var name = r.Str();
            if (Session.IsHost || !GameRefs.InGameScene) return;
            Build();
            if (!_byName.TryGetValue(name, out var g) || !U.Alive(g)) { Log.Debug("notice not found here: " + name); return; }
            try
            {
                if (!g.gameObject.activeSelf) g.gameObject.SetActive(true);
                if (!g.enabled) g.enabled = true;
                g.timer = 0f; g.textOnOff = true; // its Update shows the picture and hides it again
                Log.Debug("notice from the host: " + name);
            }
            catch (Exception e) { Log.Debug("notice: " + e.Message); }
        }
    }
}
