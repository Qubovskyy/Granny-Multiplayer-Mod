using System;
using System.Collections.Generic;
using UnityEngine;

namespace GrannyMP
{
    /// <summary>
    /// What a player's item use / lever / shot does to objects of the house, beyond the objects the world sync knows.
    /// Using an item makes the player's own scripts (PickUp, openDoors, UseRemote, shootGun…) switch objects on or off
    /// and re-tag them: the wooden stick switches the "needs a lever" target off, the book re-tags the shelf, the weapon
    /// key switches all the tranquilizer darts on, the Switch Lock lever the lamps at the exit door. The game knows
    /// what is done by these objects and tags ("I need some kind of a lever" while the target is still there), so the
    /// other players need them exactly. While such a script runs (Patches: Interaction), every GameObject.SetActive /
    /// set_tag on an object of the scene is sent – a client's to the host, which applies it and tells everybody, also
    /// players who join later. Objects are found by the path id every machine gives every object of the scene (World).
    /// </summary>
    internal static class TagSync
    {
        /// <summary>Set while one of the local player's interaction scripts runs (Patches).</summary>
        public static bool InInteraction;
        public static bool Applying;

        static readonly Dictionary<uint, string> _tags = new();           // host: every change so far (late joiners)
        static readonly Dictionary<uint, bool> _active = new();
        static readonly Dictionary<uint, (int n, float since)> _rate = new();
        static readonly HashSet<uint> _ignored = new();
        static readonly Dictionary<uint, bool> _worldThing = new();   // id -> a real object of the house (WorldThing)
        static System.Text.RegularExpressions.Regex _exclude;

        public static void RegisterHandlers()
        {
            Session.On(Msg.TagSet, HandleTag);
            Session.On(Msg.ActiveSet, HandleActive);
        }

        public static void Clear() { _tags.Clear(); _active.Clear(); _rate.Clear(); _ignored.Clear(); _worldThing.Clear(); _exclude = null; }

        /// <summary>
        /// Only real things of the house: something with a model or a collider (itself or below), never a camera rig
        /// (the window jump switches on its own cameras – everybody was pulled into the host's window), never the
        /// player's on-screen buttons (the weapon buttons: everybody saw "Shoot") or anything [Advanced] ExcludePathRegex names.
        /// </summary>
        static bool WorldThing(uint id, Transform t)
        {
            if (_worldThing.TryGetValue(id, out var ok)) return ok;
            ok = false;
            try
            {
                _exclude ??= new System.Text.RegularExpressions.Regex(string.IsNullOrEmpty(Cfg.ExcludePathRegex.Value) ? "$^" : Cfg.ExcludePathRegex.Value);
                var path = "";
                bool camAbove = false, canvas = false;
                for (var p = t; p != null; p = p.parent)
                {
                    path = "/" + p.name + path;
                    if (p.name == "JumpOutWindow") camAbove = true;
                    if (p.GetComponent(Il2CppInterop.Runtime.Il2CppType.Of<Camera>()) != null) camAbove = true;
                    if (p.GetComponent(Il2CppInterop.Runtime.Il2CppType.Of<Canvas>()) != null) canvas = true;
                }
                if (!camAbove && !canvas && !_exclude.IsMatch(path) && !U.HasCameraBelow(t))
                    ok = t.GetComponentInChildren(Il2CppInterop.Runtime.Il2CppType.Of<Renderer>(), true) != null
                      || t.GetComponentInChildren(Il2CppInterop.Runtime.Il2CppType.Of<Collider>(), true) != null;
                if (!ok && Cfg.DebugLog.Value) Log.Debug("interaction sync: not a thing of the house – " + path);
            }
            catch { }
            _worldThing[id] = ok;
            return ok;
        }

        /// <summary>An object this sync may touch: in the scene, not the player's own, not on-screen UI, not the AI.</summary>
        static uint IdFor(GameObject go)
        {
            if (Applying || !InInteraction || !Session.Active || !GameRefs.InGameScene || !World.Registered || go == null) return 0;
            var t = go.transform;
            if (U.IsUI(t)) return 0;
            if (U.Alive(GameRefs.PlayerRoot) && U.IsUnder(t, GameRefs.PlayerRoot)) return 0;
            for (var p = t; p != null; p = p.parent) if (p.name == "HandHoldObjects") return 0;
            uint id = World.ExactIdOf(t);
            if (id == 0 || _ignored.Contains(id)) return 0;
            if (World.TryGetObj(id, out var o) && o.HostOnly) return 0;
            if (!WorldThing(id, t)) return 0;
            // something switched all the time is not an item use – it is left alone
            float now = Time.unscaledTime;
            _rate.TryGetValue(id, out var r);
            if (now - r.since > 5f) r = (0, now);
            r.n++; _rate[id] = r;
            if (r.n > 8) { _ignored.Add(id); Log.Debug($"interaction sync: {go.name} changes all the time – not synced"); return 0; }
            if (!Session.IsHost && (Session.Local?.Ghost ?? false)) return 0;
            return id;
        }

        // ---------------- tags ----------------
        /// <summary>After the game set a tag here (Patches: GameObject.set_tag postfix).</summary>
        public static void OnLocalTag(GameObject go, string tag)
        {
            if (string.IsNullOrEmpty(tag)) return;
            uint id = IdFor(go);
            if (id == 0) return;
            World.NoteLocalTag(id, tag);
            if (Session.IsHost) { _tags[id] = tag; Send(Msg.TagSet, id, tag, true); }
            else Send(Msg.TagSet, id, tag, false);
            if (Cfg.DebugLog.Value) Log.Debug($"interaction: tag {go.name} -> {tag}");
        }

        static void HandleTag(NetPlayer from, NetReader r)
        {
            var id = r.UInt(); var tag = r.Str();
            if (!World.Registered) return;
            if (Session.IsHost)
            {
                if (from == null || from.Ghost || !ApplyTag(id, tag)) return;
                _tags[id] = tag;
                Send(Msg.TagSet, id, tag, true);
                Log.Debug($"tag from {from.Name}: {World.TrOf(id)?.name} -> {tag}");
            }
            else ApplyTag(id, tag);
        }

        static bool ApplyTag(uint id, string tag)
        {
            var t = World.TrOf(id);
            if (!U.Alive(t) || string.IsNullOrEmpty(tag)) return false;
            Applying = true;
            try { if (t.gameObject.tag != tag) t.gameObject.tag = tag; World.NoteLocalTag(id, tag); return true; }
            catch (Exception e) { Log.Debug($"tag {t.name}: {e.Message}"); return false; }
            finally { Applying = false; }
        }

        // ---------------- switched on / off ----------------
        /// <summary>After the game switched an object on / off during an interaction (Patches: GameObject.SetActive postfix).</summary>
        public static void OnLocalActive(GameObject go, bool on)
        {
            uint id = IdFor(go);
            if (id == 0) return;
            World.NoteLocalActive(id, on);
            if (Session.IsHost) { _active[id] = on; SendActive(id, on, true); }
            else SendActive(id, on, false);
            if (Cfg.DebugLog.Value) Log.Debug($"interaction: {go.name} {(on ? "on" : "off")}");
        }

        /// <summary>
        /// The game destroyed an object during an interaction (the car battery's spot when the battery goes in): for the
        /// others it is switched off – the same for the game (the spot is gone), and the id stays valid. Objects the
        /// world sync knows (items picked up) have their own way.
        /// </summary>
        public static void OnLocalDestroy(GameObject go)
        {
            uint id = IdFor(go);
            if (id == 0 || World.TryGetObj(id, out _)) return;
            if (Session.IsHost) { _active[id] = false; SendActive(id, false, true); }
            else SendActive(id, false, false);
            if (Cfg.DebugLog.Value) Log.Debug($"interaction: {go.name} destroyed – switched off for the others");
        }

        static void HandleActive(NetPlayer from, NetReader r)
        {
            var id = r.UInt(); var on = r.Bool();
            if (!World.Registered) return;
            if (Session.IsHost)
            {
                if (from == null || from.Ghost || !ApplyActive(id, on)) return;
                _active[id] = on;
                SendActive(id, on, true);
                Log.Debug($"from {from.Name}: {World.TrOf(id)?.name} {(on ? "on" : "off")}");
            }
            else ApplyActive(id, on);
        }

        static bool ApplyActive(uint id, bool on)
        {
            var t = World.TrOf(id);
            if (!U.Alive(t)) return false;
            Applying = true;
            try { if (t.gameObject.activeSelf != on) t.gameObject.SetActive(on); World.NoteLocalActive(id, on); return true; }
            catch (Exception e) { Log.Debug($"active {t.name}: {e.Message}"); return false; }
            finally { Applying = false; }
        }

        static void Send(Msg m, uint id, string tag, bool all)
        {
            var w = Session.W(m); w.UInt(id); w.Str(tag);
            if (all) Session.Broadcast(w, true); else Session.SendToHost(w, true);
        }

        static void SendActive(uint id, bool on, bool all)
        {
            var w = Session.W(Msg.ActiveSet); w.UInt(id); w.Bool(on);
            if (all) Session.Broadcast(w, true); else Session.SendToHost(w, true);
        }

        /// <summary>Host: a player who has just loaded the house gets every change made so far.</summary>
        public static void SendAllTo(byte pid)
        {
            foreach (var kv in _active) { var w = Session.W(Msg.ActiveSet); w.UInt(kv.Key); w.Bool(kv.Value); Session.SendTo(pid, w, true); }
            foreach (var kv in _tags) { var w = Session.W(Msg.TagSet); w.UInt(kv.Key); w.Str(kv.Value); Session.SendTo(pid, w, true); }
        }
    }
}
