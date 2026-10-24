using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;

namespace GrannyMP
{
    /// <summary>
    /// Direct IP: UDP + own reliability layer (sequence numbers, ACK, retransmission,
    /// ordered delivery, fragmentation of large messages). Used when Steam is unavailable.
    /// </summary>
    public sealed class UdpTransport : ITransport
    {
        const byte T_Connect = 1, T_Accept = 2, T_DataU = 3, T_DataR = 4, T_Ack = 5, T_Keep = 6, T_Disc = 7, T_Reject = 8;
        const int MaxDatagram = 1200, FragSize = 1100, Header = 3;
        const double PeerTimeout = 15.0;

        public bool IsHost { get; }
        public string Kind => "Direct IP (UDP)";
        public INetHandler Handler { get; set; }
        public string JoinInfo { get; private set; } = "";

        sealed class Unacked { public byte[] Data; public double LastSend, FirstSend; public int Tries; }
        sealed class Peer
        {
            public int Conn; public IPEndPoint Ep; public double LastRecv, LastSend;
            public ushort SendSeq; public ushort RecvNext;
            public readonly Dictionary<ushort, Unacked> Unacked = new();
            public readonly Dictionary<ushort, byte[]> OutOfOrder = new();
            public readonly List<ushort> PendingAcks = new();
            public MemoryStream Assembly;
            public double Rtt = 0.15;
            public bool Accepted;
            public uint Nonce;
        }

        readonly Socket _sock;
        readonly Thread _thread;
        volatile bool _running = true;
        readonly ConcurrentQueue<(IPEndPoint ep, byte[] data)> _inbox = new();
        readonly Dictionary<string, Peer> _byEp = new();
        readonly Dictionary<int, Peer> _byConn = new();
        int _nextConn = 1;
        readonly Stopwatch _clock = Stopwatch.StartNew();
        double Now => _clock.Elapsed.TotalSeconds;

        // client
        readonly IPEndPoint _hostEp;
        double _connectStarted, _lastConnectSend;
        readonly uint _nonce = (uint)new Random().Next();
        public string FailReason;

        UdpTransport(bool host, int port, IPEndPoint hostEp)
        {
            IsHost = host; _hostEp = hostEp;
            _sock = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            try { _sock.IOControl(-1744830452, new byte[] { 0 }, null); } catch { } // SIO_UDP_CONNRESET off (Windows)
            _sock.ReceiveBufferSize = 1 << 20; _sock.SendBufferSize = 1 << 20;
            _sock.Bind(new IPEndPoint(IPAddress.Any, host ? port : 0));
            _thread = new Thread(RecvLoop) { IsBackground = true, Name = "GrannyMP-UDP" };
            _thread.Start();
            if (host)
            {
                var ips = LocalIPs();
                JoinInfo = string.Join(", ", ips.Select(i => i + ":" + port));
            }
            else
            {
                JoinInfo = hostEp.ToString();
                _connectStarted = Now;
                var p = AddPeer(hostEp, 0);
                p.Nonce = _nonce;
            }
        }

        public static UdpTransport Host(int port) => new UdpTransport(true, port, null);
        public static UdpTransport Connect(IPEndPoint ep) => new UdpTransport(false, 0, ep);

        public static List<string> LocalIPs()
        {
            var list = new List<string>();
            try
            {
                foreach (var a in Dns.GetHostAddresses(Dns.GetHostName()))
                    if (a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a)) list.Add(a.ToString());
            }
            catch { }
            if (list.Count == 0) list.Add("127.0.0.1");
            return list;
        }

        public static bool TryParseEndpoint(string s, int defPort, out IPEndPoint ep)
        {
            ep = null; s = (s ?? "").Trim();
            if (s.Length == 0) return false;
            string host = s; int port = defPort;
            var i = s.LastIndexOf(':');
            if (i > 0 && s.IndexOf(':') == i) { host = s.Substring(0, i); if (!int.TryParse(s.Substring(i + 1), out port)) return false; }
            if (!IPAddress.TryParse(host, out var ip))
            {
                try { ip = Dns.GetHostAddresses(host).FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork); } catch { }
                if (ip == null) return false;
            }
            ep = new IPEndPoint(ip, port);
            return true;
        }

        void RecvLoop()
        {
            var buf = new byte[65536];
            EndPoint any = new IPEndPoint(IPAddress.Any, 0);
            while (_running)
            {
                try
                {
                    int n = _sock.ReceiveFrom(buf, ref any);
                    if (n < Header || buf[0] != (byte)'G' || buf[1] != (byte)'M') continue;
                    var d = new byte[n]; Buffer.BlockCopy(buf, 0, d, 0, n);
                    _inbox.Enqueue(((IPEndPoint)any, d));
                }
                catch (SocketException) { if (!_running) break; }
                catch (ObjectDisposedException) { break; }
                catch (Exception e) { Log.Debug("UDP recv: " + e.Message); }
            }
        }

        Peer AddPeer(IPEndPoint ep, int conn)
        {
            var p = new Peer { Conn = conn, Ep = ep, LastRecv = Now };
            _byEp[ep.ToString()] = p; _byConn[conn] = p;
            return p;
        }

        void Raw(Peer p, byte type, byte[] payload, int off, int len)
        {
            var d = new byte[Header + len];
            d[0] = (byte)'G'; d[1] = (byte)'M'; d[2] = type;
            if (len > 0) Buffer.BlockCopy(payload, off, d, Header, len);
            RawSend(p.Ep, d);
            p.LastSend = Now;
        }

        void RawSend(IPEndPoint ep, byte[] d)
        {
            try { _sock.SendTo(d, ep); } catch (Exception e) { Log.Debug("UDP send: " + e.Message); }
        }

        public ulong SteamIdOf(int conn) => 0;

        public void Send(int conn, byte[] buf, int len, bool reliable)
        {
            if (!_byConn.TryGetValue(conn, out var p) || !p.Accepted) return;
            if (!reliable && len + Header <= MaxDatagram) { Raw(p, T_DataU, buf, 0, len); return; }
            int frags = Math.Max(1, (len + FragSize - 1) / FragSize);
            if (frags > 255) { Log.Error($"UDP: message too large ({len} B)"); return; }
            for (int f = 0; f < frags; f++)
            {
                int off = f * FragSize, n = Math.Min(FragSize, len - off);
                var d = new byte[Header + 4 + n];
                d[0] = (byte)'G'; d[1] = (byte)'M'; d[2] = T_DataR;
                ushort seq = p.SendSeq++;
                d[3] = (byte)seq; d[4] = (byte)(seq >> 8); d[5] = (byte)f; d[6] = (byte)frags;
                Buffer.BlockCopy(buf, off, d, 7, n);
                p.Unacked[seq] = new Unacked { Data = d, LastSend = Now, FirstSend = Now, Tries = 1 };
                RawSend(p.Ep, d);
            }
            p.LastSend = Now;
        }

        public void Poll()
        {
            double now = Now;
            // client: handshake
            if (!IsHost && _byConn.TryGetValue(0, out var hp) && !hp.Accepted)
            {
                if (now - _lastConnectSend > 0.5)
                {
                    _lastConnectSend = now;
                    var w = new NetWriter(); w.UInt(_nonce); w.Int(Plugin.ProtocolVersion);
                    Raw(hp, T_Connect, w.Buf, 0, w.Len);
                }
                if (now - _connectStarted > 12) { FailReason = L.T("No answer from the host (check IP/port and firewall).", "Brak odpowiedzi hosta (sprawdź IP/port i firewall)."); Drop(hp, FailReason); return; }
            }

            int guard = 0;
            while (guard++ < 5000 && _inbox.TryDequeue(out var pkt)) Handle(pkt.ep, pkt.data, now);

            foreach (var p in _byConn.Values.ToList())
            {
                if (!p.Accepted) continue;
                // ACK
                if (p.PendingAcks.Count > 0)
                {
                    int i = 0;
                    while (i < p.PendingAcks.Count)
                    {
                        int n = Math.Min(250, p.PendingAcks.Count - i);
                        var d = new byte[1 + n * 2]; d[0] = (byte)n;
                        for (int k = 0; k < n; k++) { var s = p.PendingAcks[i + k]; d[1 + k * 2] = (byte)s; d[2 + k * 2] = (byte)(s >> 8); }
                        Raw(p, T_Ack, d, 0, d.Length);
                        i += n;
                    }
                    p.PendingAcks.Clear();
                }
                // retransmisja
                double rto = Math.Clamp(p.Rtt * 1.8, 0.08, 1.0);
                foreach (var kv in p.Unacked)
                {
                    var u = kv.Value;
                    if (now - u.LastSend > rto * Math.Min(u.Tries, 4)) { u.LastSend = now; u.Tries++; RawSend(p.Ep, u.Data); }
                }
                if (now - p.LastSend > 1.0) Raw(p, T_Keep, Array.Empty<byte>(), 0, 0);
                if (now - p.LastRecv > PeerTimeout) Drop(p, L.T("timed out", "przekroczony czas oczekiwania"));
            }
        }

        void Handle(IPEndPoint ep, byte[] d, double now)
        {
            byte type = d[2];
            _byEp.TryGetValue(ep.ToString(), out var p);

            if (type == T_Connect)
            {
                if (!IsHost) return;
                if (d.Length < Header + 8) return;
                var r = new NetReader().Set(d, Header, d.Length - Header);
                var nonce = r.UInt(); var proto = r.Int();
                if (proto != Plugin.ProtocolVersion)
                {
                    var w = new NetWriter(); w.Str(L.Pair($"Mod version mismatch (host: {Plugin.ModVersion}/p{Plugin.ProtocolVersion}, you: p{proto}).", $"Niezgodna wersja moda (host: {Plugin.ModVersion}/p{Plugin.ProtocolVersion}, ty: p{proto})."));
                    var tmp = new Peer { Ep = ep }; Raw(tmp, T_Reject, w.Buf, 0, w.Len);
                    return;
                }
                if (p == null)
                {
                    p = AddPeer(ep, _nextConn++);
                    p.Nonce = nonce; p.Accepted = true;
                    Handler?.OnConnected(p.Conn);
                }
                var aw = new NetWriter(); aw.UInt(nonce);
                Raw(p, T_Accept, aw.Buf, 0, aw.Len);
                p.LastRecv = now;
                return;
            }
            if (p == null) return;
            p.LastRecv = now;

            switch (type)
            {
                case T_Accept:
                    if (!IsHost && !p.Accepted) { p.Accepted = true; Handler?.OnConnected(0); }
                    break;
                case T_Reject:
                    {
                        var r = new NetReader().Set(d, Header, d.Length - Header);
                        FailReason = r.Remaining > 2 ? L.Show(r.Str()) : L.T("rejected", "odrzucono");
                        Drop(p, FailReason);
                        break;
                    }
                case T_Disc:
                    Drop(p, L.T("disconnected by the other side", "rozłączono przez drugą stronę"), false);
                    break;
                case T_Keep: break;
                case T_DataU:
                    if (p.Accepted) Handler?.OnData(p.Conn, d, Header, d.Length - Header);
                    break;
                case T_Ack:
                    {
                        int n = d[3];
                        for (int k = 0; k < n && 4 + k * 2 + 1 < d.Length; k++)
                        {
                            ushort s = (ushort)(d[4 + k * 2] | (d[5 + k * 2] << 8));
                            if (p.Unacked.TryGetValue(s, out var u))
                            {
                                if (u.Tries == 1) p.Rtt = p.Rtt * 0.85 + (now - u.FirstSend) * 0.15;
                                p.Unacked.Remove(s);
                            }
                        }
                        break;
                    }
                case T_DataR:
                    {
                        if (!p.Accepted || d.Length < 7) return;
                        ushort seq = (ushort)(d[3] | (d[4] << 8));
                        p.PendingAcks.Add(seq);
                        short diff = (short)(seq - p.RecvNext);
                        if (diff < 0) return;                     // duplikat
                        if (diff > 0) { if (diff < 4096) p.OutOfOrder[seq] = d; return; }
                        Deliver(p, d);
                        p.RecvNext++;
                        while (p.OutOfOrder.TryGetValue(p.RecvNext, out var nd))
                        {
                            p.OutOfOrder.Remove(p.RecvNext);
                            Deliver(p, nd);
                            p.RecvNext++;
                            if (!_byConn.ContainsKey(p.Conn)) return;
                        }
                        break;
                    }
            }
        }

        void Deliver(Peer p, byte[] d)
        {
            int fi = d[5], fc = d[6];
            if (fc <= 1) { Handler?.OnData(p.Conn, d, 7, d.Length - 7); return; }
            if (fi == 0) p.Assembly = new MemoryStream();
            if (p.Assembly == null) return;
            p.Assembly.Write(d, 7, d.Length - 7);
            if (fi == fc - 1)
            {
                var all = p.Assembly.ToArray(); p.Assembly = null;
                Handler?.OnData(p.Conn, all, 0, all.Length);
            }
        }

        void Drop(Peer p, string reason, bool notify = true)
        {
            if (!_byConn.Remove(p.Conn)) return;
            _byEp.Remove(p.Ep.ToString());
            if (notify) try { Raw(p, T_Disc, Array.Empty<byte>(), 0, 0); } catch { }
            Handler?.OnDisconnected(p.Conn, reason);
        }

        public void Close(int conn)
        {
            if (!_byConn.TryGetValue(conn, out var p)) return;
            try { Raw(p, T_Disc, Array.Empty<byte>(), 0, 0); } catch { }
            _byConn.Remove(conn); _byEp.Remove(p.Ep.ToString());
        }

        public void Shutdown()
        {
            foreach (var p in _byConn.Values.ToList()) try { Raw(p, T_Disc, Array.Empty<byte>(), 0, 0); } catch { }
            _byConn.Clear(); _byEp.Clear();
            _running = false;
            try { _sock.Close(); } catch { }
        }
    }
}
