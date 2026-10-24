using System;
using UnityEngine;

namespace GrannyMP
{
    /// <summary>
    /// What the game does by itself together with a change that reaches this machine over the network – things that are
    /// not synced as objects (local physics, material colours). Called when an object is switched on by the network.
    /// </summary>
    internal static class Reactions
    {
        public static void OnActivated(NetObj o)
        {
            if (o == null || !U.Alive(o.Go)) return;
            try
            {
                var name = o.Go.name;
                if (name == "BookParent") MotherFalls();
                else if (name == "KabelAV" && o.Path.IndexOf("/LarmDosa#", StringComparison.Ordinal) >= 0) AlarmLampsOff(o.Tr.parent);
            }
            catch (Exception e) { Log.Debug("reaction: " + e.Message); }
        }

        /// <summary>
        /// The book is on the shelf: Granny's mother falls from where she hangs. Like the game's own PickUp her ragdoll
        /// bones become physical – every machine simulates the fall itself (the bones are not network objects).
        /// </summary>
        static void MotherFalls()
        {
            var mom = GameObject.Find("OldMom_Hang");
            if (!U.Alive(mom)) return;
            int n = 0;
            foreach (var c in mom.GetComponentsInChildren(Il2CppInterop.Runtime.Il2CppType.Of<Rigidbody>(), true))
            {
                var rb = c?.TryCast<Rigidbody>();
                if (rb != null && rb.isKinematic) { rb.isKinematic = false; n++; }
            }
            var snd = Find(mom.transform, "MomSound");
            if (U.Alive(snd) && !snd.gameObject.activeSelf) snd.gameObject.SetActive(true);
            Log.Info($"Granny's mother falls – the book was placed by another player ({n} bones)");
        }

        /// <summary>The alarm cable was cut: both alarm lamps turn green, as in the game's own PickUp.</summary>
        static void AlarmLampsOff(Transform box)
        {
            if (!U.Alive(box)) return;
            foreach (var lamp in new[] { "Larmlampa1", "Larmlampa2" })
            {
                var t = box.Find(lamp);
                var r = U.Alive(t) ? t.GetComponent<Renderer>() : null;
                if (U.Alive(r)) r.material.color = Color.green;
            }
        }

        static Transform Find(Transform root, string name)
        {
            if (!U.Alive(root)) return null;
            if (root.name == name) return root;
            for (int i = 0; i < root.childCount; i++) { var f = Find(root.GetChild(i), name); if (f != null) return f; }
            return null;
        }
    }
}
