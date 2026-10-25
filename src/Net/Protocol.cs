using System;
using System.Text;
using UnityEngine;

namespace GrannyMP
{
    /// <summary>Session/game message types. The first byte of every packet.</summary>
    public enum Msg : byte
    {
        // --- sesja / lobby ---
        Hello = 1,          // C->H  version, name, steamId, password
        Welcome = 2,        // H->C  your id, settings, game state
        Deny = 3,           // H->C  reason for rejection
        PlayerList = 4,     // H->*  full player list (id, name, ready, ghost, ping)
        SetReady = 5,       // C->H  ready state
        Settings = 6,       // H->*  lobby settings (mode etc.)
        Chat = 7,           // *     czat
        Ping = 8,           // *
        Pong = 9,           // *
        Kick = 10,          // H->C
        Bye = 11,           // *     graceful disconnect
        Notify = 12,        // H->*  komunikat na ekranie
        SetModel = 14,      // C->H  my player model (man / woman)
        MenuOptions = 13,   // H->*  game options chosen in the main menu (theme, difficulty, extra locks, light sources)

        // --- przebieg gry ---
        StartGame = 20,     // H->*  scene + PlayerPrefs + difficulty
        SceneState = 21,    // C->H  which scene I am in / ready in scene
        LoadScene = 22,     // H->*  go to scene (e.g. EndScene, Menu)
        DayEnd = 23,        // H->*  end of day (caught) – everybody runs the sequence
        RequestDayEnd = 24, // C->H  client died locally (e.g. fall)
        Caught = 25,        // H->*  player X caught
        Ghost = 26,         // H->*  player X is a ghost / revived

        // --- players ---
        PlayerInput = 30,   // C->H  input + przewidziana pozycja (unreliable)
        PlayerStates = 31,  // H->*  authoritative positions of all players (unreliable)
        Correction = 32,    // H->C  korekta pozycji

        // --- world ---
        WorldFull = 40,     // H->C  full world snapshot (reliable)
        WorldDelta = 41,    // H->*  world changes (reliable)
        Proposal = 42,      // C->H  world changes made by a client (to be accepted)
        OwnClaim = 43,      // C->H  I take this object
        OwnResult = 44,     // H->C  zgoda/odmowa
        OwnRelease = 45,    // C->H  I release this object (final state)
        ItemPose = 46,      // owner->H->*  pose of an owned object (unreliable)
        AiPose = 47,        // H->*  pozycje AI (unreliable)
        Spawn = 48,         // H->*  new object (prefab clone)
        Despawn = 49,       // H->*
        Sound = 50,         // *     sound
        TriggerFwd = 51,    // C->H  noise/sensor trigger
        HostRpc = 52,       // C->H  run a method on the host (e.g. Granny got hit)
        DestroyReq = 53,    // C->H  I picked up / destroyed this object
        SpawnReq = 54,      // C->H  I dropped an item (local clone) – please spawn it for everyone
        Music = 55,         // H->*  state of AI music (chase music…)
        WorldResync = 57,   // C->H  send me the full world state again (after the local game finished its start-up)
        AudioState = 56,    // *     a world AudioSource started/stopped (alarms, loops, sound holders switched on)
        DayCount = 58,      // H->*  the current day number (sent regularly between days)
        GrannyVoice = 59,   // H->*  Granny says a line / laughs (GrannySounds)
        Voice = 60,         // *     proximity voice chat (Steam voice, compressed)
        Notice = 69,        // H->*  one of the game's notices about Granny was shown (NoticeSync)
        GrannyEyes = 68,    // H->*  Granny's eyes: closed (EyesLock) and their colour (red while hunting)
        FuelFill = 67,      // *     the fuel bar's level (FuelSync) – C->H, H->*
        Escape = 66,        // *     an escape cutscene started (EscapeSync) – C->H, H->*
        TrapState = 65,     // *     a freeze trap armed / closed (FreezeTrapSync) – C->H, H->*
        ActiveSet = 64,     // *     an object switched on/off by a player's item use (TagSync) – C->H, H->*
        TagSet = 63,        // *     a tag the game changed on an object (TagSync) – C->H, H->*
        StartGo = 62,       // H->*  everybody has loaded Granny's house – the game starts now (see GameFlow start gate)
        Pvp = 61,           // *     PvP: Granny's state (H->*), the Granny player's grab / voice / trap (G->H)
    }

    public sealed class NetWriter
    {
        public byte[] Buf = new byte[1024];
        public int Len;

        public NetWriter Reset(Msg m) { Len = 0; Byte((byte)m); return this; }
        public NetWriter Reset() { Len = 0; return this; }

        void Ensure(int n)
        {
            if (Len + n <= Buf.Length) return;
            var nb = new byte[Math.Max(Buf.Length * 2, Len + n)];
            Buffer.BlockCopy(Buf, 0, nb, 0, Len);
            Buf = nb;
        }

        public void Byte(byte v) { Ensure(1); Buf[Len++] = v; }
        public void Bool(bool v) => Byte(v ? (byte)1 : (byte)0);
        public void UShort(ushort v) { Ensure(2); Buf[Len++] = (byte)v; Buf[Len++] = (byte)(v >> 8); }
        public void Int(int v) => UInt((uint)v);
        public void UInt(uint v) { Ensure(4); Buf[Len++] = (byte)v; Buf[Len++] = (byte)(v >> 8); Buf[Len++] = (byte)(v >> 16); Buf[Len++] = (byte)(v >> 24); }
        public void ULong(ulong v) { UInt((uint)v); UInt((uint)(v >> 32)); }
        public unsafe void Float(float v) => UInt(*(uint*)&v);
        public void Vec3(Vector3 v) { Float(v.x); Float(v.y); Float(v.z); }
        public void Quat(Quaternion q)
        {
            // 4 x int16 – enough precision, 8 bytes
            q = Normalize(q);
            Short(q.x); Short(q.y); Short(q.z); Short(q.w);
        }
        void Short(float f) { var s = (short)Mathf.Clamp(Mathf.RoundToInt(f * 32767f), -32767, 32767); UShort((ushort)s); }
        public void Str(string s)
        {
            if (string.IsNullOrEmpty(s)) { UShort(0); return; }
            var b = Encoding.UTF8.GetBytes(s);
            var n = Math.Min(b.Length, 60000);
            UShort((ushort)n); Ensure(n); Buffer.BlockCopy(b, 0, Buf, Len, n); Len += n;
        }
        public void Bytes(byte[] b, int off, int n) { Ensure(n); Buffer.BlockCopy(b, off, Buf, Len, n); Len += n; }

        public static Quaternion Normalize(Quaternion q)
        {
            var m = Mathf.Sqrt(q.x * q.x + q.y * q.y + q.z * q.z + q.w * q.w);
            if (m < 1e-6f) return Quaternion.identity;
            return new Quaternion(q.x / m, q.y / m, q.z / m, q.w / m);
        }

        public byte[] ToArray() { var a = new byte[Len]; Buffer.BlockCopy(Buf, 0, a, 0, Len); return a; }
    }

    public sealed class NetReader
    {
        public byte[] Buf; public int Pos; public int End;
        public NetReader Set(byte[] b, int off, int len) { Buf = b; Pos = off; End = off + len; return this; }
        public bool HasData => Pos < End;
        public int Remaining => End - Pos;

        void Need(int n) { if (Pos + n > End) throw new IndexOutOfRangeException("packet too short"); }
        public byte Byte() { Need(1); return Buf[Pos++]; }
        public bool Bool() => Byte() != 0;
        public ushort UShort() { Need(2); var v = (ushort)(Buf[Pos] | (Buf[Pos + 1] << 8)); Pos += 2; return v; }
        public int Int() => (int)UInt();
        public uint UInt() { Need(4); var v = (uint)(Buf[Pos] | (Buf[Pos + 1] << 8) | (Buf[Pos + 2] << 16) | (Buf[Pos + 3] << 24)); Pos += 4; return v; }
        public ulong ULong() { ulong lo = UInt(); ulong hi = UInt(); return lo | (hi << 32); }
        public unsafe float Float() { var u = UInt(); return *(float*)&u; }
        public Vector3 Vec3() => new Vector3(Float(), Float(), Float());
        public Quaternion Quat()
        {
            float x = (short)UShort() / 32767f, y = (short)UShort() / 32767f, z = (short)UShort() / 32767f, w = (short)UShort() / 32767f;
            return NetWriter.Normalize(new Quaternion(x, y, z, w));
        }
        public string Str()
        {
            int n = UShort(); if (n == 0) return "";
            Need(n); var s = Encoding.UTF8.GetString(Buf, Pos, n); Pos += n; return s;
        }
    }
}
