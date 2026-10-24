using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace GrannyMP
{
    public sealed class NetPlayer
    {
        public byte Id;
        public string Name = "?";
        public ulong SteamId;
        public bool Ready;
        public int Conn = -1;
        public int PingMs;
        public string Scene = "";
        public bool SceneReady;
        public bool Ghost;
        /// <summary>Player model (file name in the models folder: male, female or a custom one).</summary>
        public string Model = PlayerModels.Default;
        public float LastRecv;
        public bool IsLocal => Id == Session.LocalId;
        public bool IsHost => Id == 0;
        public Color Color => Session.ColorOf(Id);
    }

    public enum SessionState { Offline, Connecting, Connected }

    /// <summary>
    /// Session layer: lobby, players, ready state, settings, chat, ping, message routing.
    /// The host is authoritative (id 0); clients have ids 1..8.
    /// </summary>
    public static class Session
    {
        public static SessionState State { get; private set; } = SessionState.Offline;
        public static ITransport Transport { get; private set; }
        public static bool IsHost => State != SessionState.Offline && Transport != null && Transport.IsHost;
        public static bool IsClient => State != SessionState.Offline && Transport != null && !Transport.IsHost;
        public static bool Active => State == SessionState.Connected;
        public static byte LocalId { get; private set; }
        public static readonly Dictionary<byte, NetPlayer> Players = new();
        public static NetPlayer Local => Players.TryGetValue(LocalId, out var p) ? p : null;

        // lobby settings (host)
        public static CoopMode Mode = CoopMode.SharedDays;
        public static bool JoinInProgress = true;
        /// <summary>Granny finding a player in a hiding spot also catches the others hiding in the same spot.</summary>
        public static bool SpotCatchAll = true;
        /// <summary>Only one player per hiding spot (bed, coffin, car).</summary>
        public static bool SpotSingle = false;
        /// <summary>PvP: the player who plays Granny (255 = nobody – the AI plays her).</summary>
        public static byte GrannyId = 255;
        /// <summary>PvP is on (on top of the co-op rules: shared days / last survivor).</summary>
        public static bool PvpOn;
        /// <summary>PvP: who the host wants as Granny (255 = a random player).</summary>
        public static byte GrannyChoice = 255;
        /// <summary>Host option: the players cannot use their own field of view.</summary>
        public static bool LockFov;
        /// <summary>Host's debug panel: nobody can be caught or die.</summary>
        public static bool GodMode;
        public static bool HostInGame;      // the host is in the game scene
        public static string HostScene = "";

        public static string Status = "Offline";
        public static string LastError = "";
        public static ulong SteamLobby;
        public static readonly List<(float t, string text, Color c)> ChatLog = new();
        public static readonly List<(float until, string text)> Notices = new();

        static readonly Dictionary<Msg, Action<NetPlayer, NetReader>> _handlers = new();
        static readonly Dictionary<int, byte> _connToId = new();
        static readonly Dictionary<int, float> _pendingConns = new();
        static readonly NetReader _reader = new();
        static readonly NetWriter _w = new();
        static float _lastPing, _lastList, _lastHostRecv, _lastProbe;
        static ulong _pendingCreateCall, _pendingJoinCall;
        static readonly byte[] _callBuf = new byte[64];
        static bool _handlersRegistered;
        static string _pendingPassword = "";

        /// <summary>A player's colour (name tags, chat, lists) – the same white for everybody.</summary>
        public static Color ColorOf(byte id) => Color.white;

        public static NetWriter W(Msg m) => _w.Reset(m);
        public static void On(Msg m, Action<NetPlayer, NetReader> h) => _handlers[m] = h;

        public static string LocalName
        {
            get
            {
                var n = Cfg.PlayerName.Value;
                if (string.IsNullOrWhiteSpace(n) && SteamNative.TryInit()) n = SteamNative.MyName;
                if (string.IsNullOrWhiteSpace(n)) n = Environment.UserName;
                if (string.IsNullOrWhiteSpace(n)) n = "Player";
                return n.Length > 24 ? n.Substring(0, 24) : n;
            }
        }

        static void EnsureHandlers()
        {
            if (_handlersRegistered) return;
            _handlersRegistered = true;
            On(Msg.Hello, HandleHello);
            On(Msg.Welcome, HandleWelcome);
            On(Msg.Deny, (p, r) =>
            {
                var why = r.Str(); var target = SteamLobby != 0 ? SteamLobby.ToString() : "";
                LastError = L.T("Denied: ", "Odmowa: ") + L.Show(why.StartsWith("PASSWORD") ? why.Substring(8) : why); Shutdown(LastError);
                if (why.StartsWith("PASSWORD")) LobbyUI.OpenWithJoin(target, L.T("Wrong or missing password – type it and click Join.", "Złe lub brak hasła – wpisz hasło i kliknij Dołącz."));
            });
            On(Msg.Kick, (p, r) => { LastError = L.T("Kicked from the lobby: ", "Wyrzucono z lobby: ") + L.Show(r.Str()); Shutdown(LastError); });
            On(Msg.Bye, (p, r) => { if (IsClient) Shutdown(L.T("The host closed the lobby", "Host zamknął lobby")); else if (p != null) RemovePlayer(p.Id, L.Pair("left", "wyszedł")); });
            On(Msg.PlayerList, HandlePlayerList);
            // the ready check is only for the lobby – once the game runs nobody can become "not ready"
            On(Msg.SetReady, (p, r) => { if (IsHost && p != null) { bool ready = r.Bool(); if (HostInGame) return; p.Ready = ready; BroadcastPlayerList(); } });
            On(Msg.Settings, (p, r) => { if (IsClient) ReadSettings(r); });
            On(Msg.SetModel, (p, r) => { if (IsHost && p != null) { var m = r.Str(); p.Model = string.IsNullOrWhiteSpace(m) || m.Length > 48 ? PlayerModels.Default : m; BroadcastPlayerList(); } });
            On(Msg.Chat, HandleChat);
            On(Msg.Notify, (p, r) => { if (IsClient) Notify(L.Show(r.Str()), false); });
            On(Msg.Ping, (p, r) => { var t = r.Float(); var w = W(Msg.Pong); w.Float(t); if (IsClient) SendToHost(w, false); });
            On(Msg.Pong, (p, r) => { if (IsHost && p != null) p.PingMs = (int)((Time.realtimeSinceStartup - r.Float()) * 1000f); });
            GameFlow.RegisterHandlers();
            World.RegisterHandlers();
            TagSync.RegisterHandlers();
            FreezeTrapSync.RegisterHandlers();
            EscapeSync.RegisterHandlers();
            FuelSync.RegisterHandlers();
            PlayerSync.RegisterHandlers();
            Pvp.RegisterHandlers();
        }

        // ================= hosting / joining =================
        public static void HostSteam()
        {
            if (State != SessionState.Offline) return;
            EnsureHandlers();
            if (!SteamNative.TryInit()) { LastError = L.T("Steam unavailable: ", "Steam niedostępny: ") + SteamNative.LastError + L.T(". Use Direct IP.", ". Użyj Direct IP."); return; }
            var t = SteamTransport.Host();
            StartAsHost(t);
            _pendingCreateCall = SteamNative.CreateLobby(Mathf.Clamp(Cfg.SteamLobbyType.Value, 0, 2), Cfg.MaxPlayers.Value);
            Status = L.T("Creating Steam lobby...", "Tworzenie lobby Steam...");
        }

        public static void HostDirect(int port)
        {
            if (State != SessionState.Offline) return;
            EnsureHandlers();
            try { StartAsHost(UdpTransport.Host(port)); Status = L.T("Direct IP host on port ", "Host Direct IP na porcie ") + port; }
            catch (Exception e) { LastError = L.T("Cannot open port ", "Nie można otworzyć portu ") + port + ": " + e.Message; Shutdown(LastError); }
        }

        static void StartAsHost(ITransport t)
        {
            Transport = t; t.Handler = new Handler();
            State = SessionState.Connected;
            LocalId = 0;
            Players.Clear(); _connToId.Clear(); _pendingConns.Clear();
            Mode = Cfg.DefaultMode.Value; JoinInProgress = Cfg.AllowJoinInProgress.Value; SpotCatchAll = Cfg.SpotCatchAll.Value; SpotSingle = Cfg.SpotSingle.Value; GrannyId = 255;
            PvpOn = Mode == CoopMode.PvP; if (PvpOn) Mode = CoopMode.LastSurvivor;
            GrannyChoice = 255; LockFov = Cfg.LockFov.Value;
            Players[0] = new NetPlayer { Id = 0, Name = LocalName, SteamId = SteamNative.Ready ? SteamNative.MySteamId : 0, Ready = true, Conn = -1, Model = Cfg.Character.Value };
            LastError = "";
            Status = "Host: " + t.Kind;
            GameFlow.OnSessionStarted();
            Log.Info("Hosting lobby (" + t.Kind + ")");
        }

        /// <summary>Join: Steam Lobby ID, host SteamID or IP[:port].</summary>
        public static void Join(string target, string password)
        {
            if (State != SessionState.Offline) return;
            EnsureHandlers();
            LastError = ""; _pendingPassword = password ?? "";
            target = (target ?? "").Trim();
            if (ulong.TryParse(target, out var id) && id > 0xFFFFFFFFUL)
            {
                if (!SteamNative.TryInit()) { LastError = L.T("Steam unavailable: ", "Steam niedostępny: ") + SteamNative.LastError; return; }
                if (SteamNative.IsLobbyId(id))
                {
                    _pendingJoinCall = SteamNative.JoinLobby(id);
                    State = SessionState.Connecting; Status = L.T("Joining Steam lobby...", "Dołączanie do lobby Steam...");
                    SteamLobby = id;
                    return;
                }
                ConnectSteam(id);
                return;
            }
            if (!UdpTransport.TryParseEndpoint(target, Cfg.Port.Value, out var ep)) { LastError = L.T("Invalid address. Enter a Lobby ID, SteamID or IP:port.", "Nieprawidłowy adres. Podaj Lobby ID, SteamID albo IP:port."); return; }
            try
            {
                var t = UdpTransport.Connect(ep);
                Transport = t; t.Handler = new Handler();
                State = SessionState.Connecting; Status = L.T("Connecting to ", "Łączenie z ") + ep + "...";
                _lastHostRecv = Time.realtimeSinceStartup;
            }
            catch (Exception e) { LastError = e.Message; Shutdown(e.Message); }
        }

        static void ConnectSteam(ulong hostId)
        {
            if (hostId == SteamNative.MySteamId) { LastError = L.T("That is your own SteamID.", "To jest Twoje własne SteamID."); Shutdown(LastError); return; }
            var t = SteamTransport.Connect(hostId);
            Transport = t; t.Handler = new Handler();
            State = SessionState.Connecting; Status = L.T("Connecting via Steam to ", "Łączenie przez Steam z ") + SteamNative.NameOf(hostId) + "...";
            _lastHostRecv = Time.realtimeSinceStartup;
            t.Begin();
        }

        public static void Leave() => Shutdown(L.T("Left the lobby", "Opuszczono lobby"));

        public static void Shutdown(string reason)
        {
            if (State == SessionState.Offline && Transport == null) return;
            try
            {
                if (Transport != null)
                {
                    var w = W(Msg.Bye);
                    if (IsHost) foreach (var p in Players.Values.Where(p => p.Conn >= 0)) Xmit(p.Conn, w.Buf, w.Len, true);
                    else Xmit(0, w.Buf, w.Len, true);
                    Transport.Poll();
                    Transport.Shutdown();
                }
            }
            catch (Exception e) { Log.Debug("shutdown: " + e.Message); }
            if (SteamLobby != 0) { try { SteamNative.LeaveLobby(SteamLobby); SteamNative.ClearRichPresence(); } catch { } }
            SteamLobby = 0; _pendingCreateCall = 0; _pendingJoinCall = 0;
            Transport = null;
            State = SessionState.Offline;
            HostInGame = false; GrannyId = 255; PvpOn = false; GrannyChoice = 255; LockFov = false;
            Players.Clear(); _connToId.Clear(); _pendingConns.Clear();
            Status = "Offline";
            if (!string.IsNullOrEmpty(reason)) { Log.Info("Session ended: " + reason); Notify(reason, false); }
            try { GameFlow.OnSessionEnded(); } catch (Exception e) { Log.Error(e.ToString()); }
        }

        // ================= loop =================
        public static void Update()
        {
            try { if (SteamNative.Ready) SteamNative.Pump(); } catch (Exception e) { Log.Error(e.ToString()); }
            PollSteamCalls();
            if (Transport == null) return;
            try { Transport.Poll(); } catch (Exception e) { Log.Error("Transport.Poll: " + e); }
            if (Transport == null) return;
            float now = Time.realtimeSinceStartup;

            if (IsHost)
            {
                if (now - _lastPing > 1f)
                {
                    _lastPing = now;
                    var w = W(Msg.Ping); w.Float(now);
                    BroadcastRaw(w, false);
                    foreach (var p in Players.Values.ToList())
                        if (p.Conn >= 0 && now - p.LastRecv > 20f) { Kick(p.Id, L.Pair("timeout", "przekroczony czas")); }
                    foreach (var kv in _pendingConns.ToList())
                        if (now - kv.Value > 15f) { Transport.Close(kv.Key); _pendingConns.Remove(kv.Key); }
                }
                if (now - _lastList > 3f) { _lastList = now; BroadcastPlayerList(); }
                if (SteamLobby != 0 && Transport is SteamTransport st)
                {
                    st.LobbyId = SteamLobby;
                    // proactively open P2P sessions with lobby members (in case the P2PSessionRequest callback never arrives)
                    if (now - _lastProbe > 2f)
                    {
                        _lastProbe = now;
                        try
                        {
                            int n = SteamNative.LobbyMemberCount(SteamLobby);
                            for (int i = 0; i < n; i++) { var m = SteamNative.LobbyMember(SteamLobby, i); if (m != 0 && m != SteamNative.MySteamId) st.Probe(m); }
                        }
                        catch (Exception e) { Log.Debug("probe: " + e.Message); }
                    }
                }
            }
            else if (now - _lastHostRecv > 20f) { LastError = L.T("Lost connection to the host.", "Utracono połączenie z hostem."); Shutdown(LastError); }

            Notices.RemoveAll(n => n.until < now);
        }

        static void PollSteamCalls()
        {
            if (!SteamNative.Ready) return;
            if (_pendingCreateCall != 0 && SteamNative.PollCall(_pendingCreateCall, 513, 16, _callBuf, out var failed))
            {
                _pendingCreateCall = 0;
                int result = BitConverter.ToInt32(_callBuf, 0);
                ulong lobby = BitConverter.ToUInt64(_callBuf, 8);
                if (failed || result != 1 || lobby == 0) { LastError = L.T("Could not create a Steam lobby (code " + result + "). Friends can still join with your SteamID.", "Nie udało się utworzyć lobby Steam (kod " + result + "). Znajomi mogą dołączyć przez Twoje SteamID."); Status = L.T("Steam host (no lobby)", "Host Steam (bez lobby)"); }
                else
                {
                    SteamLobby = lobby;
                    SteamNative.SetLobbyData(lobby, "grannymp", Plugin.ModVersion);
                    SteamNative.SetLobbyData(lobby, "proto", Plugin.ProtocolVersion.ToString());
                    SteamNative.SetLobbyData(lobby, "host", SteamNative.MySteamId.ToString());
                    SteamNative.SetLobbyData(lobby, "name", LocalName);
                    SteamNative.SetLobbyData(lobby, "pw", string.IsNullOrEmpty(Cfg.Password.Value) ? "0" : "1");
                    SteamNative.SetLobbyMemberLimit(lobby, Cfg.MaxPlayers.Value);
                    SteamNative.SetRichPresence("connect", "+connect_lobby " + lobby);
                    SteamNative.SetRichPresence("status", "Granny MP");
                    Status = L.T("Steam host – lobby ", "Host Steam – lobby ") + lobby;
                    Log.Info("Created Steam lobby " + lobby);
                }
            }
            if (_pendingJoinCall != 0 && SteamNative.PollCall(_pendingJoinCall, 504, 24, _callBuf, out var jf))
            {
                _pendingJoinCall = 0;
                ulong lobby = BitConverter.ToUInt64(_callBuf, 0);
                uint resp = BitConverter.ToUInt32(_callBuf, 16);
                if (jf || resp != 1 || lobby == 0) { LastError = L.T("Cannot join the Steam lobby (code " + resp + ").", "Nie można dołączyć do lobby Steam (kod " + resp + ")."); SteamLobby = 0; State = SessionState.Offline; Status = "Offline"; return; }
                SteamLobby = lobby;
                var proto = SteamNative.GetLobbyData(lobby, "proto");
                if (!string.IsNullOrEmpty(proto) && proto != Plugin.ProtocolVersion.ToString())
                { LastError = L.T("The host has a different mod version (", "Host ma inną wersję moda (") + SteamNative.GetLobbyData(lobby, "grannymp") + ")."; SteamNative.LeaveLobby(lobby); SteamLobby = 0; State = SessionState.Offline; return; }
                if (SteamNative.GetLobbyData(lobby, "pw") == "1" && string.IsNullOrEmpty(_pendingPassword))
                {
                    SteamNative.LeaveLobby(lobby); SteamLobby = 0; State = SessionState.Offline; Status = "Offline";
                    LastError = L.T("This lobby is password protected – type the password and click Join.", "To lobby jest zabezpieczone hasłem – wpisz hasło i kliknij Dołącz.");
                    LobbyUI.OpenWithJoin(lobby.ToString(), LastError);
                    return;
                }
                var owner = SteamNative.LobbyOwner(lobby);
                State = SessionState.Offline; // ConnectSteam sets Connecting
                ConnectSteam(owner);
                SteamLobby = lobby;
            }
        }

        // ================= sending =================
        static void Xmit(int conn, byte[] buf, int len, bool reliable)
        {
            if (len > 0) NetStats.Sent((Msg)buf[0], len);
            Transport.Send(conn, buf, len, reliable);
        }

        public static void SendToHost(NetWriter w, bool reliable)
        {
            if (Transport == null || IsHost) return;
            Xmit(0, w.Buf, w.Len, reliable);
        }

        public static void SendTo(byte pid, NetWriter w, bool reliable)
        {
            if (Transport == null) return;
            if (IsClient) { if (pid == 0) Xmit(0, w.Buf, w.Len, reliable); return; }
            if (Players.TryGetValue(pid, out var p) && p.Conn >= 0) Xmit(p.Conn, w.Buf, w.Len, reliable);
        }

        /// <summary>Host: to all clients (optionally except one). Client: to the host.</summary>
        public static void Broadcast(NetWriter w, bool reliable, byte except = 255)
        {
            if (Transport == null) return;
            if (IsClient) { Xmit(0, w.Buf, w.Len, reliable); return; }
            foreach (var p in Players.Values)
                if (p.Conn >= 0 && p.Id != except && p.SceneReadyForTraffic(w)) Xmit(p.Conn, w.Buf, w.Len, reliable);
        }

        static void BroadcastRaw(NetWriter w, bool reliable)
        {
            foreach (var p in Players.Values) if (p.Conn >= 0) Xmit(p.Conn, w.Buf, w.Len, reliable);
        }

        /// <summary>World messages go only to players already in the game scene (the rest arrives in WorldFull).</summary>
        static bool SceneReadyForTraffic(this NetPlayer p, NetWriter w)
        {
            var m = (Msg)w.Buf[0];
            if (m == Msg.WorldDelta || m == Msg.AiPose || m == Msg.ItemPose || m == Msg.Spawn || m == Msg.Despawn || m == Msg.PlayerStates || m == Msg.Sound || m == Msg.Music || m == Msg.AudioState || m == Msg.GrannyVoice || m == Msg.DayCount)
                return p.SceneReady;
            return true;
        }

        // ================= receiving =================
        sealed class Handler : INetHandler
        {
            public void OnConnected(int conn)
            {
                if (IsHost) { _pendingConns[conn] = Time.realtimeSinceStartup; return; }
                // client – introduce yourself
                var w = W(Msg.Hello);
                w.Int(Plugin.ProtocolVersion); w.Str(Plugin.ModVersion); w.Str(LocalName);
                w.ULong(SteamNative.Ready ? SteamNative.MySteamId : 0); w.Str(_pendingPassword);
                Xmit(0, w.Buf, w.Len, true);
                Status = L.T("Connected – waiting for the host...", "Połączono – oczekiwanie na hosta...");
            }

            public void OnDisconnected(int conn, string reason)
            {
                if (IsHost)
                {
                    _pendingConns.Remove(conn);
                    if (_connToId.TryGetValue(conn, out var id)) RemovePlayer(id, reason);
                }
                else
                {
                    LastError = Transport is UdpTransport u && u.FailReason != null ? u.FailReason : L.T("Disconnected: ", "Rozłączono: ") + reason;
                    Shutdown(LastError);
                }
            }

            public void OnData(int conn, byte[] buf, int off, int len)
            {
                if (len < 1) return;
                var m = (Msg)buf[off];
                NetStats.Recv(m, len);
                NetPlayer from = null;
                if (IsHost)
                {
                    if (_connToId.TryGetValue(conn, out var id)) Players.TryGetValue(id, out from);
                    if (from == null && m != Msg.Hello) return; // not introduced yet
                    if (from != null) from.LastRecv = Time.realtimeSinceStartup;
                }
                else
                {
                    _lastHostRecv = Time.realtimeSinceStartup;
                    Players.TryGetValue(0, out from);
                    if (State != SessionState.Connected && m != Msg.Welcome && m != Msg.Deny && m != Msg.Bye && m != Msg.Kick) return;
                }
                if (!_handlers.TryGetValue(m, out var h)) { Log.Debug("Unknown message " + m); return; }
                _reader.Set(buf, off + 1, len - 1);
                try
                {
                    if (m == Msg.Hello) HandleHello(conn, _reader);
                    else h(from, _reader);
                }
                catch (Exception e) { Log.Error($"Error handling {m}: {e}"); }
            }
        }

        static void HandleHello(NetPlayer _, NetReader r) { }

        static void HandleHello(int conn, NetReader r)
        {
            if (!IsHost) return;
            int proto = r.Int(); string ver = r.Str(); string name = r.Str(); ulong sid = r.ULong(); string pass = r.Str();
            string deny = null;
            if (proto != Plugin.ProtocolVersion) deny = L.Pair($"Mod version mismatch (host {Plugin.ModVersion}, you {ver}).", $"Niezgodna wersja moda (host {Plugin.ModVersion}, ty {ver}).");
            else if (!string.IsNullOrEmpty(Cfg.Password.Value) && pass != Cfg.Password.Value) deny = "PASSWORD" + L.Pair("Wrong password.", "Złe hasło.");
            else if (Players.Count >= Cfg.MaxPlayers.Value) deny = L.Pair("The lobby is full.", "Lobby jest pełne.");
            else if (HostInGame && !JoinInProgress) deny = L.Pair("The game is already running (joining in progress is disabled).", "Gra już trwa (dołączanie w trakcie wyłączone).");
            if (_connToId.ContainsKey(conn)) return;
            if (deny != null)
            {
                var dw = W(Msg.Deny); dw.Str(deny);
                Xmit(conn, dw.Buf, dw.Len, true);
                Log.Info($"Rejected {name}: {deny}");
                return;
            }
            byte id = 1;
            while (Players.ContainsKey(id)) id++;
            name = UniqueName(name);
            // over Steam the connection's own SteamID counts (it cannot be faked like the one in the Hello message)
            var connSid = Transport.SteamIdOf(conn);
            if (connSid != 0) sid = connSid;
            name = string.IsNullOrWhiteSpace(name) ? "Player" + id : name.Trim();
            var p = new NetPlayer { Id = id, Name = name, SteamId = sid, Conn = conn, LastRecv = Time.realtimeSinceStartup };
            Players[id] = p; _connToId[conn] = id; _pendingConns.Remove(conn);

            var w = W(Msg.Welcome);
            w.Byte(id);
            WriteSettings(w);
            w.Bool(HostInGame); w.Str(HostScene);
            Xmit(conn, w.Buf, w.Len, true);
            BroadcastPlayerList();
            Notify2($"{name} joined the game", $"{name} dołączył(a) do gry", true);
            Log.Info($"Player {name} (id {id}) joined");
            GameFlow.OnPlayerJoined(p);
        }

        /// <summary>Host: "Name (2)" when somebody in the lobby already has that name (e.g. two copies of the game on one PC).</summary>
        static string UniqueName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return name;
            name = name.Trim();
            string n = name; int k = 2;
            while (Players.Values.Any(p => string.Equals(p.Name, n, StringComparison.OrdinalIgnoreCase))) n = $"{name} ({k++})";
            return n;
        }

        static void HandleWelcome(NetPlayer _, NetReader r)
        {
            if (Transport == null || Transport.IsHost) return;
            LocalId = r.Byte();
            ReadSettings(r);
            HostInGame = r.Bool(); HostScene = r.Str();
            State = SessionState.Connected;
            Status = L.T("In lobby (", "W lobby (") + Transport.Kind + ")";
            Players.Clear();
            Players[0] = new NetPlayer { Id = 0, Name = "Host" };
            Players[LocalId] = new NetPlayer { Id = LocalId, Name = LocalName, Model = Cfg.Character.Value };
            Log.Info($"Joined the lobby as id {LocalId}");
            SendModel();
            GameFlow.OnSessionStarted();
        }

        static void WriteSettings(NetWriter w)
        {
            w.Byte((byte)Mode); w.Bool(JoinInProgress); w.Byte((byte)Cfg.MaxPlayers.Value); w.Bool(SpotCatchAll); w.Bool(SpotSingle); w.Byte(GrannyId);
            w.Bool(PvpOn); w.Byte(GrannyChoice); w.Bool(LockFov); w.Bool(GodMode);
        }
        static void ReadSettings(NetReader r)
        {
            Mode = (CoopMode)r.Byte(); JoinInProgress = r.Bool(); r.Byte(); SpotCatchAll = r.Bool(); SpotSingle = r.Bool(); GrannyId = r.Byte();
            PvpOn = r.Bool(); GrannyChoice = r.Byte(); LockFov = r.Bool(); GodMode = r.Bool();
        }

        /// <summary>Caught players spectate until the end of the day ("last survivor", also in PvP).</summary>
        public static bool GhostMode => Mode == CoopMode.LastSurvivor;

        public static void PushSettings()
        {
            if (!IsHost) return;
            var w = W(Msg.Settings); WriteSettings(w); Broadcast(w, true);
        }

        public static void BroadcastPlayerList()
        {
            if (!IsHost) return;
            var w = W(Msg.PlayerList);
            w.Byte((byte)Players.Count);
            foreach (var p in Players.Values.OrderBy(p => p.Id))
            {
                w.Byte(p.Id); w.Str(p.Name); w.ULong(p.SteamId); w.Bool(p.Ready); w.UShort((ushort)Mathf.Clamp(p.PingMs, 0, 65535)); w.Bool(p.Ghost); w.Str(p.Scene); w.Str(p.Model ?? PlayerModels.Default);
            }
            w.Bool(HostInGame); w.Str(HostScene);
            Broadcast(w, true);
        }

        static void HandlePlayerList(NetPlayer _, NetReader r)
        {
            if (!IsClient) return;
            int n = r.Byte();
            var seen = new HashSet<byte>();
            for (int i = 0; i < n; i++)
            {
                var id = r.Byte();
                if (!Players.TryGetValue(id, out var p)) Players[id] = p = new NetPlayer { Id = id };
                p.Name = r.Str(); p.SteamId = r.ULong(); p.Ready = r.Bool(); p.PingMs = r.UShort(); p.Ghost = r.Bool(); p.Scene = r.Str(); p.Model = r.Str();
                seen.Add(id);
            }
            HostInGame = r.Bool(); HostScene = r.Str();
            foreach (var id in Players.Keys.ToList()) if (!seen.Contains(id)) { Players.Remove(id); PlayerSync.RemoveAvatar(id); }
        }

        public static void SetReady(bool ready)
        {
            var me = Local; if (me == null) return;
            me.Ready = ready;
            if (IsClient) { var w = W(Msg.SetReady); w.Bool(ready); SendToHost(w, true); }
            else BroadcastPlayerList();
        }

        /// <summary>The local player's model choice (settings) – sent to the host, which tells everybody.</summary>
        public static void SendModel()
        {
            var me = Local; if (me == null) return;
            me.Model = Cfg.Character.Value;
            if (IsClient) { var w = W(Msg.SetModel); w.Str(me.Model); SendToHost(w, true); }
            else BroadcastPlayerList();
        }

        public static bool AllReady => Players.Values.All(p => p.Ready || p.IsHost);

        public static void Kick(byte id, string reason)
        {
            if (!IsHost || id == 0 || !Players.TryGetValue(id, out var p)) return;
            var w = W(Msg.Kick); w.Str(reason);
            if (p.Conn >= 0) { Xmit(p.Conn, w.Buf, w.Len, true); Transport.Close(p.Conn); }
            RemovePlayer(id, reason);
        }

        static void RemovePlayer(byte id, string reason)
        {
            if (!Players.TryGetValue(id, out var p)) return;
            Players.Remove(id);
            if (p.Conn >= 0) _connToId.Remove(p.Conn);
            PlayerSync.RemoveAvatar(id);
            World.OnPlayerLeft(id);
            GameFlow.OnPlayerLeft(p);
            Notify2($"{p.Name} left the game ({L.Show(reason)})", $"{p.Name} opuścił(a) grę ({L.Show(reason)})", true);
            BroadcastPlayerList();
        }

        // ================= czat / komunikaty =================
        public static void SendChat(string text)
        {
            text = (text ?? "").Trim();
            if (text.Length == 0 || !Active) return;
            if (text.Length > 200) text = text.Substring(0, 200);
            if (IsHost) { AddChat(LocalId, text); var w = W(Msg.Chat); w.Byte(LocalId); w.Str(text); Broadcast(w, true); }
            else { var w = W(Msg.Chat); w.Byte(LocalId); w.Str(text); SendToHost(w, true); }
        }

        static void HandleChat(NetPlayer from, NetReader r)
        {
            var id = r.Byte(); var text = r.Str();
            if (IsHost) { if (from == null) return; id = from.Id; var w = W(Msg.Chat); w.Byte(id); w.Str(text); Broadcast(w, true); }
            AddChat(id, text);
        }

        static void AddChat(byte id, string text)
        {
            var name = Players.TryGetValue(id, out var p) ? p.Name : "?";
            ChatLog.Add((Time.realtimeSinceStartup, name + ": " + text, ColorOf(id)));
            if (ChatLog.Count > 60) ChatLog.RemoveAt(0);
        }

        /// <summary>Notification in both languages – every player sees it in their own language.</summary>
        public static void Notify2(string en, string pl, bool broadcast)
        {
            Notices.Add((Time.realtimeSinceStartup + 5f, L.T(en, pl)));
            if (Notices.Count > 6) Notices.RemoveAt(0);
            if (broadcast && IsHost) { var w = W(Msg.Notify); w.Str(L.Pair(en, pl)); Broadcast(w, true); }
        }

        public static void Notify(string text, bool broadcast)
        {
            Notices.Add((Time.realtimeSinceStartup + 5f, text));
            if (Notices.Count > 6) Notices.RemoveAt(0);
            if (broadcast && IsHost) { var w = W(Msg.Notify); w.Str(text); Broadcast(w, true); }
        }

        public static string NameOf(byte id) => Players.TryGetValue(id, out var p) ? p.Name : ("#" + id);

        public static string JoinInfo => Transport?.JoinInfo ?? "";
    }
}
