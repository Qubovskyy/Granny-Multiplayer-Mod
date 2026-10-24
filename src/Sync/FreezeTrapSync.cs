using System;
using UnityEngine;

namespace GrannyMP
{
    /// <summary>
    /// The freeze trap (freezeTrap on the trap, also on dropped clones). Its trigger runs in every game: the trap arms
    /// when it lands (green), snaps on the player, an item or Granny (red, steam, Granny frozen + GrannyFreezeDoll).
    /// Every game did that on its own – a trap was armed on one machine and closed on another, and Granny touched by her
    /// copy on a client was "frozen" there only. Now Granny's touch is decided by the host alone (her copy on a client
    /// does not spring a trap) and every change of a trap's state (armed / closed) is sent, so all copies are the same.
    /// </summary>
    internal static class FreezeTrapSync
    {
        public static bool Applying;

        public static void RegisterHandlers() => Session.On(Msg.TrapState, Handle);

        /// <summary>Prefix of freezeTrap.OnTriggerEnter: false = this game does not run it.</summary>
        public static bool Pre(freezeTrap t, Collider other, out bool before)
        {
            before = false;
            try { before = t.trapActivated; } catch { }
            if (!Session.Active || Applying || !GameRefs.InGameScene) return true;
            // Granny's copy on a client never springs a trap – the host's Granny does, and the result comes from there
            if (Session.IsClient && other != null && U.Alive(GameRefs.Granny) && U.IsUnder(other.transform, GameRefs.Granny.transform)) return false;
            return true;
        }

        /// <summary>Postfix: the trap armed / closed in this game – everybody else gets the same.</summary>
        public static void Post(freezeTrap t, bool before)
        {
            if (!Session.Active || Applying || !GameRefs.InGameScene) return;
            bool now; try { now = t.trapActivated; } catch { return; }
            if (now == before) return;
            var o = World.NetObjOf(t.transform);
            if (o == null) return;
            if (Session.IsClient && (Session.Local?.Ghost ?? false)) return;
            var w = Session.W(Msg.TrapState); w.UInt(o.Id); w.Bool(now);
            if (Session.IsHost) Session.Broadcast(w, true); else Session.SendToHost(w, true);
            Log.Debug($"freeze trap {(now ? "armed" : "closed")} here – sent");
        }

        static void Handle(NetPlayer from, NetReader r)
        {
            var id = r.UInt(); var on = r.Bool();
            if (!World.Registered || !World.TryGetObj(id, out var o) || !U.Alive(o.Go)) return;
            if (Session.IsHost && (from == null || from.Ghost)) return;
            Apply(o.Go, on);
            if (Session.IsHost) { var w = Session.W(Msg.TrapState); w.UInt(id); w.Bool(on); Session.Broadcast(w, true); }
        }

        static void Apply(GameObject go, bool on)
        {
            var ft = go.GetComponentInChildren(Il2CppInterop.Runtime.Il2CppType.Of<freezeTrap>(), true)?.TryCast<freezeTrap>();
            if (ft == null) return;
            Applying = true;
            try
            {
                if (ft.trapActivated == on) return;
                ft.trapActivated = on;
                if (U.Alive(ft.rend)) { var m = ft.rend.material; var tex = on ? ft.green : ft.red; if (m != null && tex != null) m.mainTexture = tex; }
                if (!on)
                {
                    // snapped shut: the steam, the sound, the game's own timer (as in its trigger)
                    try { if (ft.steam != null) ft.steam.enableEmission = true; } catch { }
                    try { var src = ft.GetComponent<AudioSource>(); if (src != null && ft.freezeSound != null) src.PlayOneShot(ft.freezeSound); } catch { }
                    try { ft.StartCoroutine(ft.timer()); } catch { }
                }
                Log.Debug($"freeze trap {(on ? "armed" : "closed")} (from the network)");
            }
            catch (Exception e) { Log.Debug("freeze trap: " + e.Message); }
            finally { Applying = false; }
        }
    }
}
