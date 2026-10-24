using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.InteropServices;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes;
using UnityEngine;

namespace GrannyMP
{
    internal static class U
    {
        public static bool Alive(UnityEngine.Object o)
        {
            if (o == null) return false;
            try { return o.Pointer != IntPtr.Zero && !o.WasCollected && o != null; } catch { return false; }
        }

        public static uint Fnv(string s)
        {
            uint h = 2166136261;
            foreach (var ch in s) { h ^= ch; h *= 16777619; }
            return h & 0x7FFFFFFF; // top bit reserved for spawned objects
        }

        public static string Il2CppTypeName(Il2CppSystem.Object o)
        {
            try { return o.GetIl2CppType().Name; } catch { return ""; }
        }

        /// <summary>All scene transforms (including inactive) in hierarchy order.</summary>
        public static void CollectAll(Transform t, List<Transform> outList)
        {
            outList.Add(t);
            int n = t.childCount;
            for (int i = 0; i < n; i++) CollectAll(t.GetChild(i), outList);
        }

        public static bool IsUnder(Transform t, Transform root)
        {
            if (!Alive(root)) return false;
            while (t != null) { if (t.Pointer == root.Pointer) return true; t = t.parent; }
            return false;
        }

        // Generic GetComponentInChildren<T> and Resources.FindObjectsOfTypeAll<T> are broken in this game
        // (the restored IL violates type constraints). We use the Il2CppType versions instead.
        public static T ChildComp<T>(Component c, bool inactive = true) where T : Component
        {
            if (!Alive(c)) return null;
            try { var r = c.GetComponentInChildren(Il2CppInterop.Runtime.Il2CppType.Of<T>(), inactive); return r == null ? null : r.TryCast<T>(); }
            catch { return null; }
        }

        public static List<T> AllOfType<T>() where T : UnityEngine.Object
        {
            var res = new List<T>();
            try
            {
                foreach (var o in Resources.FindObjectsOfTypeAll(Il2CppInterop.Runtime.Il2CppType.Of<T>()))
                {
                    var t = o?.TryCast<T>(); if (t != null) res.Add(t);
                }
            }
            catch (Exception e) { Log.Debug("AllOfType: " + e.Message); }
            return res;
        }

        public static bool HasCameraBelow(Transform t)
        {
            try
            {
                return t.GetComponentInChildren(Il2CppInterop.Runtime.Il2CppType.Of<Camera>(), true) != null
                    || t.GetComponentInChildren(Il2CppInterop.Runtime.Il2CppType.Of<AudioListener>(), true) != null;
            }
            catch { return false; }
        }

        public static bool IsUI(Transform t)
        {
            try { return t.TryCast<RectTransform>() != null; } catch { return false; }
        }

        static readonly Dictionary<string, Type> _types = new();
        /// <summary>Typ z Assembly-CSharp (interop) po nazwie (np. "EnemyAIGranny").</summary>
        public static Type GameType(string name)
        {
            if (_types.TryGetValue(name, out var t)) return t;
            t = typeof(EnemyAIGranny).Assembly.GetType(name) ?? typeof(EnemyAIGranny).Assembly.GetType("UnityStandardAssets.Characters.FirstPerson." + name);
            _types[name] = t;
            return t;
        }

        /// <summary>Creates an interop wrapper of type T pointing at the same native object.</summary>
        public static object Wrap(Type t, IntPtr ptr) => Activator.CreateInstance(t, ptr);

        public static float Dist(Vector3 a, Vector3 b) => (a - b).magnitude;
        public static float FlatDist(Vector3 a, Vector3 b) { a.y = 0; b.y = 0; return (a - b).magnitude; }

        // --------- schowek (Win32, fallback GUIUtility) ---------
        [DllImport("user32.dll")] static extern bool OpenClipboard(IntPtr h);
        [DllImport("user32.dll")] static extern bool CloseClipboard();
        [DllImport("user32.dll")] static extern bool EmptyClipboard();
        [DllImport("user32.dll")] static extern IntPtr SetClipboardData(uint fmt, IntPtr mem);
        [DllImport("user32.dll")] static extern IntPtr GetClipboardData(uint fmt);
        [DllImport("kernel32.dll")] static extern IntPtr GlobalAlloc(uint flags, UIntPtr bytes);
        [DllImport("kernel32.dll")] static extern IntPtr GlobalLock(IntPtr h);
        [DllImport("kernel32.dll")] static extern bool GlobalUnlock(IntPtr h);

        public static void CopyToClipboard(string s)
        {
            try { GUIUtility.systemCopyBuffer = s; return; } catch { }
            try
            {
                if (!OpenClipboard(IntPtr.Zero)) return;
                EmptyClipboard();
                var bytes = (s.Length + 1) * 2;
                var h = GlobalAlloc(0x0002, (UIntPtr)bytes);
                var p = GlobalLock(h);
                var chars = (s + "\0").ToCharArray();
                Marshal.Copy(chars, 0, p, chars.Length);
                GlobalUnlock(h);
                SetClipboardData(13, h);
                CloseClipboard();
            }
            catch (Exception e) { Log.Warn("Clipboard: " + e.Message); }
        }

        public static string PasteFromClipboard()
        {
            try { var s = GUIUtility.systemCopyBuffer; if (!string.IsNullOrEmpty(s)) return s; } catch { }
            try
            {
                if (!OpenClipboard(IntPtr.Zero)) return "";
                var h = GetClipboardData(13);
                string r = "";
                if (h != IntPtr.Zero) { var p = GlobalLock(h); r = Marshal.PtrToStringUni(p) ?? ""; GlobalUnlock(h); }
                CloseClipboard();
                return r;
            }
            catch { return ""; }
        }
    }

    /// <summary>Input that works with both the legacy Input Manager and the new Input System.</summary>
    internal static class In
    {
        static bool _legacyBroken;

        public static bool KeyDown(KeyCode k)
        {
            if (!_legacyBroken)
            {
                try { return Input.GetKeyDown(k); } catch { _legacyBroken = true; }
            }
            try
            {
                var kb = UnityEngine.InputSystem.Keyboard.current;
                if (kb == null) return false;
                var key = Map(k);
                if (key == UnityEngine.InputSystem.Key.None) return false;
                return kb[key].wasPressedThisFrame;
            }
            catch { return false; }
        }

        /// <summary>Key (or mouse button Mouse0–Mouse6) held down.</summary>
        public static bool Key(KeyCode k)
        {
            if (!_legacyBroken)
            {
                try { return Input.GetKey(k); } catch { _legacyBroken = true; }
            }
            try
            {
                if (k >= KeyCode.Mouse0 && k <= KeyCode.Mouse6)
                {
                    var m = UnityEngine.InputSystem.Mouse.current;
                    if (m == null) return false;
                    switch (k)
                    {
                        case KeyCode.Mouse0: return m.leftButton.isPressed;
                        case KeyCode.Mouse1: return m.rightButton.isPressed;
                        case KeyCode.Mouse2: return m.middleButton.isPressed;
                        case KeyCode.Mouse3: return m.backButton.isPressed;
                        case KeyCode.Mouse4: return m.forwardButton.isPressed;
                        default: return false;
                    }
                }
                var kb = UnityEngine.InputSystem.Keyboard.current;
                if (kb == null) return false;
                var key = Map(k);
                if (key == UnityEngine.InputSystem.Key.None) return false;
                return kb[key].isPressed;
            }
            catch { return false; }
        }

        /// <summary>Mouse button pressed this frame (0 = left, 1 = right).</summary>
        public static bool MouseDown(int b)
        {
            if (!_legacyBroken)
            {
                try { return Input.GetMouseButtonDown(b); } catch { _legacyBroken = true; }
            }
            try
            {
                var m = UnityEngine.InputSystem.Mouse.current;
                if (m == null) return false;
                return (b == 0 ? m.leftButton : b == 1 ? m.rightButton : m.middleButton).wasPressedThisFrame;
            }
            catch { return false; }
        }

        public static bool AnyHeld()
        {
            if (!_legacyBroken)
            {
                try { return Input.anyKey; } catch { _legacyBroken = true; }
            }
            try
            {
                var kb = UnityEngine.InputSystem.Keyboard.current;
                var m = UnityEngine.InputSystem.Mouse.current;
                return (kb != null && kb.anyKey.isPressed) || (m != null && (m.leftButton.isPressed || m.rightButton.isPressed));
            }
            catch { return false; }
        }

        static UnityEngine.InputSystem.Key Map(KeyCode k)
        {
            switch (k)
            {
                case KeyCode.F1: return UnityEngine.InputSystem.Key.F1;
                case KeyCode.F2: return UnityEngine.InputSystem.Key.F2;
                case KeyCode.F3: return UnityEngine.InputSystem.Key.F3;
                case KeyCode.F4: return UnityEngine.InputSystem.Key.F4;
                case KeyCode.F5: return UnityEngine.InputSystem.Key.F5;
                case KeyCode.F6: return UnityEngine.InputSystem.Key.F6;
                case KeyCode.F7: return UnityEngine.InputSystem.Key.F7;
                case KeyCode.F8: return UnityEngine.InputSystem.Key.F8;
                case KeyCode.F9: return UnityEngine.InputSystem.Key.F9;
                case KeyCode.F10: return UnityEngine.InputSystem.Key.F10;
                case KeyCode.F11: return UnityEngine.InputSystem.Key.F11;
                case KeyCode.F12: return UnityEngine.InputSystem.Key.F12;
                case KeyCode.Return: return UnityEngine.InputSystem.Key.Enter;
                case KeyCode.Escape: return UnityEngine.InputSystem.Key.Escape;
                case KeyCode.Tab: return UnityEngine.InputSystem.Key.Tab;
                case KeyCode.BackQuote: return UnityEngine.InputSystem.Key.Backquote;
                case KeyCode.Insert: return UnityEngine.InputSystem.Key.Insert;
                case KeyCode.Home: return UnityEngine.InputSystem.Key.Home;
                case KeyCode.Space: return UnityEngine.InputSystem.Key.Space;
                case KeyCode.CapsLock: return UnityEngine.InputSystem.Key.CapsLock;
                case KeyCode.LeftAlt: return UnityEngine.InputSystem.Key.LeftAlt;
                case KeyCode.RightAlt: return UnityEngine.InputSystem.Key.RightAlt;
                case KeyCode.LeftControl: return UnityEngine.InputSystem.Key.LeftCtrl;
                case KeyCode.RightControl: return UnityEngine.InputSystem.Key.RightCtrl;
                case KeyCode.LeftShift: return UnityEngine.InputSystem.Key.LeftShift;
                case KeyCode.RightShift: return UnityEngine.InputSystem.Key.RightShift;
            }
            if (k == KeyCode.Alpha0) return UnityEngine.InputSystem.Key.Digit0;
            if (k >= KeyCode.Alpha1 && k <= KeyCode.Alpha9) return UnityEngine.InputSystem.Key.Digit1 + (k - KeyCode.Alpha1);
            if (k >= KeyCode.A && k <= KeyCode.Z) return UnityEngine.InputSystem.Key.A + (k - KeyCode.A);
            return UnityEngine.InputSystem.Key.None;
        }
    }
}

namespace System.Runtime.CompilerServices
{
    [AttributeUsage(AttributeTargets.All, Inherited = false)]
    internal sealed class NullableAttribute : Attribute
    {
        public NullableAttribute(byte b) { }
        public NullableAttribute(byte[] b) { }
    }
    [AttributeUsage(AttributeTargets.All, Inherited = false)]
    internal sealed class NullableContextAttribute : Attribute
    {
        public NullableContextAttribute(byte b) { }
    }
}
