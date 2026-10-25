using System;
using System.Collections.Generic;
using UnityEngine;

namespace GrannyMP
{
    public enum VoiceMode { PushToTalk = 0, Toggle = 1, Always = 2 }

    /// <summary>
    /// Proximity voice chat. The microphone is read with Windows' own audio API (any connection type – Steam or Direct
    /// IP), compressed with IMA ADPCM (16 kHz, about 8 KB/s) and sent through the session (clients → host → the others).
    /// Voices are mixed on their own output stream: in Granny's house each voice comes from the speaker's avatar
    /// (fades out with distance, left / right by direction); in the lobby, menu and intro voices are not positional.
    /// Modes: push to talk, toggle, always on (voice activation). In "last survivor" a spectator hears the living
    /// around the player it watches, the living do not hear spectators, and spectators hear each other.
    /// </summary>
    internal static class VoiceChat
    {
        const int PacketSamples = 640; // 40 ms per packet
        const byte LoopbackId = 254;   // the microphone test plays the own voice back

        static MicCapture _mic;
        static VoiceOutput _out;
        static string _micOpenedFor;
        static float _retryAt;
        static readonly short[] _acc = new short[PacketSamples];
        static int _accN;
        static int _encPred, _encIdx;
        static ushort _seq;
        static readonly byte[] _enc = new byte[PacketSamples / 2];
        static short[] _dec = new short[PacketSamples * 2];
        static bool _toggleOn, _open;
        static float _vadUntil, _lastSent = -100f, _level, _levelPeak;
        static readonly Dictionary<byte, float> _lastHeard = new();

        /// <summary>The microphone test in the settings: the own voice is played back.</summary>
        public static bool Testing;
        public static string Error { get; private set; }
        /// <summary>Microphone level 0–1 (for the meter in the settings).</summary>
        public static float Level => _levelPeak;
        public static bool MicOpen => _mic != null;
        public static string MicName => _mic?.Device ?? "";

        /// <summary>The local player is sending voice right now.</summary>
        /// <summary>Toggle mode: the microphone is switched on (shown in the HUD).</summary>
        public static bool ToggledOn => _toggleOn && Session.Active && Cfg.VoiceModeCfg.Value == VoiceMode.Toggle;
        /// <summary>The player tried to talk recently (the HUD then shows a device error).</summary>
        public static bool WantsToTalk => Time.unscaledTime - _wantAt < 4f;
        static float _wantAt = -100f;
        static bool _warned;
        public static bool Talking => _open && Session.Active && Time.unscaledTime - _lastSent < 0.3f;
        public static bool IsSpeaking(byte id) => id == Session.LocalId ? Talking
            : _lastHeard.TryGetValue(id, out var t) && Time.unscaledTime - t < 0.3f;

        public static void RegisterHandlers() => Session.On(Msg.Voice, Handle);

        static bool Wanted => Cfg.VoiceEnabled.Value && (Session.Active || Testing);

        /// <summary>Called every frame.</summary>
        public static void Update()
        {
            float now = Time.unscaledTime;
            if (!Wanted) { CloseDevices(); _toggleOn = false; _open = false; return; }
            EnsureDevices(now);

            var mode = Cfg.VoiceModeCfg.Value;
            bool keysFree = !LobbyUI.TypingInField && !LobbyUI.ChatOpen && !LobbyUI.CapturingKey;
            bool held = keysFree && In.Key(Cfg.VoiceKey.Value);
            if (mode == VoiceMode.Toggle && keysFree && In.KeyDown(Cfg.VoiceKey.Value))
            {
                _toggleOn = !_toggleOn;
            }
            if (mode != VoiceMode.Toggle) _toggleOn = false;
            if ((mode == VoiceMode.PushToTalk && held) || _toggleOn) _wantAt = now;

            if (_mic != null)
                while (_mic.Frames.TryDequeue(out var f)) ProcessFrame(f, mode, held, now);
            _levelPeak = Mathf.Max(_level, _levelPeak - Time.unscaledDeltaTime * 0.8f);
            UpdateSpeakers();
        }

        static void EnsureDevices(float now)
        {
            string want = Cfg.MicDevice.Value ?? "";
            if (_mic != null && _micOpenedFor != want) { _mic.Dispose(); _mic = null; }
            if ((_out != null && _mic != null) || now < _retryAt) return;
            _retryAt = now + 5f;
            try
            {
                string err = null;
                if (_out == null) { _out = VoiceOutput.Open(out err); if (_out == null) Log.Warn("Voice output: " + err); }
                if (_mic == null)
                {
                    _mic = MicCapture.Open(want, out var merr);
                    _micOpenedFor = want;
                    if (_mic == null) { err = merr; Log.Warn("Microphone: " + merr); }
                    else Log.Info($"Voice chat: microphone '{(string.IsNullOrEmpty(_mic.Device) ? "Windows default" : _mic.Device)}'");
                }
                Error = err == null ? null : L.T("Voice chat: no microphone / audio device (", "Czat głosowy: brak mikrofonu / urządzenia audio (") + err + ")";
                if (Error != null && !_warned && Session.Active) { _warned = true; Session.Notify(Error, false); }
            }
            catch (Exception e) { Error = e.Message; Log.Warn("Voice chat: " + e.Message); }
        }

        static void CloseDevices()
        {
            if (_mic != null) { try { _mic.Dispose(); } catch { } _mic = null; }
            if (_out != null) { try { _out.Dispose(); } catch { } _out = null; }
            _accN = 0; _lastHeard.Clear(); _level = 0; _levelPeak = 0; _retryAt = 0;
        }

        static void ProcessFrame(short[] f, VoiceMode mode, bool held, float now)
        {
            float gain = Mathf.Clamp(Cfg.MicGain.Value, 0.1f, 8f);
            double sum = 0;
            for (int i = 0; i < f.Length; i++)
            {
                int v = (int)(f[i] * gain);
                if (v > 32767) v = 32767; else if (v < -32768) v = -32768;
                f[i] = (short)v; sum += (double)v * v;
            }
            _level = f.Length > 0 ? Mathf.Clamp01((float)Math.Sqrt(sum / f.Length) / 32768f * 4f) : 0f;
            if (_level / 4f > Cfg.MicThreshold.Value) _vadUntil = now + 0.4f;
            _open = mode switch
            {
                VoiceMode.Toggle => _toggleOn,
                VoiceMode.Always => now < _vadUntil,
                _ => held,
            };
            bool send = _open && Session.Active;
            if (!send && !Testing) { _accN = 0; return; }
            int pos = 0;
            while (pos < f.Length)
            {
                int n = Math.Min(PacketSamples - _accN, f.Length - pos);
                Array.Copy(f, pos, _acc, _accN, n);
                _accN += n; pos += n;
                if (_accN < PacketSamples) break;
                _accN = 0;
                int pred = _encPred, idx = _encIdx;
                Adpcm.Encode(_acc, PacketSamples, _enc, 0, ref _encPred, ref _encIdx);
                if (send) Send(pred, idx);
                if (Testing && _out != null)
                {
                    Adpcm.Decode(_enc, 0, _enc.Length, _dec, pred, idx);
                    _out.Push(LoopbackId, _dec, PacketSamples);
                }
            }
        }

        static void Send(int pred, int idx)
        {
            var w = Session.W(Msg.Voice);
            w.Byte(Session.LocalId); w.UShort(_seq++); w.UShort((ushort)(short)pred); w.Byte((byte)idx);
            w.Bytes(_enc, 0, _enc.Length);
            if (Session.IsClient) Session.SendToHost(w, false);
            else foreach (var p in Session.Players.Values) if (p.Conn >= 0 && !p.IsLocal && Hears(p, Session.Local)) Session.SendTo(p.Id, w, false);
            _lastSent = Time.unscaledTime;
        }

        /// <summary>Spectators are heard only by spectators (in the game).</summary>
        static bool Hears(NetPlayer listener, NetPlayer speaker) =>
            !Session.HostInGame || speaker == null || !speaker.Ghost || (listener != null && listener.Ghost);

        static void Handle(NetPlayer from, NetReader r)
        {
            byte id = r.Byte(); r.UShort();
            int pred = (short)r.UShort(); int idx = r.Byte();
            int off = r.Pos, len = r.Remaining;
            if (len <= 0 || len > 4096 || idx > 88) return;
            if (Session.IsHost)
            {
                if (from == null) return;
                id = from.Id;
                // relay to the others
                var w = Session.W(Msg.Voice); w.Byte(id); w.UShort(0); w.UShort((ushort)(short)pred); w.Byte((byte)idx); w.Bytes(r.Buf, off, len);
                foreach (var p in Session.Players.Values)
                    if (p.Conn >= 0 && p.Id != id && !p.IsLocal && Hears(p, from)) Session.SendTo(p.Id, w, false);
            }
            if (id == Session.LocalId || !Cfg.VoiceEnabled.Value || _out == null) return;
            Session.Players.TryGetValue(id, out var speaker);
            if (!Hears(Session.Local, speaker)) return;
            if (_dec.Length < len * 2) _dec = new short[len * 2];
            Adpcm.Decode(r.Buf, off, len, _dec, pred, idx);
            _out.Push(id, _dec, len * 2);
            _lastHeard[id] = Time.unscaledTime;
        }

        /// <summary>Main thread: the volume of each voice for the left / right ear.</summary>
        static void UpdateSpeakers()
        {
            if (_out == null) return;
            float vol = Mathf.Clamp(Cfg.VoiceVolume.Value, 0f, 3f);
            float range = Mathf.Max(3f, Session.VoiceRangeNow); // the host's, in a session
            Transform lis = null;
            if (GameRefs.InGameScene)
            {
                var sc = Spectator.ViewTransform;
                lis = U.Alive(sc) ? sc : (U.Alive(GameRefs.PlayerCam) ? GameRefs.PlayerCam : null);
            }
            foreach (var id in _out.Ids())
            {
                if (id == LoopbackId) { _out.SetGains(id, 1f, 1f); continue; }
                if (!Session.Players.TryGetValue(id, out var p)) { _out.Remove(id); continue; }
                float l = vol, rr = vol;
                if (lis != null && !p.Ghost && PlayerSync.TryGetAvatar(id, out var a) && a.HasData)
                {
                    var head = U.Alive(a.Head) ? a.Head.position : a.Root.transform.position + Vector3.up * a.CamY;
                    var dir = head - lis.position;
                    float d = dir.magnitude;
                    const float near = 1.5f;
                    float att = d <= near ? 1f : Mathf.Clamp01(1f - (d - near) / Mathf.Max(0.5f, range - near));
                    att *= att; // quieter sooner, closer to how a voice fades in a house
                    float pan = d > 0.3f ? Mathf.Clamp(lis.InverseTransformDirection(dir).x / d, -1f, 1f) * 0.8f : 0f;
                    float ang = (pan + 1f) * Mathf.PI / 4f;
                    l = vol * att * Mathf.Min(1f, Mathf.Cos(ang) * 1.4142f);
                    rr = vol * att * Mathf.Min(1f, Mathf.Sin(ang) * 1.4142f);
                }
                _out.SetGains(id, l, rr);
            }
        }

        public static List<string> Devices() => WinMM.InputDevices();
    }
}
