using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;

namespace GrannyMP
{
    /// <summary>
    /// Windows audio for the voice chat (winmm waveIn / waveOut) – independent of Steam and of Unity's audio, so it
    /// works with every connection type. 16 kHz mono 16-bit in, 16 kHz stereo out. The worker threads never touch
    /// Unity or Il2Cpp objects (only System.* code runs on them).
    /// </summary>
    internal static class WinMM
    {
        public const int Rate = 16000;
        public const int Frame = 320; // 20 ms
        internal const uint WAVE_MAPPER = 0xFFFFFFFF;
        internal const int WHDR_DONE = 1, HdrSize = 48;
        // WAVEHDR (x64): lpData 0, dwBufferLength 8, dwBytesRecorded 12, dwUser 16, dwFlags 24, dwLoops 28, lpNext 32, reserved 40
        internal const int OffData = 0, OffLen = 8, OffRecorded = 12, OffFlags = 24;

        [StructLayout(LayoutKind.Sequential)]
        internal struct WaveFormat { public ushort wFormatTag, nChannels; public uint nSamplesPerSec, nAvgBytesPerSec; public ushort nBlockAlign, wBitsPerSample, cbSize; }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct WaveInCaps
        {
            public ushort wMid, wPid; public uint vDriverVersion;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szPname;
            public uint dwFormats; public ushort wChannels, wReserved1;
        }

        [DllImport("winmm.dll")] static extern uint waveInGetNumDevs();
        [DllImport("winmm.dll", CharSet = CharSet.Unicode, EntryPoint = "waveInGetDevCapsW")] static extern uint waveInGetDevCaps(UIntPtr id, ref WaveInCaps caps, uint size);
        [DllImport("winmm.dll")] internal static extern uint waveInOpen(out IntPtr h, uint dev, ref WaveFormat fmt, IntPtr cb, IntPtr inst, uint flags);
        [DllImport("winmm.dll")] internal static extern uint waveInPrepareHeader(IntPtr h, IntPtr hdr, uint size);
        [DllImport("winmm.dll")] internal static extern uint waveInUnprepareHeader(IntPtr h, IntPtr hdr, uint size);
        [DllImport("winmm.dll")] internal static extern uint waveInAddBuffer(IntPtr h, IntPtr hdr, uint size);
        [DllImport("winmm.dll")] internal static extern uint waveInStart(IntPtr h);
        [DllImport("winmm.dll")] internal static extern uint waveInReset(IntPtr h);
        [DllImport("winmm.dll")] internal static extern uint waveInClose(IntPtr h);
        [DllImport("winmm.dll")] internal static extern uint waveOutOpen(out IntPtr h, uint dev, ref WaveFormat fmt, IntPtr cb, IntPtr inst, uint flags);
        [DllImport("winmm.dll")] internal static extern uint waveOutPrepareHeader(IntPtr h, IntPtr hdr, uint size);
        [DllImport("winmm.dll")] internal static extern uint waveOutUnprepareHeader(IntPtr h, IntPtr hdr, uint size);
        [DllImport("winmm.dll")] internal static extern uint waveOutWrite(IntPtr h, IntPtr hdr, uint size);
        [DllImport("winmm.dll")] internal static extern uint waveOutReset(IntPtr h);
        [DllImport("winmm.dll")] internal static extern uint waveOutClose(IntPtr h);

        internal static WaveFormat Format(ushort channels) => new WaveFormat
        {
            wFormatTag = 1, nChannels = channels, nSamplesPerSec = Rate, wBitsPerSample = 16,
            nBlockAlign = (ushort)(channels * 2), nAvgBytesPerSec = (uint)(Rate * channels * 2), cbSize = 0
        };

        /// <summary>Names of the recording devices (as Windows reports them, max. 31 characters).</summary>
        public static List<string> InputDevices()
        {
            var list = new List<string>();
            try
            {
                uint n = waveInGetNumDevs();
                for (uint i = 0; i < n && i < 32; i++)
                {
                    var caps = new WaveInCaps();
                    if (waveInGetDevCaps((UIntPtr)i, ref caps, (uint)Marshal.SizeOf<WaveInCaps>()) == 0) list.Add(caps.szPname ?? ("#" + i));
                    else list.Add("#" + i);
                }
            }
            catch { }
            return list;
        }

        /// <summary>A WAVEHDR followed by its data, in one block of unmanaged memory.</summary>
        internal static IntPtr AllocHeader(int bytes)
        {
            var p = Marshal.AllocHGlobal(HdrSize + bytes);
            for (int i = 0; i < HdrSize; i += 4) Marshal.WriteInt32(p, i, 0);
            Marshal.WriteIntPtr(p, OffData, p + HdrSize);
            Marshal.WriteInt32(p, OffLen, bytes);
            return p;
        }
    }

    /// <summary>Microphone capture: 20 ms frames of 16 kHz mono samples in <see cref="Frames"/>.</summary>
    internal sealed class MicCapture : IDisposable
    {
        const int NBuf = 10;
        IntPtr _h;
        readonly IntPtr[] _hdr = new IntPtr[NBuf];
        Thread _thread;
        volatile bool _run;
        public readonly ConcurrentQueue<short[]> Frames = new();
        public string Device { get; private set; }

        /// <summary>Opens the device with this name (empty or not found = the Windows default microphone).</summary>
        public static MicCapture Open(string name, out string error)
        {
            error = null;
            uint dev = WinMM.WAVE_MAPPER;
            string used = "";
            if (!string.IsNullOrEmpty(name))
            {
                var devs = WinMM.InputDevices();
                int i = devs.IndexOf(name);
                if (i >= 0) { dev = (uint)i; used = name; }
            }
            var m = new MicCapture { Device = used };
            var fmt = WinMM.Format(1);
            uint r = WinMM.waveInOpen(out m._h, dev, ref fmt, IntPtr.Zero, IntPtr.Zero, 0);
            if (r != 0) { error = "waveInOpen " + r; return null; }
            for (int i = 0; i < NBuf; i++)
            {
                m._hdr[i] = WinMM.AllocHeader(WinMM.Frame * 2);
                WinMM.waveInPrepareHeader(m._h, m._hdr[i], WinMM.HdrSize);
                WinMM.waveInAddBuffer(m._h, m._hdr[i], WinMM.HdrSize);
            }
            r = WinMM.waveInStart(m._h);
            if (r != 0) { error = "waveInStart " + r; m.Dispose(); return null; }
            m._run = true;
            m._thread = new Thread(m.Loop) { IsBackground = true, Name = "GrannyMP mic" };
            m._thread.Start();
            return m;
        }

        void Loop()
        {
            int next = 0;
            while (_run)
            {
                bool any = false;
                for (int k = 0; k < NBuf && _run; k++)
                {
                    var hdr = _hdr[next];
                    int flags = Marshal.ReadInt32(hdr, WinMM.OffFlags);
                    if ((flags & WinMM.WHDR_DONE) == 0) break;
                    int bytes = Marshal.ReadInt32(hdr, WinMM.OffRecorded);
                    if (bytes > 1)
                    {
                        var f = new short[bytes / 2];
                        Marshal.Copy(Marshal.ReadIntPtr(hdr, WinMM.OffData), f, 0, f.Length);
                        if (Frames.Count < 100) Frames.Enqueue(f);
                    }
                    Marshal.WriteInt32(hdr, WinMM.OffFlags, flags & ~WinMM.WHDR_DONE);
                    Marshal.WriteInt32(hdr, WinMM.OffRecorded, 0);
                    WinMM.waveInAddBuffer(_h, hdr, WinMM.HdrSize);
                    next = (next + 1) % NBuf;
                    any = true;
                }
                if (!any) Thread.Sleep(5);
            }
        }

        public void Dispose()
        {
            _run = false;
            try { _thread?.Join(200); } catch { }
            if (_h != IntPtr.Zero)
            {
                WinMM.waveInReset(_h);
                foreach (var h in _hdr) if (h != IntPtr.Zero) WinMM.waveInUnprepareHeader(_h, h, WinMM.HdrSize);
                WinMM.waveInClose(_h);
                _h = IntPtr.Zero;
            }
            for (int i = 0; i < NBuf; i++) if (_hdr[i] != IntPtr.Zero) { Marshal.FreeHGlobal(_hdr[i]); _hdr[i] = IntPtr.Zero; }
        }
    }

    /// <summary>
    /// Voice output: one jitter buffer per speaker, mixed on a worker thread with a left/right gain per speaker
    /// (distance fade + direction, set from the main thread) and played through the default output device.
    /// </summary>
    internal sealed class VoiceOutput : IDisposable
    {
        sealed class Stream
        {
            public readonly short[] Buf = new short[WinMM.Rate]; // 1 s
            public int Read, Count;
            public bool Playing;
            public float L = 1f, R = 1f;
        }

        const int NBuf = 8, Ahead = 4;               // at most 4 × 20 ms queued in Windows
        const int StartFill = WinMM.Frame * 3;        // 60 ms jitter buffer before a voice starts playing
        const int MaxFill = WinMM.Rate * 3 / 10;      // more than 300 ms buffered = drop the oldest part
        IntPtr _h;
        readonly IntPtr[] _hdr = new IntPtr[NBuf];
        readonly bool[] _queued = new bool[NBuf];
        readonly Dictionary<byte, Stream> _streams = new();
        readonly object _lock = new();
        Thread _thread;
        volatile bool _run;
        readonly int[] _accL = new int[WinMM.Frame], _accR = new int[WinMM.Frame];
        readonly short[] _mix = new short[WinMM.Frame * 2];

        public static VoiceOutput Open(out string error)
        {
            error = null;
            var o = new VoiceOutput();
            var fmt = WinMM.Format(2);
            uint r = WinMM.waveOutOpen(out o._h, WinMM.WAVE_MAPPER, ref fmt, IntPtr.Zero, IntPtr.Zero, 0);
            if (r != 0) { error = "waveOutOpen " + r; return null; }
            for (int i = 0; i < NBuf; i++)
            {
                o._hdr[i] = WinMM.AllocHeader(WinMM.Frame * 4);
                WinMM.waveOutPrepareHeader(o._h, o._hdr[i], WinMM.HdrSize);
            }
            o._run = true;
            o._thread = new Thread(o.Loop) { IsBackground = true, Name = "GrannyMP voice out" };
            o._thread.Start();
            return o;
        }

        public void Push(byte id, short[] pcm, int n)
        {
            lock (_lock)
            {
                if (!_streams.TryGetValue(id, out var s)) _streams[id] = s = new Stream();
                int len = s.Buf.Length;
                if (s.Count + n > MaxFill)
                {
                    int drop = Math.Min(s.Count, s.Count + n - MaxFill);
                    s.Read = (s.Read + drop) % len; s.Count -= drop;
                }
                int w = (s.Read + s.Count) % len;
                for (int i = 0; i < n; i++) { s.Buf[w] = pcm[i]; if (++w == len) w = 0; }
                s.Count += n;
                if (!s.Playing && s.Count >= StartFill) s.Playing = true;
            }
        }

        public void SetGains(byte id, float l, float r)
        {
            lock (_lock) { if (_streams.TryGetValue(id, out var s)) { s.L = l; s.R = r; } }
        }

        public List<byte> Ids()
        {
            lock (_lock) return new List<byte>(_streams.Keys);
        }

        public void Remove(byte id) { lock (_lock) _streams.Remove(id); }

        void Loop()
        {
            int next = 0;
            while (_run)
            {
                int queued = 0;
                for (int i = 0; i < NBuf; i++)
                {
                    if (_queued[i] && (Marshal.ReadInt32(_hdr[i], WinMM.OffFlags) & WinMM.WHDR_DONE) != 0) _queued[i] = false;
                    if (_queued[i]) queued++;
                }
                if (queued < Ahead && !_queued[next])
                {
                    Mix();
                    var hdr = _hdr[next];
                    Marshal.Copy(_mix, 0, Marshal.ReadIntPtr(hdr, WinMM.OffData), _mix.Length);
                    Marshal.WriteInt32(hdr, WinMM.OffFlags, Marshal.ReadInt32(hdr, WinMM.OffFlags) & ~WinMM.WHDR_DONE);
                    if (WinMM.waveOutWrite(_h, hdr, WinMM.HdrSize) == 0) _queued[next] = true;
                    next = (next + 1) % NBuf;
                    continue;
                }
                Thread.Sleep(4);
            }
        }

        void Mix()
        {
            Array.Clear(_accL, 0, _accL.Length); Array.Clear(_accR, 0, _accR.Length);
            lock (_lock)
            {
                foreach (var s in _streams.Values)
                {
                    if (!s.Playing) continue;
                    int n = Math.Min(WinMM.Frame, s.Count);
                    int len = s.Buf.Length;
                    int gl = (int)(s.L * 1024f), gr = (int)(s.R * 1024f);
                    for (int i = 0; i < n; i++)
                    {
                        int v = s.Buf[s.Read];
                        if (++s.Read == len) s.Read = 0;
                        _accL[i] += (v * gl) >> 10; _accR[i] += (v * gr) >> 10;
                    }
                    s.Count -= n;
                    if (s.Count < WinMM.Frame) s.Playing = s.Count > 0 && n == WinMM.Frame; // ran dry – buffer again
                }
            }
            for (int i = 0; i < WinMM.Frame; i++)
            {
                _mix[2 * i] = (short)Math.Clamp(_accL[i], -32768, 32767);
                _mix[2 * i + 1] = (short)Math.Clamp(_accR[i], -32768, 32767);
            }
        }

        public void Dispose()
        {
            _run = false;
            try { _thread?.Join(200); } catch { }
            if (_h != IntPtr.Zero)
            {
                WinMM.waveOutReset(_h);
                foreach (var h in _hdr) if (h != IntPtr.Zero) WinMM.waveOutUnprepareHeader(_h, h, WinMM.HdrSize);
                WinMM.waveOutClose(_h);
                _h = IntPtr.Zero;
            }
            for (int i = 0; i < NBuf; i++) if (_hdr[i] != IntPtr.Zero) { Marshal.FreeHGlobal(_hdr[i]); _hdr[i] = IntPtr.Zero; }
        }
    }

    /// <summary>IMA ADPCM (4 bits per sample) – every packet starts with the coder state, so a lost packet costs nothing more.</summary>
    internal static class Adpcm
    {
        static readonly int[] IndexTable = { -1, -1, -1, -1, 2, 4, 6, 8, -1, -1, -1, -1, 2, 4, 6, 8 };
        static readonly int[] StepTable =
        {
            7, 8, 9, 10, 11, 12, 13, 14, 16, 17, 19, 21, 23, 25, 28, 31, 34, 37, 41, 45, 50, 55, 60, 66, 73, 80, 88, 97, 107, 118,
            130, 143, 157, 173, 190, 209, 230, 253, 279, 307, 337, 371, 408, 449, 494, 544, 598, 658, 724, 796, 876, 963, 1060,
            1166, 1282, 1411, 1552, 1707, 1878, 2066, 2272, 2499, 2749, 3024, 3327, 3660, 4026, 4428, 4871, 5358, 5894, 6484,
            7132, 7845, 8630, 9493, 10442, 11487, 12635, 13899, 15289, 16818, 18500, 20350, 22385, 24623, 27086, 29794, 32767
        };

        static int Step(int code, ref int pred, ref int index)
        {
            int step = StepTable[index];
            int delta = step >> 3;
            if ((code & 4) != 0) delta += step;
            if ((code & 2) != 0) delta += step >> 1;
            if ((code & 1) != 0) delta += step >> 2;
            pred += (code & 8) != 0 ? -delta : delta;
            if (pred > 32767) pred = 32767; else if (pred < -32768) pred = -32768;
            index += IndexTable[code];
            if (index < 0) index = 0; else if (index > 88) index = 88;
            return pred;
        }

        /// <summary>Encodes <paramref name="n"/> samples (even) into n/2 bytes at <paramref name="off"/>.</summary>
        public static void Encode(short[] pcm, int n, byte[] dst, int off, ref int pred, ref int index)
        {
            for (int i = 0; i < n; i++)
            {
                int diff = pcm[i] - pred;
                int code = 0;
                if (diff < 0) { code = 8; diff = -diff; }
                int step = StepTable[index];
                if (diff >= step) { code |= 4; diff -= step; }
                step >>= 1;
                if (diff >= step) { code |= 2; diff -= step; }
                step >>= 1;
                if (diff >= step) code |= 1;
                Step(code, ref pred, ref index);
                if ((i & 1) == 0) dst[off + (i >> 1)] = (byte)code;
                else dst[off + (i >> 1)] |= (byte)(code << 4);
            }
        }

        public static void Decode(byte[] src, int off, int bytes, short[] dst, int pred, int index)
        {
            for (int i = 0; i < bytes; i++)
            {
                int b = src[off + i];
                dst[2 * i] = (short)Step(b & 15, ref pred, ref index);
                dst[2 * i + 1] = (short)Step(b >> 4, ref pred, ref index);
            }
        }
    }
}
