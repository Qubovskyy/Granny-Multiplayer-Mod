using System;
using UnityEngine;

namespace GrannyMP
{
    /// <summary>
    /// Whether the shotgun / crossbow is loaded. The game keeps it per player (PickUp / InventoryController), not on the
    /// weapon – so a dropped loaded weapon was empty for whoever picked it up. The state now travels with the dropped
    /// weapon (spawn messages) and is put into the inventory of the player who picks it up.
    /// </summary>
    internal static class Weapons
    {
        public const string Shotgun = "ShotgunDrop_Prefab", Crossbow = "Armborst";

        public static bool IsWeapon(string prefab) => prefab == Shotgun || prefab == Crossbow;

        /// <summary>The local player's loaded state of that weapon right now (when it is dropped): 1 loaded, 0 empty, -1 unknown.</summary>
        public static sbyte LocalLoaded(string prefab)
        {
            var inv = GameRefs.Inventory;
            if (!U.Alive(inv)) return -1;
            try
            {
                if (prefab == Shotgun)
                {
                    var pu = ShotgunPickUp();
                    return U.Alive(pu) ? (sbyte)(pu.oldShotgunLoaded ? 1 : 0) : (sbyte)-1;
                }
                if (prefab == Crossbow) return (sbyte)(inv.armborstArrowOK ? 1 : 0);
            }
            catch (Exception e) { Log.Debug("weapon state: " + e.Message); }
            return -1;
        }

        // a weapon the local player has just picked up – its state is set once the game has put it into the hand
        static string _pending; static bool _pendingLoaded; static float _pendingUntil;

        public static void PickedUp(NetObj o)
        {
            if (o == null || o.Loaded < 0 || !IsWeapon(o.PrefabName)) return;
            _pending = o.PrefabName; _pendingLoaded = o.Loaded == 1; _pendingUntil = Time.unscaledTime + 2f;
        }

        static float _holdUntil; static bool _holdLoaded;

        public static void Update()
        {
            if (_holdUntil > 0)
            {
                if (Time.unscaledTime > _holdUntil || !GameRefs.InGameScene) _holdUntil = 0;
                else
                {
                    try
                    {
                        var pu = ShotgunPickUp();
                        if (U.Alive(pu) && pu.oldShotgunLoaded != _holdLoaded)
                        {
                            Log.Info($"The game changed the picked-up shotgun to {(pu.oldShotgunLoaded ? "loaded" : "empty")} – set back to {(_holdLoaded ? "loaded" : "empty")}");
                            SetShotgun(_holdLoaded);
                        }
                    }
                    catch { }
                }
            }
            if (_pending == null) return;
            if (Time.unscaledTime > _pendingUntil || !GameRefs.InGameScene) { _pending = null; return; }
            var inv = GameRefs.Inventory;
            if (!U.Alive(inv)) return;
            try
            {
                if (_pending == Shotgun && inv.haveoldShotgun) { SetShotgun(_pendingLoaded); _holdLoaded = _pendingLoaded; _holdUntil = Time.unscaledTime + 1.5f; _pending = null; }
                else if (_pending == Crossbow && inv.havearmborst) { SetCrossbow(_pendingLoaded); _pending = null; }
            }
            catch (Exception e) { Log.Debug("weapon pick-up: " + e.Message); _pending = null; }
        }

        /// <summary>
        /// The shotgun's loaded state lives in ONE PickUp: the one shootGun checks (shootGun.ammoCheckHolder) – reloading
        /// sets it, a shot clears it. The first PickUp found may be another one, and InventoryController.oldShotgunLoaded
        /// is not used by the game at all (it kept an old value: an emptied shotgun went to the others loaded).
        /// </summary>
        static PickUp _shotgunPu;
        static PickUp ShotgunPickUp()
        {
            if (U.Alive(_shotgunPu)) return _shotgunPu;
            try
            {
                var sg = UnityEngine.Object.FindObjectOfType(Il2CppInterop.Runtime.Il2CppType.Of<shootGun>(), true)?.TryCast<shootGun>();
                if (U.Alive(sg) && U.Alive(sg.ammoCheckHolder)) _shotgunPu = sg.ammoCheckHolder.GetComponent<PickUp>();
            }
            catch { }
            if (!U.Alive(_shotgunPu)) _shotgunPu = GameRefs.PickUp;
            return _shotgunPu;
        }

        static void SetShotgun(bool loaded)
        {
            var inv = GameRefs.Inventory; var pu = ShotgunPickUp();
            try { inv.oldShotgunLoaded = loaded; } catch { }
            if (U.Alive(inv.ammo)) inv.ammo.SetActive(loaded);
            // the game reads the flag once, at the pick-up, and switches the shoot button / the shooting ray / the shells
            // from it – set a moment later, a loaded shotgun had no shoot button until it was picked up again
            try
            {
                if (U.Alive(pu))
                {
                    if (U.Alive(pu.shootButton)) pu.shootButton.SetActive(loaded);
                    if (U.Alive(pu.shootRay)) pu.shootRay.SetActive(loaded);
                }
                if (U.Alive(inv.shootButton)) inv.shootButton.SetActive(loaded);
                if (U.Alive(inv.shootRay)) inv.shootRay.SetActive(loaded);
            }
            catch (Exception e) { Log.Debug("shotgun buttons: " + e.Message); }
            // ...and its pose in the hand (OldShotgunHand/ShotgunHand: Load / OpenEmpty / Shoot) – the game played the
            // "open, empty" pose at the pick-up; the loaded one is the end of the reload clip
            try
            {
                var an = U.Alive(pu) && U.Alive(pu.oldShotgunAnim) ? pu.oldShotgunAnim.GetComponent<Animation>() : null;
                if (an != null)
                {
                    string clip = loaded ? "Load" : "OpenEmpty";
                    var st = an[clip];
                    if (st != null)
                    {
                        an.Play(clip);
                        st.time = st.length;
                        an.Sample();
                    }
                }
            }
            catch (Exception e) { Log.Debug("shotgun pose: " + e.Message); }
            if (U.Alive(pu)) { pu.oldShotgunLoaded = loaded; if (U.Alive(pu.ammo)) pu.ammo.SetActive(loaded); }
            Log.Info($"Picked up the shotgun – {(loaded ? "loaded" : "empty")} as it was dropped");
        }

        static void SetCrossbow(bool loaded)
        {
            var inv = GameRefs.Inventory; var pu = GameRefs.PickUp;
            inv.armborstArrowOK = loaded;
            if (U.Alive(pu))
            {
                if (U.Alive(pu.Armborstladdad)) pu.Armborstladdad.SetActive(loaded);
                if (U.Alive(pu.ArmborstOladdad)) pu.ArmborstOladdad.SetActive(!loaded);
                if (U.Alive(pu.arrowArmborst)) pu.arrowArmborst.SetActive(loaded);
            }
            Log.Info($"Picked up the crossbow – {(loaded ? "loaded" : "empty")} as it was dropped");
        }
    }
}
