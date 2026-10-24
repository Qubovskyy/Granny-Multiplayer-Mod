using System;
using UnityEngine;
using UnityStandardAssets.Characters.FirstPerson;

namespace GrannyMP
{
    /// <summary>References to key objects of the game scene (refreshed after a scene load).</summary>
    internal static class GameRefs
    {
        public static FirstPersonController_Egen Fps;
        public static Transform PlayerRoot;
        public static CharacterController PlayerCC;
        public static Transform PlayerCam;
        public static EnemyAIGranny Granny;
        public static EnemyEye Eye;
        public static endDay EndDay;
        public static playerCaughtUnderBed BedCatch;
        public static startNewDay NewDay;
        public static InventoryController Inventory;
        public static PickUp PickUp;
        public static Collider PlayerCollider;

        public static Transform OrigGrannyPlayer, OrigGrannyPlayerPos;
        public static bool InGameScene;

        public static void Clear()
        {
            Fps = null; PlayerRoot = null; PlayerCC = null; PlayerCam = null; Granny = null; EndDay = null; NewDay = null;
            Inventory = null; PickUp = null; BedCatch = null; Eye = null; PlayerCollider = null; OrigGrannyPlayer = null; OrigGrannyPlayerPos = null; InGameScene = false;
        }

        static T Find<T>() where T : UnityEngine.Object
        {
            try
            {
                var arr = UnityEngine.Object.FindObjectsOfType(Il2CppInterop.Runtime.Il2CppType.Of<T>(), true);
                foreach (var o in arr) { var t = o.TryCast<T>(); if (t != null) return t; }
            }
            catch (Exception e) { Log.Debug("Find<" + typeof(T).Name + ">: " + e.Message); }
            return null;
        }

        public static void Refresh()
        {
            Clear();
            Fps = Find<FirstPersonController_Egen>();
            if (Fps != null)
            {
                PlayerRoot = Fps.transform;
                PlayerCC = Fps.GetComponent<CharacterController>();
                PlayerCollider = PlayerCC;
                try { PlayerCam = Fps.m_Camera; } catch { }
                if (!U.Alive(PlayerCam)) { var c = U.ChildComp<Camera>(Fps); if (c != null) PlayerCam = c.transform; }
            }
            Granny = Find<EnemyAIGranny>();
            Eye = Find<EnemyEye>();
            EndDay = Find<endDay>();
            BedCatch = Find<playerCaughtUnderBed>();
            NewDay = Find<startNewDay>();
            Inventory = Find<InventoryController>();
            PickUp = Find<PickUp>();
            if (U.Alive(Granny))
            {
                try { OrigGrannyPlayer = Granny.player; OrigGrannyPlayerPos = Granny.playerPos; } catch { }
            }
            InGameScene = U.Alive(PlayerRoot) && U.Alive(Granny);
            Log.Info($"Scene: player={(U.Alive(PlayerRoot) ? PlayerRoot.name : "-")}, Granny={(U.Alive(Granny) ? Granny.name : "-")}, endDay={U.Alive(EndDay)}, inventory={U.Alive(Inventory)}");
        }

        static Camera _renderCam; static float _renderCamAt;
        /// <summary>
        /// The camera drawing the local player's picture right now (Camera.main is a disabled camera in this game):
        /// the player's camera, a hiding spot's camera, the catch animation's camera. Never the spectator camera.
        /// </summary>
        public static Camera RenderCamera()
        {
            float now = Time.unscaledTime;
            if (now < _renderCamAt && U.Alive(_renderCam) && _renderCam.isActiveAndEnabled) return _renderCam;
            _renderCamAt = now + 0.1f; _renderCam = null;
            try
            {
                // hiding (bed, coffin, car): that spot's camera
                int spot = LocalSpot;
                if (spot >= 1 && spot <= 6)
                {
                    var hc = SpotCamera(spot, LocalPos);
                    var cam = U.Alive(hc) ? hc.GetComponent<Camera>() : null;
                    if (cam != null && cam.isActiveAndEnabled && cam.targetTexture == null) return _renderCam = cam;
                }
                // the player's own camera while it is on; otherwise whatever took over (catch, ragdoll, cut scene)
                var spec = Spectator.ViewTransform;
                Camera any = null;
                foreach (var c in Camera.allCameras)
                {
                    if (c == null || !c.enabled || c.targetTexture != null) continue;
                    if (U.Alive(spec) && U.IsUnder(c.transform, spec)) continue;
                    if (U.Alive(PlayerRoot) && U.IsUnder(c.transform, PlayerRoot)) { if (_renderCam == null || c.depth > _renderCam.depth) _renderCam = c; }
                    else if (any == null || c.depth > any.depth) any = c;
                }
                if (_renderCam == null || (any != null && any.depth > _renderCam.depth)) _renderCam = any;
            }
            catch { }
            return _renderCam;
        }

        public static Vector3 LocalPos => U.Alive(PlayerRoot) ? PlayerRoot.position : Vector3.zero;

        public static void TeleportLocal(Vector3 pos, float? yaw = null)
        {
            if (!U.Alive(PlayerRoot)) return;
            bool was = U.Alive(PlayerCC) && PlayerCC.enabled;
            if (was) PlayerCC.enabled = false;
            PlayerRoot.position = pos;
            if (yaw.HasValue) PlayerRoot.rotation = Quaternion.Euler(0, yaw.Value, 0);
            if (was) PlayerCC.enabled = true;
        }

        /// <summary>The local player's hiding spot (PState.Spot): 1–3 bed, 4 coffin, 5 backyard coffin, 6 car, 7 other, 0 none.</summary>
        public static byte LocalSpot
        {
            get
            {
                if (!U.Alive(Granny)) return 0;
                try
                {
                    var g = Granny;
                    if (g.hidingUnderBed1) return 1; if (g.hidingUnderBed2) return 2; if (g.hidingUnderBed3) return 3;
                    if (g.hidingInCoffin4) return 4; if (g.hidingInCoffinBY) return 5; if (g.hidingInCar) return 6;
                    return LocalHiding ? (byte)7 : (byte)0;
                }
                catch { return 0; }
            }
        }

        /// <summary>
        /// The camera a player looks through while hiding in spot <paramref name="spot"/> (1–3 beds, 4–5 coffins, 6 car);
        /// with several candidates the one nearest to <paramref name="near"/>. Null for other spots.
        /// </summary>
        public static Transform SpotCamera(int spot, Vector3 near)
        {
            if (!U.Alive(Granny) || spot < 1 || spot > 6) return null;
            try
            {
                var g = Granny;
                GameObject[] c = spot switch
                {
                    1 => new[] { g.bedCam1 }, 2 => new[] { g.bedCam2 }, 3 => new[] { g.bedCam3 },
                    4 or 5 => new[] { g.coffinHead1, g.coffinHead2 },
                    _ => new[] { g.carHead },
                };
                Transform best = null; float bestD = float.MaxValue;
                foreach (var go in c)
                {
                    if (!U.Alive(go)) continue;
                    var cam = go.GetComponentInChildren(Il2CppInterop.Runtime.Il2CppType.Of<Camera>(), true)?.TryCast<Camera>();
                    var t = cam != null ? cam.transform : go.transform;
                    float d = (t.position - near).sqrMagnitude;
                    if (d < bestD) { bestD = d; best = t; }
                }
                return best;
            }
            catch { return null; }
        }

        /// <summary>Whether the local player is hiding (bed, wardrobe, coffin, car) – from EnemyAIGranny fields on this machine.</summary>
        public static bool LocalHiding
        {
            get
            {
                if (!U.Alive(Granny)) return false;
                try
                {
                    var g = Granny;
                    return g.playerHiding || g.playerHidingUnderBed || g.playerHidingInCoffin || g.playerHidingInCoffinBackyard || g.playerHidingInCar
                        || g.hidingUnderBed1 || g.hidingUnderBed2 || g.hidingUnderBed3 || g.hidingInCoffin4 || g.hidingInCoffinBY || g.hidingInCar;
                }
                catch { return false; }
            }
        }
    }
}
