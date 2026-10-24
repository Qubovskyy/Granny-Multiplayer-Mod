using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace GrannyMP
{
    /// <summary>Steam server list: public lobbies of this mod (same protocol) + lobbies of friends playing Granny.</summary>
    internal static class LobbyBrowser
    {
        public sealed class Entry
        {
            public ulong Lobby; public string Name = ""; public int Players, Max; public bool Password, Friend; public string Version = "";
        }

        public static readonly List<Entry> Entries = new();
        public static bool Busy => _call != 0;
        public static string Status = "";
        static ulong _call;
        static float _started, _nextDataRefresh;
        static readonly byte[] _buf = new byte[16];
        static List<(ulong friend, ulong lobby)> _friends = new();

        public static void Refresh()
        {
            if (!SteamNative.TryInit()) { Status = SteamNative.LastError; return; }
            try
            {
                _friends = SteamNative.FriendLobbies();
                foreach (var f in _friends) SteamNative.RequestLobbyData(f.lobby);
                _call = SteamNative.RequestModLobbies();
                _started = Time.unscaledTime;
                Status = L.T("Searching...", "Szukanie...");
                Log.Debug($"Lobby list requested ({_friends.Count} friend lobbies)");
            }
            catch (Exception e) { Status = e.Message; _call = 0; Log.Warn("Lobby list: " + e.Message); }
        }

        public static void Update()
        {
            if (_call != 0)
            {
                if (SteamNative.PollCall(_call, SteamNative.LobbyMatchListId, 4, _buf, out bool failed))
                {
                    _call = 0;
                    var found = new List<ulong>();
                    if (!failed)
                    {
                        uint n = BitConverter.ToUInt32(_buf, 0);
                        for (int i = 0; i < n && i < 50; i++) found.Add(SteamNative.LobbyByIndex(i));
                    }
                    Build(found);
                    Status = failed ? L.T("Steam did not answer.", "Steam nie odpowiedział.") : "";
                    Log.Debug($"Lobby list: {found.Count} public, {_friends.Count} friends");
                }
                else if (Time.unscaledTime - _started > 10f) { _call = 0; Status = L.T("Steam did not answer.", "Steam nie odpowiedział."); Build(new List<ulong>()); }
            }
            // friend lobby data arrives a moment later
            if (Entries.Count > 0 && Time.unscaledTime >= _nextDataRefresh) { _nextDataRefresh = Time.unscaledTime + 1f; foreach (var e in Entries) Fill(e); }
        }

        static void Build(List<ulong> pub)
        {
            Entries.Clear();
            foreach (var id in pub.Distinct()) { var e = new Entry { Lobby = id }; Fill(e); Entries.Add(e); }
            foreach (var (friend, lobby) in _friends)
            {
                var e = Entries.FirstOrDefault(x => x.Lobby == lobby);
                if (e == null) { e = new Entry { Lobby = lobby }; Entries.Add(e); }
                e.Friend = true;
                Fill(e);
                if (string.IsNullOrEmpty(e.Name)) e.Name = SteamNative.NameOf(friend);
            }
            _nextDataRefresh = Time.unscaledTime + 1f;
        }

        static void Fill(Entry e)
        {
            try
            {
                var name = SteamNative.GetLobbyData(e.Lobby, "name");
                if (!string.IsNullOrEmpty(name)) e.Name = name;
                e.Version = SteamNative.GetLobbyData(e.Lobby, "grannymp");
                e.Password = SteamNative.GetLobbyData(e.Lobby, "pw") == "1";
                e.Players = SteamNative.LobbyMemberCount(e.Lobby);
                e.Max = SteamNative.LobbyMemberLimit(e.Lobby);
            }
            catch { }
        }
    }
}
