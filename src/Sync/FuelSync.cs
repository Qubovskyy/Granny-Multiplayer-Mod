using System;
using UnityEngine;
using UnityEngine.UI;

namespace GrannyMP
{
    /// <summary>
    /// Pouring fuel into the car: holding the button fills the bar of the player's own fillingFuel (FuelTankRay) –
    /// every player had their own progress. The fill level is shared now: whoever pours raises it for everybody
    /// (the highest level counts), so another player goes on where the first one stopped. When it is full the game
    /// itself sets checkTheCar.fuelOK (synced as a progress fact).
    /// </summary>
    internal static class FuelSync
    {
        static fillingFuel _ff; static float _nextFind, _next, _sent;

        public static void RegisterHandlers() => Session.On(Msg.FuelFill, Handle);
        public static void OnSceneChanged() { _ff = null; _nextFind = 0; _sent = 0; }

        static Image Bar()
        {
            if (!U.Alive(_ff) && Time.unscaledTime >= _nextFind)
            {
                _nextFind = Time.unscaledTime + 3f;
                try { _ff = UnityEngine.Object.FindObjectOfType(Il2CppInterop.Runtime.Il2CppType.Of<fillingFuel>(), true)?.TryCast<fillingFuel>(); } catch { }
            }
            try { return U.Alive(_ff) ? _ff.fillFuealBar : null; } catch { return null; }
        }

        public static void Update()
        {
            if (!Session.Active || !GameRefs.InGameScene || Time.unscaledTime < _next) return;
            _next = Time.unscaledTime + 0.25f;
            var bar = Bar();
            if (bar == null) return;
            float v; try { v = bar.fillAmount; } catch { return; }
            if (v <= _sent + 0.01f) return;
            _sent = v;
            var w = Session.W(Msg.FuelFill); w.Float(v);
            if (Session.IsHost) Session.Broadcast(w, false); else Session.SendToHost(w, false);
        }

        static void Handle(NetPlayer from, NetReader r)
        {
            float v = r.Float();
            var bar = Bar();
            if (bar == null) return;
            try
            {
                if (bar.fillAmount < v) { bar.fillAmount = v; }
                if (v > _sent) _sent = v;
                if (Session.IsHost && from != null) { var w = Session.W(Msg.FuelFill); w.Float(Mathf.Max(v, bar.fillAmount)); Session.Broadcast(w, false); }
            }
            catch (Exception e) { Log.Debug("fuel: " + e.Message); }
        }
    }
}
