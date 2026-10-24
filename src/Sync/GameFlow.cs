using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace GrannyMP
{
    /// <summary>
    /// Game flow: starting the game (host), copying settings (PlayerPrefs / difficulty), intro sync,
    /// join in progress, scene sync, end of day, catches, ghost mode, escape, leaving to the main menu.
    /// All timers use unscaled time (the game pauses with Time.timeScale = 0).
    /// </summary>
    internal static class GameFlow
    {
        public static string CurrentScene = "";
        static string _prevScene = "";
        static float _lastEndDayPostfix = -100f;
        public static bool InTransition => Time.unscaledTime < _transitionUntil;
        static float _transitionUntil;
        static float _lastLocalDayEnd = -100f;
        public static bool ApplyingRemoteDayEnd;
        static bool _loadingByHost;
        static bool _waitingWorld;
        static readonly Dictionary<string, (int type, string val)> _prefsBackup = new();
        static readonly Dictionary<byte, float> _caughtAt = new();
        static bool _ownDayEnd;
        static float _heldDayEndAt, _nextDaySync;
        static float _waitPlayersUntil;
        static float _pendingDayEndAt;

        // ---- start gate: everybody starts at the same moment ----
        // The host loaded Granny's house seconds before the others and could walk around, the day's start (the wake-up)
        // ran earlier there. Now every machine stops its game (time scale 0, black screen) after loading, until the host
        // sees everybody ready and sends the go – then all start together.
        public static bool StartHolding { get; private set; }
        static float _gateUntil, _allReadySince;

        static void CloseGate(float timeout)
        {
            StartHolding = true; _gateUntil = Time.unscaledTime + timeout; _allReadySince = 0;
            try { Time.timeScale = 0f; } catch { }
            Log.Info("Waiting until everybody has loaded Granny's house");
        }

        static void OpenGate(bool broadcast)
        {
            if (!StartHolding) return;
            StartHolding = false;
            try { Time.timeScale = 1f; } catch { }
            if (broadcast && Session.IsHost) { var w = Session.W(Msg.StartGo); Session.Broadcast(w, true); Log.Info("Everybody is ready – the game starts"); }
        }

        static void UpdateGate(float now)
        {
            if (!StartHolding) return;
            if (!Session.Active) { OpenGate(false); return; }
            if (now > _gateUntil) { Log.Warn("Not everybody loaded in time – the game starts anyway"); OpenGate(Session.IsHost); return; }
            if (!Session.IsHost) { if (Time.timeScale > 0f) Time.timeScale = 0f; return; }
            bool all = Session.Players.Values.All(p => p.IsHost || p.SceneReady);
            if (!all) { _allReadySince = 0; if (Time.timeScale > 0f) Time.timeScale = 0f; return; }
            if (_allReadySince == 0) _allReadySince = now;
            if (now - _allReadySince > 1f) OpenGate(true); // a moment for the last world state to be applied
        }

        /// <summary>Host: after the game starts the AI waits (max 25 s) until clients loaded the scene and got the world state.</summary>
        public static bool WaitingForPlayers => Session.IsHost && Time.unscaledTime < _waitPlayersUntil &&
            Session.Players.Values.Any(p => !p.IsHost && !p.SceneReady);

        public static bool WaitingForWorld => _waitingWorld;

        public static void Init()
        {
            // SceneManager.sceneLoaded is stripped in this game – scenes are detected every frame (PollScene).
            SteamNative.OnLobbyJoinRequested += (lobby, friend) => { Log.Info("Steam invite to lobby " + lobby); if (!Session.Active) Session.Join(lobby.ToString(), ""); };
            SteamNative.OnRichPresenceJoin += (friend, connect) =>
            {
                var m = Regex.Match(connect ?? "", @"\+connect_lobby\s+(\d+)");
                if (m.Success && !Session.Active) Session.Join(m.Groups[1].Value, "");
            };
        }

        /// <summary>Game started from a Steam invite: "+connect_lobby &lt;id&gt;" on the command line.</summary>
        public static void CheckCommandLine()
        {
            try
            {
                var args = Environment.GetCommandLineArgs();
                for (int i = 0; i < args.Length - 1; i++)
                    if (args[i] == "+connect_lobby" && ulong.TryParse(args[i + 1], out var id))
                    {
                        Log.Info("Joining lobby from the command line: " + id);
                        if (SteamNative.TryInit()) Session.Join(id.ToString(), "");
                    }
            }
            catch { }
        }

        // ================= scenes =================
        static int _lastSceneHandle = int.MinValue;

        public static void PollScene()
        {
            Scene sc;
            try { sc = SceneManager.GetActiveScene(); } catch { return; }
            if (!sc.IsValid() || !sc.isLoaded) return;
            int h = sc.handle;
            if (h == _lastSceneHandle) return;
            _lastSceneHandle = h;
            OnSceneLoaded(sc);
        }

        static bool _splashSkipped;

        /// <summary>Option "skip the DVloper intro": go straight to the menu (once, at game start).</summary>
        public static bool SkipSplash()
        {
            if (_splashSkipped || !Cfg.SkipSplash.Value) return false;
            _splashSkipped = true;
            try
            {
                Log.Info("Skipping the splash screen");
                Cursor.visible = false;
                SceneManager.LoadScene(Cfg.MenuScene.Value);
                return true;
            }
            catch (Exception e) { Log.Warn("Splash skip: " + e.Message); return false; }
        }

        static bool IsPlayScene(string s) => s == Cfg.GameScene.Value || s == Cfg.IntroScene.Value;

        static void OnSceneLoaded(Scene scene)
        {
            try
            {
                _prevScene = CurrentScene;
                CurrentScene = scene.name;
                Log.Info("Scene loaded: " + scene.name);
                if (scene.name == "SplashScreen") { if (SkipSplash()) return; }
                else _splashSkipped = true;
                PlayerSync.OnSceneChanged();
                MomSpider.OnSceneChanged();
                SprayFx.OnSceneChanged();
                EscapeSync.OnSceneChanged();
                FuelSync.OnSceneChanged();
                TagSync.Clear();
                AtticSpider.OnSceneChanged();
                World.Clear();
                _resyncStage = 0;
                MusicSync.Clear();
                AudioStateSync.Clear();
                GameRefs.Clear();
                AiControl.Reset();
                Pvp.Reset();
                _menuButtons.Clear();
                bool commanded = _loadingByHost;
                _loadingByHost = false;
                if (scene.name != Cfg.GameScene.Value) OpenGate(false);
                if (scene.name == Cfg.GameScene.Value)
                {
                    World.Register(scene);
                    if (Session.IsClient) ApplyBetweenScenes();
                }
                if (!Session.Active) return;

                // Main menu chosen in the pause menu (not commanded by the host) = leave the session
                if (scene.name == Cfg.MenuScene.Value && IsPlayScene(_prevScene) && !commanded)
                {
                    Log.Info("Returned to the main menu from the game – leaving the session");
                    Session.Leave();
                    return;
                }

                if (Session.IsHost)
                {
                    bool inGame = scene.name == Cfg.GameScene.Value;
                    var local = Session.Local; if (local != null) { local.Scene = scene.name; local.SceneReady = inGame; }
                    if (scene.name == Cfg.IntroScene.Value)
                    {
                        // host started with the intro – everybody watches it, only the host can skip
                        Session.HostScene = scene.name;
                        BroadcastStartGame(255, scene.name);
                    }
                    else if (inGame && !Session.HostInGame)
                    {
                        Session.HostInGame = true; Session.HostScene = scene.name;
                        foreach (var p in Session.Players.Values) { p.Ghost = false; p.SceneReady = false; }
                        if (Session.PvpOn) Pvp.PickGranny();
                        BroadcastStartGame(255, scene.name);
                        _waitPlayersUntil = Time.unscaledTime + 25f;
                        CloseGate(30f);
                    }
                    else if (!inGame && Session.HostInGame)
                    {
                        Session.HostInGame = false; Session.HostScene = scene.name;
                        // with the host's PlayerPrefs: the end scene chooses its ending from values the game set while
                        // playing (the teddy given to Slendrina) – the clients had their own and got another ending
                        try { PlayerPrefs.Save(); } catch { }
                        var prefs = CollectPrefs();
                        var w = Session.W(Msg.LoadScene); w.Str(scene.name);
                        w.UShort((ushort)prefs.Count);
                        foreach (var (k, type, v) in prefs) { w.Str(k); w.Byte((byte)type); w.Str(v); }
                        Session.Broadcast(w, true);
                        Pvp.OnGameEnded();
                    }
                    Session.BroadcastPlayerList();
                }
                else
                {
                    var me = Session.Local; if (me != null) { me.Scene = scene.name; me.SceneReady = false; }
                    _waitingWorld = scene.name == Cfg.GameScene.Value;
                    if (_waitingWorld) CloseGate(45f); // until the host says everybody is ready
                    if (scene.name == Cfg.IntroScene.Value) DisableClientSkip();
                    SendSceneState();
                }
            }
            catch (Exception e) { Log.Error("OnSceneLoaded: " + e); }
        }

        /// <summary>Client in the intro: only the host can skip it (the skip reaches us as a scene change).</summary>
        static void DisableClientSkip()
        {
            try
            {
                foreach (var o in UnityEngine.Object.FindObjectsOfType(Il2CppInterop.Runtime.Il2CppType.Of<clickSkipButton>(), true))
                {
                    var b = o.TryCast<clickSkipButton>();
                    if (b != null) b.enabled = false;
                }
                Session.Notify(L.T("Intro – only the host can skip it.", "Intro – pominąć może tylko host."), false);
            }
            catch (Exception e) { Log.Debug("DisableClientSkip: " + e.Message); }
        }

        static void SendSceneState()
        {
            var w = Session.W(Msg.SceneState);
            w.Str(CurrentScene); w.Bool(World.Registered); w.Int(World.Count);
            Session.SendToHost(w, true);
        }

        public static void OnWorldFullReceived()
        {
            _waitingWorld = false;
            var me = Session.Local; if (me != null) me.SceneReady = true;
            World.SuppressProposalsUntil = Time.unscaledTime + 3f;
            if (_resyncStage == 0)
            {
                // the local game keeps placing items for a few seconds after loading (startNewDay) –
                // ask for the host's state again afterwards, and until then the host's placement wins
                _resyncStage = 1;
                _resyncAt1 = Time.unscaledTime + 4f; _resyncAt2 = Time.unscaledTime + 10f;
                World.StartupUntil = Time.unscaledTime + 12f;
                Session.Notify(L.T("Synchronised with the host's world", "Zsynchronizowano ze światem hosta"), false);
            }
            else Log.Debug("World re-synchronised with the host");
        }

        static int _resyncStage; static float _resyncAt1, _resyncAt2;

        static void UpdateResync()
        {
            if (!Session.IsClient || _resyncStage == 0 || _resyncStage >= 3 || !GameRefs.InGameScene) return;
            float now = Time.unscaledTime;
            if ((_resyncStage == 1 && now >= _resyncAt1) || (_resyncStage == 2 && now >= _resyncAt2))
            {
                _resyncStage++;
                Session.SendToHost(Session.W(Msg.WorldResync), true);
                Log.Debug("Asked the host for a world re-sync");
            }
        }

        public static void RegisterHandlers()
        {
            Session.On(Msg.StartGame, HandleStartGame);
            Session.On(Msg.StartGo, (p, r) => { if (Session.IsClient && StartHolding) { Log.Info("Everybody is ready – the game starts"); OpenGate(false); } });
            MenuOptions.RegisterHandlers();
            Session.On(Msg.WorldResync, (p, r) =>
            {
                if (!Session.IsHost || p == null || !p.SceneReady || !Session.HostInGame) return;
                World.SendFullTo(p.Id);
            });
            Session.On(Msg.LoadScene, (p, r) =>
            {
                if (!Session.IsClient) return;
                var name = r.Str();
                int n = r.UShort();
                if (n > 0) { BackupPrefs(); for (int i = 0; i < n; i++) { var k = r.Str(); var t = r.Byte(); var v = r.Str(); ApplyPref(k, t, v); } }
                if (name == CurrentScene) return;
                Log.Info($"Host goes to scene {name} ({n} settings)");
                _loadingByHost = true;
                if (name == Cfg.EndScene.Value)
                    Session.Notify(Pvp.LocalIsGranny ? L.T("The survivors escaped – Granny lost!", "Ocalali uciekli – Granny przegrała!") : L.T("Escape successful! You won!", "Ucieczka udana! Wygraliście!"), false);
                SceneManager.LoadScene(name);
            });
            Session.On(Msg.SceneState, (p, r) =>
            {
                if (!Session.IsHost || p == null) return;
                p.Scene = r.Str(); bool reg = r.Bool(); int count = r.Int();
                p.SceneReady = false;
                if (p.Scene == Cfg.GameScene.Value && Session.HostInGame && reg)
                {
                    if (count != World.Count) Log.Warn($"{p.Name}: different number of network objects ({count} vs {World.Count}) – maybe a different game version");
                    World.SendFullTo(p.Id);
                    SendDayCount(p.Id);
                    p.SceneReady = true;
                    p.Ghost = false;
                    if (!StartHolding) { var gw = Session.W(Msg.StartGo); Session.SendTo(p.Id, gw, true); }
                }
                else if (p.Scene == Cfg.EndScene.Value && Session.HostInGame)
                {
                    if (Pvp.Active) Session.Notify2($"{p.Name} escaped from Granny's house – the survivors win!", $"{p.Name} uciekł(a) z domu Granny – wygrywają ocalali!", true);
                    else Session.Notify2($"{p.Name} escaped from Granny's house! You win!", $"{p.Name} uciekł(a) z domu Granny! Wygrana!", true);
                    SceneManager.LoadScene(Cfg.EndScene.Value);
                }
                Session.BroadcastPlayerList();
            });
            Session.On(Msg.DayEnd, (p, r) =>
            {
                if (!Session.IsClient) return;
                float days = r.Float();
                _heldDayEndAt = 0;
                BeginTransition();
                foreach (var pl in Session.Players.Values) pl.Ghost = false;
                if (Time.unscaledTime - _lastLocalDayEnd > 8f) StartLocalDayEnd(days);
            });
            Session.On(Msg.DayCount, (p, r) =>
            {
                if (!Session.IsClient) return;
                float days = r.Float();
                // between days only – during the end-of-day sequence the local game counts the new day itself
                if (InTransition || _heldDayEndAt > 0 || !U.Alive(GameRefs.NewDay)) return;
                try
                {
                    var nd = GameRefs.NewDay;
                    if (Mathf.Abs(nd.daysCounter - days) > 0.01f) { Log.Info($"Day counter {nd.daysCounter} -> {days} (host)"); nd.daysCounter = days; }
                }
                catch (Exception e) { Log.Debug("day count: " + e.Message); }
            });
            Session.On(Msg.RequestDayEnd, (p, r) =>
            {
                if (!Session.IsHost || p == null) return;
                var why = r.Str();
                if (Session.GodMode) { Log.Info($"God mode – {p.Name}'s death ({why}) does not count"); return; }
                if (InTransition) return;
                if (Session.GhostMode) { if (!p.Ghost) PlayerCaught(p.Id, why == "died" ? "fall" : why); return; }
                Session.Notify2($"{p.Name} died", $"{p.Name} zginął(-ęła)", true);
                StartLocalDayEnd(-1);
            });
            Session.On(Msg.Caught, (p, r) =>
            {
                if (!Session.IsClient) return;
                var id = r.Byte(); var reason = r.Str();
                if (id != Session.LocalId)
                {
                    PlayCatchSoundFor(id, reason);
                    Session.Notify(reason.Contains("spider") ? L.T($"The spider caught {Session.NameOf(id)}!", $"Pająk złapał gracza {Session.NameOf(id)}!")
                                                      : L.T($"Granny caught {Session.NameOf(id)}!", $"Granny złapała gracza {Session.NameOf(id)}!"), false);
                    return;
                }
                if (Session.Mode == CoopMode.SharedDays) BeginTransition();
                else { Spectator.DelayStart(3.5f); Session.Notify(L.T("You were caught!", "Zostałeś złapany!"), false); }
                // the catch looks like in single player: Granny's grab and the jumpscare (not after a deadly fall –
                // the game already played that one)
                if (reason == "bed") PlayLocalBedCatch();
                else if (reason == "Granny") PlayLocalJumpscare();
                else if (reason == "spider") MomSpider.PlayLocalEat();
                else if (reason == "attic spider") AtticSpider.PlayLocalBite();
            });
            Session.On(Msg.Ghost, (p, r) =>
            {
                if (!Session.IsClient) return;
                var id = r.Byte(); var g = r.Bool();
                if (Session.Players.TryGetValue(id, out var pl)) pl.Ghost = g;
                if (id == Session.LocalId) Session.Notify(g ? L.T("You are spectating until the end of the day (LMB / RMB: switch player).", "Obserwujesz innych do końca dnia (LPM / PPM: zmiana gracza).") : L.T("You are back in the game!", "Wracasz do gry!"), false);
            });
        }

        // ================= starting the game =================
        /// <summary>
        /// Host (F1 → Start game): runs the game's own "Continue" logic, which reads the intro setting and loads
        /// either the intro (ForestScene) or Granny's house. Difficulty is chosen in the game's Play/Options menu.
        /// </summary>
        public static void HostStartGame()
        {
            if (!Session.IsHost) return;
            if (!Session.AllReady) { Session.Notify(L.T("Not all players are ready.", "Nie wszyscy gracze są gotowi."), false); return; }
            if (IsPlayScene(CurrentScene)) { Session.Notify(L.T("The game is already running.", "Gra już trwa."), false); return; }
            Log.Info("Host starts the game");
            try
            {
                foreach (var o in UnityEngine.Object.FindObjectsOfType(Il2CppInterop.Runtime.Il2CppType.Of<ClickContinueButton>(), true))
                {
                    var b = o.TryCast<ClickContinueButton>();
                    if (b == null) continue;
                    _allowContinue = true;
                    b.TaskOnClick();
                    _allowContinue = false;
                    return;
                }
            }
            catch (Exception e) { Log.Warn("Continue button: " + e.Message); _allowContinue = false; }
            SceneManager.LoadScene(Cfg.GameScene.Value);
        }

        static bool _allowContinue;

        static void BroadcastStartGame(byte only, string scene)
        {
            var w = Session.W(Msg.StartGame);
            w.Str(scene);
            var prefs = CollectPrefs();
            w.UShort((ushort)prefs.Count);
            foreach (var (k, type, v) in prefs) { w.Str(k); w.Byte((byte)type); w.Str(v); }
            w.Bool(true);
            w.Bool(BetweenScenesValues.difficultyEasy); w.Bool(BetweenScenesValues.difficultyMedium); w.Bool(BetweenScenesValues.difficultyHard);
            if (only == 255) Session.Broadcast(w, true); else Session.SendTo(only, w, true);
            Log.Info($"Sent StartGame {scene} ({prefs.Count} settings)");
        }

        static bool _bsvValid; static bool _bsvE, _bsvM, _bsvH;

        static void HandleStartGame(NetPlayer from, NetReader r)
        {
            if (!Session.IsClient) return;
            var scene = r.Str();
            int n = r.UShort();
            for (int i = 0; i < n; i++)
            {
                var k = r.Str(); var t = r.Byte(); var v = r.Str();
                ApplyPref(k, t, v);
            }
            try { PlayerPrefs.Save(); } catch { }
            _bsvValid = r.Bool();
            if (_bsvValid) { _bsvE = r.Bool(); _bsvM = r.Bool(); _bsvH = r.Bool(); ApplyBetweenScenes(); }
            if (scene == CurrentScene)
            {
                // already there (e.g. the intro ended here before the host skipped) – just report readiness
                SendSceneState();
                return;
            }
            Session.Notify(scene == Cfg.IntroScene.Value ? L.T("The host started the game – intro...", "Host rozpoczął grę – intro...") : L.T("Loading Granny's house...", "Ładowanie domu Granny..."), false);
            _loadingByHost = true;
            Log.Info("Loading scene " + scene + " (host)");
            SceneManager.LoadScene(scene);
        }

        static void ApplyBetweenScenes()
        {
            if (!_bsvValid) return;
            try { BetweenScenesValues.difficultyEasy = _bsvE; BetweenScenesValues.difficultyMedium = _bsvM; BetweenScenesValues.difficultyHard = _bsvH; }
            catch (Exception e) { Log.Debug("BetweenScenesValues: " + e.Message); }
        }

        /// <summary>Blocks the menu buttons that start the game while in a session (the host starts from F1).</summary>
        public static bool BlockLocalPlay(out string why)
        {
            why = null;
            if (!Session.Active || _allowContinue) return false;
            why = Session.IsHost ? L.T("Start the game from the F1 window.", "Rozpocznij grę w oknie F1.") : L.T("The host starts the game.", "Grę rozpoczyna host.");
            return true;
        }

        // ---- menu: hide only the "Continue" button while in a lobby ----
        // (ClickContinueButton2 sits on a panel with the other menu options – it must NOT be touched)
        static readonly List<GameObject> _menuButtons = new();
        static float _nextMenuScan;

        static void UpdateMenuButtons()
        {
            if (CurrentScene != Cfg.MenuScene.Value) return;
            if (Time.unscaledTime >= _nextMenuScan)
            {
                _nextMenuScan = Time.unscaledTime + 1f;
                _menuButtons.RemoveAll(g => !U.Alive(g));
                if (_menuButtons.Count == 0)
                {
                    try
                    {
                        foreach (var o in UnityEngine.Object.FindObjectsOfType(Il2CppInterop.Runtime.Il2CppType.Of<ClickContinueButton>(), true))
                        {
                            var c = o.TryCast<ClickContinueButton>();
                            if (c == null) continue;
                            GameObject g = null;
                            try { if (U.Alive(c.btn1)) g = c.btn1.gameObject; } catch { }
                            if (g == null) g = c.gameObject;
                            // never hide a container that also holds other buttons
                            int buttons = 0;
                            try { buttons = g.GetComponentsInChildren(Il2CppInterop.Runtime.Il2CppType.Of<UnityEngine.UI.Button>(), true).Length; } catch { }
                            if (buttons > 1) { Log.Debug("Continue button shares its object with other buttons – not hidden: " + g.name); continue; }
                            _menuButtons.Add(g);
                            Log.Debug("Continue button: " + g.name);
                        }
                    }
                    catch (Exception e) { Log.Debug("menu buttons: " + e.Message); }
                }
            }
            bool hide = Session.Active;
            foreach (var g in _menuButtons)
                if (U.Alive(g) && hide && g.activeSelf) { g.SetActive(false); _hiddenByUs.Add(g); }
        }

        static readonly HashSet<GameObject> _hiddenByUs = new();

        static void RestoreMenuButtons()
        {
            foreach (var g in _hiddenByUs) if (U.Alive(g)) g.SetActive(true);
            _hiddenByUs.Clear();
        }

        // ================= PlayerPrefs (host game settings) =================
        [DllImport("advapi32.dll", CharSet = CharSet.Unicode)] static extern int RegOpenKeyEx(UIntPtr hKey, string subKey, int options, int sam, out UIntPtr result);
        [DllImport("advapi32.dll", CharSet = CharSet.Unicode)] static extern int RegEnumValue(UIntPtr hKey, int index, StringBuilder name, ref int nameLen, IntPtr reserved, IntPtr type, IntPtr data, IntPtr dataLen);
        [DllImport("advapi32.dll")] static extern int RegCloseKey(UIntPtr hKey);
        static readonly UIntPtr HKCU = new UIntPtr(0x80000001u);

        static List<string> PrefKeys()
        {
            var keys = new List<string>();
            try
            {
                var sub = $"Software\\{Application.companyName}\\{Application.productName}";
                if (RegOpenKeyEx(HKCU, sub, 0, 0x20019, out var h) != 0) return keys;
                for (int i = 0; i < 4096; i++)
                {
                    var sb = new StringBuilder(512); int len = 512;
                    if (RegEnumValue(h, i, sb, ref len, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero) != 0) break;
                    var name = sb.ToString();
                    var m = Regex.Match(name, @"^(.*)_h\d+$");
                    keys.Add(m.Success ? m.Groups[1].Value : name);
                }
                RegCloseKey(h);
            }
            catch (Exception e) { Log.Debug("PrefKeys: " + e.Message); }
            return keys.Distinct().ToList();
        }

        static List<(string, int, string)> CollectPrefs()
        {
            var list = new List<(string, int, string)>();
            Regex ex;
            try { ex = new Regex(Cfg.PrefsExcludeRegex.Value); } catch { ex = new Regex("^$"); }
            foreach (var k in PrefKeys())
            {
                if (ex.IsMatch(k)) continue;
                if (TryReadPref(k, out var t, out var v)) list.Add((k, t, v));
            }
            return list;
        }

        const int IntSentinel = int.MinValue + 7;
        static bool TryReadPref(string k, out int type, out string val)
        {
            type = 0; val = "";
            try
            {
                if (!PlayerPrefs.HasKey(k)) return false;
                int iv = PlayerPrefs.GetInt(k, IntSentinel);
                if (iv != IntSentinel) { type = 0; val = iv.ToString(); return true; }
                float fv = PlayerPrefs.GetFloat(k, float.NaN);
                if (!float.IsNaN(fv)) { type = 1; val = BitConverter.SingleToInt32Bits(fv).ToString(); return true; }
                var sv = PlayerPrefs.GetString(k, "\u0001");
                if (sv != "\u0001") { type = 2; val = sv; return true; }
            }
            catch { }
            return false;
        }

        static void ApplyPref(string k, int type, string v)
        {
            try
            {
                if (!_prefsBackup.ContainsKey(k))
                {
                    if (TryReadPref(k, out var ot, out var ov)) _prefsBackup[k] = (ot, ov); else _prefsBackup[k] = (-1, null);
                }
                switch (type)
                {
                    case 0: PlayerPrefs.SetInt(k, int.Parse(v)); break;
                    case 1: PlayerPrefs.SetFloat(k, BitConverter.Int32BitsToSingle(int.Parse(v))); break;
                    default: PlayerPrefs.SetString(k, v); break;
                }
            }
            catch (Exception e) { Log.Debug("ApplyPref " + k + ": " + e.Message); }
        }

        /// <summary>Saves the player's own settings once (restored when the session ends) before the host's are applied.</summary>
        public static void BackupPrefs()
        {
            foreach (var k in PrefKeys())
                if (!_prefsBackup.ContainsKey(k) && TryReadPref(k, out var t, out var v)) _prefsBackup[k] = (t, v);
        }

        static void RestorePrefs()
        {
            if (_prefsBackup.Count == 0) return;
            foreach (var kv in _prefsBackup)
            {
                try
                {
                    var (t, v) = kv.Value;
                    if (t < 0) PlayerPrefs.DeleteKey(kv.Key);
                    else if (t == 0) PlayerPrefs.SetInt(kv.Key, int.Parse(v));
                    else if (t == 1) PlayerPrefs.SetFloat(kv.Key, BitConverter.Int32BitsToSingle(int.Parse(v)));
                    else PlayerPrefs.SetString(kv.Key, v);
                }
                catch { }
            }
            try { PlayerPrefs.Save(); } catch { }
            _prefsBackup.Clear();
            Log.Info("Restored your own game settings from before the session");
        }

        // ================= end of day / catches =================
        static void BeginTransition()
        {
            _transitionUntil = Time.unscaledTime + 12f;
            World.SuppressProposalsUntil = Time.unscaledTime + 12f;
        }

        /// <summary>
        /// Postfix of endDay.EndDay – the local game started the end of the day. Returns true when that end-of-day
        /// sequence must NOT run:
        ///  * on a client the host decides when a day ends – the local sequence starts with the host's DayEnd
        ///    (which also carries the host's day count, so the days never drift apart);
        ///  * "last survivor": a caught player only spectates while others are still alive.
        /// </summary>
        public static bool OnLocalEndDayStarted(bool underBed = false)
        {
            if (!Session.Active || !GameRefs.InGameScene) return false;
            float now = Time.unscaledTime;
            // god mode (host's debug panel): a death does not end anybody's day
            if (Session.GodMode && !_ownDayEnd) { Log.Info("God mode – the local end of the day is skipped"); return true; }
            if (_ownDayEnd)
            {
                if (Session.IsHost) BroadcastDayEnd();
                return false;
            }
            if (Session.IsClient)
            {
                if (!Session.HostInGame) return false;
                // the host's end of day already runs here (e.g. it came before the jumpscare finished) – never a second one
                if (now - _lastLocalDayEnd < 8f) return true;
                // the game may call EndDay many times in a row (e.g. every frame after a deadly fall)
                if (now - _lastEndDayPostfix >= 8f)
                {
                    _lastEndDayPostfix = now;
                    _heldDayEndAt = now;
                    if (!(Session.Local?.Ghost ?? false)) { var w = Session.W(Msg.RequestDayEnd); w.Str("died"); Session.SendToHost(w, true); }
                    Log.Info("End of day started by the local game – waiting for the host");
                }
                return true;
            }
            // an end-of-day sequence is already running – never a second one (the day would be counted twice)
            if (now - _lastLocalDayEnd < 8f) { if (underBed) AiControl.AfterHostBedCatch(); return true; }
            // Granny looked under the bed for another player who hides there with the host – with "catch everybody in
            // the hiding spot" off only that player is caught, the host stays hidden
            if (underBed && !Session.SpotCatchAll && AiControl.RecentRemoteCatch)
            {
                Log.Info("Granny found another player under the host's bed – the host stays hidden");
                AiControl.AfterHostBedCatch();
                return true;
            }
            if (Session.GhostMode && !InTransition)
            {
                int others = Session.Players.Values.Count(p => !p.IsLocal && !p.Ghost && p.SceneReady && !Pvp.IsGranny(p.Id));
                if (others > 0)
                {
                    // Granny caught the host (also under a bed) – the host spectates, the day goes on for the others
                    int spot = GameRefs.LocalSpot;
                    if (!(Session.Local?.Ghost ?? false)) PlayerCaught(Session.LocalId, underBed ? "bed" : "died");
                    if (underBed) { AiControl.AfterHostBedCatch(); CatchOthersInSpot(spot, Session.LocalId, "bed"); }
                    return true;
                }
            }
            _pendingDayEndAt = 0;
            BroadcastDayEnd();
            return false;
        }

        static void BroadcastDayEnd()
        {
            float now = Time.unscaledTime;
            _lastEndDayPostfix = now; _lastLocalDayEnd = now;
            BeginTransition();
            foreach (var p in Session.Players.Values) p.Ghost = false;
            float days = 0; try { days = GameRefs.NewDay != null ? GameRefs.NewDay.daysCounter : 0; } catch { }
            var w = Session.W(Msg.DayEnd); w.Float(days); Session.Broadcast(w, true);
            Session.BroadcastPlayerList();
            Log.Info($"End of day {days} – sent to players");
        }

        /// <summary>Host: the current day number, sent regularly so a client's counter can never drift.</summary>
        static void SendDayCount(byte to = 255)
        {
            if (!Session.IsHost || !U.Alive(GameRefs.NewDay)) return;
            float days; try { days = GameRefs.NewDay.daysCounter; } catch { return; }
            var w = Session.W(Msg.DayCount); w.Float(days);
            if (to == 255) Session.Broadcast(w, true); else Session.SendTo(to, w, true);
        }

        public static void StartLocalDayEnd(float hostDays)
        {
            var ed = GameRefs.EndDay;
            if (!U.Alive(ed)) { Log.Warn("No endDay in the scene"); return; }
            try { if (hostDays >= 0 && U.Alive(GameRefs.NewDay)) GameRefs.NewDay.daysCounter = hostDays; } catch { }
            _lastLocalDayEnd = Time.unscaledTime;
            _heldDayEndAt = 0;
            BeginTransition();
            ApplyingRemoteDayEnd = !Session.IsHost;
            _ownDayEnd = true;
            try
            {
                // a player lying under a bed needs the game's own under-bed end of day (it takes the player out of the bed)
                var bc = GameRefs.BedCatch;
                if (U.Alive(bc) && LocalUnderBed()) { Log.Info("End of day under a bed"); ed.StartCoroutine(bc.EndDayUnderBed()); }
                else ed.StartCoroutine(ed.EndDay());
            }
            catch (Exception e) { Log.Error("StartLocalDayEnd: " + e); }
            finally { ApplyingRemoteDayEnd = false; _ownDayEnd = false; }
        }

        static bool LocalUnderBed()
        {
            try
            {
                var g = GameRefs.Granny;
                return U.Alive(g) && (g.hidingUnderBed1 || g.hidingUnderBed2 || g.hidingUnderBed3 || g.playerHidingUnderBed);
            }
            catch { return false; }
        }

        // ================= hiding spots shared by several players (host options) =================
        static int SpotOfButton(string type) => type switch
        {
            "bedButton" => 1, "bedButton2" => 2, "bedButton3" => 3, "coffinButton" => 4, "coffinButtonBackyard" => 5, "insideCarButton" => 6, _ => 0,
        };

        /// <summary>Another player hides in hiding spot <paramref name="spot"/> (1–6).</summary>
        public static bool SpotTaken(int spot)
        {
            foreach (var p in Session.Players.Values)
            {
                if (p.IsLocal || p.Ghost) continue;
                if (PlayerSync.TryGetState(p.Id, out var s) && (s.Flags & PlayerSync.F_Hiding) != 0 && s.Spot == spot) return true;
            }
            return false;
        }

        static float _nextTakenNotice;
        /// <summary>
        /// Hiding-spot button (bed, coffin, car): with "one player per hiding spot" the local player cannot get in
        /// while somebody else is inside. Getting out always works.
        /// </summary>
        public static bool HideSpotBlocked(Component button)
        {
            if (!Session.SpotSingle || !GameRefs.InGameScene || !U.Alive(button) || GameRefs.LocalHiding) return false;
            int spot = SpotOfButton(U.Il2CppTypeName(button));
            if (spot == 0 || !SpotTaken(spot)) return false;
            // tell the player why the hiding spot does not work when they try it nearby
            if (Time.unscaledTime >= _nextTakenNotice && (In.MouseDown(0) || In.KeyDown(KeyCode.E)) && U.Alive(GameRefs.PlayerRoot)
                && (button.transform.position - GameRefs.PlayerRoot.position).sqrMagnitude < 16f)
            {
                _nextTakenNotice = Time.unscaledTime + 2f;
                Session.Notify(L.T("Somebody is already hiding there.", "Ktoś już się tam chowa."), false);
            }
            return true;
        }

        /// <summary>
        /// Host, "catch everybody in the hiding spot": Granny found a player in hiding spot <paramref name="spot"/> –
        /// the other players hiding in the same spot are caught too.
        /// </summary>
        public static void CatchOthersInSpot(int spot, byte found, string reason)
        {
            if (!Session.IsHost || !Session.SpotCatchAll || spot < 1 || spot > 6) return;
            foreach (var p in Session.Players.Values.ToList())
            {
                if (p.Id == found || p.Ghost) continue;
                bool inSpot = p.IsLocal ? GameRefs.LocalSpot == spot
                    : PlayerSync.TryGetState(p.Id, out var s) && (s.Flags & PlayerSync.F_Hiding) != 0 && s.Spot == spot;
                if (!inSpot) continue;
                Log.Info($"{p.Name} hid in the same spot – caught too");
                if (p.IsLocal && reason == "bed") { PlayerCaught(p.Id, reason); PlayLocalBedCatch(); }
                else if (p.IsLocal) { PlayerCaught(p.Id, reason); PlayLocalJumpscare(); }
                else PlayerCaught(p.Id, reason);
            }
        }

        /// <summary>
        /// Host: Granny looked into the hiding spot the host hides in (the mod did it for her) – like her own look under
        /// the bed: the bed camera turns to her face and the bed's scream plays, then the catch (the others in the spot too).
        /// </summary>
        public static void HostFoundInSpot(int spot)
        {
            bool bed = spot >= 1 && spot <= 3;
            if (bed) PlayLocalBedCatch();
            PlayerCaught(Session.LocalId, bed ? "bed" : "Granny");
            CatchOthersInSpot(spot, Session.LocalId, bed ? "bed" : "Granny");
        }

        /// <summary>Host: whether player <paramref name="id"/> can be caught now.</summary>
        public static bool CanCatch(byte id)
        {
            if (!Session.IsHost || InTransition || !Session.Players.TryGetValue(id, out var p)) return false;
            if (Session.GodMode) return false;
            if (Session.GhostMode && p.Ghost) return false;
            if (Pvp.IsGranny(id)) return false; // PvP: the Granny player is never caught
            return !(_caughtAt.TryGetValue(id, out var t) && Time.unscaledTime - t < 3f);
        }

        /// <summary>Host: a player was caught (by the AI or reported by a client).</summary>
        public static void PlayerCaught(byte id, string reason)
        {
            if (!CanCatch(id)) return;
            _caughtAt[id] = Time.unscaledTime;
            var p = Session.Players[id];
            Log.Info($"Caught: {p.Name} ({reason})");
            var cw = Session.W(Msg.Caught); cw.Byte(id); cw.Str(reason); Session.Broadcast(cw, true);
            if (id != Session.LocalId) PlayCatchSoundFor(id, reason);

            if (Session.Mode == CoopMode.SharedDays)
            {
                if (reason.Contains("spider")) Session.Notify2($"The spider caught {p.Name}! Everybody loses a day.", $"Pająk złapał gracza {p.Name}! Wszyscy tracicie dzień.", true);
                else Session.Notify2($"Granny caught {p.Name}! Everybody loses a day.", $"Granny złapała gracza {p.Name}! Wszyscy tracicie dzień.", true);
                BeginTransition();
                // the victim first sees the jumpscare, then everybody ends the day
                if (id == Session.LocalId) StartLocalDayEnd(-1); else _pendingDayEndAt = Time.unscaledTime + 4.5f;
                return;
            }
            // LastSurvivor
            p.Ghost = true;
            var gw = Session.W(Msg.Ghost); gw.Byte(id); gw.Bool(true); Session.Broadcast(gw, true);
            if (id == Session.LocalId)
            {
                Spectator.DelayStart(3.5f); // the catch / death animation plays first
                Session.Notify(L.T("You were caught – you spectate the others until the end of the day.", "Zostałeś złapany – obserwujesz innych do końca dnia."), false);
            }
            if (!AiControl.HoldingCatch) AiControl.ResetChase();
            int alive = Session.Players.Values.Count(x => !x.Ghost && (x.IsHost || x.SceneReady) && !Pvp.IsGranny(x.Id));
            if (alive == 0)
            {
                Session.Notify2("Everybody was caught – end of the day!", "Wszyscy zostali złapani – koniec dnia!", true);
                // the last victim first sees the catch, then everybody ends the day
                BeginTransition();
                if (id == Session.LocalId) StartLocalDayEnd(-1); else _pendingDayEndAt = Time.unscaledTime + 4.5f;
            }
            else Session.Notify2($"{p.Name} was caught and is spectating. Players alive: {alive}", $"{p.Name} został złapany i obserwuje. Zostało żywych: {alive}", true);
            Session.BroadcastPlayerList();
        }

        /// <summary>The bed's sound holder (its PCaught clip is the scream of the player found under that bed).</summary>
        static soundEffectsBeds BedSound(EnemyAIGranny g, int bed)
        {
            try
            {
                var h = bed == 1 ? g.soundHolder1 : bed == 2 ? g.soundHolder2 : bed == 3 ? g.soundHolder3 : null;
                return U.Alive(h) ? h.GetComponent<soundEffectsBeds>() : null;
            }
            catch { return null; }
        }

        /// <summary>
        /// Granny looked under the bed the local player hides under – like in single player: the bed camera turns
        /// to her face and the scream of that bed plays.
        /// </summary>
        static void PlayLocalBedCatch()
        {
            var g = GameRefs.Granny;
            if (!U.Alive(g)) return;
            try
            {
                Behaviour eyes = null; int bed = 0;
                if (g.hidingUnderBed1 && U.Alive(g.bedCam1)) { bed = 1; var b = g.bedCam1.GetComponent<bedEyes>(); if (b != null) { b.lookAtGranny = true; eyes = b; } }
                else if (g.hidingUnderBed2 && U.Alive(g.bedCam2)) { bed = 2; var b = g.bedCam2.GetComponent<bedEyes2>(); if (b != null) { b.lookAtGranny = true; eyes = b; } }
                else if (g.hidingUnderBed3 && U.Alive(g.bedCam3)) { bed = 3; var b = g.bedCam3.GetComponent<bedEyes3>(); if (b != null) { b.lookAtGranny = true; eyes = b; } }
                if (eyes == null) { PlayLocalJumpscare(); return; }
                var snd = BedSound(g, bed);
                if (snd != null)
                {
                    var src = snd.GetComponent<AudioSource>();
                    if (U.Alive(src) && src.isActiveAndEnabled) { Events.PlayingRemote = true; try { snd.playerCaught(); } finally { Events.PlayingRemote = false; } }
                    else Events.PlayLocalOnly(snd.PCaught, U.Alive(src) ? src.volume : 1f);
                }
                Log.Info($"Jumpscare: caught under bed {bed}");
            }
            catch (Exception e) { Log.Warn("Bed jumpscare: " + e.Message); PlayLocalJumpscare(); }
        }

        /// <summary>The others hear the scream of a player Granny found under a bed, from that bed.</summary>
        static void PlayCatchSoundFor(byte id, string reason)
        {
            if (reason != "bed" || !GameRefs.InGameScene) return;
            var g = GameRefs.Granny;
            if (!U.Alive(g)) return;
            try
            {
                int spot = PlayerSync.TryGetState(id, out var st) ? st.Spot : 0;
                PlayerSync.TryGetPos(id, out var pos);
                var snd = BedSound(g, spot >= 1 && spot <= 3 ? spot : 1);
                if (snd == null || snd.PCaught == null) return;
                var cam = GameRefs.SpotCamera(spot, pos);
                if (U.Alive(cam)) pos = cam.position;
                var src = snd.GetComponent<AudioSource>();
                Events.PlayLocalOnly(snd.PCaught, U.Alive(src) ? src.volume : 1f, pos);
                Log.Debug($"Catch sound of {Session.NameOf(id)} at bed {spot}");
            }
            catch (Exception e) { Log.Debug("catch sound: " + e.Message); }
        }

        /// <summary>Client: play the local catch sequence (Granny grabs the player) – jumpscare.</summary>
        static void PlayLocalJumpscare()
        {
            var g = GameRefs.Granny;
            if (!U.Alive(g)) return;
            try
            {
                Patches.AllowClientCatchUntil = Time.unscaledTime + 1f;
                g.StartCoroutine(g.Playercaught());
                Log.Info("Jumpscare: local catch sequence");
            }
            catch (Exception e) { Log.Warn("Jumpscare: " + e.Message); }
        }

        // ================= session lifecycle =================
        public static void OnSessionStarted()
        {
            if (Session.IsHost)
            {
                var me = Session.Local; if (me != null) { me.Scene = CurrentScene; me.SceneReady = CurrentScene == Cfg.GameScene.Value; }
                if (CurrentScene == Cfg.GameScene.Value) { Session.HostInGame = true; Session.HostScene = CurrentScene; }
            }
            else SendSceneState();
        }

        public static void OnPlayerJoined(NetPlayer p)
        {
            if (Session.HostInGame) BroadcastStartGame(p.Id, Cfg.GameScene.Value);
            MenuOptions.Resend();
        }

        public static void OnPlayerLeft(NetPlayer p)
        {
            Pvp.OnPlayerLeft(p);
            if (Session.IsHost && Session.GhostMode && Session.HostInGame)
            {
                int alive = Session.Players.Values.Count(x => !x.Ghost && (x.IsHost || x.SceneReady) && !Pvp.IsGranny(x.Id));
                if (alive == 0) StartLocalDayEnd(-1);
            }
        }

        public static void GoToMenu()
        {
            Time.timeScale = 1f;
            Cursor.lockState = CursorLockMode.None; Cursor.visible = true;
            _loadingByHost = true; // our own transition – not a "quit to menu" by the player
            try { SceneManager.LoadScene(Cfg.MenuScene.Value); } catch (Exception e) { Log.Error("GoToMenu: " + e.Message); }
        }

        /// <summary>Called every frame.</summary>
        public static void Update()
        {
            float now = Time.unscaledTime;
            UpdateGate(now);
            if (_pendingDayEndAt > 0 && now >= _pendingDayEndAt)
            {
                _pendingDayEndAt = 0;
                if (Session.IsHost && GameRefs.InGameScene && now - _lastLocalDayEnd > 8f) StartLocalDayEnd(-1);
            }
            if (Session.IsHost && Session.HostInGame && GameRefs.InGameScene && !InTransition && now >= _nextDaySync)
            {
                _nextDaySync = now + 2f;
                SendDayCount();
            }
            // client: the local game ended the day but the host never answered (lost connection, host already in its
            // own transition) – do not leave the player frozen in the caught state
            if (Session.IsClient && _heldDayEndAt > 0 && now - _heldDayEndAt > 12f)
            {
                _heldDayEndAt = 0;
                if (GameRefs.InGameScene && !(Session.Local?.Ghost ?? false) && now - _lastLocalDayEnd > 12f)
                {
                    Log.Warn("No end of day from the host – running it locally");
                    StartLocalDayEnd(-1);
                }
            }
            KeepFallTimerReset();
            UpdateMenuButtons();
            MenuOptions.Update();
            UpdatePauseMenu();
            UpdateResync();
        }

        // ---- the game's fall timer ----
        // FirstPersonController_Egen adds up timeInAir every physics step the player is not grounded (unless caught)
        // and kills the player when he lands after more than fallDead seconds. A spectating player's body is not
        // grounded (its controller is off), and the mod keeps it from looking "caught" – the time added up for the
        // whole spectating, and the first step out of bed the next day was a deadly fall. While spectating, during the
        // change of day and at the start gate the timer stays at 0.
        static void KeepFallTimerReset()
        {
            if (!Session.Active || !GameRefs.InGameScene) return;
            bool hold = InTransition || StartHolding || Session.GodMode || (Session.Local?.Ghost ?? false) || Spectator.Active;
            if (!hold) return;
            var fps = GameRefs.Fps;
            try
            {
                if (!U.Alive(fps)) return;
                if (fps.timeInAir > 0f) fps.timeInAir = 0f;
                if (fps.fallTimerStarted) fps.fallTimerStarted = false;
            }
            catch { }
        }

        // ---- pause menu in multiplayer ----
        // The game pauses with Time.timeScale = 0. In multiplayer the world keeps running, but then
        // FirstPersonController_Egen.FixedUpdate calls MouseLook.UpdateCursorLock every physics step, which
        // re-locks the cursor on every mouse click – the menu buttons never receive the click.
        // So while the menu is open the MouseLook cursor lock is switched off and the cursor is kept free.
        static bool _mlOverridden, _mlOrigLock;

        static void UpdatePauseMenu()
        {
            if (!Session.Active || !GameRefs.InGameScene) { _mlOverridden = false; return; }
            var fps = GameRefs.Fps;
            bool menu = false;
            try { menu = U.Alive(fps) && fps.menuEnabled; } catch { }
            if (!InTransition && !StartHolding && Time.timeScale < 0.01f) Time.timeScale = 1f;
            try
            {
                var ml = U.Alive(fps) ? fps.m_MouseLook : null;
                if (ml == null) return;
                if (menu)
                {
                    if (!_mlOverridden) { _mlOrigLock = ml.lockCursor; _mlOverridden = true; }
                    ml.lockCursor = false;
                    ml.m_cursorIsLocked = false;
                    if (Cursor.lockState != CursorLockMode.None) Cursor.lockState = CursorLockMode.None;
                    if (!Cursor.visible) Cursor.visible = true;
                }
                else if (_mlOverridden)
                {
                    _mlOverridden = false;
                    ml.lockCursor = _mlOrigLock;
                    ml.m_cursorIsLocked = true;   // Resume locks the cursor again
                }
            }
            catch (Exception e) { Log.Debug("pause menu: " + e.Message); }
        }

        public static void OnSessionEnded()
        {
            _pendingDayEndAt = 0; _heldDayEndAt = 0; _caughtAt.Clear();
            OpenGate(false);
            RestoreMenuButtons();
            Spectator.Stop();
            if (IsPlayScene(CurrentScene)) GoToMenu();
            try { World.RestoreClientRules(); } catch (Exception e) { Log.Debug("restore: " + e.Message); }
            PlayerSync.ClearAll();
            _waitingWorld = false;
            RestorePrefs();
        }
    }
}
