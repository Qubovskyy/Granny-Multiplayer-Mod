using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Il2CppInterop.Runtime;
using UnityEngine;

namespace GrannyMP
{
    /// <summary>
    /// Screen resolution and window mode (fullscreen / borderless window / window), chosen in the F1 settings and
    /// kept in [Display]. Screen.SetResolution and Screen.fullScreenMode are stripped from the game's code, so the
    /// engine's own functions (icalls) are called directly. The choice is applied again when the game starts.
    /// </summary>
    internal static class Display
    {
        // the order of the choices in the settings; the values are Unity's FullScreenMode
        public const int Fullscreen = 0, Borderless = 1, Windowed = 3;
        public static readonly int[] Modes = { Fullscreen, Borderless, Windowed };

        [StructLayout(LayoutKind.Sequential)] struct Rate { public uint Num, Den; }
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate void SetResFn(int w, int h, int mode, ref Rate rate);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int GetModeFn();
        static SetResFn _setRes; static GetModeFn _getMode;
        static bool _resolved, _appliedSaved;
        static float _applySavedAt = -1f;

        static bool Resolve()
        {
            if (_resolved) return _setRes != null;
            _resolved = true;
            try { _setRes = IL2CPP.ResolveICall<SetResFn>("UnityEngine.Screen::SetResolution_Injected"); }
            catch (Exception e) { Log.Warn("Screen resolution cannot be changed (engine function not found): " + e.Message); }
            try { _getMode = IL2CPP.ResolveICall<GetModeFn>("UnityEngine.Screen::get_fullScreenMode"); }
            catch (Exception e) { Log.Debug("get_fullScreenMode: " + e.Message); }
            return _setRes != null;
        }

        public static bool Available => Resolve();

        public static int CurrentMode
        {
            get
            {
                Resolve();
                try { if (_getMode != null) return _getMode(); } catch { }
                try { return Screen.fullScreen ? Borderless : Windowed; } catch { return Borderless; }
            }
        }

        public static string ModeName(int mode) => mode switch
        {
            Fullscreen => L.T("Fullscreen", "Pełny ekran"),
            Borderless => L.T("Borderless window", "Okno bez ramek"),
            Windowed => L.T("Window", "Okno"),
            _ => L.T("Fullscreen (maximized)", "Pełny ekran (zmaksymalizowany)"),
        };

        /// <summary>The screen's resolutions (each size once, smallest first) plus the current size of the game.</summary>
        public static List<(int w, int h)> Resolutions()
        {
            var list = new List<(int w, int h)>();
            try
            {
                var arr = Screen.resolutions;
                if (arr != null)
                    for (int i = 0; i < arr.Length; i++)
                    {
                        var r = (arr[i].width, arr[i].height);
                        if (r.Item1 >= 640 && r.Item2 >= 480 && !list.Contains(r)) list.Add(r);
                    }
            }
            catch (Exception e) { Log.Debug("resolutions: " + e.Message); }
            var cur = Current;
            if (!list.Contains(cur)) list.Add(cur);
            list.Sort((a, b) => a.w != b.w ? a.w.CompareTo(b.w) : a.h.CompareTo(b.h));
            return list;
        }

        public static (int w, int h) Current => (Screen.width, Screen.height);

        public static bool Apply((int w, int h) res, int mode, bool save)
        {
            if (!Resolve()) return false;
            try
            {
                var rate = new Rate { Num = 0, Den = 1 }; // the screen's own refresh rate
                _setRes(res.w, res.h, mode, ref rate);
                Log.Info($"Screen: {res.w}x{res.h}, {ModeName(mode)}");
                if (save) { Cfg.ScreenResolution.Value = $"{res.w}x{res.h}"; Cfg.ScreenMode.Value = mode; }
                return true;
            }
            catch (Exception e) { Log.Warn("Screen resolution: " + e.Message); return false; }
        }

        static bool TryParse(string s, out (int w, int h) r)
        {
            r = default;
            if (string.IsNullOrEmpty(s)) return false;
            var p = s.ToLowerInvariant().Split('x');
            if (p.Length != 2 || !int.TryParse(p[0].Trim(), out int w) || !int.TryParse(p[1].Trim(), out int h) || w < 640 || h < 480) return false;
            r = (w, h); return true;
        }

        public static (int w, int h) Saved(out bool have) { have = TryParse(Cfg.ScreenResolution.Value, out var r); return have ? r : Current; }

        /// <summary>Once, shortly after the game started: the resolution chosen in the settings (the game may set its own).</summary>
        public static void Update()
        {
            if (_appliedSaved) return;
            if (_applySavedAt < 0f) { _applySavedAt = Time.unscaledTime + 1.5f; return; }
            if (Time.unscaledTime < _applySavedAt) return;
            _appliedSaved = true;
            var res = Saved(out bool have);
            int mode = Cfg.ScreenMode.Value;
            if (!have || Array.IndexOf(Modes, mode) < 0) return;
            if (res == Current && mode == CurrentMode) return;
            Apply(res, mode, false);
        }
    }
}
