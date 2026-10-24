using System;

namespace GrannyMP
{
    public interface INetHandler
    {
        /// <summary>New connection (on the host: a client; on a client: the host = conn 0).</summary>
        void OnConnected(int conn);
        void OnDisconnected(int conn, string reason);
        void OnData(int conn, byte[] buf, int offset, int len);
    }

    /// <summary>Common transport interface: Steam P2P or Direct IP (UDP with a reliability layer).</summary>
    public interface ITransport
    {
        bool IsHost { get; }
        string Kind { get; }
        /// <summary>Text the host can copy and give to friends (SteamID / Lobby ID / IP:port).</summary>
        string JoinInfo { get; }
        INetHandler Handler { get; set; }
        void Send(int conn, byte[] buf, int len, bool reliable);
        void Poll();
        void Close(int conn);
        void Shutdown();
        /// <summary>Optional Steam id of the connection (0 if none).</summary>
        ulong SteamIdOf(int conn);
    }
}
