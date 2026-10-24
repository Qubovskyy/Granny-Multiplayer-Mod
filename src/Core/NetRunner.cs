using System;
using UnityEngine;

namespace GrannyMP
{
    /// <summary>Main mod loop (injected MonoBehaviour, kept alive across scenes by BepInEx).</summary>
    public class NetRunner : MonoBehaviour
    {
        public NetRunner(IntPtr ptr) : base(ptr) { }

        float _tickAcc;
        bool _started;

        public void Start()
        {
            if (_started) return;
            try
            {
                GameFlow.Init();
                Events.RegisterHandlers();
                MusicSync.RegisterHandlers();
                AudioStateSync.RegisterHandlers();
                VoiceChat.RegisterHandlers();
                GameFlow.CheckCommandLine();
                _started = true;
            }
            catch (Exception e) { Log.Error("NetRunner.Start: " + e); }
        }

        public void Update()
        {
            if (!_started) Start();
            try
            {
                GameFlow.PollScene();
                if (!LobbyUI.CapturingKey && In.KeyDown(Cfg.ToggleKey.Value)) LobbyUI.Toggle();
                if (!LobbyUI.TypingInField && !LobbyUI.ChatOpen && !LobbyUI.Open && !LobbyUI.CapturingKey && Session.Active && In.KeyDown(Cfg.ChatKey.Value)) LobbyUI.ToggleChat(true);

                Session.Update();
                KeepRunningInBackground();
                LobbyBrowser.Update();
                DebugTools.Update();
                GameFlow.Update();
                Display.Update();
                MenuBrand.Update();
                SprayFx.Update();
                EscapeSync.Update();
                FuelSync.Update();
                DebugMenu.Update();
                Spectator.Update();
                VoiceChat.Update();
                AiControl.EnsureRestored();

                if (Session.Active && GameRefs.InGameScene)
                {
                    _tickAcc += Time.unscaledDeltaTime;
                    float step = 1f / Mathf.Max(5, Cfg.TickRate.Value);
                    if (_tickAcc >= step)
                    {
                        _tickAcc = Math.Min(_tickAcc - step, step);
                        PlayerSync.Tick();
                        World.Tick();
                        MusicSync.HostTick();
                    }
                    World.FrameUpdate();
                    AudioStateSync.Tick();
                    PlayerSync.FrameUpdate();
                    Pvp.Update();
                    Weapons.Update();
                    MusicSync.Update();
                }
            }
            catch (Exception e) { Log.Error("NetRunner.Update: " + e); }
        }

        bool _bgSet, _bgWas;

        /// <summary>
        /// In a session the game keeps running when its window is not focused – otherwise an unfocused copy (alt-tab,
        /// or the second copy of the game on one PC) stops and the others lose the connection to it.
        /// </summary>
        void KeepRunningInBackground()
        {
            try
            {
                if (Session.Active && !_bgSet) { _bgSet = true; _bgWas = Application.runInBackground; Application.runInBackground = true; }
                else if (!Session.Active && _bgSet) { _bgSet = false; Application.runInBackground = _bgWas; }
            }
            catch { }
        }

        public void LateUpdate()
        {
            try
            {
                LobbyUI.LateUpdate();
                MenuOptions.LateUpdate();
                Fov.LateUpdate();
                Pvp.LateUpdate();
            }
            catch (Exception e) { Log.Debug("LateUpdate: " + e.Message); }
        }

        public void OnGUI()
        {
            try { LobbyUI.Draw(); }
            catch (Exception e) { Log.Debug("OnGUI: " + e.Message); }
        }

        public void OnApplicationQuit()
        {
            try { Session.Shutdown("Game closed"); } catch { }
        }
    }
}
