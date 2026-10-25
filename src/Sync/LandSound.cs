using System;
using UnityEngine;

namespace GrannyMP
{
    /// <summary>
    /// The thud of a hard landing (a fall from a height: the camera falls over and gets up). The game does not play it
    /// with a play call – it switches on FirstPersonController_Egen.landHardSound, a sound holder that plays on awake –
    /// so the sound sync never saw it and the others heard nothing. Here the switch-on is noticed and sent.
    /// </summary>
    internal static class LandSound
    {
        static bool _was;

        public static void OnSceneChanged() => _was = false;

        public static void Update()
        {
            if (!Session.Active || !GameRefs.InGameScene) { _was = false; return; }
            var fps = GameRefs.Fps;
            GameObject holder;
            try { holder = U.Alive(fps) ? fps.landHardSound : null; } catch { return; }
            if (!U.Alive(holder)) return;
            bool on; try { on = holder.activeInHierarchy; } catch { return; }
            if (on == _was) return;
            _was = on;
            if (!on || (Session.Local?.Ghost ?? false)) return;
            try
            {
                var src = holder.GetComponent<AudioSource>();
                if (src == null) src = holder.GetComponentInChildren<AudioSource>(true);
                if (src == null || src.clip == null) return;
                Events.SendPlayerSound(src.clip, src.volume);
                Log.Debug($"hard landing: '{src.clip.name}' sent");
            }
            catch (Exception e) { Log.Debug("land sound: " + e.Message); }
        }
    }
}
