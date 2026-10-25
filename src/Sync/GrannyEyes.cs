using System;
using UnityEngine;

namespace GrannyMP
{
    /// <summary>
    /// Granny's eyes. Her AI (on the host only) colours them red while she hunts (EnemyAIGranny.grannyEyeColor: the
    /// colour of the Eyes material) and closes them (EnemyAIGranny.grannyLock: the EyesLock object switched on) –
    /// on the clients her copy kept white, open eyes. The host sends both whenever they change (and every few
    /// seconds for players who joined later); the clients keep them as the host has them.
    /// </summary>
    internal static class GrannyEyes
    {
        static float _next, _lastSent;
        static bool _sentLock; static Color _sentColor; static bool _sentAny;
        static bool _has, _lock; static Color _color;   // client: the host's state

        public static void RegisterHandlers() => Session.On(Msg.GrannyEyes, Handle);
        public static void OnSceneChanged() { _sentAny = false; _has = false; _next = 0; }

        static bool Read(EnemyAIGranny g, out bool closed, out Color c)
        {
            closed = false; c = Color.white;
            try
            {
                closed = U.Alive(g.grannyLock) && g.grannyLock.activeSelf;
                var r = U.Alive(g.grannyEyeColor) ? g.grannyEyeColor.GetComponent<Renderer>() : null;
                if (r != null && r.sharedMaterial != null) c = r.sharedMaterial.color;
                return true;
            }
            catch { return false; }
        }

        public static void Update()
        {
            if (!Session.Active || !GameRefs.InGameScene || Time.unscaledTime < _next) return;
            _next = Time.unscaledTime + 0.1f;
            var g = GameRefs.Granny;
            if (!U.Alive(g)) return;
            if (Session.IsHost)
            {
                if (!Read(g, out var closed, out var c)) return;
                bool changed = !_sentAny || closed != _sentLock || !Same(c, _sentColor);
                if (!changed && Time.unscaledTime - _lastSent < 3f) return;
                _sentAny = true; _sentLock = closed; _sentColor = c; _lastSent = Time.unscaledTime;
                var w = Session.W(Msg.GrannyEyes); w.Bool(closed); w.Float(c.r); w.Float(c.g); w.Float(c.b); w.Float(c.a);
                Session.Broadcast(w, changed);
                if (changed && Cfg.DebugLog.Value) Log.Debug($"Granny's eyes -> {(closed ? "closed" : "open")}, colour {c}");
                return;
            }
            if (_has) Apply(g);
        }

        static void Handle(NetPlayer from, NetReader r)
        {
            if (Session.IsHost) return;
            _lock = r.Bool(); _color = new Color(r.Float(), r.Float(), r.Float(), r.Float()); _has = true;
            var g = GameRefs.Granny;
            if (U.Alive(g) && GameRefs.InGameScene) Apply(g);
        }

        /// <summary>Client: the host's eyes on Granny's copy (whatever the local copy of her AI set).</summary>
        static void Apply(EnemyAIGranny g)
        {
            try
            {
                if (U.Alive(g.grannyLock) && g.grannyLock.activeSelf != _lock) g.grannyLock.SetActive(_lock);
                var rend = U.Alive(g.grannyEyeColor) ? g.grannyEyeColor.GetComponent<Renderer>() : null;
                if (rend != null && rend.sharedMaterial != null && !Same(rend.sharedMaterial.color, _color)) rend.material.color = _color;
            }
            catch (Exception e) { Log.Debug("granny eyes: " + e.Message); }
        }

        static bool Same(Color a, Color b) => Mathf.Abs(a.r - b.r) < 0.02f && Mathf.Abs(a.g - b.g) < 0.02f && Mathf.Abs(a.b - b.b) < 0.02f;
    }
}
