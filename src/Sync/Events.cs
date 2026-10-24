using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace GrannyMP
{
    /// <summary>Events: forwarding noise triggers to the host, remote calls (Granny getting hit), sounds.</summary>
    internal static class Events
    {
        public static bool Forwarding;      // the host is running a forwarded trigger
        public static byte ActionBy = 255;  // host: the client whose action (forwarded trigger / RPC) is being executed
        public static bool ActionAlsoLocal; // ...and that client ran it locally too (it already heard the sounds)
        public static bool PlayingRemote;   // playing a sound received from the network
        static bool _suppressSend; static int _suppressFrame;
        /// <summary>Host: sounds played inside this call are not sent (they have their own message, e.g. Granny's voice).</summary>
        public static bool SuppressSend { get => _suppressSend && _suppressFrame == Time.frameCount; set { _suppressSend = value; _suppressFrame = Time.frameCount; } }
        static readonly Dictionary<string, float> _stayLimiter = new();
        static Dictionary<string, AudioClip> _clips;
        static float _clipsBuiltAt;
        static readonly Dictionary<byte, (float t, int n)> _soundRate = new();
        static int _localSoundsThisSecond; static float _localSoundSecond;

        public static void RegisterHandlers()
        {
            Session.On(Msg.TriggerFwd, HandleTrigger);
            Session.On(Msg.HostRpc, HandleRpc);
            Session.On(Msg.Sound, HandleSound);
            Session.On(Msg.GrannyVoice, HandleGrannyVoice);
        }

        // ================= Granny's voice =================
        // GrannySounds.startGrannySound picks a random line ("where are you?", laughing…) and plays it on the
        // AudioSource on Granny's head. The host sends WHICH line (index in her list – several clips share names
        // like "1", "2"); clients play it on the same AudioSource, so a new line replaces the previous one exactly
        // like in single player and nothing can repeat.
        static GrannySounds _gs;
        static float _lastVoiceSent;

        static GrannySounds FindGrannySounds()
        {
            if (U.Alive(_gs)) return _gs;
            try
            {
                foreach (var o in UnityEngine.Object.FindObjectsOfType(Il2CppInterop.Runtime.Il2CppType.Of<GrannySounds>(), true))
                {
                    var g = o.TryCast<GrannySounds>();
                    if (g != null) { _gs = g; break; }
                }
            }
            catch (Exception e) { Log.Debug("GrannySounds: " + e.Message); }
            return _gs;
        }

        /// <summary>Host, PvP: the Granny player makes Granny say a line or laugh (her own code – the patches send it to everybody).</summary>
        public static void GrannyTalk(bool laugh)
        {
            var gs = FindGrannySounds();
            if (!U.Alive(gs)) return;
            try { if (laugh) gs.grannySkrattar(); else gs.startGrannySound(); }
            catch (Exception e) { Log.Debug("granny talk: " + e.Message); }
        }

        // ---- Granny's voice: never faster than one line a second ----
        // In a test a line ("where are you?") was heard several times a second. Whatever asks for it that often
        // (the host's game, the audio state scan, the generic sound sync), a new line needs 1 s after the previous one.
        const float VoiceGap = 1f;
        static float _hostVoiceAt = -10f, _voiceBurstFrom; static int _voiceBurst, _voiceSkipped; static float _voiceWarnAt;

        /// <summary>Host: may Granny start a new line now?</summary>
        public static bool HostVoiceAllowed()
        {
            float now = Time.unscaledTime;
            if (now - _hostVoiceAt >= VoiceGap) { _hostVoiceAt = now; return true; }
            _voiceSkipped++;
            if (now >= _voiceWarnAt) { _voiceWarnAt = now + 10f; Log.Info($"Granny's voice was asked for again {now - _hostVoiceAt:F2} s after the last line – repeats skipped ({_voiceSkipped} so far)"); }
            return false;
        }

        /// <summary>
        /// Client: Granny's voice copy is about to play <paramref name="clip"/> (<paramref name="path"/> = which sync
        /// asked). False when a line started less than a second ago.
        /// </summary>
        public static bool ClientVoiceAllowed(AudioSource proxy, string clip, string path)
        {
            float now = Time.unscaledTime;
            if (now - _voiceBurstFrom > 1f) { _voiceBurstFrom = now; _voiceBurst = 0; }
            _voiceBurst++;
            bool ok = !(U.Alive(proxy) && proxy.isPlaying && proxy.time < VoiceGap);
            if (Cfg.DebugLog.Value) Log.Debug($"Granny voice '{clip}' via {path}{(ok ? "" : " – skipped, a line just started")}");
            if (_voiceBurst > 2 && now >= _voiceWarnAt) { _voiceWarnAt = now + 10f; Log.Info($"Granny's voice was sent {_voiceBurst} times within a second (last via {path}) – repeats skipped"); }
            return ok;
        }

        public static void SendGrannyVoice(GrannySounds gs, bool laugh)
        {
            if (!Session.IsHost || !Cfg.ReplicateSounds.Value || !World.Registered || !U.Alive(gs)) return;
            byte idx = 255; string name = "";
            if (laugh) { var c = gs.skratt; if (c == null) return; name = c.name; }
            else
            {
                var a = gs.GetComponent<AudioSource>();
                var clip = U.Alive(a) ? a.clip : null;
                if (clip == null) return;
                name = clip.name;
                var arr = gs.Glaugh;
                if (arr != null) for (int i = 0; i < arr.Length && i < 255; i++) if (arr[i] != null && arr[i].Pointer == clip.Pointer) { idx = (byte)i; break; }
            }
            if (Time.unscaledTime - _lastVoiceSent < 0.3f) return;
            _lastVoiceSent = Time.unscaledTime;
            // this line has its own message – the audio state scan must not send the same start again
            try { var src = gs.GetComponent<AudioSource>(); if (U.Alive(src)) AudioStateSync.Note(src, src.clip, false, true); } catch { }
            var w = Session.W(Msg.GrannyVoice); w.Bool(laugh); w.Byte(idx); w.Str(name);
            Session.Broadcast(w, true);
            if (Cfg.DebugLog.Value) Log.Debug($"Granny {(laugh ? "laughs" : "says")} '{name}' (#{idx})");
        }

        static void HandleGrannyVoice(NetPlayer from, NetReader r)
        {
            if (!Session.IsClient || !GameRefs.InGameScene) return;
            bool laugh = r.Bool(); byte idx = r.Byte(); string name = r.Str();
            var gs = FindGrannySounds();
            if (!U.Alive(gs)) return;
            PlayingRemote = true;
            try
            {
                if (laugh)
                {
                    var c = gs.skratt;
                    if (c != null) AudioSource.PlayClipAtPoint(c, gs.transform.position);
                    return;
                }
                AudioClip clip = null;
                var arr = gs.Glaugh;
                if (arr != null && idx < arr.Length) clip = arr[idx];
                if ((clip == null || clip.name != name) && arr != null)
                    for (int i = 0; i < arr.Length; i++) if (arr[i] != null && arr[i].name == name) { clip = arr[i]; break; }
                var a = VoiceProxy(gs);
                if (clip == null || !U.Alive(a)) return;
                if (!ClientVoiceAllowed(a, name, "voice message")) return; // a line just started
                a.loop = false;
                a.clip = clip;
                a.Play();
                if (Cfg.DebugLog.Value) Log.Debug($"Granny says '{name}' (host)");
            }
            catch (Exception e) { Log.Debug("granny voice: " + e.Message); }
            finally { PlayingRemote = false; }
        }

        // Client: Granny's own voice AudioSource stays muted – whatever the local game starts on it (animation events,
        // play-on-awake after a world sync, her laugh whose method cannot be patched) is not heard. Only the lines the
        // host sends are played, on a copy of that AudioSource (same position, volume, distance settings).
        static AudioSource _voiceSrc, _voiceProxy;
        public static AudioSource VoiceProxy(GrannySounds gs)
        {
            if (!U.Alive(gs)) return null;
            var src = gs.GetComponent<AudioSource>();
            if (!U.Alive(src)) return null;
            if (U.Alive(_voiceProxy) && _voiceSrc == src) { if (!src.mute) src.mute = true; return _voiceProxy; }
            try
            {
                var go = new GameObject("GrannyMP_Voice");
                go.transform.SetParent(src.transform, false);
                var p = go.AddComponent<AudioSource>();
                p.playOnAwake = false; p.loop = false;
                p.volume = src.volume; p.pitch = src.pitch; p.spatialBlend = src.spatialBlend; p.priority = src.priority;
                p.minDistance = src.minDistance; p.maxDistance = src.maxDistance; p.rolloffMode = src.rolloffMode;
                p.dopplerLevel = src.dopplerLevel; p.spread = src.spread; p.reverbZoneMix = src.reverbZoneMix;
                p.outputAudioMixerGroup = src.outputAudioMixerGroup;
                if (src.rolloffMode == AudioRolloffMode.Custom) p.SetCustomCurve(AudioSourceCurveType.CustomRolloff, src.GetCustomCurve(AudioSourceCurveType.CustomRolloff));
                src.mute = true;
                if (src.isPlaying) src.Stop();
                _voiceSrc = src; _voiceProxy = p;
                Log.Debug($"Granny voice: proxy AudioSource on '{src.name}' (2D/3D {src.spatialBlend:F1}, {src.minDistance:F0}-{src.maxDistance:F0} m)");
            }
            catch (Exception e) { Log.Debug("granny voice proxy: " + e.Message); return src; }
            return _voiceProxy;
        }

        /// <summary>Client: the AudioSource a synced sound for <paramref name="a"/> must play on (Granny's voice → the proxy).</summary>
        public static AudioSource ClientVoiceTarget(AudioSource a)
        {
            if (!Session.IsClient || !U.Alive(a)) return a;
            var gs = FindGrannySounds();
            if (!U.Alive(gs) || gs.gameObject != a.gameObject) return a;
            return VoiceProxy(gs) ?? a;
        }

        /// <summary>Client, regularly: keeps Granny's own voice source muted.</summary>
        public static void KeepVoiceMuted()
        {
            if (!Session.IsClient || !GameRefs.InGameScene) return;
            var gs = FindGrannySounds();
            if (U.Alive(gs)) VoiceProxy(gs);
        }

        // ================= triggery =================
        /// <summary>Client: a forwarded trigger – send it to the host. Returns true when sent (skipped locally).</summary>
        public static bool ForwardTrigger(Component inst, Collider other, byte kind, out bool alsoLocal)
        {
            alsoLocal = false;
            if (!Session.IsClient || !World.Registered || !U.Alive(inst)) return false;
            var tn = U.Il2CppTypeName(inst);
            // also run here: things that must physically happen under this player (attic floor breaking), and
            // falling objects this client simulates itself (their collider goes off on the floor, like in single player)
            alsoLocal = Cfg.LocalForwardSet.Contains(tn) || (Cfg.OwnedLocalSet.Contains(tn) && World.IsLocallySimulated(inst.transform));
            uint id = World.ExactIdOf(inst.transform);
            if (id == 0) return false;
            byte otherKind = 2; uint otherId = 0;
            if (U.Alive(other))
            {
                if (U.IsUnder(other.transform, GameRefs.PlayerRoot)) otherKind = 0;
                else { otherId = World.IdOf(other.transform); otherKind = otherId != 0 ? (byte)1 : (byte)2; }
                if (PlayerSync.IsProxy(other)) { alsoLocal = false; return true; } // another player's avatar – ignored on the client
                // an item I just dropped/threw: the host does not know its id yet → send as soon as it is bound
                if (otherKind != 0 && (World.IsPendingId(otherId) || (otherId == 0 && other.transform.root.name.EndsWith("(Clone)", StringComparison.Ordinal))))
                {
                    if (_deferred.Count < 32) _deferred.Add((inst, other.transform, kind, tn, id, Time.unscaledTime, alsoLocal));
                    return true;
                }
            }
            if (otherKind == 2) { alsoLocal = false; return true; }
            if (kind == 1)
            {
                var key = id + tn;
                if (_stayLimiter.TryGetValue(key, out var t) && Time.unscaledTime - t < 0.25f) { alsoLocal = false; return true; }
                _stayLimiter[key] = Time.unscaledTime;
            }
            var w = Session.W(Msg.TriggerFwd);
            w.UInt(id); w.Str(tn); w.Byte(kind); w.Byte(otherKind); w.UInt(otherId); w.Bool(alsoLocal);
            Session.SendToHost(w, kind != 1);
            if (kind != 1 && Cfg.DebugLog.Value) Log.Debug($"Trigger -> host: {tn}.{(kind == 0 ? "Enter" : "Exit")} ({(otherKind == 0 ? "me" : "object " + otherId)}){(alsoLocal ? " + local" : "")}");
            return true;
        }

        static readonly List<(Component inst, Transform other, byte kind, string tn, uint id, float t, bool local)> _deferred = new();

        /// <summary>Client: send deferred triggers whose item has got its network id from the host.</summary>
        public static void FlushDeferred()
        {
            if (_deferred.Count == 0) return;
            float now = Time.unscaledTime;
            for (int i = 0; i < _deferred.Count; i++)
            {
                var d = _deferred[i];
                bool drop = now - d.t > 3f || !U.Alive(d.inst) || !U.Alive(d.other);
                if (!drop)
                {
                    uint otherId = World.IdOf(d.other);
                    if (otherId == 0 || World.IsPendingId(otherId)) continue;
                    var w = Session.W(Msg.TriggerFwd);
                    w.UInt(d.id); w.Str(d.tn); w.Byte(d.kind); w.Byte(1); w.UInt(otherId); w.Bool(d.local);
                    Session.SendToHost(w, true);
                }
                _deferred.RemoveAt(i); i--;
            }
        }

        static void HandleTrigger(NetPlayer from, NetReader r)
        {
            if (!Session.IsHost || from == null || from.Ghost) return;
            var id = r.UInt(); var tn = r.Str(); var kind = r.Byte(); var otherKind = r.Byte(); var otherId = r.UInt(); var ranLocally = r.Bool();
            var tr = World.TrOf(id);
            if (!U.Alive(tr)) return;
            Collider sub = null;
            bool spoof = false;
            // a hidden player is invisible to Granny's sensors (her sight / hearing triggers could still fire while the
            // client sat under a bed next to her, and she then went for the hiding spot)
            if (otherKind == 0 && AiControl.IsHiddenRemote(from.Id) && (tn == "grannySeePlayerTrigger" || tn == "cameraSeeTrigger" || tn == "GrannyHear")) return;
            if (otherKind == 0)
            {
                // the game's trigger scripts check the "Player" tag and call GetComponent on the player,
                // so we pass the host's REAL player, moved for this call to the position of the client
                sub = GameRefs.PlayerCollider;
                spoof = true;
                if (tn == "grannySeePlayerTrigger" || tn == "cameraSeeTrigger") AiControl.OnRemoteSeen(from.Id);
                else if (tn == "GrannyHear" && kind == 0) AiControl.OnRemoteHeard(from.Id);
            }
            else if (otherKind == 1)
            {
                var ot = World.TrOf(otherId);
                if (U.Alive(ot)) sub = U.ChildComp<Collider>(ot);
            }
            if (!U.Alive(sub)) return;
            var type = U.GameType(tn);
            if (type == null) return;
            var comp = tr.gameObject.GetComponent(Il2CppInterop.Runtime.Il2CppType.From(type));
            if (comp == null) return;
            string mname = kind == 0 ? "OnTriggerEnter" : kind == 1 ? "OnTriggerStay" : "OnTriggerExit";
            var mi = type.GetMethod(mname, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, null, new[] { typeof(Collider) }, null);
            if (mi == null) return;
            Forwarding = true; ActionBy = from.Id; ActionAlsoLocal = ranLocally;
            bool spoofed = spoof && AiControl.BeginSpoof(from.Id);
            try
            {
                var res = mi.Invoke(U.Wrap(type, comp.Pointer), new object[] { sub });
                if (res is Il2CppSystem.Collections.IEnumerator en) new MonoBehaviour(comp.Pointer).StartCoroutine(en);
                Log.Debug($"Trigger {tn}.{mname} from {from.Name}{(ranLocally ? " (also ran there)" : "")}");
            }
            catch (Exception e) { Log.Debug("Trigger fwd: " + (e.InnerException?.Message ?? e.Message)); }
            finally { Forwarding = false; ActionBy = 255; ActionAlsoLocal = false; if (spoofed) AiControl.EndSpoof(); }
        }

        // ================= RPC to the host =================
        public static void SendRpc(Component inst, string typeName, string method)
        {
            if (!Session.IsClient || !U.Alive(inst)) return;
            uint id = World.ExactIdOf(inst.transform);
            if (id == 0) id = World.IdOf(inst.transform);
            if (id == 0) return;
            var w = Session.W(Msg.HostRpc); w.UInt(id); w.Str(typeName); w.Str(method);
            Session.SendToHost(w, true);
            Log.Debug($"RPC → host: {typeName}.{method}");
        }

        /// <summary>RPCs whose effect every game must show itself (it is not world state): run on the clients too.</summary>
        static bool Echoed(string tn, string method) => tn == "explode" && method == "explodeNow";

        /// <summary>Host: an echoed RPC ran here – every client runs it too.</summary>
        public static void EchoRpc(Component inst, string typeName, string method, byte except = 255)
        {
            if (!Session.IsHost || !Echoed(typeName, method) || !U.Alive(inst)) return;
            uint id = World.ExactIdOf(inst.transform);
            if (id == 0) id = World.IdOf(inst.transform);
            if (id == 0) return;
            var w = Session.W(Msg.HostRpc); w.UInt(id); w.Str(typeName); w.Str(method);
            Session.Broadcast(w, true);
            Log.Debug($"RPC → everybody: {typeName}.{method}");
        }

        static void HandleRpc(NetPlayer from, NetReader r)
        {
            if (Session.IsClient) { HandleEchoedRpc(r); return; }
            if (!Session.IsHost || from == null) return;
            var id = r.UInt(); var tn = r.Str(); var method = r.Str();
            bool allowed = false;
            foreach (var (t, m) in Cfg.HostRpcList) if (t == tn && m == method) { allowed = true; break; }
            if (!allowed) return;
            var tr = World.TrOf(id);
            var type = U.GameType(tn);
            if (!U.Alive(tr) || type == null) return;
            var comp = tr.gameObject.GetComponent(Il2CppInterop.Runtime.Il2CppType.From(type)) ?? tr.GetComponentInChildren(Il2CppInterop.Runtime.Il2CppType.From(type), true);
            if (comp == null) return;
            var mi = type.GetMethod(method, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, null, Type.EmptyTypes, null);
            if (mi == null) return;
            try
            {
                Patches.RunningRpc = true; ActionBy = from.Id;
                var res = mi.Invoke(U.Wrap(type, comp.Pointer), null);
                if (res is Il2CppSystem.Collections.IEnumerator en) new MonoBehaviour(comp.Pointer).StartCoroutine(en);
                var (msgEn, msgPl) = Describe(method);
                if (msgEn != null) Session.Notify2($"{from.Name} {msgEn}", $"{from.Name} {msgPl}", true);
                if (Echoed(tn, method)) EchoRpc(comp.TryCast<Component>(), tn, method);
            }
            catch (Exception e) { Log.Debug("RPC: " + (e.InnerException?.Message ?? e.Message)); }
            finally { Patches.RunningRpc = false; ActionBy = 255; }
        }

        static (string, string) Describe(string m) => m switch
        {
            "grannyHitByPepper" => ("pepper-sprayed Granny!", "psiknął Granny gazem pieprzowym!"),
            "grannyHitByArrow" => ("hit Granny with a tranquilizer dart!", "trafił Granny strzałką usypiającą!"),
            "grannyHitByGun" => ("shot Granny!", "postrzelił Granny!"),
            "grannyHitByCar" => ("ran Granny over with the car!", "potrącił Granny autem!"),
            "grannyFreeze" => ("froze Granny with a trap!", "zamroził Granny pułapką!"),
            "spiderIsDead" => ("killed the spider!", "pokonał pająka!"),
            _ => (null, null), // no notice (e.g. the gas can's explodeNow)
        };

        /// <summary>Client: the host ran an effect every game shows itself (the gas can's explosion).</summary>
        static void HandleEchoedRpc(NetReader r)
        {
            var id = r.UInt(); var tn = r.Str(); var method = r.Str();
            if (!Echoed(tn, method) || !GameRefs.InGameScene) return;
            var tr = World.TrOf(id);
            var type = U.GameType(tn);
            if (!U.Alive(tr) || type == null) return;
            var comp = tr.gameObject.GetComponent(Il2CppInterop.Runtime.Il2CppType.From(type)) ?? tr.GetComponentInChildren(Il2CppInterop.Runtime.Il2CppType.From(type), true);
            if (comp == null) return;
            var mi = type.GetMethod(method, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, null, Type.EmptyTypes, null);
            if (mi == null) return;
            try { Patches.RunningRpc = true; mi.Invoke(U.Wrap(type, comp.Pointer), null); Log.Debug($"RPC from the host ran here: {tn}.{method}"); }
            catch (Exception e) { Log.Debug("RPC here: " + (e.InnerException?.Message ?? e.Message)); }
            finally { Patches.RunningRpc = false; }
        }

        // ================= sounds =================
        /// <summary>Called from AudioSource patches. Returns false when local playback must be blocked.</summary>
        public static bool OnLocalSound(AudioSource src, AudioClip clip, float vol, Vector3? atPoint, bool viaPlay = false)
        {
            if (!Cfg.ReplicateSounds.Value || PlayingRemote || World.Applying || SuppressSend) return true;
            if (!Session.Active || !World.Registered || clip == null) return true;
            // the network has just played this clip on this source (the other machine's copy arrived first)
            if (src != null && PlayedRemotely(src, clip.name, 0.35f)) return false;
            AudioStateSync.Note(src, clip, !viaPlay, false);
            try
            {
                Transform tr = src != null ? src.transform : null;
                uint objId = tr != null ? World.ExactIdOf(tr) : 0;   // exact source (where to play)
                var obj = tr != null ? World.NetObjOf(tr) : null;     // the network object decides the policy
                // AI on a client: AI sounds come only from the host
                if (Session.IsClient && obj != null && obj.HostOnly) return false;
                // an object moved by another player (held / thrown / pushed) or corrected by the network:
                // its owner's game makes the sound and sends it – a local copy would double it (or loop on corrections)
                byte action = Session.IsHost ? ActionBy : (byte)255;  // host executing a client's action
                if (obj != null && action == 255 && World.IsDrivenRemotely(obj)) return false;
                // loops and long clips started with Play() are handled by AudioStateSync (start AND stop)
                if (src != null && (src.loop || (viaPlay && clip.length > 4f))) return true;
                if (clip.length > 30f) return true;

                if (Time.realtimeSinceStartup - _localSoundSecond > 1f) { _localSoundSecond = Time.realtimeSinceStartup; _localSoundsThisSecond = 0; }
                if (++_localSoundsThisSecond > (Session.IsHost ? 45 : 25)) return true;

                bool flat = src != null && src.spatialBlend < 0.5f;   // 2D sound (UI, player feedback, bell…)
                byte kind; Vector3 pos;
                if (tr != null && U.IsUnder(tr, GameRefs.PlayerRoot))
                {
                    if (action != 255) { if (!ActionAlsoLocal) SendFlat(action, clip, vol); AudioStateSync.Note(src, clip, !viaPlay, true); return true; } // the client's action on the host player – only that client hears it
                    kind = 1; pos = tr.position; objId = 0;
                }
                else if (obj != null)
                {
                    pos = tr.position;
                    if (objId == 0) objId = obj.Id;
                    if (Session.IsClient && U.Dist(pos, GameRefs.LocalPos) > Cfg.ProposeRadius.Value) return true;
                    kind = obj.HostOnly ? (byte)2 : (byte)3;
                }
                else if (atPoint.HasValue)
                {
                    pos = atPoint.Value; objId = 0;
                    if (Session.IsClient && U.Dist(pos, GameRefs.LocalPos) > Cfg.ProposeRadius.Value) return true;
                    kind = 4;
                }
                else if (tr != null && action != 255)
                {
                    // a sound caused by a client's action executed on the host (doorbell, noise traps…)
                    if (flat) { if (!ActionAlsoLocal) SendFlat(action, clip, vol); AudioStateSync.Note(src, clip, !viaPlay, true); return true; }
                    kind = 4; pos = tr.position; objId = 0;
                }
                else if (tr != null && Session.IsHost && !flat && objId != 0)
                {
                    // host: a 3D world sound from an object that is not synced (bell ringer, alarms, host-only scripts…)
                    // – played on the same AudioSource on the clients
                    kind = 3; pos = tr.position;
                }
                else return true;

                // the same clip from the same object again within 0.8 s (bouncing physics) is not sent again
                if (objId != 0)
                {
                    var sk = clip.name + "|" + objId;
                    float tn = Time.unscaledTime;
                    if (_sentSounds.TryGetValue(sk, out var lt) && tn - lt < 0.8f) return true;
                    _sentSounds[sk] = tn;
                    if (_sentSounds.Count > 256) _sentSounds.Clear();
                }
                var w = Session.W(Msg.Sound);
                w.Byte(kind); w.Byte(Session.LocalId); w.UInt(objId); w.Vec3(pos); w.Str(clip.name); w.Float(vol);
                // the client whose forwarded action made this sound (an item it threw hitting the floor) must hear it
                // although the item is its own – its game did not play it (the trigger ran here)
                w.Byte(action);
                // a client that ran the action locally already heard it
                Session.Broadcast(w, false, action != 255 && ActionAlsoLocal ? action : (byte)255);
                AudioStateSync.Note(src, clip, !viaPlay, true);
            }
            catch (Exception e) { Log.Debug("sound: " + e.Message); }
            return true;
        }

        /// <summary>Host: a 2D sound produced by a client's action – played 2D for that client only.</summary>
        static void SendFlat(byte to, AudioClip clip, float vol)
        {
            var w = Session.W(Msg.Sound);
            w.Byte(5); w.Byte(Session.LocalId); w.UInt(0); w.Vec3(Vector3.zero); w.Str(clip.name); w.Float(vol); w.Byte(to);
            Session.SendTo(to, w, false);
        }

        // ---- Granny's lines: one at a time, from her head, never loud across the house ----
        static readonly HashSet<IntPtr> _voiceClips = new();
        static float _voiceClipsAt = -100f, _anyVoiceAt = -100f, _voiceBlockLogAt;

        static bool IsVoiceClip(AudioClip clip)
        {
            float now = Time.unscaledTime;
            if (_voiceClips.Count == 0 && now - _voiceClipsAt > 5f)
            {
                _voiceClipsAt = now;
                var gs = FindGrannySounds();
                if (U.Alive(gs))
                {
                    try
                    {
                        var arr = gs.Glaugh;
                        if (arr != null) for (int i = 0; i < arr.Length; i++) if (arr[i] != null) _voiceClips.Add(arr[i].Pointer);
                        if (gs.skratt != null) _voiceClips.Add(gs.skratt.Pointer);
                    }
                    catch { }
                }
            }
            return _voiceClips.Contains(clip.Pointer);
        }

        /// <summary>
        /// Every AudioSource play in a session passes here first. Granny's lines (her "where are you?", the laugh) are
        /// played at most once a second, whoever asks – the host's game, the network, the audio-state scan – and every
        /// one is logged with its source, so a line heard too often can be traced.
        /// </summary>
        public static bool VoiceGate(AudioSource src, AudioClip clip, string how)
        {
            if (clip == null || !GameRefs.InGameScene || !IsVoiceClip(clip)) return true;
            float now = Time.unscaledTime;
            bool ok = now - _anyVoiceAt >= 1f;
            if (ok) _anyVoiceAt = now;
            if (Cfg.DebugLog.Value || (!ok && now >= _voiceBlockLogAt))
            {
                if (!ok) _voiceBlockLogAt = now + 5f;
                string where = src != null ? PathOf(src.transform) : "a point";
                string msg = $"Granny's line '{clip.name}' ({how} on {where}, {(PlayingRemote ? "from the network" : "this game")}){(ok ? "" : " – blocked, a line started less than a second ago")}";
                if (ok) Log.Debug(msg); else Log.Info(msg);
            }
            return ok;
        }

        static string PathOf(Transform t)
        {
            var sb = new System.Text.StringBuilder();
            for (int i = 0; t != null && i < 6; t = t.parent, i++) sb.Insert(0, i == 0 ? t.name : t.name + "/");
            return sb.ToString();
        }

        /// <summary>A line of Granny heard from the network: on her voice copy, otherwise from her head – never PlayClipAtPoint (its default rolloff is heard across the whole house).</summary>
        static bool PlayVoiceAt(AudioClip clip, float vol, Vector3 pos)
        {
            var gs = FindGrannySounds();
            var proxy = U.Alive(gs) ? VoiceProxy(gs) : null;
            if (!U.Alive(proxy) || !proxy.isActiveAndEnabled) return false;
            if (!ClientVoiceAllowed(proxy, clip.name, "sound sync (point)")) return true;
            proxy.PlayOneShot(clip, Mathf.Clamp01(vol));
            NoteRemote(proxy, clip.name);
            return true;
        }

        // ---- items that already lie on the floor make no more fall noise ----
        static readonly Dictionary<IntPtr, Vector3> _landed = new();
        const float LandedMove = 0.3f;

        /// <summary>
        /// A fall-noise trigger of an item fired. False when the item has not moved (0.3 m) since its last one – it is
        /// lying there, the trigger fired again only because its body was switched kinematic / simulated.
        /// </summary>
        public static bool FallAllowed(Component self, Collider other)
        {
            if (!Session.Active || self == null) return true;
            Rigidbody rb = null;
            try { rb = self.GetComponentInParent<Rigidbody>(); } catch { }
            if (rb == null && other != null) { try { rb = other.attachedRigidbody; } catch { } }
            if (rb == null) return true;
            Vector3 pos; try { pos = rb.position; } catch { return true; }
            bool falling = false; try { falling = !rb.isKinematic && rb.linearVelocity.y < -0.5f; } catch { }
            if (!falling && _landed.TryGetValue(rb.Pointer, out var at) && (pos - at).sqrMagnitude < LandedMove * LandedMove)
            {
                if (Cfg.DebugLog.Value) Log.Debug($"fall noise of {rb.name} skipped – it has not moved since it landed");
                return false;
            }
            _landed[rb.Pointer] = pos;
            if (_landed.Count > 512) _landed.Clear();
            return true;
        }

        // what the network played on which AudioSource – the local game playing the same clip there a moment later
        // would be heard twice
        static readonly Dictionary<IntPtr, (string clip, float t)> _remotePlayed = new();
        public static void NoteRemote(AudioSource a, string clip)
        {
            if (!U.Alive(a) || clip == null) return;
            _remotePlayed[a.Pointer] = (clip, Time.unscaledTime);
            if (_remotePlayed.Count > 256) _remotePlayed.Clear();
        }
        static bool PlayedRemotely(AudioSource a, string clip, float within) =>
            _remotePlayed.TryGetValue(a.Pointer, out var r) && r.clip == clip && Time.unscaledTime - r.t < within;

        static readonly Dictionary<string, float> _recentSounds = new();
        static readonly Dictionary<string, float> _sentSounds = new();
        static AudioSource _flatSource;

        static void HandleSound(NetPlayer from, NetReader r)
        {
            var kind = r.Byte(); var pid = r.Byte(); var id = r.UInt(); var pos = r.Vec3(); var clipName = r.Str(); var vol = r.Float();
            byte byAction = r.HasData ? r.Byte() : (byte)255;
            if (Session.IsHost)
            {
                if (from == null) return;
                pid = from.Id; byAction = 255;
                if (!_soundRate.TryGetValue(pid, out var rt) || Time.unscaledTime - rt.t > 1f) rt = (Time.unscaledTime, 0);
                if (++rt.n > 20) { _soundRate[pid] = rt; return; }
                _soundRate[pid] = rt;
                var w = Session.W(Msg.Sound); w.Byte(kind); w.Byte(pid); w.UInt(id); w.Vec3(pos); w.Str(clipName); w.Float(vol); w.Byte(255);
                Session.Broadcast(w, false, pid);
            }
            if (pid == Session.LocalId && kind != 5) return;
            if (!GameRefs.InGameScene) return;
            var clip = FindClip(clipName);
            if (clip == null) return;
            // the same sound from the same place twice within 0.3 s = duplicate (physics jitter, double triggers)
            var key = clipName + "|" + id + "|" + Mathf.RoundToInt(pos.x * 2) + "," + Mathf.RoundToInt(pos.y * 2) + "," + Mathf.RoundToInt(pos.z * 2);
            float tnow = Time.unscaledTime;
            if (_recentSounds.TryGetValue(key, out var last) && tnow - last < 0.3f) return;
            _recentSounds[key] = tnow;
            if (_recentSounds.Count > 256) _recentSounds.Clear();
            PlayingRemote = true;
            try
            {
                vol = Mathf.Clamp(vol, 0f, 2f);
                if (kind == 5)
                {
                    if (!U.Alive(_flatSource))
                    {
                        var go = new GameObject("GrannyMP_FlatAudio");
                        UnityEngine.Object.DontDestroyOnLoad(go);
                        _flatSource = go.AddComponent<AudioSource>();
                        _flatSource.spatialBlend = 0f; _flatSource.playOnAwake = false;
                    }
                    _flatSource.PlayOneShot(clip, vol);
                    return;
                }
                // my own object – my game already played it (unless the host played it for my forwarded action)
                if ((kind == 2 || kind == 3) && id != 0 && byAction != Session.LocalId && World.TryGetObj(id, out var no) && no.Owner == Session.LocalId) return;
                if (kind == 1 && PlayerSync.TryGetAvatar(pid, out var a)) pos = a.Root.transform.position;
                if ((kind == 2 || kind == 3) && id != 0)
                {
                    var tr = World.TrOf(id);
                    var src = U.Alive(tr) ? (tr.GetComponent<AudioSource>() ?? U.ChildComp<AudioSource>(tr)) : null;
                    var orig = src;
                    src = ClientVoiceTarget(src);
                    if (U.Alive(src) && src != orig && !ClientVoiceAllowed(src, clipName, "sound sync")) return;
                    // the local game has just played the same clip on this source itself – do not play it twice
                    if (U.Alive(orig) && AudioStateSync.PlayedLocally(orig, clipName, 0.35f)) return;
                    if (U.Alive(src) && src.isActiveAndEnabled) { src.PlayOneShot(clip, vol); NoteRemote(src, clipName); return; }
                    if (U.Alive(tr)) pos = tr.position;
                }
                // Granny's line: from her head or not at all (with Granny switched off – practice – nobody talks)
                if (IsVoiceClip(clip)) { if (!PlayVoiceAt(clip, vol, pos)) Log.Debug($"Granny's line '{clipName}' from the network not played – her head is not here"); return; }
                AudioSource.PlayClipAtPoint(clip, pos, vol);
            }
            catch (Exception e) { Log.Debug("play sound: " + e.Message); }
            finally { PlayingRemote = false; }
        }

        public static AudioClip FindClipPublic(string name) => FindClip(name);

        /// <summary>A sound only this player hears (not sent): 2D, or 3D at a point.</summary>
        public static void PlayLocalOnly(AudioClip clip, float vol, Vector3? at = null)
        {
            if (clip == null) return;
            PlayingRemote = true;
            try
            {
                if (at.HasValue) { AudioSource.PlayClipAtPoint(clip, at.Value, vol); return; }
                if (!U.Alive(_flatSource))
                {
                    var go = new GameObject("GrannyMP_FlatAudio");
                    UnityEngine.Object.DontDestroyOnLoad(go);
                    _flatSource = go.AddComponent<AudioSource>();
                    _flatSource.spatialBlend = 0f; _flatSource.playOnAwake = false;
                }
                _flatSource.PlayOneShot(clip, vol);
            }
            catch (Exception e) { Log.Debug("local sound: " + e.Message); }
            finally { PlayingRemote = false; }
        }

        static AudioClip FindClip(string name)
        {
            if (_clips == null || (!_clips.ContainsKey(name) && Time.realtimeSinceStartup - _clipsBuiltAt > 10f))
            {
                _clips = new Dictionary<string, AudioClip>();
                _clipsBuiltAt = Time.realtimeSinceStartup;
                foreach (var c in U.AllOfType<AudioClip>())
                    if (c != null && !_clips.ContainsKey(c.name)) _clips[c.name] = c;
            }
            return _clips.TryGetValue(name, out var clip) ? clip : null;
        }

        public static void OnSceneChanged() { _clips = null; _stayLimiter.Clear(); _gs = null; _landed.Clear(); _remotePlayed.Clear(); _voiceClips.Clear(); _voiceClipsAt = -100f; }

        // ================= manually polled triggers (fallback without a patch) =================
        sealed class Polled
        {
            public Component Comp; public Collider Col; public Transform Tr;
            public int Shape; public Vector3 Center, Size; public float Radius; public Bounds WorldBounds;
            public bool Inside; public bool HasStay; public float LastStay;
        }
        static readonly List<Polled> _polled = new();

        public static void AddPolledTrigger(Component comp, Collider col)
        {
            var p = new Polled { Comp = comp, Col = col, Tr = col.transform, WorldBounds = col.bounds };
            var box = col.TryCast<BoxCollider>(); var sph = col.TryCast<SphereCollider>(); var cap = col.TryCast<CapsuleCollider>();
            if (box != null) { p.Shape = 1; p.Center = box.center; p.Size = box.size; }
            else if (sph != null) { p.Shape = 2; p.Center = sph.center; p.Radius = sph.radius; }
            else if (cap != null) { p.Shape = 1; p.Center = cap.center; p.Size = new Vector3(cap.radius * 2, cap.height, cap.radius * 2); }
            var type = U.GameType(U.Il2CppTypeName(comp));
            p.HasStay = type?.GetMethod("OnTriggerStay", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly, null, new[] { typeof(Collider) }, null) != null;
            _polled.Add(p);
        }

        public static void ClearPolled() => _polled.Clear();

        static bool Inside(Polled p, Vector3 w)
        {
            if (!U.Alive(p.Tr)) return false;
            if (p.Shape == 1)
            {
                var l = p.Tr.InverseTransformPoint(w) - p.Center;
                return Mathf.Abs(l.x) <= p.Size.x * 0.5f && Mathf.Abs(l.y) <= p.Size.y * 0.5f && Mathf.Abs(l.z) <= p.Size.z * 0.5f;
            }
            if (p.Shape == 2)
            {
                var l = p.Tr.InverseTransformPoint(w) - p.Center;
                return l.magnitude <= p.Radius;
            }
            return p.WorldBounds.Contains(w);
        }

        /// <summary>Client: player entering/leaving a trigger (without physics) → sent to the host.</summary>
        public static void PollTriggers()
        {
            if (_polled.Count == 0 || !Session.IsClient || !U.Alive(GameRefs.PlayerRoot)) return;
            if (Session.Local?.Ghost ?? false) return;
            var pos = GameRefs.PlayerRoot.position;
            var feet = pos + Vector3.down * 0.8f;
            var col = GameRefs.PlayerCollider;
            foreach (var p in _polled)
            {
                if (!U.Alive(p.Comp)) continue;
                bool inside = Inside(p, pos) || Inside(p, feet);
                if (inside != p.Inside)
                {
                    p.Inside = inside;
                    ForwardTrigger(p.Comp, col, inside ? (byte)0 : (byte)2, out _);
                }
                else if (inside && p.HasStay && Time.unscaledTime - p.LastStay > 0.25f)
                {
                    p.LastStay = Time.unscaledTime;
                    ForwardTrigger(p.Comp, col, 1, out _);
                }
            }
        }
    }
}
