using System;
using System.Collections.Generic;

namespace GrannyMP
{
    /// <summary>
    /// Transport Steam P2P (ISteamNetworking005). NAT traversal i relay robi Steam.
    /// Channel 0, send type: Reliable (2) or Unreliable (0).
    /// </summary>
    public sealed class SteamTransport : ITransport
    {
        public bool IsHost { get; }
        public string Kind => "Steam P2P";
        public INetHandler Handler { get; set; }

        readonly Dictionary<ulong, int> _byId = new();
        readonly Dictionary<int, ulong> _byConn = new();
        int _nextConn = 1;
        readonly ulong _hostId;
        readonly byte[] _rx = new byte[1 << 20];
        public ulong LobbyId;

        public string JoinInfo => LobbyId != 0 ? LobbyId.ToString() : (IsHost ? SteamNative.MySteamId.ToString() : _hostId.ToString());

        SteamTransport(bool host, ulong hostId)
        {
            IsHost = host; _hostId = hostId;
            SteamNative.OnP2PSessionRequest += OnSessionRequest;
            SteamNative.OnP2PConnectFail += OnConnectFail;
        }

        public static SteamTransport Host() => new SteamTransport(true, 0);

        public static SteamTransport Connect(ulong hostSteamId)
        {
            var t = new SteamTransport(false, hostSteamId);
            t._byId[hostSteamId] = 0; t._byConn[0] = hostSteamId;
            SteamNative.AcceptP2P(hostSteamId);
            return t;
        }

        /// <summary>Call after setting the Handler – the client "connects" to the host immediately.</summary>
        public void Begin() { if (!IsHost) Handler?.OnConnected(0); }

        void OnSessionRequest(ulong remote)
        {
            if (IsHost) { SteamNative.AcceptP2P(remote); return; } // authorisation happens in the Hello message
            if (remote == _hostId) SteamNative.AcceptP2P(remote);
        }

        void OnConnectFail(ulong remote, int err)
        {
            if (_byId.TryGetValue(remote, out var c))
            {
                Log.Warn($"Steam P2P: connection error with {remote} (code {err})");
                Drop(c, L.T("Steam P2P error ", "błąd Steam P2P ") + err);
            }
        }

        /// <summary>Host: send an empty packet to a lobby member – opens/accepts the P2P session.</summary>
        public void Probe(ulong id)
        {
            if (!IsHost || _byId.ContainsKey(id)) return;
            SteamNative.AcceptP2P(id);
            SteamNative.SendP2P(id, new byte[] { 0 }, 1, false);
        }

        public ulong SteamIdOf(int conn) => _byConn.TryGetValue(conn, out var id) ? id : 0;

        public void Send(int conn, byte[] buf, int len, bool reliable)
        {
            if (!_byConn.TryGetValue(conn, out var id)) return;
            if (!reliable && len > 1200) reliable = true; // limit pakietu unreliable
            if (!SteamNative.SendP2P(id, buf, len, reliable)) Log.Debug("SendP2P failed to " + id);
        }

        public void Poll()
        {
            int guard = 0;
            while (SteamNative.PacketAvailable(out var size) && guard++ < 2000)
            {
                if (!SteamNative.ReadP2P(_rx, out var n, out var from)) break;
                if (!_byId.TryGetValue(from, out var conn))
                {
                    if (!IsHost) continue; // a client accepts packets only from the host
                    conn = _nextConn++;
                    _byId[from] = conn; _byConn[conn] = from;
                    Handler?.OnConnected(conn);
                }
                Handler?.OnData(conn, _rx, 0, (int)n);
            }
        }

        void Drop(int conn, string reason)
        {
            if (!_byConn.TryGetValue(conn, out var id)) return;
            _byConn.Remove(conn); _byId.Remove(id);
            SteamNative.CloseP2P(id);
            Handler?.OnDisconnected(conn, reason);
        }

        public void Close(int conn)
        {
            if (!_byConn.TryGetValue(conn, out var id)) return;
            _byConn.Remove(conn); _byId.Remove(id);
            SteamNative.CloseP2P(id);
        }

        public void Shutdown()
        {
            foreach (var id in _byId.Keys) SteamNative.CloseP2P(id);
            _byId.Clear(); _byConn.Clear();
            SteamNative.OnP2PSessionRequest -= OnSessionRequest;
            SteamNative.OnP2PConnectFail -= OnConnectFail;
        }
    }
}
