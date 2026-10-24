using System;
using System.Collections.Generic;
using UnityEngine;

namespace GrannyMP
{
    /// <summary>
    /// The player's own field of view ([General] FieldOfView, set in the F1 settings; 0 = the game's). The host can lock
    /// it for everybody (server configuration). Applied to the player's camera after the game's own scripts every frame.
    /// </summary>
    internal static class Fov
    {
        static readonly List<(Camera cam, float fov)> _changed = new();

        public static void LateUpdate()
        {
            float want = Cfg.Fov.Value;
            var camT = GameRefs.PlayerCam;
            bool on = want >= 30f && GameRefs.InGameScene && U.Alive(camT) && !(Session.Active && Session.LockFov) && !Spectator.Active;
            if (!on) { Restore(); return; }
            try
            {
                foreach (var c in camT.GetComponentsInChildren<Camera>(true))
                {
                    if (c == null || c.targetTexture != null) continue;
                    if (!_changed.Exists(x => U.Alive(x.cam) && x.cam.Pointer == c.Pointer)) _changed.Add((c, c.fieldOfView));
                    if (Mathf.Abs(c.fieldOfView - want) > 0.01f) c.fieldOfView = want;
                }
            }
            catch (Exception e) { Log.Debug("fov: " + e.Message); }
        }

        static void Restore()
        {
            if (_changed.Count == 0) return;
            foreach (var (cam, fov) in _changed) if (U.Alive(cam)) try { cam.fieldOfView = fov; } catch { }
            _changed.Clear();
        }
    }
}
