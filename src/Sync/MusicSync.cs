using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace GrannyMP
{
    /// <summary>
    /// Music started by host-only AI (Granny's chase music, teddy music, mom spider…). On clients these scripts are
    /// disabled, so the host sends the play state of each configured music source and clients mirror it on their own copy.
    /// </summary>
    internal static class MusicSync
    {
        sealed class Track { public bool PerTarget; public int Index; public string Key; public AudioSource Src; public GameObject Go; public bool Playing; public float Vol; public bool Sent; public bool Want, HasWant; }
        static readonly List<Track> _tracks = new();
        static bool _resolved;
        static float _nextSend, _nextFull;
        static byte _sentOwner = 254, _owner = 255;

        /// <summary>The chase music is heard by the chased player and by a spectator watching that player.</summary>
        static bool HearsChase(byte owner) => owner != 255 && (owner == Session.LocalId || owner == Spectator.Watching);

        /// <summary>Every frame: the spectator may switch to the chased player (or away from them).</summary>
        public static void Update()
        {
            if (_tracks.Count == 0 || !Session.Active) return;
            bool mute = !HearsChase(Session.IsHost ? AiControl.ChaseOwner : _owner);
            foreach (var t in _tracks)
            {
                if (!U.Alive(t.Src)) continue;
                if (t.PerTarget && t.Src.mute != mute) t.Src.mute = mute;
                // client: the host decides when the music plays – the local game (the player's own controller, hiding,
                // the end of the day) also starts / stops these sources and must not override the host
                if (Session.IsClient && t.HasWant && GameRefs.InGameScene && !GameFlow.InTransition)
                {
                    bool playing = t.Src.isPlaying && t.Src.gameObject.activeInHierarchy;
                    if (playing == t.Want) continue;
                    Events.PlayingRemote = true;
                    try
                    {
                        if (t.Want) { if (!t.Go.activeSelf) t.Go.SetActive(true); if (!t.Src.enabled) t.Src.enabled = true; t.Src.Play(); }
                        else t.Src.Stop();
                    }
                    catch { }
                    finally { Events.PlayingRemote = false; }
                }
            }
        }

        public static void RegisterHandlers() => Session.On(Msg.Music, Handle);

        public static void Clear()
        {
            foreach (var t in _tracks) if (t.PerTarget && U.Alive(t.Src)) t.Src.mute = false;
            _tracks.Clear(); _trackPtrs.Clear(); _resolved = false; _sentOwner = 254; _owner = 255;
        }

        static readonly HashSet<IntPtr> _trackPtrs = new();
        /// <summary>Sources mirrored here – AudioStateSync must leave them alone (two systems switching the chase music fought).</summary>
        public static bool IsTrack(AudioSource a)
        {
            if (!_resolved && World.Registered && GameRefs.InGameScene) Resolve();
            return a != null && _trackPtrs.Contains(a.Pointer);
        }

        static void Resolve()
        {
            _resolved = true;
            _tracks.Clear(); _trackPtrs.Clear();
            var cache = new Dictionary<string, object>();
            var entries = (Cfg.MirroredMusic.Value ?? "").Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
            for (int index = 0; index < entries.Length && index < 255; index++)
            {
                var key = entries[index].Trim();
                int dot = key.LastIndexOf('.');
                if (dot <= 0) continue;
                string tn = key.Substring(0, dot), field = key.Substring(dot + 1);
                try
                {
                    var type = U.GameType(tn);
                    if (type == null) continue;
                    if (!cache.TryGetValue(tn, out var inst))
                    {
                        inst = null;
                        foreach (var o in UnityEngine.Object.FindObjectsOfType(Il2CppInterop.Runtime.Il2CppType.From(type), true))
                        { inst = U.Wrap(type, o.Pointer); break; }
                        cache[tn] = inst;
                    }
                    if (inst == null) continue;
                    var prop = type.GetProperty(field, BindingFlags.Public | BindingFlags.Instance);
                    var go = prop?.GetValue(inst) as GameObject;
                    if (!U.Alive(go)) continue;
                    var src = go.GetComponent<AudioSource>() ?? U.ChildComp<AudioSource>(go.transform);
                    if (!U.Alive(src)) continue;
                    _tracks.Add(new Track { PerTarget = key.StartsWith("EnemyAIGranny.GrannyHuntMusicHolder", StringComparison.Ordinal), Index = index, Key = key, Src = src, Go = go });
                    _trackPtrs.Add(src.Pointer);
                }
                catch (Exception e) { Log.Debug($"music {key}: {e.Message}"); }
            }
            Log.Debug($"Music sync: {_tracks.Count} sources");
        }

        /// <summary>Host: called every network tick.</summary>
        public static void HostTick()
        {
            if (!Session.IsHost) return;
            if (!_resolved) Resolve();
            if (_tracks.Count == 0) return;
            float now = Time.unscaledTime;
            if (now < _nextSend) return;
            _nextSend = now + 0.2f;
            bool full = now >= _nextFull;
            if (full) _nextFull = now + 2f;
            bool changed = full;
            // chase music: only the chased player hears it (Granny's code plays it on the host for anybody she chases)
            byte owner = AiControl.ChaseOwner;
            if (owner != _sentOwner) { changed = true; _sentOwner = owner; }
            foreach (var t in _tracks)
            {
                if (!U.Alive(t.Src)) continue;
                if (t.PerTarget) { bool mute = !HearsChase(owner); if (t.Src.mute != mute) t.Src.mute = mute; }
                bool playing = t.Src.isPlaying && t.Src.gameObject.activeInHierarchy;
                // the host's own player controller stops the chase music when the HOST hides or gets caught – while
                // Granny still chases another player that music keeps playing for them
                if (t.PerTarget && !playing && t.Playing && owner != 255 && owner != Session.LocalId && AiControl.ChasingNow)
                {
                    Events.PlayingRemote = true;
                    try { if (!t.Go.activeSelf) t.Go.SetActive(true); if (!t.Src.enabled) t.Src.enabled = true; t.Src.Play(); playing = true; }
                    catch { }
                    finally { Events.PlayingRemote = false; }
                }
                float vol = t.Src.volume;
                if (playing != t.Playing || Mathf.Abs(vol - t.Vol) > 0.05f || !t.Sent) { changed = true; t.Playing = playing; t.Vol = vol; t.Sent = true; }
            }
            if (!changed) return;
            var w = Session.W(Msg.Music);
            w.Byte(owner);
            w.Byte((byte)_tracks.Count);
            for (int i = 0; i < _tracks.Count; i++) { w.Byte((byte)_tracks[i].Index); w.Bool(_tracks[i].Playing); w.Float(_tracks[i].Vol); }
            Session.Broadcast(w, true);
        }

        static void Handle(NetPlayer from, NetReader r)
        {
            if (!Session.IsClient || !GameRefs.InGameScene || !World.Registered) return;
            if (!_resolved) Resolve();
            byte owner = r.Byte();
            _owner = owner;
            int n = r.Byte();
            for (int i = 0; i < n; i++)
            {
                int index = r.Byte(); bool playing = r.Bool(); float vol = r.Float();
                var t = _tracks.Find(x => x.Index == index);
                if (t == null || !U.Alive(t.Src)) continue;
                Events.PlayingRemote = true;
                try
                {
                    t.Src.volume = vol;
                    t.Want = playing; t.HasWant = true;
                    if (t.PerTarget) t.Src.mute = !HearsChase(owner); // chase music only for the chased player (and who watches them)
                    if (playing)
                    {
                        if (!t.Go.activeSelf) t.Go.SetActive(true);
                        if (!t.Src.enabled) t.Src.enabled = true;
                        if (!t.Src.isPlaying) t.Src.Play();
                    }
                    else if (t.Src.isPlaying) t.Src.Stop();
                }
                catch (Exception e) { Log.Debug("music: " + e.Message); }
                finally { Events.PlayingRemote = false; }
            }
        }
    }
}
