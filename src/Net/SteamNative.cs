using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;

namespace GrannyMP
{
    /// <summary>
    /// Direct calls into steam_api64.dll (the old "flat API" of SDK ~1.39 used by the game:
    /// SteamMatchMaking009, SteamNetworking005, SteamUtils009, SteamUser019, SteamFriends015).
    /// The game itself calls SteamAPI_Init and SteamAPI_RunCallbacks every frame (SteamManager.Update),
    /// so we only fetch the interfaces and register our own callbacks.
    /// </summary>
    internal static unsafe class SteamNative
    {
        const string Dll = "steam_api64";

        public static bool Ready;
        public static IntPtr Matchmaking, Networking, Utils, User, Friends;
        public static ulong MySteamId;
        static IntPtr _module;

        // ---------------- resolver ----------------
        [DllImport("kernel32", CharSet = CharSet.Unicode)] static extern IntPtr GetModuleHandleW(string name);

        public static void InstallResolver()
        {
            NativeLibrary.SetDllImportResolver(typeof(SteamNative).Assembly, (name, asm, path) =>
            {
                if (!name.StartsWith("steam_api", StringComparison.OrdinalIgnoreCase)) return IntPtr.Zero;
                if (_module != IntPtr.Zero) return _module;
                _module = GetModuleHandleW("steam_api64.dll");
                if (_module == IntPtr.Zero)
                {
                    var p = Path.Combine(BepInEx.Paths.GameRootPath, "Granny_Data", "Plugins", "x86_64", "steam_api64.dll");
                    if (File.Exists(p)) NativeLibrary.TryLoad(p, out _module);
                }
                return _module;
            });
        }

        // ---------------- importy ----------------
        [DllImport(Dll)] static extern IntPtr SteamClient();
        [DllImport(Dll)] static extern int SteamAPI_GetHSteamUser();
        [DllImport(Dll)] static extern int SteamAPI_GetHSteamPipe();
        [DllImport(Dll)] [return: MarshalAs(UnmanagedType.I1)] public static extern bool SteamAPI_IsSteamRunning();
        [DllImport(Dll)] static extern void SteamAPI_RegisterCallback(IntPtr pCallback, int iCallback);
        [DllImport(Dll)] static extern void SteamAPI_UnregisterCallback(IntPtr pCallback);

        [DllImport(Dll)] static extern IntPtr SteamAPI_ISteamClient_GetISteamMatchmaking(IntPtr self, int user, int pipe, [MarshalAs(UnmanagedType.LPUTF8Str)] string ver);
        [DllImport(Dll)] static extern IntPtr SteamAPI_ISteamClient_GetISteamNetworking(IntPtr self, int user, int pipe, [MarshalAs(UnmanagedType.LPUTF8Str)] string ver);
        [DllImport(Dll)] static extern IntPtr SteamAPI_ISteamClient_GetISteamUtils(IntPtr self, int pipe, [MarshalAs(UnmanagedType.LPUTF8Str)] string ver);
        [DllImport(Dll)] static extern IntPtr SteamAPI_ISteamClient_GetISteamUser(IntPtr self, int user, int pipe, [MarshalAs(UnmanagedType.LPUTF8Str)] string ver);
        [DllImport(Dll)] static extern IntPtr SteamAPI_ISteamClient_GetISteamFriends(IntPtr self, int user, int pipe, [MarshalAs(UnmanagedType.LPUTF8Str)] string ver);

        [DllImport(Dll)] static extern ulong SteamAPI_ISteamUser_GetSteamID(IntPtr self);
        [DllImport(Dll)] static extern IntPtr SteamAPI_ISteamFriends_GetPersonaName(IntPtr self);
        [DllImport(Dll)] static extern IntPtr SteamAPI_ISteamFriends_GetFriendPersonaName(IntPtr self, ulong id);
        [DllImport(Dll)] static extern void SteamAPI_ISteamFriends_ActivateGameOverlayInviteDialog(IntPtr self, ulong lobby);
        [DllImport(Dll)] [return: MarshalAs(UnmanagedType.I1)] static extern bool SteamAPI_ISteamFriends_SetRichPresence(IntPtr self, [MarshalAs(UnmanagedType.LPUTF8Str)] string key, [MarshalAs(UnmanagedType.LPUTF8Str)] string value);
        [DllImport(Dll)] static extern void SteamAPI_ISteamFriends_ClearRichPresence(IntPtr self);

        [DllImport(Dll)] static extern ulong SteamAPI_ISteamMatchmaking_CreateLobby(IntPtr self, int type, int max);
        [DllImport(Dll)] static extern ulong SteamAPI_ISteamMatchmaking_JoinLobby(IntPtr self, ulong lobby);
        [DllImport(Dll)] static extern void SteamAPI_ISteamMatchmaking_LeaveLobby(IntPtr self, ulong lobby);
        [DllImport(Dll)] static extern int SteamAPI_ISteamMatchmaking_GetNumLobbyMembers(IntPtr self, ulong lobby);
        [DllImport(Dll)] static extern ulong SteamAPI_ISteamMatchmaking_GetLobbyMemberByIndex(IntPtr self, ulong lobby, int i);
        [DllImport(Dll)] static extern ulong SteamAPI_ISteamMatchmaking_GetLobbyOwner(IntPtr self, ulong lobby);
        [DllImport(Dll)] [return: MarshalAs(UnmanagedType.I1)] static extern bool SteamAPI_ISteamMatchmaking_SetLobbyData(IntPtr self, ulong lobby, [MarshalAs(UnmanagedType.LPUTF8Str)] string k, [MarshalAs(UnmanagedType.LPUTF8Str)] string v);
        [DllImport(Dll)] static extern IntPtr SteamAPI_ISteamMatchmaking_GetLobbyData(IntPtr self, ulong lobby, [MarshalAs(UnmanagedType.LPUTF8Str)] string k);
        [DllImport(Dll)] [return: MarshalAs(UnmanagedType.I1)] static extern bool SteamAPI_ISteamMatchmaking_SetLobbyJoinable(IntPtr self, ulong lobby, [MarshalAs(UnmanagedType.I1)] bool joinable);
        [DllImport(Dll)] [return: MarshalAs(UnmanagedType.I1)] static extern bool SteamAPI_ISteamMatchmaking_SetLobbyMemberLimit(IntPtr self, ulong lobby, int max);

        [DllImport(Dll)] [return: MarshalAs(UnmanagedType.I1)] static extern bool SteamAPI_ISteamNetworking_SendP2PPacket(IntPtr self, ulong remote, byte* data, uint len, int sendType, int channel);
        [DllImport(Dll)] [return: MarshalAs(UnmanagedType.I1)] static extern bool SteamAPI_ISteamNetworking_IsP2PPacketAvailable(IntPtr self, uint* size, int channel);
        [DllImport(Dll)] [return: MarshalAs(UnmanagedType.I1)] static extern bool SteamAPI_ISteamNetworking_ReadP2PPacket(IntPtr self, byte* dest, uint cap, uint* size, ulong* remote, int channel);
        [DllImport(Dll)] [return: MarshalAs(UnmanagedType.I1)] static extern bool SteamAPI_ISteamNetworking_AcceptP2PSessionWithUser(IntPtr self, ulong remote);
        [DllImport(Dll)] [return: MarshalAs(UnmanagedType.I1)] static extern bool SteamAPI_ISteamNetworking_CloseP2PSessionWithUser(IntPtr self, ulong remote);
        [DllImport(Dll)] [return: MarshalAs(UnmanagedType.I1)] static extern bool SteamAPI_ISteamNetworking_AllowP2PPacketRelay(IntPtr self, [MarshalAs(UnmanagedType.I1)] bool allow);

        [DllImport(Dll)] [return: MarshalAs(UnmanagedType.I1)] static extern bool SteamAPI_ISteamUtils_IsAPICallCompleted(IntPtr self, ulong call, byte* failed);
        [DllImport(Dll)] [return: MarshalAs(UnmanagedType.I1)] static extern bool SteamAPI_ISteamUtils_GetAPICallResult(IntPtr self, ulong call, void* buf, int size, int expected, byte* failed);
        [DllImport(Dll)] static extern uint SteamAPI_ISteamUtils_GetAppID(IntPtr self);
        // lobby browser
        [DllImport(Dll)] static extern ulong SteamAPI_ISteamMatchmaking_RequestLobbyList(IntPtr self);
        [DllImport(Dll)] static extern void SteamAPI_ISteamMatchmaking_AddRequestLobbyListStringFilter(IntPtr self, [MarshalAs(UnmanagedType.LPUTF8Str)] string k, [MarshalAs(UnmanagedType.LPUTF8Str)] string v, int cmp);
        [DllImport(Dll)] static extern void SteamAPI_ISteamMatchmaking_AddRequestLobbyListDistanceFilter(IntPtr self, int filter);
        [DllImport(Dll)] static extern void SteamAPI_ISteamMatchmaking_AddRequestLobbyListResultCountFilter(IntPtr self, int max);
        [DllImport(Dll)] static extern ulong SteamAPI_ISteamMatchmaking_GetLobbyByIndex(IntPtr self, int i);
        [DllImport(Dll)] [return: MarshalAs(UnmanagedType.I1)] static extern bool SteamAPI_ISteamMatchmaking_RequestLobbyData(IntPtr self, ulong lobby);
        [DllImport(Dll)] static extern int SteamAPI_ISteamMatchmaking_GetLobbyMemberLimit(IntPtr self, ulong lobby);
        [DllImport(Dll)] [return: MarshalAs(UnmanagedType.I1)] static extern bool SteamAPI_ISteamMatchmaking_SetLobbyType(IntPtr self, ulong lobby, int type);
        [DllImport(Dll)] static extern int SteamAPI_ISteamFriends_GetFriendCount(IntPtr self, int flags);
        [DllImport(Dll)] static extern ulong SteamAPI_ISteamFriends_GetFriendByIndex(IntPtr self, int i, int flags);
        [DllImport(Dll)] [return: MarshalAs(UnmanagedType.I1)] static extern bool SteamAPI_ISteamFriends_GetFriendGamePlayed(IntPtr self, ulong friend, FriendGameInfo* info);

        // voice (SteamUser019 – the old signatures with the deprecated uncompressed arguments)
        [DllImport(Dll)] static extern void SteamAPI_ISteamUser_StartVoiceRecording(IntPtr self);
        [DllImport(Dll)] static extern void SteamAPI_ISteamUser_StopVoiceRecording(IntPtr self);
        [DllImport(Dll)] static extern int SteamAPI_ISteamUser_GetAvailableVoice(IntPtr self, uint* pcbCompressed, uint* pcbUncompressedDeprecated, uint uncompressedRateDeprecated);
        [DllImport(Dll)] static extern int SteamAPI_ISteamUser_GetVoice(IntPtr self, [MarshalAs(UnmanagedType.I1)] bool wantCompressed, byte* dest, uint destSize, uint* written,
            [MarshalAs(UnmanagedType.I1)] bool wantUncompressedDeprecated, byte* uncDest, uint uncSize, uint* uncWritten, uint uncRate);
        [DllImport(Dll)] static extern int SteamAPI_ISteamUser_DecompressVoice(IntPtr self, byte* compressed, uint cbCompressed, byte* dest, uint destSize, uint* written, uint rate);
        [DllImport(Dll)] static extern uint SteamAPI_ISteamUser_GetVoiceOptimalSampleRate(IntPtr self);
        [DllImport(Dll)] static extern void SteamAPI_ISteamFriends_SetInGameVoiceSpeaking(IntPtr self, ulong user, [MarshalAs(UnmanagedType.I1)] bool speaking);

        [StructLayout(LayoutKind.Sequential, Pack = 8)]
        struct FriendGameInfo { public ulong GameId; public uint GameIP; public ushort GamePort, QueryPort; public ulong Lobby; }

        // ---------------- initialisation ----------------
        public static string LastError = "";

        public static bool TryInit()
        {
            if (Ready) return true;
            if (Cfg.DisableSteam.Value) { LastError = L.T("Steam is switched off in the config ([Network] DisableSteam) – use Direct IP.", "Steam jest wyłączony w konfigu ([Network] DisableSteam) – użyj Direct IP."); return false; }
            try
            {
                var client = SteamClient();
                if (client == IntPtr.Zero) { LastError = L.T("Steam was not initialised by the game", "Steam nie jest zainicjalizowany przez grę"); return false; }
                int user = SteamAPI_GetHSteamUser(), pipe = SteamAPI_GetHSteamPipe();
                if (user == 0 || pipe == 0) { LastError = "no HSteamUser/HSteamPipe"; return false; }
                Matchmaking = SteamAPI_ISteamClient_GetISteamMatchmaking(client, user, pipe, "SteamMatchMaking009");
                Networking = SteamAPI_ISteamClient_GetISteamNetworking(client, user, pipe, "SteamNetworking005");
                Utils = SteamAPI_ISteamClient_GetISteamUtils(client, pipe, "SteamUtils009");
                User = SteamAPI_ISteamClient_GetISteamUser(client, user, pipe, "SteamUser019");
                Friends = SteamAPI_ISteamClient_GetISteamFriends(client, user, pipe, "SteamFriends015");
                if (Matchmaking == IntPtr.Zero || Networking == IntPtr.Zero || Utils == IntPtr.Zero || User == IntPtr.Zero)
                { LastError = L.T("could not get Steam interfaces", "nie udało się pobrać interfejsów Steam"); return false; }
                MySteamId = SteamAPI_ISteamUser_GetSteamID(User);
                if (MySteamId == 0) { LastError = L.T("no SteamID (offline?)", "brak SteamID (offline?)"); return false; }
                SteamAPI_ISteamNetworking_AllowP2PPacketRelay(Networking, true);
                RegisterCallbacks();
                Ready = true;
                Log.Info($"Steam OK: SteamID={MySteamId}, AppID={SteamAPI_ISteamUtils_GetAppID(Utils)}");
                return true;
            }
            catch (DllNotFoundException) { LastError = "steam_api64.dll not found"; }
            catch (EntryPointNotFoundException e) { LastError = "incompatible steam_api: " + e.Message; }
            catch (Exception e) { LastError = e.Message; }
            return false;
        }

        // ---------------- wrappers ----------------
        static string Utf8(IntPtr p) => p == IntPtr.Zero ? "" : Marshal.PtrToStringUTF8(p);

        public static string MyName => Ready && Friends != IntPtr.Zero ? Utf8(SteamAPI_ISteamFriends_GetPersonaName(Friends)) : "";
        public static string NameOf(ulong id) => Ready && Friends != IntPtr.Zero ? Utf8(SteamAPI_ISteamFriends_GetFriendPersonaName(Friends, id)) : id.ToString();
        public static void InviteOverlay(ulong lobby) { if (Ready && Friends != IntPtr.Zero) SteamAPI_ISteamFriends_ActivateGameOverlayInviteDialog(Friends, lobby); }
        public static void SetRichPresence(string k, string v) { if (Ready && Friends != IntPtr.Zero) SteamAPI_ISteamFriends_SetRichPresence(Friends, k, v); }
        public static void ClearRichPresence() { if (Ready && Friends != IntPtr.Zero) SteamAPI_ISteamFriends_ClearRichPresence(Friends); }

        // ---- voice: EVoiceResult 0 = OK, 2 = not recording, 3 = no data, 4 = buffer too small ----
        public static void StartVoice() { SteamAPI_ISteamUser_StartVoiceRecording(User); if (Friends != IntPtr.Zero) SteamAPI_ISteamFriends_SetInGameVoiceSpeaking(Friends, MySteamId, true); }
        public static void StopVoice() { SteamAPI_ISteamUser_StopVoiceRecording(User); if (Friends != IntPtr.Zero) SteamAPI_ISteamFriends_SetInGameVoiceSpeaking(Friends, MySteamId, false); }
        public static int AvailableVoice(out uint bytes) { uint b = 0; int r = SteamAPI_ISteamUser_GetAvailableVoice(User, &b, null, 0); bytes = b; return r; }
        public static int GetVoice(byte[] dest, out uint written)
        {
            uint w = 0; int r;
            fixed (byte* p = dest) r = SteamAPI_ISteamUser_GetVoice(User, true, p, (uint)dest.Length, &w, false, null, 0, null, 0);
            written = w; return r;
        }
        public static int DecompressVoice(byte[] src, int off, int len, byte[] dest, uint rate, out uint written)
        {
            uint w = 0; int r;
            fixed (byte* s = src) fixed (byte* d = dest) r = SteamAPI_ISteamUser_DecompressVoice(User, s + off, (uint)len, d, (uint)dest.Length, &w, rate);
            written = w; return r;
        }
        public static uint VoiceSampleRate() => SteamAPI_ISteamUser_GetVoiceOptimalSampleRate(User);

        public static ulong CreateLobby(int type, int max) => SteamAPI_ISteamMatchmaking_CreateLobby(Matchmaking, type, max);
        public static ulong JoinLobby(ulong lobby) => SteamAPI_ISteamMatchmaking_JoinLobby(Matchmaking, lobby);
        public static void LeaveLobby(ulong lobby) { if (Ready && lobby != 0) SteamAPI_ISteamMatchmaking_LeaveLobby(Matchmaking, lobby); }
        public static ulong LobbyOwner(ulong lobby) => SteamAPI_ISteamMatchmaking_GetLobbyOwner(Matchmaking, lobby);
        public static int LobbyMemberCount(ulong lobby) => SteamAPI_ISteamMatchmaking_GetNumLobbyMembers(Matchmaking, lobby);
        public static ulong LobbyMember(ulong lobby, int i) => SteamAPI_ISteamMatchmaking_GetLobbyMemberByIndex(Matchmaking, lobby, i);
        public static void SetLobbyData(ulong lobby, string k, string v) => SteamAPI_ISteamMatchmaking_SetLobbyData(Matchmaking, lobby, k, v);
        public static string GetLobbyData(ulong lobby, string k) => Utf8(SteamAPI_ISteamMatchmaking_GetLobbyData(Matchmaking, lobby, k));
        public static void SetLobbyJoinable(ulong lobby, bool j) => SteamAPI_ISteamMatchmaking_SetLobbyJoinable(Matchmaking, lobby, j);
        public static void SetLobbyMemberLimit(ulong lobby, int m) => SteamAPI_ISteamMatchmaking_SetLobbyMemberLimit(Matchmaking, lobby, m);

        public const int P2PUnreliable = 0, P2PReliable = 2;
        public static bool SendP2P(ulong to, byte[] data, int len, bool reliable)
        {
            fixed (byte* p = data) return SteamAPI_ISteamNetworking_SendP2PPacket(Networking, to, p, (uint)len, reliable ? P2PReliable : P2PUnreliable, 0);
        }
        public static bool PacketAvailable(out uint size) { uint s; var r = SteamAPI_ISteamNetworking_IsP2PPacketAvailable(Networking, &s, 0); size = s; return r; }
        public static bool ReadP2P(byte[] buf, out uint size, out ulong from)
        {
            uint s; ulong f;
            bool r;
            fixed (byte* p = buf) r = SteamAPI_ISteamNetworking_ReadP2PPacket(Networking, p, (uint)buf.Length, &s, &f, 0);
            size = s; from = f; return r;
        }
        public static void AcceptP2P(ulong id) => SteamAPI_ISteamNetworking_AcceptP2PSessionWithUser(Networking, id);
        public static void CloseP2P(ulong id) { if (Ready) SteamAPI_ISteamNetworking_CloseP2PSessionWithUser(Networking, id); }

        /// <summary>Polls a SteamAPICall_t result (no CallResult). Returns true when finished.</summary>
        public static bool PollCall(ulong call, int expectedId, int structSize, byte[] outBuf, out bool failed)
        {
            byte f = 0;
            failed = false;
            if (!SteamAPI_ISteamUtils_IsAPICallCompleted(Utils, call, &f)) return false;
            fixed (byte* p = outBuf)
            {
                byte f2 = 0;
                var ok = SteamAPI_ISteamUtils_GetAPICallResult(Utils, call, p, Math.Min(structSize, outBuf.Length), expectedId, &f2);
                failed = !ok || f2 != 0 || f != 0;
            }
            return true;
        }

        public static bool IsLobbyId(ulong id) => ((id >> 52) & 0xF) == 8; // k_EAccountTypeChat

        public const int LobbyMatchListId = 510; // k_iSteamMatchmakingCallbacks + 10, struct { uint32 m_nLobbiesMatching }
        public static uint AppId => Ready ? SteamAPI_ISteamUtils_GetAppID(Utils) : 0;

        /// <summary>Public lobbies of this mod with the same protocol version (worldwide).</summary>
        public static ulong RequestModLobbies()
        {
            SteamAPI_ISteamMatchmaking_AddRequestLobbyListStringFilter(Matchmaking, "proto", Plugin.ProtocolVersion.ToString(), 0);
            SteamAPI_ISteamMatchmaking_AddRequestLobbyListDistanceFilter(Matchmaking, 3);
            SteamAPI_ISteamMatchmaking_AddRequestLobbyListResultCountFilter(Matchmaking, 50);
            return SteamAPI_ISteamMatchmaking_RequestLobbyList(Matchmaking);
        }
        public static ulong LobbyByIndex(int i) => SteamAPI_ISteamMatchmaking_GetLobbyByIndex(Matchmaking, i);
        public static void RequestLobbyData(ulong lobby) { if (Ready) SteamAPI_ISteamMatchmaking_RequestLobbyData(Matchmaking, lobby); }
        public static int LobbyMemberLimit(ulong lobby) => SteamAPI_ISteamMatchmaking_GetLobbyMemberLimit(Matchmaking, lobby);
        public static void SetLobbyType(ulong lobby, int type) { if (Ready && lobby != 0) SteamAPI_ISteamMatchmaking_SetLobbyType(Matchmaking, lobby, type); }

        /// <summary>Lobbies of friends playing this game right now (friends-only lobbies are not in the public list).</summary>
        public static List<(ulong friend, ulong lobby)> FriendLobbies()
        {
            var list = new List<(ulong, ulong)>();
            if (!Ready || Friends == IntPtr.Zero) return list;
            uint app = AppId;
            int n = SteamAPI_ISteamFriends_GetFriendCount(Friends, 4); // k_EFriendFlagImmediate
            for (int i = 0; i < n && i < 500; i++)
            {
                ulong f = SteamAPI_ISteamFriends_GetFriendByIndex(Friends, i, 4);
                FriendGameInfo info;
                if (!SteamAPI_ISteamFriends_GetFriendGamePlayed(Friends, f, &info)) continue;
                if ((uint)(info.GameId & 0xFFFFFF) != app || !IsLobbyId(info.Lobby)) continue;
                list.Add((f, info.Lobby));
            }
            return list;
        }
        public static bool IsUserId(ulong id) => ((id >> 52) & 0xF) == 1;

        // ---------------- callbacks (fake CCallbackBase) ----------------
        // CCallbackBase layout (x64): vtable*, uint8 flags @8, int iCallback @12.
        // On MSVC the order of the overloaded virtual Run methods is reversed: [0]=Run(param,bIOFailure,hCall), [1]=Run(param), [2]=GetCallbackSizeBytes.
        [UnmanagedFunctionPointer(CallingConvention.ThisCall)] delegate void RunCB(IntPtr self, IntPtr param);
        [UnmanagedFunctionPointer(CallingConvention.ThisCall)] delegate void RunCR(IntPtr self, IntPtr param, byte ioFail, ulong call);
        [UnmanagedFunctionPointer(CallingConvention.ThisCall)] delegate int GetSize(IntPtr self);

        sealed class Cb
        {
            public int Id, Size; public Action<IntPtr> Handler;
            public IntPtr Obj, VTable;
            public RunCB DRun; public RunCR DRunCR; public GetSize DSize;
        }
        static readonly List<Cb> _cbs = new();
        static readonly Queue<Action> _pending = new();

        public static event Action<ulong, ulong> OnLobbyJoinRequested;   // lobby, friend
        public static event Action<ulong, string> OnRichPresenceJoin;    // friend, connect string
        public static event Action<ulong> OnP2PSessionRequest;           // remote
        public static event Action<ulong, int> OnP2PConnectFail;         // remote, error

        static void RegisterCallbacks()
        {
            Register(333, 16, p => { var lobby = *(ulong*)p; var fr = *(ulong*)(p + 8); _pending.Enqueue(() => OnLobbyJoinRequested?.Invoke(lobby, fr)); });
            Register(337, 8 + 256, p => { var fr = *(ulong*)p; var s = Marshal.PtrToStringUTF8(p + 8) ?? ""; _pending.Enqueue(() => OnRichPresenceJoin?.Invoke(fr, s)); });
            Register(1202, 8, p => { var r = *(ulong*)p; _pending.Enqueue(() => OnP2PSessionRequest?.Invoke(r)); });
            Register(1203, 16, p => { var r = *(ulong*)p; int e = *(byte*)(p + 8); _pending.Enqueue(() => OnP2PConnectFail?.Invoke(r, e)); });
        }

        static void Register(int id, int size, Action<IntPtr> handler)
        {
            var cb = new Cb { Id = id, Size = size, Handler = handler };
            cb.DRun = (self, param) => { try { cb.Handler(param); } catch (Exception e) { Log.Error("Steam cb " + id + ": " + e); } };
            cb.DRunCR = (self, param, io, call) => { try { cb.Handler(param); } catch (Exception e) { Log.Error("Steam cr " + id + ": " + e); } };
            cb.DSize = self => cb.Size;
            cb.VTable = Marshal.AllocHGlobal(IntPtr.Size * 3);
            Marshal.WriteIntPtr(cb.VTable, 0, Marshal.GetFunctionPointerForDelegate(cb.DRunCR));
            Marshal.WriteIntPtr(cb.VTable, IntPtr.Size, Marshal.GetFunctionPointerForDelegate(cb.DRun));
            Marshal.WriteIntPtr(cb.VTable, IntPtr.Size * 2, Marshal.GetFunctionPointerForDelegate(cb.DSize));
            cb.Obj = Marshal.AllocHGlobal(16);
            for (int i = 0; i < 16; i++) Marshal.WriteByte(cb.Obj, i, 0);
            Marshal.WriteIntPtr(cb.Obj, 0, cb.VTable);
            Marshal.WriteInt32(cb.Obj, 12, id);
            SteamAPI_RegisterCallback(cb.Obj, id);
            _cbs.Add(cb);
        }

        /// <summary>Called from the main loop – callback events are processed outside the Steam thread.</summary>
        public static void Pump()
        {
            while (_pending.Count > 0)
            {
                var a = _pending.Dequeue();
                try { a(); } catch (Exception e) { Log.Error("Steam event: " + e); }
            }
        }
    }
}
