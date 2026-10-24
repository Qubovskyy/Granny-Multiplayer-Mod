using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEngine;

namespace GrannyMP
{
    /// <summary>Network traffic counters (per message type, per second).</summary>
    internal static class NetStats
    {
        static readonly Dictionary<Msg, (int n, int bytes)> _out = new(), _in = new();
        static float _windowStart;
        public static int OutBps, InBps, OutPps, InPps;
        public static string TopOut = "", TopIn = "";

        public static void Sent(Msg m, int len) { _out.TryGetValue(m, out var v); _out[m] = (v.n + 1, v.bytes + len); }
        public static void Recv(Msg m, int len) { _in.TryGetValue(m, out var v); _in[m] = (v.n + 1, v.bytes + len); }

        public static void Update()
        {
            float now = Time.unscaledTime;
            if (now - _windowStart < 1f) return;
            float dt = Mathf.Max(0.001f, now - _windowStart);
            _windowStart = now;
            OutBps = (int)(_out.Values.Sum(v => v.bytes) / dt); OutPps = (int)(_out.Values.Sum(v => v.n) / dt);
            InBps = (int)(_in.Values.Sum(v => v.bytes) / dt); InPps = (int)(_in.Values.Sum(v => v.n) / dt);
            TopOut = string.Join(", ", _out.OrderByDescending(k => k.Value.bytes).Take(4).Select(k => $"{k.Key} {k.Value.n}"));
            TopIn = string.Join(", ", _in.OrderByDescending(k => k.Value.bytes).Take(4).Select(k => $"{k.Key} {k.Value.n}"));
            _out.Clear(); _in.Clear();
        }
    }

    /// <summary>Periodic status in the log (VerboseLogging).</summary>
    internal static class DebugTools
    {
        static float _nextLog;

        public static void Update()
        {
            NetStats.Update();
            if (Cfg.DebugLog.Value && Session.Active && Time.unscaledTime >= _nextLog)
            {
                _nextLog = Time.unscaledTime + 10f;
                Log.Debug($"status: {Session.State} {(Session.IsHost ? "host" : "client")} scene={GameFlow.CurrentScene} transition={GameFlow.InTransition} " +
                          $"net out {NetStats.OutBps} B/s ({NetStats.TopOut}) in {NetStats.InBps} B/s ({NetStats.TopIn})");
                if (World.Registered) Log.Debug("status: " + World.Summary());
                Log.Debug("status: players " + PlayersLine());
            }
        }

        static string PlayersLine() => string.Join(" | ", Session.Players.Values.OrderBy(p => p.Id).Select(p =>
        {
            var line = $"{p.Name}#{p.Id} {(p.IsLocal ? "me" : p.PingMs + "ms")} scene={p.Scene}{(p.SceneReady ? "(ready)" : "")}{(p.Ghost ? " SPECTATOR" : "")}";
            if (!p.IsLocal && PlayerSync.TryGetState(p.Id, out var s)) line += $" pos={s.Pos.x:F1},{s.Pos.y:F1},{s.Pos.z:F1} flags={s.Flags}";
            return line;
        }));
    }
}
