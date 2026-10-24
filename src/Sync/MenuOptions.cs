using System;
using System.Collections.Generic;
using UnityEngine;

namespace GrannyMP
{
    /// <summary>
    /// Main menu: the host's game options (Game theme, Difficulty, Extra locks, Light sources, Skip intro) are shown
    /// on the clients' menus while the lobby is in the menu. The clients cannot change them: their controls are
    /// switched off (the component, not only "interactable" – the game turns that back on every frame).
    /// The values are set through the game's own menu controls, so the game saves them like a normal choice.
    /// </summary>
    internal static class MenuOptions
    {
        static gameThemeOptions _theme;
        static dropDownOptions _diff;
        static LightsourceOption _light;
        static DiffEasyTextCheck _locks;
        static ShowIntroOrNot _intro;
        static SkipIntroToggle _introAlt;
        static float _next, _nextFind, _nextSend;
        static string _sent;
        static bool _have; static int _wTheme, _wDiff, _wLight, _wIntro = -1; static bool _wLocks;
        static readonly List<UnityEngine.UI.Selectable> _lockedByUs = new();

        public static void RegisterHandlers() => Session.On(Msg.MenuOptions, Handle);

        /// <summary>A player joined: send the current options again.</summary>
        public static void Resend() => _sent = null;

        static T Find<T>() where T : UnityEngine.Object
        {
            try
            {
                foreach (var o in UnityEngine.Object.FindObjectsOfType(Il2CppInterop.Runtime.Il2CppType.Of<T>(), true))
                {
                    var t = o.TryCast<T>();
                    if (t != null) return t;
                }
            }
            catch { }
            return null;
        }

        static void Refs()
        {
            if (U.Alive(_theme) && U.Alive(_diff) && U.Alive(_light) && U.Alive(_locks) && (U.Alive(_intro) || U.Alive(_introAlt))) return;
            if (Time.unscaledTime < _nextFind) return;
            _nextFind = Time.unscaledTime + 2f;
            if (!U.Alive(_theme)) _theme = Find<gameThemeOptions>();
            if (!U.Alive(_diff)) _diff = Find<dropDownOptions>();
            if (!U.Alive(_light)) _light = Find<LightsourceOption>();
            if (!U.Alive(_locks)) _locks = Find<DiffEasyTextCheck>();
            if (!U.Alive(_intro)) _intro = Find<ShowIntroOrNot>();
            if (!U.Alive(_introAlt)) _introAlt = Find<SkipIntroToggle>();
        }

        public static void Update()
        {
            bool menu = Session.Active && GameFlow.CurrentScene == Cfg.MenuScene.Value;
            if (!menu) { Unlock(); _have = false; _sent = null; return; }
            float now = Time.unscaledTime;
            if (now < _next) return;
            _next = now + 0.25f;
            Refs();
            try
            {
                if (Session.IsHost) HostTick(now);
                else if (Session.IsClient) { Lock(); if (_have) Apply(); }
            }
            catch (Exception e) { Log.Debug("menu options: " + e.Message); }
        }

        static void HostTick(float now)
        {
            int theme = U.Alive(_theme) && _theme.dropdownOptions != null ? _theme.dropdownOptions.value : -1;
            int diff = U.Alive(_diff) && _diff.dropdownOptions != null ? _diff.dropdownOptions.value : -1;
            int light = U.Alive(_light) && _light.dropdownOptions != null ? _light.dropdownOptions.value : -1;
            int locks = U.Alive(_locks) && _locks.extraLockToggle != null ? (_locks.extraLockToggle.isOn ? 1 : 0) : -1;
            var it = IntroToggle();
            int intro = it != null ? (it.isOn ? 1 : 0) : -1;
            var key = $"{theme},{diff},{light},{locks},{intro}";
            if (key == _sent && now < _nextSend) return;
            _sent = key; _nextSend = now + 2f;
            var w = Session.W(Msg.MenuOptions);
            w.Int(theme); w.Int(diff); w.Int(light); w.Int(locks); w.Int(intro);
            Session.Broadcast(w, true);
        }

        static void Handle(NetPlayer from, NetReader r)
        {
            if (!Session.IsClient) return;
            _wTheme = r.Int(); _wDiff = r.Int(); _wLight = r.Int(); int locks = r.Int(); _wIntro = r.Int();
            _wLocks = locks == 1;
            _have = true;
            if (locks < 0) _wLocks = U.Alive(_locks) && _locks.extraLockToggle != null && _locks.extraLockToggle.isOn;
            if (GameFlow.CurrentScene == Cfg.MenuScene.Value) { Refs(); try { Apply(); } catch (Exception e) { Log.Debug("menu options: " + e.Message); } }
        }

        static void Apply()
        {
            if (_wTheme >= 0 && U.Alive(_theme) && _theme.dropdownOptions != null && _theme.dropdownOptions.value != _wTheme)
            { GameFlow.BackupPrefs(); _theme.dropdownOptions.value = _wTheme; Log.Debug("menu options: theme " + _wTheme); }
            if (_wDiff >= 0 && U.Alive(_diff) && _diff.dropdownOptions != null && _diff.dropdownOptions.value != _wDiff)
            { GameFlow.BackupPrefs(); _diff.dropdownOptions.value = _wDiff; Log.Debug("menu options: difficulty " + _wDiff); }
            if (_wLight >= 0 && U.Alive(_light) && _light.dropdownOptions != null && _light.dropdownOptions.value != _wLight)
            { GameFlow.BackupPrefs(); _light.dropdownOptions.value = _wLight; Log.Debug("menu options: light sources " + _wLight); }
            if (U.Alive(_locks) && _locks.extraLockToggle != null && _locks.extraLockToggle.isOn != _wLocks)
            { GameFlow.BackupPrefs(); _locks.extraLockToggle.isOn = _wLocks; Log.Debug("menu options: extra locks " + _wLocks); }
            var it = IntroToggle();
            if (_wIntro >= 0 && it != null && it.isOn != (_wIntro == 1))
            { GameFlow.BackupPrefs(); it.isOn = _wIntro == 1; Log.Debug("menu options: intro toggle " + (_wIntro == 1)); }
        }

        // A switched-off Selectable ignores clicks (Toggle / Dropdown check IsActive()); "interactable" only greys it out
        // and the game sets it back (DiffEasyTextCheck does that to the extra locks every frame).
        /// <summary>
        /// The "show intro" toggle: OptionsMenu/…/ShowIntro/ToggleShowIntro, under the options panel ShowIntroOrNot sits
        /// on (the generic GetComponentsInChildren&lt;Toggle&gt; has no IL2CPP instance – the Type overload is used).
        /// </summary>
        static UnityEngine.UI.Toggle _introToggle; static bool _introMissingLogged;
        static UnityEngine.UI.Toggle IntroToggle()
        {
            if (U.Alive(_introToggle)) return _introToggle;
            try
            {
                var type = Il2CppInterop.Runtime.Il2CppType.Of<UnityEngine.UI.Toggle>();
                Component under = U.Alive(_intro) ? _intro : U.Alive(_introAlt) ? _introAlt : null;
                if (under != null)
                    foreach (var c in under.GetComponentsInChildren(type, true))
                    {
                        var t = c?.TryCast<UnityEngine.UI.Toggle>();
                        if (t != null && t.gameObject.name.IndexOf("Intro", StringComparison.OrdinalIgnoreCase) >= 0) return _introToggle = t;
                    }
                if (U.Alive(_intro) && _intro.m_Toggle != null) return _introToggle = _intro.m_Toggle;
                // anywhere in the menu (inactive panels too)
                foreach (var o in UnityEngine.Object.FindObjectsOfType(type, true))
                {
                    var t = o?.TryCast<UnityEngine.UI.Toggle>();
                    if (t != null && t.gameObject.name == "ToggleShowIntro") return _introToggle = t;
                }
            }
            catch (Exception e) { Log.Debug("intro toggle: " + e.Message); }
            if (!_introMissingLogged && (U.Alive(_intro) || U.Alive(_introAlt))) { _introMissingLogged = true; Log.Debug("menu options: the show-intro toggle was not found"); }
            return null;
        }

        static void LockOne(UnityEngine.UI.Selectable s)
        {
            if (s == null) return;
            if (s.enabled) s.enabled = false;
            if (s.interactable) s.interactable = false;
            if (!_lockedByUs.Contains(s)) _lockedByUs.Add(s);
        }

        static void Lock()
        {
            if (U.Alive(_theme) && _theme.dropdownOptions != null) LockOne(_theme.dropdownOptions);
            if (U.Alive(_diff) && _diff.dropdownOptions != null) LockOne(_diff.dropdownOptions);
            if (U.Alive(_light) && _light.dropdownOptions != null) LockOne(_light.dropdownOptions);
            if (U.Alive(_locks) && _locks.extraLockToggle != null) LockOne(_locks.extraLockToggle);
            var it = IntroToggle();
            if (it != null) LockOne(it);
        }

        /// <summary>Client in the menu, every frame after the game's scripts: the locked controls stay greyed out.</summary>
        public static void LateUpdate()
        {
            if (!Session.IsClient || _lockedByUs.Count == 0 || GameFlow.CurrentScene != Cfg.MenuScene.Value) return;
            foreach (var s in _lockedByUs) { try { if (U.Alive(s)) { if (s.enabled) s.enabled = false; if (s.interactable) s.interactable = false; } } catch { } }
        }

        static void Unlock()
        {
            if (_lockedByUs.Count == 0) return;
            foreach (var s in _lockedByUs) { try { if (U.Alive(s)) { s.enabled = true; s.interactable = true; } } catch { } }
            _lockedByUs.Clear();
        }
    }
}
