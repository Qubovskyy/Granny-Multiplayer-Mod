using System;
using UnityEngine;

namespace GrannyMP
{
    /// <summary>
    /// Escapes that end inside Granny's house with the game's own cutscene. The main door leads to EndScene, which every
    /// player loads (its cutscene is in that scene). Two others did not: the car through the garage tunnel
    /// (theEndCarEscape on TunnelCameraParent / TunnelCameraParent_NoGranny) and the spider cellar
    /// (escapeSpiderCellarTrigger.escapeCam) played only in the game of the player who escaped. When one of these starts
    /// in any game it is sent, and every other game plays the same cutscene.
    /// </summary>
    internal static class EscapeSync
    {
        const string TunnelPath = "HouseUpdate4Test11Garage/Garage_EscapeTunnel/";
        static GameObject _tunnel, _tunnelNoGranny; static escapeSpiderCellarTrigger _cellar;
        static float _nextFind, _nextCheck;
        static bool _done; // this game already plays an escape (its own or from the network)

        /// <summary>An escape cutscene from the network is playing here – the spectator keeps away.</summary>
        public static bool Playing { get; private set; }

        public static void RegisterHandlers() => Session.On(Msg.Escape, Handle);

        public static void OnSceneChanged() { _tunnel = null; _tunnelNoGranny = null; _cellar = null; _done = false; Playing = false; _nextFind = 0; }

        static void Find()
        {
            if (Time.unscaledTime < _nextFind) return;
            _nextFind = Time.unscaledTime + 3f;
            try
            {
                var house = GameObject.Find("HouseBakedReady");
                if (house != null)
                {
                    if (!U.Alive(_tunnel)) _tunnel = house.transform.Find(TunnelPath + "TunnelCameraParent")?.gameObject;
                    if (!U.Alive(_tunnelNoGranny)) _tunnelNoGranny = house.transform.Find(TunnelPath + "TunnelCameraParent_NoGranny")?.gameObject;
                }
                if (!U.Alive(_cellar)) _cellar = UnityEngine.Object.FindObjectOfType(Il2CppInterop.Runtime.Il2CppType.Of<escapeSpiderCellarTrigger>(), true)?.TryCast<escapeSpiderCellarTrigger>();
            }
            catch { }
        }

        /// <summary>Every frame in Granny's house: did this game start an escape cutscene?</summary>
        public static void Update()
        {
            if (!Session.Active || !GameRefs.InGameScene || _done) return;
            if (Time.unscaledTime < _nextCheck) return;
            _nextCheck = Time.unscaledTime + 0.2f;
            Find();
            int kind = 0;
            try
            {
                if (U.Alive(_tunnel) && _tunnel.activeInHierarchy) kind = 1;
                else if (U.Alive(_tunnelNoGranny) && _tunnelNoGranny.activeInHierarchy) kind = 2;
                else if (U.Alive(_cellar) && U.Alive(_cellar.escapeScene) && _cellar.escapeScene.activeInHierarchy) kind = 3;
            }
            catch { }
            if (kind == 0) return;
            _done = true;
            var w = Session.W(Msg.Escape); w.Byte(Session.LocalId); w.Byte((byte)kind);
            if (Session.IsHost) Session.Broadcast(w, true); else Session.SendToHost(w, true);
            Log.Info($"Escape here ({Name(kind)}) – the others play the cutscene too");
        }

        static string Name(int kind) => kind == 3 ? "spider cellar" : "car";

        static void Handle(NetPlayer from, NetReader r)
        {
            var who = r.Byte(); var kind = r.Byte();
            if (Session.IsHost)
            {
                if (from == null) return;
                var w = Session.W(Msg.Escape); w.Byte(who); w.Byte(kind); Session.Broadcast(w, true);
            }
            if (who == Session.LocalId || _done || !GameRefs.InGameScene) return;
            _done = true;
            Session.Notify(kind == 3 ? L.T($"{Session.NameOf(who)} escaped through the spider cellar!", $"{Session.NameOf(who)} uciekł(a) przez piwnicę pająka!")
                                     : L.T($"{Session.NameOf(who)} escaped by car!", $"{Session.NameOf(who)} uciekł(a) samochodem!"), false);
            Play(kind);
        }

        static void Play(int kind)
        {
            Find();
            try
            {
                Playing = true;
                if (Spectator.Active) Spectator.Stop();
                if (kind == 3)
                {
                    // the game's own cutscene of that escape, with this player's own player / camera
                    if (U.Alive(_cellar)) { _cellar.StartCoroutine(_cellar.escapeCam()); Log.Info("Escape cutscene (spider cellar) from the network"); }
                    return;
                }
                var go = kind == 1 ? _tunnel : _tunnelNoGranny;
                if (!U.Alive(go)) { Log.Warn("Escape cutscene: the tunnel camera was not found"); return; }
                // the player's own view off – the tunnel camera (theEndCarEscape) films the end
                foreach (var c in Camera.allCameras) { if (c != null && !U.IsUnder(c.transform, go.transform)) c.enabled = false; }
                try { if (U.Alive(GameRefs.Fps)) GameRefs.Fps.enabled = false; } catch { }
                for (var t = go.transform.parent; t != null; t = t.parent) if (!t.gameObject.activeSelf) t.gameObject.SetActive(true);
                go.SetActive(true);
                Log.Info("Escape cutscene (car) from the network");
            }
            catch (Exception e) { Log.Warn("Escape cutscene: " + e.Message); }
        }
    }
}
