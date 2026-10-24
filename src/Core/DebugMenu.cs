using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace GrannyMP
{
    /// <summary>
    /// The host's debug panel ([General] DebugKey, F9, in Granny's house): spawn one of every item of the game in front
    /// of the host, god mode for everybody (Granny, the spiders and falls catch nobody – Session.GodMode, sent with the
    /// session settings).
    /// </summary>
    internal static class DebugMenu
    {
        public static bool Open;

        public static void Update()
        {
            if (!Session.Active || !GameRefs.InGameScene) { if (Open) Toggle(false); return; }
            if (!Session.IsHost) { if (Open) Toggle(false); return; }
            if (!LobbyUI.CapturingKey && !LobbyUI.TypingInField && In.KeyDown(Cfg.DebugKey.Value)) Toggle(!Open);
        }

        public static void Toggle(bool open)
        {
            Open = open;
            LobbyUI.UpdateInputCapture();
        }

        public static void SetGodMode(bool on)
        {
            if (!Session.IsHost) return;
            Session.GodMode = on;
            Session.PushSettings();
            Session.Notify2(on ? "God mode is ON – nobody can be caught or die." : "God mode is OFF.",
                            on ? "Tryb boga WŁĄCZONY – nikt nie może zostać złapany ani zginąć." : "Tryb boga WYŁĄCZONY.", true);
            Log.Info("God mode " + (on ? "on" : "off"));
        }

        /// <summary>
        /// One of every item, in a grid in front of the host: the drop prefabs the game itself uses when an item is
        /// dropped (InventoryController.newX) – the clones go to everybody like any dropped item.
        /// </summary>
        public static void SpawnAllItems()
        {
            var inv = GameRefs.Inventory; var cam = GameRefs.PlayerCam; var root = GameRefs.PlayerRoot;
            if (!U.Alive(inv) || !U.Alive(root)) return;
            var seen = new HashSet<string>();
            var prefabs = new List<GameObject>();
            foreach (var p in typeof(InventoryController).GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (p.PropertyType != typeof(Transform) || !p.Name.StartsWith("new", StringComparison.Ordinal)) continue;
                try
                {
                    var t = p.GetValue(inv) as Transform;
                    if (!U.Alive(t) || !seen.Add(t.gameObject.name)) continue;
                    prefabs.Add(t.gameObject);
                }
                catch { }
            }
            var fwd = U.Alive(cam) ? cam.forward : root.forward; fwd.y = 0; if (fwd.sqrMagnitude < 0.01f) fwd = Vector3.forward; fwd.Normalize();
            var right = Vector3.Cross(Vector3.up, fwd);
            var origin = root.position + fwd * 1.6f + Vector3.up * 0.4f;
            int n = 0, cols = 6;
            foreach (var go in prefabs)
            {
                try
                {
                    var pos = origin + right * ((n % cols) - (cols - 1) / 2f) * 0.6f + fwd * (n / cols) * 0.6f;
                    var clone = UnityEngine.Object.Instantiate(go, pos, go.transform.rotation);
                    clone.SetActive(true);
                    n++;
                }
                catch (Exception e) { Log.Debug($"spawn {go.name}: {e.Message}"); }
            }
            Session.Notify2($"Spawned {n} items.", $"Zespawnowano {n} przedmiotów.", true);
            Log.Info($"Debug: spawned {n} items ({string.Join(", ", prefabs.Select(p => p.name))})");
        }
    }
}
