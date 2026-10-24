using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Il2CppInterop.Runtime.InteropTypes;
using UnityEngine;

namespace GrannyMP
{
    /// <summary>
    /// All Harmony patches. Applied manually (via reflection) so that a missing method
    /// in a given game version does not break the rest of the mod.
    /// </summary>
    internal static class Patches
    {
        public static bool RunningRpc;
        public static float AllowClientCatchUntil;
        static readonly HashSet<IntPtr> _allowedCatch = new();
        static int _ok, _fail;
        static readonly HashSet<MethodBase> _patched = new();

        static bool ClientInGame => Session.IsClient && Session.Active && GameRefs.InGameScene && Session.HostInGame;
        static bool LocalGhost => Session.Active && (Session.Local?.Ghost ?? false);

        static HarmonyMethod HM(string name) => new HarmonyMethod(typeof(Patches).GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public));

        // ---------- protection against shared native code (IL2CPP / ICF) ----------
        // The compiler folds identical functions into one. Patching such a function also changes other methods
        // (e.g. the GameObject constructor) and ends in infinite recursion. Such methods are never patched.
        static Dictionary<IntPtr, int> _nativeUse;
        static readonly HashSet<string> _skippedShared = new();

        static unsafe void BuildNativeMap()
        {
            _nativeUse = new Dictionary<IntPtr, int>(200000);
            try
            {
                uint n = 0;
                var asms = Il2CppInterop.Runtime.IL2CPP.il2cpp_domain_get_assemblies(Il2CppInterop.Runtime.IL2CPP.il2cpp_domain_get(), ref n);
                for (uint a = 0; a < n; a++)
                {
                    var img = Il2CppInterop.Runtime.IL2CPP.il2cpp_assembly_get_image(asms[a]);
                    uint cc = Il2CppInterop.Runtime.IL2CPP.il2cpp_image_get_class_count(img);
                    for (uint c = 0; c < cc; c++)
                    {
                        var klass = Il2CppInterop.Runtime.IL2CPP.il2cpp_image_get_class(img, c);
                        if (klass == IntPtr.Zero) continue;
                        IntPtr iter = IntPtr.Zero, mi;
                        while ((mi = Il2CppInterop.Runtime.IL2CPP.il2cpp_class_get_methods(klass, ref iter)) != IntPtr.Zero)
                        {
                            var fp = *(IntPtr*)mi;
                            if (fp == IntPtr.Zero) continue;
                            _nativeUse.TryGetValue(fp, out var k);
                            _nativeUse[fp] = k + 1;
                        }
                    }
                }
                Log.Info($"Native code map: {_nativeUse.Count} functions");
            }
            catch (Exception e) { Log.Warn("Native code map unavailable: " + e.Message); _nativeUse = null; }
        }

        static unsafe IntPtr NativePtr(MethodBase m)
        {
            try
            {
                var f = Il2CppInterop.Common.Il2CppInteropUtils.GetIl2CppMethodInfoPointerFieldForGeneratedMethod(m);
                if (f == null) return IntPtr.Zero;
                var mi = (IntPtr)f.GetValue(null);
                return mi == IntPtr.Zero ? IntPtr.Zero : *(IntPtr*)mi;
            }
            catch { return IntPtr.Zero; }
        }

        /// <summary>Whether a method can be patched safely (it has its own, non-shared native code).</summary>
        static bool SafeToPatch(MethodBase m)
        {
            var fp = NativePtr(m);
            if (fp == IntPtr.Zero) { _skippedShared.Add(m.DeclaringType?.Name + "." + m.Name + " (no code)"); return false; }
            if (_nativeUse == null) return false; // without the map we do not take the risk
            if (_nativeUse.TryGetValue(fp, out var uses) && uses > 1) { _skippedShared.Add(m.DeclaringType?.Name + "." + m.Name + $" (shared code x{uses})"); return false; }
            // druga metoda z tym samym adresem w naszym zestawie
            if (_patchedPtrs.Contains(fp)) { _skippedShared.Add(m.DeclaringType?.Name + "." + m.Name + " (address already patched)"); return false; }
            return true;
        }

        static readonly HashSet<IntPtr> _patchedPtrs = new();
        public static readonly HashSet<string> Applied = new();

        static bool Patch(Harmony h, MethodBase m, string prefix = null, string postfix = null)
        {
            if (m == null || _patched.Contains(m)) return false;
            if (!SafeToPatch(m)) return false;
            try
            {
                h.Patch(m, prefix != null ? HM(prefix) : null, postfix != null ? HM(postfix) : null);
                _patched.Add(m); _patchedPtrs.Add(NativePtr(m)); _ok++;
                Applied.Add(m.DeclaringType?.Name + "." + m.Name);
                return true;
            }
            catch (Exception e) { _fail++; Log.Warn($"Patch {m.DeclaringType?.Name}.{m.Name}: {e.Message}"); return false; }
        }

        public static bool Has(string type, string method) => Applied.Contains(type + "." + method);

        static MethodInfo M(Type t, string name, params Type[] args) =>
            t?.GetMethod(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly, null, args, null);

        public static void ApplyAll(Harmony h)
        {
            BuildNativeMap();
            var asm = typeof(EnemyAIGranny).Assembly;
            var mbTypes = asm.GetTypes().Where(t => typeof(MonoBehaviour).IsAssignableFrom(t) && !t.IsAbstract).ToList();
            var coll = new[] { typeof(Collider) };

            // --- Granny ---
            var g = typeof(EnemyAIGranny);
            Patch(h, M(g, "FixedUpdate"), nameof(GrannyFixedPrefix), nameof(GrannyFixedPostfix));
            // Granny's sight (linecasts to the player) – spoofed to the chosen remote target on the host
            if (!Patch(h, M(typeof(EnemyEye), "Update"), nameof(EyePrefix), nameof(EyePostfix))) Log.Warn("EnemyEye.Update could not be patched – Granny will only see remote players during a chase");
            foreach (var n in new[] { "OnTriggerEnter", "OnTriggerStay", "OnTriggerExit" }) Patch(h, M(g, n, coll), nameof(GrannyTriggerPrefix));
            foreach (var nt in g.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic))
            {
                if (nt.Name.IndexOf("Playercaught", StringComparison.OrdinalIgnoreCase) >= 0) Patch(h, M(nt, "MoveNext"), nameof(PlayercaughtMoveNext), nameof(PlayercaughtMoveNextPost));
            }

            // --- RPC to the host (Granny getting hit etc.) ---
            foreach (var (tn, mn) in Cfg.HostRpcList)
            {
                var t = U.GameType(tn); if (t == null) continue;
                var m = M(t, mn);
                if (m == null) continue;
                if (m.ReturnType == typeof(void)) Patch(h, m, nameof(RpcVoidPrefix));
                else
                {
                    Patch(h, m, nameof(RpcEnumPrefix));
                    foreach (var nt in t.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic))
                        if (nt.Name.IndexOf(mn, StringComparison.OrdinalIgnoreCase) >= 0) Patch(h, M(nt, "MoveNext"), nameof(BlockOnClientMoveNext));
                }
            }

            // --- triggers: host-only types (blocked on clients) and types forwarded to the host ---
            // (Update/FixedUpdate of host-only types are NOT patched – on clients these components are disabled.
            //  Avatars have no Rigidbody/active collider, so they never fire static triggers.)
            foreach (var t in mbTypes)
            {
                if (t == g) continue;
                var name = t.Name;
                bool hostOnly = Cfg.HostOnlySet.Contains(name);
                bool fwd = Cfg.ForwardSet.Contains(name);
                if (!hostOnly && !fwd) continue;
                foreach (var (n, kind) in new[] { ("OnTriggerEnter", 0), ("OnTriggerStay", 1), ("OnTriggerExit", 2) })
                {
                    var m = M(t, n, coll);
                    if (m == null || m.ReturnType != typeof(void)) continue;
                    if (hostOnly) Patch(h, m, nameof(HostOnlyTriggerPrefix));
                    else if (kind == 0 && FallTypes.Contains(name)) Patch(h, m, nameof(FallEnter));
                    else if (name == "grannySeePlayerTrigger") Patch(h, m, kind == 0 ? nameof(SightEnter) : kind == 1 ? nameof(SightStay) : nameof(SightExit));
                    else if (name == "spiderTrigger" && kind == 1) Patch(h, m, nameof(AtticSpiderAreaStay));
                    else Patch(h, m, kind == 0 ? nameof(FwdEnter) : kind == 1 ? nameof(FwdStay) : nameof(FwdExit));
                }
            }

            // --- item fall sounds: an item that has landed makes no more noise while it lies there. Unity starts the
            //     trigger again whenever the body is switched kinematic / simulated (taking over a thrown item does that),
            //     so the vase on the floor "fell" again a few times. Most of these scripts are coroutines. ---
            foreach (var tn in FallTypes)
            {
                var ft = U.GameType(tn); if (ft == null) continue;
                foreach (var nt in ft.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic))
                    if (nt.Name.IndexOf("OnTriggerEnter", StringComparison.OrdinalIgnoreCase) >= 0) Patch(h, M(nt, "MoveNext"), nameof(FallMoveNext));
            }

            // --- Granny's bear traps: on a client only the local player springs a trap; whatever else lands in it
            //     (items, Granny, the trap moved to the host's position) is decided by the host ---
            var trap = U.GameType("BearTrap");
            if (trap != null && !Patch(h, M(trap, "OnTriggerEnter", coll), nameof(TrapTriggerPrefix)))
                Log.Warn("BearTrap.OnTriggerEnter could not be patched – a client's trap may close by itself");

            // --- pushing bodies: on clients the body is taken over just before the game pushes it ---
            var push = U.GameType("pushRigidbody");
            if (push != null && !Patch(h, push.GetMethod("OnControllerColliderHit", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly), nameof(PushPrefix)))
                Log.Warn("pushRigidbody.OnControllerColliderHit could not be patched – clients take over bodies by proximity instead");

            // --- end of day: the host decides when a day ends (see GameFlow.OnLocalEndDayStarted) ---
            Patch(h, M(typeof(endDay), "EndDay"), null, nameof(EndDayPostfix));

            // --- tags the game changes while playing (an item used on an object re-tags it) – TagSync ---
            if (!Patch(h, M(typeof(GameObject), "set_tag", typeof(string)), nameof(SetTagPrefix), nameof(SetTagPostfix)))
                Log.Warn("GameObject.set_tag could not be patched – objects re-tagged by using an item are not synced");

            // --- the attic spider hunts the player in its area, not only the host (AtticSpider) ---
            if (!Patch(h, M(typeof(spiderControll), "Update"), nameof(AtticSpiderUpdatePrefix), nameof(AtticSpiderUpdatePostfix)))
                Log.Warn("spiderControll.Update could not be patched – the attic spider hunts only the host");

            // --- the spider in the spider cellar hunts every player (MomSpider) ---
            if (!Patch(h, M(typeof(MomSpiderHead), "Update"), nameof(SpiderUpdatePrefix), nameof(SpiderUpdatePostfix)))
                Log.Warn("MomSpiderHead.Update could not be patched – the spider hunts only the host");
            foreach (var nt in typeof(MomSpiderHead).GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic))
                if (nt.Name.IndexOf("momSpiderEatPlayer", StringComparison.OrdinalIgnoreCase) >= 0) Patch(h, M(nt, "MoveNext"), nameof(SpiderEatMoveNext));
            bool edIter = false;
            foreach (var nt in typeof(endDay).GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic))
                if (nt.Name.IndexOf("EndDay", StringComparison.OrdinalIgnoreCase) >= 0 && Patch(h, M(nt, "MoveNext"), nameof(EndDayMoveNext))) edIter = true;
            if (!edIter) Log.Warn("endDay.EndDay coroutine could not be patched – a client's own end of day cannot be held back");
            // Granny caught the player under a bed: the game has a separate end of day for that (playerCaughtUnderBed)
            if (!Patch(h, M(typeof(playerCaughtUnderBed), "EndDayUnderBed"), null, nameof(EndDayUnderBedPostfix)))
                Log.Warn("playerCaughtUnderBed.EndDayUnderBed could not be patched – a catch under a bed always ends the day");
            foreach (var nt in typeof(playerCaughtUnderBed).GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic))
                if (nt.Name.IndexOf("EndDayUnderBed", StringComparison.OrdinalIgnoreCase) >= 0) Patch(h, M(nt, "MoveNext"), nameof(EndDayMoveNext));

            // --- [Network] DisableSteam: the game starts without Steam (two copies on one PC for testing) ---
            if (Cfg.DisableSteam.Value)
            {
                var sm = U.GameType("SteamManager");
                if (sm != null && Patch(h, M(sm, "Awake"), nameof(SteamManagerAwakePrefix))) Log.Info("Steam is switched off ([Network] DisableSteam) – the game starts without Steam, only Direct IP works");
                else Log.Warn("SteamManager.Awake could not be patched – the game still starts Steam");
            }

            // --- PvP: Granny does not crouch (the C key calls playerCrawl.crouching through the crouch button) ---
            var crawl = U.GameType("playerCrawl");
            if (crawl != null && !Patch(h, M(crawl, "crouching"), nameof(CrouchPrefix))) Log.Warn("playerCrawl.crouching could not be patched – the PvP Granny can crouch");

            // --- skip the DVloper splash screen (option) ---
            Patch(h, M(typeof(splashScreenStart), "Start"), nameof(SplashPrefix));

            // --- Granny's voice ("where are you?", laughing) – played by the host, sent to everybody ---
            var gs = typeof(GrannySounds);
            if (!Patch(h, M(gs, "startGrannySound"), nameof(GrannyVoicePrefix), nameof(GrannyVoicePostfix))) Log.Warn("GrannySounds.startGrannySound could not be patched – Granny's voice uses the generic sound sync");
            Patch(h, M(gs, "grannySkrattar"), nameof(GrannyLaughPrefix), nameof(GrannyLaughPostfix));

            // --- start gry z menu ---
            Patch(h, M(typeof(ClickContinueButton), "TaskOnClick"), nameof(PlayButtonPrefix));

            // --- ghosts cannot interact ---
            // ...and in PvP the Granny player cannot pick up or use items either (doors and drawers: yes)
            // (both also mark the time the local player's interaction scripts run – TagSync syncs what they switch on/off)
            Patch(h, M(typeof(openDoors), "Update"), nameof(DoorsPrefix), nameof(InteractionEnd));
            foreach (var t in new[] { typeof(PickUp), typeof(PickUpNew), typeof(ShootArrow), typeof(shootGun), typeof(sprayPepper), typeof(UseRemote),
                                      typeof(fillingFuel), typeof(playerVevar), typeof(seeBearTrap) })
                Patch(h, M(t, "Update"), nameof(ItemBlockPrefix), nameof(InteractionEnd));
            if (!Patch(h, M(typeof(UnityEngine.Object), "Destroy", typeof(UnityEngine.Object)), nameof(DestroyPrefix)))
                Log.Warn("Object.Destroy could not be patched – what using an item destroys (the car battery's spot) is not synced");
            if (!Patch(h, M(typeof(freezeTrap), "OnTriggerEnter", typeof(Collider)), nameof(FreezeTrapPrefix), nameof(FreezeTrapPostfix)))
                Log.Warn("freezeTrap.OnTriggerEnter could not be patched – freeze traps are not synced");
            if (!Patch(h, M(typeof(GameObject), "SetActive", typeof(bool)), nameof(SetActivePrefix), nameof(SetActivePostfix)))
                Log.Warn("GameObject.SetActive could not be patched – what using an item switches on/off far away is not synced");
            // ...and cannot hide (bed, coffin, car) – a spectator could get into the bed it died next to
            // ...and (host option) cannot get into a hiding spot another player already uses
            foreach (var tn in new[] { "bedButton", "bedButton2", "bedButton3", "coffinButton", "coffinButtonBackyard", "insideCarButton" })
                Patch(h, M(U.GameType(tn), "Update"), nameof(HideButtonPrefix));

            // --- sounds ---
            var a = typeof(AudioSource);
            // PlayOneShotHelper is INLINED into PlayOneShot in this build – game code never calls it, so the
            // two public PlayOneShot overloads are patched instead
            Patch(h, M(a, "PlayOneShot", typeof(AudioClip)), nameof(OneShot1Prefix));
            Patch(h, M(a, "PlayOneShot", typeof(AudioClip), typeof(float)), nameof(OneShot2Prefix));
            Patch(h, M(a, "Play"), nameof(PlayPrefix));
            Patch(h, M(a, "PlayClipAtPoint", typeof(AudioClip), typeof(Vector3), typeof(float)), nameof(ClipAtPointPrefix));

            Log.Info($"Patches: {_ok} applied, {_fail} failed, {_skippedShared.Count} skipped (shared native code)");
            foreach (var x in _skippedShared) Log.Debug("skipped patch: " + x);
        }

        // ================= Granny =================
        static bool GrannyFixedPrefix(EnemyAIGranny __instance)
        {
            if (!Session.Active) return true;
            if (ClientInGame) return false;                 // on clients the AI does not think – position/animations come from the network
            if (GameFlow.WaitingForPlayers) return false;  // host waits until everybody loaded the scene
            if (GameFlow.InTransition && !Pvp.Active) return false; // the day changes – Granny does not hunt anybody
            try { return AiControl.PreFixedUpdate(__instance); } catch (Exception e) { Log.Debug("AI pre: " + e.Message); }
            return true;
        }

        static bool EyePrefix(EnemyEye __instance)
        {
            if (!Session.Active) return true;
            if (ClientInGame || Pvp.Active) return false; // PvP: a player is Granny – her eyes are that player's
            if (GameFlow.InTransition) return false;       // the day changes – she sees nobody
            try { AiControl.PreEye(__instance); } catch (Exception e) { Log.Debug("eye pre: " + e.Message); }
            return true;
        }

        static void EyePostfix(EnemyEye __instance)
        {
            try { AiControl.PostEye(__instance); } catch { }
        }

        static void GrannyFixedPostfix(EnemyAIGranny __instance)
        {
            if (!Session.Active || Session.IsClient) return;
            try { AiControl.PostFixedUpdate(__instance); } catch { }
        }

        static bool GrannyTriggerPrefix(EnemyAIGranny __instance, Collider __0)
        {
            if (!Session.Active) return true;
            if (ClientInGame || Pvp.Active) return false;
            return true;
        }

        static readonly HashSet<IntPtr> _seenCatch = new();
        static readonly HashSet<IntPtr> _skipCatch = new();

        static bool PlayercaughtMoveNext(Il2CppObjectBase __instance, ref bool __result)
        {
            if (!Session.Active) return true;
            var key = __instance.Pointer;
            if (ClientInGame)
            {
                if (_allowedCatch.Contains(key)) return true;
                if (Time.unscaledTime < AllowClientCatchUntil) { AllowClientCatchUntil = 0; _allowedCatch.Add(key); return true; }
                __result = false; return false;
            }
            if (!_seenCatch.Contains(key))
            {
                _seenCatch.Add(key);
                bool skip = false;
                try { skip = AiControl.ShouldSkipHostCatch(); } catch (Exception e) { Log.Debug("catch: " + e.Message); }
                if (skip) _skipCatch.Add(key);
            }
            if (_skipCatch.Contains(key)) { __result = false; _skipCatch.Remove(key); _seenCatch.Remove(key); return false; }
            return true;
        }

        static void PlayercaughtMoveNextPost(Il2CppObjectBase __instance, bool __result)
        {
            if (!__result) { _seenCatch.Remove(__instance.Pointer); _allowedCatch.Remove(__instance.Pointer); }
        }

        // ================= RPC =================
        static bool RpcVoidPrefix(Component __instance, MethodBase __originalMethod)
        {
            if (RunningRpc) return true;
            if (!ClientInGame)
            {
                // the host's own call of an effect every game must show (the gas can's explosion)
                if (Session.Active && Session.IsHost && GameRefs.InGameScene) try { Events.EchoRpc(__instance, __originalMethod.DeclaringType.Name, __originalMethod.Name); } catch { }
                return true;
            }
            Events.SendRpc(__instance, __originalMethod.DeclaringType.Name, __originalMethod.Name);
            return false;
        }

        static bool RpcEnumPrefix(Component __instance, MethodBase __originalMethod)
        {
            if (!ClientInGame || RunningRpc) return true;
            Events.SendRpc(__instance, __originalMethod.DeclaringType.Name, __originalMethod.Name);
            return true; // the enumerator is created, but its MoveNext is blocked on the client
        }

        static bool BlockOnClientMoveNext(ref bool __result)
        {
            if (!ClientInGame) return true;
            __result = false; return false;
        }

        // ================= host-only / triggers =================
        static bool HostOnlyTriggerPrefix(Collider __0)
        {
            if (ClientInGame) return false;
            if (LocalGrannyCollider(__0)) return false;
            if (Session.Active && PlayerSync.IsProxy(__0) && !Events.Forwarding) return false;
            return true;
        }

        static bool Fwd(Component inst, Collider other, byte kind)
        {
            if (!Session.Active) return true;
            if (ClientInGame)
            {
                if (LocalGhost || Pvp.LocalGrannyInGame) return false;
                bool sent = Events.ForwardTrigger(inst, other, kind, out bool alsoLocal);
                if (sent && alsoLocal) return true; // also run locally
                return !sent;
            }
            if (PlayerSync.IsProxy(other) && !Events.Forwarding) return false;
            if (LocalGrannyCollider(other)) return false;
            return true;
        }

        /// <summary>PvP: the collider is the local player's who plays Granny (noise, sensors and traps ignore Granny).</summary>
        static bool LocalGrannyCollider(Collider c)
        {
            if (!Pvp.LocalGrannyInGame || c == null) return false;
            try { return U.IsUnder(c.transform, GameRefs.PlayerRoot); } catch { return false; }
        }

        static void PushPrefix(ControllerColliderHit __0)
        {
            if (!ClientInGame || LocalGhost) return;
            try { World.OnLocalPush(__0); } catch (Exception e) { Log.Debug("push: " + e.Message); }
        }

        static bool TrapTriggerPrefix(Collider __0)
        {
            if (LocalGrannyCollider(__0)) return false; // Granny does not step into her own traps
            if (!ClientInGame) return true;
            if (LocalGhost) return false;
            try { return __0 != null && U.IsUnder(__0.transform, GameRefs.PlayerRoot); } catch { return false; }
        }

        // PvP: Granny's sight cone (the AI is off – a player is Granny)
        static bool SightEnter(Component __instance, Collider __0) => !Pvp.Active && Fwd(__instance, __0, 0);
        static bool SightStay(Component __instance, Collider __0) => !Pvp.Active && Fwd(__instance, __0, 1);
        static bool SightExit(Component __instance, Collider __0) => !Pvp.Active && Fwd(__instance, __0, 2);

        /// <summary>Scripts that make an item's "fell on the floor" noise (sound + Granny hears it).</summary>
        static readonly HashSet<string> FallTypes = new()
        {
            "HandObjectFalls", "objectFallFloor", "noiceObjectFalls", "ObjectFallFloorDestroys", "ObjectFallFloorMultiple",
            "objectsFallFloorTavla", "ljusstakeFallFloar", "teddyFalls",
        };

        static bool FallEnter(Component __instance, Collider __0) => Events.FallAllowed(__instance, __0) && Fwd(__instance, __0, 0);

        static readonly Dictionary<Type, (PropertyInfo state, PropertyInfo self, PropertyInfo other)> _fallProps = new();

        /// <summary>The first step of a fall-noise coroutine: skipped when the item has not moved since it last landed.</summary>
        static bool FallMoveNext(Il2CppObjectBase __instance, ref bool __result)
        {
            if (!Session.Active || __instance == null) return true;
            try
            {
                var t = __instance.GetType();
                if (!_fallProps.TryGetValue(t, out var pr))
                {
                    const BindingFlags bf = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
                    pr = (t.GetProperty("__1__state", bf), t.GetProperty("__4__this", bf), t.GetProperty("other", bf));
                    _fallProps[t] = pr;
                }
                if (pr.state == null || pr.self == null || (int)pr.state.GetValue(__instance) != 0) return true;
                var self = pr.self.GetValue(__instance) as Component;
                var other = pr.other?.GetValue(__instance) as Collider;
                if (self == null || Events.FallAllowed(self, other)) return true;
                __result = false;
                return false;
            }
            catch (Exception e) { Log.Debug("fall noise: " + e.Message); return true; }
        }

        static bool FwdEnter(Component __instance, Collider __0) => Fwd(__instance, __0, 0);
        static bool FwdStay(Component __instance, Collider __0) => Fwd(__instance, __0, 1);

        /// <summary>The attic spider's area: forwarded like the others; the host also notes who is in it (AtticSpider).</summary>
        static void SetTagPrefix(GameObject __instance, out string __state)
        {
            __state = null;
            if (!Session.Active || TagSync.Applying || !TagSync.InInteraction || !GameRefs.InGameScene) return;
            try { __state = __instance.tag; } catch { }
        }

        static void SetTagPostfix(GameObject __instance, string __0, string __state)
        {
            if (__state == null || __state == __0) return;
            try { TagSync.OnLocalTag(__instance, __0); } catch (Exception e) { Log.Debug("tag: " + e.Message); }
        }

        static bool AtticSpiderAreaStay(Component __instance, Collider __0)
        {
            bool run = Fwd(__instance, __0, 1);
            if (run && Session.Active && Session.IsHost) try { AtticSpider.AreaStay(__0); } catch { }
            return run;
        }

        static void AtticSpiderUpdatePrefix(spiderControll __instance) { if (Session.Active) AtticSpider.PreUpdate(__instance); }
        static void AtticSpiderUpdatePostfix(spiderControll __instance) { if (Session.Active) AtticSpider.PostUpdate(__instance); }
        static bool FwdExit(Component __instance, Collider __0) => Fwd(__instance, __0, 2);

        // ================= przebieg gry =================
        static readonly HashSet<IntPtr> _heldEndDay = new();

        static void EndDayPostfix(Il2CppObjectBase __result) => HoldEndDay(__result, false);
        static void EndDayUnderBedPostfix(Il2CppObjectBase __result) => HoldEndDay(__result, true);

        static void HoldEndDay(Il2CppObjectBase __result, bool underBed)
        {
            try
            {
                if (GameFlow.OnLocalEndDayStarted(underBed) && __result != null)
                {
                    if (_heldEndDay.Count > 64) _heldEndDay.Clear();
                    _heldEndDay.Add(__result.Pointer);
                }
            }
            catch (Exception e) { Log.Error("EndDay: " + e); }
        }

        static bool SpiderUpdatePrefix(MomSpiderHead __instance)
        {
            if (!Session.Active) return true;
            try { return MomSpider.PreUpdate(__instance); } catch (Exception e) { Log.Debug("spider: " + e.Message); return true; }
        }

        static void SpiderUpdatePostfix(MomSpiderHead __instance)
        {
            if (!Session.Active) return;
            try { MomSpider.PostUpdate(__instance); } catch (Exception e) { Log.Debug("spider: " + e.Message); }
        }

        static bool SpiderEatMoveNext(ref bool __result)
        {
            if (MomSpider.EatMoveNext()) return true;
            __result = false; return false;
        }

        static bool EndDayMoveNext(Il2CppObjectBase __instance, ref bool __result)
        {
            if (_heldEndDay.Count == 0 || !_heldEndDay.Remove(__instance.Pointer)) return true;
            __result = false; // this end-of-day sequence does not run
            return false;
        }

        // ================= Granny's voice =================
        static bool GrannyVoicePrefix()
        {
            if (!Session.Active) return true;
            if (ClientInGame && !Events.PlayingRemote) return false; // the host's Granny talks, clients hear it from the network
            if (Session.IsClient) return false;                       // a client outside the game: nothing to say
            if (!Events.HostVoiceAllowed()) return false;             // a new line at most once a second
            Events.SuppressSend = true;
            return true;
        }

        static void GrannyVoicePostfix(GrannySounds __instance)
        {
            if (!Events.SuppressSend) return; // the prefix skipped this call
            Events.SuppressSend = false;
            if (!Session.Active || !Session.IsHost) return;
            try { Events.SendGrannyVoice(__instance, false); } catch (Exception e) { Log.Debug("granny voice: " + e.Message); }
        }

        static bool GrannyLaughPrefix() => GrannyVoicePrefix();

        static void GrannyLaughPostfix(GrannySounds __instance)
        {
            if (!Events.SuppressSend) return;
            Events.SuppressSend = false;
            if (!Session.Active || !Session.IsHost) return;
            try { Events.SendGrannyVoice(__instance, true); } catch (Exception e) { Log.Debug("granny laugh: " + e.Message); }
        }

        static bool SplashPrefix() => !GameFlow.SkipSplash();

        static bool CrouchPrefix() => !Pvp.LocalGrannyInGame;

        // SteamManager.Awake asks Steam to restart the game through the Steam client (a second copy then quits) and
        // initialises the Steam API. Skipped, SteamManager.Initialized stays false – the game's only Steam use
        // (SteamScript: the player's Steam name) checks it first.
        static bool SteamManagerAwakePrefix() => false;

        static bool PlayButtonPrefix()
        {
            if (GameFlow.BlockLocalPlay(out var why)) { Session.Notify(why, false); return false; }
            return true;
        }

        static bool GhostBlockPrefix() => !LocalGhost;
        // PvP Granny: doors yes – drawers, cupboards, levers no
        static bool DoorsPrefix(Component __instance)
        {
            bool run = !LocalGhost && Pvp.GrannyMayUse(__instance);
            TagSync.InInteraction = run && Session.Active;
            return run;
        }

        static bool ItemBlockPrefix()
        {
            bool run = !LocalGhost && !Pvp.LocalGrannyInGame;
            TagSync.InInteraction = run && Session.Active;
            return run;
        }

        static void InteractionEnd() => TagSync.InInteraction = false;

        static void DestroyPrefix(UnityEngine.Object __0)
        {
            if (!TagSync.InInteraction || TagSync.Applying || __0 == null) return;
            try { var go = __0.TryCast<GameObject>(); if (go != null) TagSync.OnLocalDestroy(go); } catch { }
        }

        static bool FreezeTrapPrefix(freezeTrap __instance, Collider __0, out bool __state)
        {
            try { return FreezeTrapSync.Pre(__instance, __0, out __state); } catch { __state = false; return true; }
        }

        static void FreezeTrapPostfix(freezeTrap __instance, bool __state)
        {
            try { FreezeTrapSync.Post(__instance, __state); } catch (Exception e) { Log.Debug("freeze trap: " + e.Message); }
        }

        static void SetActivePrefix(GameObject __instance, bool __0, out bool __state)
        {
            __state = false;
            if (!TagSync.InInteraction || TagSync.Applying) return;
            try { __state = __instance.activeSelf != __0; } catch { }
        }

        static void SetActivePostfix(GameObject __instance, bool __0, bool __state)
        {
            if (!__state) return;
            try { TagSync.OnLocalActive(__instance, __0); } catch (Exception e) { Log.Debug("active: " + e.Message); }
        }

        static bool HideButtonPrefix(Il2CppObjectBase __instance)
        {
            if (!Session.Active) return true;
            if (LocalGhost || Pvp.LocalGrannyInGame) return false;
            try { return !GameFlow.HideSpotBlocked(__instance?.TryCast<Component>()); } catch { return true; }
        }

        // ================= sounds =================
        // every sound goes through the voice gate first: Granny's lines at most once a second, whatever plays them
        static bool OneShot1Prefix(AudioSource __instance, AudioClip __0)
        {
            if (!Session.Active) return true;
            try { return Events.VoiceGate(__instance, __0, "PlayOneShot") && Events.OnLocalSound(__instance, __0, 1f, null); } catch { return true; }
        }

        static bool OneShot2Prefix(AudioSource __instance, AudioClip __0, float __1)
        {
            if (!Session.Active) return true;
            try { return Events.VoiceGate(__instance, __0, "PlayOneShot") && Events.OnLocalSound(__instance, __0, __1, null); } catch { return true; }
        }

        static bool PlayPrefix(AudioSource __instance)
        {
            if (!Session.Active) return true;
            try { var c = __instance.clip; return Events.VoiceGate(__instance, c, "Play") && Events.OnLocalSound(__instance, c, __instance.volume, null, true); } catch { return true; }
        }

        static bool ClipAtPointPrefix(AudioClip __0, Vector3 __1, float __2)
        {
            if (!Session.Active) return true;
            try { return Events.VoiceGate(null, __0, "PlayClipAtPoint") && Events.OnLocalSound(null, __0, __2, __1); } catch { return true; }
        }
    }
}
