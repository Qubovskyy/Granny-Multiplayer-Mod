using System;
using System.Collections.Generic;
using UnityEngine;

namespace GrannyMP
{
    /// <summary>
    /// World sounds that do not go through PlayOneShot: alarms and other loops, long clips, and "sound holders"
    /// (objects with a play-on-awake AudioSource that a script switches on). Every 0.15 s the play state of all 3D
    /// AudioSources in the scene is compared; a change is sent (host → everybody, client → host for sources near it)
    /// and played on the same AudioSource on the other machines.
    /// </summary>
    internal static class AudioStateSync
    {
        sealed class Src { public uint Id; public AudioSource A; public bool Playing; public bool SentPlaying; public bool Ai; }
        static readonly List<Src> _srcs = new();
        static readonly Dictionary<IntPtr, Src> _byPtr = new();
        static readonly Dictionary<IntPtr, (float t, string clip, bool oneShot, bool sent)> _handled = new();
        static bool _built;
        static string _pendingClip;
        static float _next;

        public static void RegisterHandlers() => Session.On(Msg.AudioState, Handle);

        public static void Clear() { _srcs.Clear(); _byPtr.Clear(); _handled.Clear(); _built = false; }

        /// <summary>Called by Events.OnLocalSound for every local play request on an AudioSource.</summary>
        public static void Note(AudioSource a, AudioClip clip, bool oneShot, bool sent)
        {
            if (a == null) return;
            _handled[a.Pointer] = (Time.unscaledTime, clip != null ? clip.name : null, oneShot, sent);
            if (_handled.Count > 512) _handled.Clear();
        }

        /// <summary>The local game played <paramref name="clip"/> on <paramref name="a"/> within the last <paramref name="within"/> seconds.</summary>
        public static bool PlayedLocally(AudioSource a, string clip, float within) =>
            a != null && _handled.TryGetValue(a.Pointer, out var h) && h.clip == clip && Time.unscaledTime - h.t < within;

        static void Build()
        {
            _built = true;
            _srcs.Clear(); _byPtr.Clear();
            var player = GameRefs.PlayerRoot; var cam = GameRefs.PlayerCam;
            foreach (var o in UnityEngine.Object.FindObjectsOfType(Il2CppInterop.Runtime.Il2CppType.Of<AudioSource>(), true))
            {
                var a = o.TryCast<AudioSource>();
                if (a == null) continue;
                var t = a.transform;
                if (U.IsUnder(t, player) || (U.Alive(cam) && U.IsUnder(t, cam))) continue;
                if (t.root.name.StartsWith("GrannyMP_", StringComparison.Ordinal)) continue;
                if (MusicSync.IsTrack(a)) continue; // AI music has its own sync (MusicSync)
                uint id = World.ExactIdOf(t);
                if (id == 0) continue;
                var no = World.NetObjOf(t);
                var s = new Src { Id = id, A = a, Playing = a.isPlaying, Ai = no != null && no.HostOnly };
                _srcs.Add(s); _byPtr[a.Pointer] = s;
            }
            Log.Debug($"Audio state sync: {_srcs.Count} sources");
        }

        /// <summary>Called every frame from the main loop (in the game scene).</summary>
        public static void Tick()
        {
            if (!Cfg.ReplicateSounds.Value || !World.Registered) return;
            if (!_built) Build();
            float now = Time.unscaledTime;
            if (now < _next) return;
            _next = now + 0.15f;
            Events.KeepVoiceMuted();
            NetWriter w = null; int n = 0; int countPos = 0;
            Vector3 me = GameRefs.LocalPos; float radius = Cfg.ProposeRadius.Value;
            foreach (var s in _srcs)
            {
                if (!U.Alive(s.A)) continue;
                bool playing;
                try { playing = s.A.isPlaying; } catch { continue; }
                if (playing == s.Playing) continue;
                s.Playing = playing;
                if (!playing)
                {
                    if (!s.SentPlaying) continue;              // we never announced it – nothing to stop
                    s.SentPlaying = false;
                }
                else
                {
                    // 2D = feedback for the local player only – except AI sounds (Granny's voice is 2D) sent by the host
                    if (s.A.spatialBlend < 0.5f && !(Session.IsHost && s.Ai)) continue;
                    string clipName0 = null; bool fromCall = false;
                    if (_handled.TryGetValue(s.A.Pointer, out var h) && now - h.t < 0.5f)
                    {
                        fromCall = true;
                        if (h.oneShot || h.sent) continue;      // one-shots follow Events.OnLocalSound's policy
                        clipName0 = h.clip;
                    }
                    if (Session.IsClient)
                    {
                        var obj = World.NetObjOf(s.A.transform);
                        if (obj != null && obj.HostOnly) continue; // AI sounds come from the host
                        if (U.Dist(s.A.transform.position, me) > radius) continue;
                    }
                    if (clipName0 == null && s.A.clip != null) clipName0 = s.A.clip.name;
                    if (clipName0 == null) continue;
                    _pendingClip = clipName0;
                    s.SentPlaying = true;
                    _ = fromCall;
                }
                string clipName = playing ? _pendingClip : ""; bool oneShot = false;
                if (w == null) { w = Session.W(Msg.AudioState); countPos = w.Len; w.Byte(0); }
                w.UInt(s.Id); w.Bool(playing); w.Str(clipName ?? ""); w.Bool(oneShot); w.Bool(s.A.loop); w.Float(s.A.volume);
                if (++n >= 60) break;
            }
            if (w == null) return;
            w.Buf[countPos] = (byte)n;
            Session.Broadcast(w, true);
        }

        static void Handle(NetPlayer from, NetReader r)
        {
            if (!GameRefs.InGameScene || !World.Registered) return;
            if (!_built) Build();
            int n = r.Byte();
            NetWriter fw = null; int fn = 0, fpos = 0;
            for (int i = 0; i < n; i++)
            {
                uint id = r.UInt(); bool playing = r.Bool(); string clipName = r.Str(); bool oneShot = r.Bool(); bool loop = r.Bool(); float vol = r.Float();
                var tr = World.TrOf(id);
                if (!U.Alive(tr)) continue;
                var a = tr.GetComponent<AudioSource>();
                if (!U.Alive(a) || MusicSync.IsTrack(a)) continue;
                if (Session.IsHost)
                {
                    if (from == null || from.Ghost) continue;
                    var obj = World.NetObjOf(tr);
                    if (obj != null && obj.HostOnly) continue;
                    // forward to the other clients
                    if (fw == null) { fw = Session.W(Msg.AudioState); fpos = fw.Len; fw.Byte(0); }
                    fw.UInt(id); fw.Bool(playing); fw.Str(clipName); fw.Bool(oneShot); fw.Bool(loop); fw.Float(vol); fn++;
                }
                _byPtr.TryGetValue(a.Pointer, out var s);
                var orig = a;
                a = Events.ClientVoiceTarget(a); // Granny's voice plays on its unmuted copy
                if (playing && a != orig && !Events.ClientVoiceAllowed(a, clipName, "audio state")) continue;
                Events.PlayingRemote = true;
                try
                {
                    if (playing)
                    {
                        var clip = string.IsNullOrEmpty(clipName) ? a.clip : Events.FindClipPublic(clipName);
                        if (clip == null) continue;
                        if (!a.gameObject.activeInHierarchy) a.gameObject.SetActive(true);
                        if (!a.enabled) a.enabled = true;
                        if (!(a.isPlaying && a.clip == clip))
                        {
                            a.clip = clip; a.loop = loop; a.volume = vol;
                            a.Play();
                            Events.NoteRemote(a, clip.name);
                        }
                    }
                    else if (a.isPlaying) a.Stop();
                }
                catch (Exception e) { Log.Debug("audio state: " + e.Message); }
                finally
                {
                    Events.PlayingRemote = false;
                    // the local scan must not send this change back
                    if (s != null) { s.Playing = U.Alive(a) && a.isPlaying; s.SentPlaying = false; }
                }
            }
            if (fw != null && fn > 0) { fw.Buf[fpos] = (byte)fn; Session.Broadcast(fw, true, from.Id); }
        }
    }
}
