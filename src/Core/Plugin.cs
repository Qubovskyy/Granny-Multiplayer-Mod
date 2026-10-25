using System;
using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using Il2CppInterop.Runtime.Injection;

namespace GrannyMP
{
    [BepInPlugin(Guid, ModName, ModVersion)]
    public class Plugin : BasePlugin
    {
        public const string Guid = "pl.grannymp.coop";
        public const string ModName = "Granny Multiplayer Co-op";
        public const string ModVersion = "0.9.0";
        /// <summary>Protocol version – host and clients must match.</summary>
        public const int ProtocolVersion = 36;

        public static Plugin Instance;
        public static Harmony HarmonyInstance;

        public override void Load()
        {
            Instance = this;
            GrannyMP.Log.Init(base.Log);
            Cfg.Bind(Config);
            GrannyMP.Log.Info($"{ModName} v{ModVersion} (protocol {ProtocolVersion}) – loading");

            try { SteamNative.InstallResolver(); } catch (Exception e) { GrannyMP.Log.Warn("Steam resolver: " + e.Message); }

            ClassInjector.RegisterTypeInIl2Cpp<NetRunner>();

            HarmonyInstance = new Harmony(Guid);
            Patches.ApplyAll(HarmonyInstance);

            var runner = AddComponent<NetRunner>();
            runner.hideFlags = UnityEngine.HideFlags.HideAndDontSave;
            GrannyMP.Log.Info("Ready. Press " + Cfg.ToggleKey.Value + " to open the multiplayer window.");
        }

        public override bool Unload()
        {
            try { Session.Shutdown("unload"); } catch { }
            HarmonyInstance?.UnpatchSelf();
            return true;
        }
    }

    internal static class Log
    {
        private static ManualLogSource _src;
        public static void Init(ManualLogSource s) => _src = s;
        public static void Info(string m) => _src?.LogInfo(m);
        public static void Warn(string m) => _src?.LogWarning(m);
        public static void Error(string m) => _src?.LogError(m);
        public static void Debug(string m) { if (Cfg.DebugLog != null && Cfg.DebugLog.Value) _src?.LogInfo("[dbg] " + m); }
    }
}
