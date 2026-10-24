using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace GrannyMP
{
    /// <summary>
    /// "Last survivor" mode: a caught player spectates the players still alive until the end of the day.
    /// The local player's cameras, audio listener, controller, hand items and lights are switched off and a spectator
    /// camera (a copy of the player's own camera, with the same post-processing) looks through the eyes of the watched
    /// player (left / right mouse button = next / previous player).
    /// </summary>
    internal static class Spectator
    {
        static GameObject _camGo;
        static readonly List<Behaviour> _disabled = new();
        static readonly List<GameObject> _deactivated = new();
        static byte _watching = 255;
        static Light _dim;
        static float _notBefore, _nextScan;

        public static bool Active => U.Alive(_camGo);
        /// <summary>The player whose view is shown (255 = nobody).</summary>
        public static byte Watching => Active ? _watching : (byte)255;
        /// <summary>The spectator camera (listener position for voice chat), null when not spectating.</summary>
        public static Transform ViewTransform => Active ? _camGo.transform : null;

        /// <summary>The local player was just caught: the catch animation plays first, the spectator view starts afterwards.</summary>
        public static void DelayStart(float seconds) => _notBefore = Mathf.Max(_notBefore, Time.unscaledTime + seconds);

        public static string HudText
        {
            get
            {
                if (!Active) return null;
                if (_watching == 255) return L.T("You were caught – nobody is left to watch.", "Zostałeś złapany – nie ma kogo obserwować.");
                return L.T($"Spectating {Session.NameOf(_watching)}  ·  LMB / RMB: next / previous player",
                           $"Obserwujesz: {Session.NameOf(_watching)}  ·  LPM / PPM: następny / poprzedni gracz");
            }
        }

        // the field of view the player normally has (the catch animation zooms the camera in)
        static Camera _viewCam; static float _viewFov = 65f, _nextFov;
        static Vector3 _hhPos; static Quaternion _hhRot = Quaternion.identity;
        // the watched player's hands in the spectator view: a copy of the game's own flashlight / lantern (with
        // their real lights – shadows, cookie) and of the held item, posed like the local player's hand items
        static GameObject _rig, _rigLights, _rigFlash, _rigLantern, _rigDark, _rigHeld;
        static byte _rigHeldIdx = 254, _heldPending = 254; static float _heldPendingAt; static bool _rigSprayShown;
        static GameObject _crosshair;
        static bool _cursorLocked;

        public static void Update()
        {
            TrackLamps();
            if (EscapeSync.Playing) { if (Active) Stop(); return; } // an escape cutscene films the end
            bool ghost = Session.Active && GameRefs.InGameScene && (Session.Local?.Ghost ?? false) && U.Alive(GameRefs.PlayerRoot);
            if (!ghost)
            {
                _notBefore = 0;
                if (Active || _disabled.Count > 0 || _deactivated.Count > 0) Stop();
                if (Session.Active && GameRefs.InGameScene && U.Alive(GameRefs.PlayerRoot) && Time.unscaledTime >= _nextFov)
                {
                    _nextFov = Time.unscaledTime + 1f;
                    try
                    {
                        if (!U.Alive(_viewCam)) _viewCam = SourceCamera(GameRefs.PlayerRoot);
                        bool caught = U.Alive(GameRefs.Fps) && GameRefs.Fps.playerCaught;
                        if (U.Alive(_viewCam) && _viewCam.isActiveAndEnabled && !caught && _viewCam.fieldOfView > 20f)
                        {
                            _viewFov = _viewCam.fieldOfView;
                            if (!U.Alive(_hands)) _hands = GameObject.Find("HandHoldObjects");
                        }
                    }
                    catch { }
                }
                SampleHands();
                return;
            }
            if (!Active)
            {
                if (Time.unscaledTime < _notBefore) return; // the catch animation is still playing
                Begin();
            }
            if (Time.unscaledTime >= _nextScan) { _nextScan = Time.unscaledTime + 0.25f; KeepOff(); KeepCrosshair(); }
            else KeepSpotLightsOff();
            UpdateMenu();

            var alive = Session.Players.Values
                .Where(p => !p.IsLocal && !p.Ghost && !Pvp.IsGranny(p.Id) && PlayerSync.TryGetAvatar(p.Id, out var av) && av.HasData && U.Alive(av.Root))
                .Select(p => p.Id).OrderBy(x => x).ToList();
            if (alive.Count == 0) _watching = 255;
            else
            {
                int i = alive.IndexOf(_watching);
                if (i < 0) i = 0;
                if (!LobbyUI.Open && !LobbyUI.ChatOpen && !MenuOpen)
                {
                    if (In.MouseDown(0)) i = (i + 1) % alive.Count;
                    else if (In.MouseDown(1)) i = (i - 1 + alive.Count) % alive.Count;
                }
                if (alive[i] != _watching) { _watching = alive[i]; Snap(); }
            }

            if (_watching != 255 && PlayerSync.TryGetAvatar(_watching, out var a) && U.Alive(a.Root))
            {
                var t = _camGo.transform;
                // 1:1 – the watched player's own camera (position, rotation, field of view: head bob, crouching,
                // hiding spots, the catch animation), replayed between the received states
                if (PlayerSync.SampleView(a, out var vp, out var vr, out var fov))
                {
                    t.position = vp; t.rotation = vr;
                    SetFov(fov);
                }
                else
                {
                    ViewOf(a, out var head, out var rot);
                    float k = 1f - Mathf.Exp(-Time.unscaledDeltaTime * 12f);
                    t.position = Vector3.Lerp(t.position, head, k);
                    t.rotation = Quaternion.Slerp(t.rotation, rot, k);
                }
            }
            // the watched player's own light is on their avatar (flashlight / lantern); without one the faint light
            // the player normally has keeps the spectator from staring at a black screen
            PState st = default;
            bool has = _watching != 255 && PlayerSync.TryGetState(_watching, out st);
            bool flash = has && (st.Flags & PlayerSync.F_Flashlight) != 0, lantern = has && !flash && (st.Flags & PlayerSync.F_Lantern) != 0;
            // hiding (bed, coffin, car): the hand items are put away and the game lights the hiding spot's own camera
            // with its own flashlight / lantern light – the same light is put on the spectator camera
            bool inSpot = has && (st.Flags & PlayerSync.F_Hiding) != 0 && st.Spot >= 1 && st.Spot <= 6;
            // the game puts the hand lamp away while hiding (its flag goes off) – the lamp the player had before counts
            if (inSpot)
            {
                UpdateRig(false, false, 255);
                Vector3 near = PlayerSync.TryGetAvatar(_watching, out var ha) ? ha.TPos : _camGo.transform.position;
                int kind = flash ? 1 : lantern ? 2 : _lampBefore.TryGetValue(_watching, out var lb) ? lb : 0;
                UpdateSpotLight(st.Spot, kind, near);
                UpdateSpotLook(GameRefs.SpotCamera(st.Spot, near));
            }
            else
            {
                UpdateSpotLook(null);
                UpdateRig(flash, lantern, has && (st.Flags & PlayerSync.F_Hiding) == 0 ? st.Held : (byte)255);
                SprayFx.Show(_rigHeld, has && (st.Flags2 & PlayerSync.F2_Spray) != 0, ref _rigSprayShown);
                // climbing out of the window: the game puts the hand lamp away and films with the window's own cameras,
                // which carry their own flashlight / lantern light – the same light is put on the spectator camera
                var wcam = has && !flash && !lantern ? WindowCam(st.ViewPos) : null;
                int wkind = wcam != null && _lampBefore.TryGetValue(_watching, out var wlb) ? wlb : 0;
                if (wcam != null && wkind != 0) { UpdateCamLight(wcam, 1000 + wkind, wkind); _windowLight = true; }
                else { UpdateSpotLight(0, 0, default); _windowLight = false; }
            }
            bool hasLamp = inSpot || _windowLight ? U.Alive(_spotLight) : flash || lantern;
            bool dark = !hasLamp && U.Alive(_rigDark);
            if (U.Alive(_rigDark) && _rigDark.activeSelf != dark) _rigDark.SetActive(dark);
            bool dim = !hasLamp && !dark;
            if (U.Alive(_dim) && _dim.enabled != dim) _dim.enabled = dim;
        }

        // where the hand items (flashlight, lantern, held item) are relative to the eyes: HandHoldObjects follows the
        // camera with a lag, so the pose is taken only after the camera and the hands have been still for a while
        static Vector3 _prevCamPos, _prevRel; static Quaternion _prevCamRot = Quaternion.identity; static int _stillFrames; static bool _hhHave;
        static void SampleHands()
        {
            try
            {
                bool caught = U.Alive(GameRefs.Fps) && GameRefs.Fps.playerCaught;
                if (caught || !U.Alive(_viewCam) || !_viewCam.isActiveAndEnabled || !U.Alive(_hands) || !_hands.activeInHierarchy) { _stillFrames = 0; return; }
                var ct = _viewCam.transform;
                var rel = ct.InverseTransformPoint(_hands.transform.position);
                bool still = (ct.position - _prevCamPos).sqrMagnitude < 1e-8f && Quaternion.Angle(ct.rotation, _prevCamRot) < 0.01f && (rel - _prevRel).sqrMagnitude < 1e-8f;
                _prevCamPos = ct.position; _prevCamRot = ct.rotation; _prevRel = rel;
                _stillFrames = still ? _stillFrames + 1 : 0;
                // a first rough pose until the player stands still once
                if (rel.sqrMagnitude <= 1f && (_stillFrames >= 15 || !_hhHave))
                {
                    _hhHave = true;
                    _hhPos = rel;
                    _hhRot = Quaternion.Inverse(ct.rotation) * _hands.transform.rotation;
                }
            }
            catch { }
        }

        static void Snap()
        {
            if (!Active || !PlayerSync.TryGetAvatar(_watching, out var a) || !U.Alive(a.Root)) return;
            bool exact = PlayerSync.SampleView(a, out var head, out var rot, out var fov);
            if (!exact) ViewOf(a, out head, out rot); else SetFov(fov);
            _camGo.transform.position = head; _camGo.transform.rotation = rot;
            Log.Debug($"Spectator: watching {Session.NameOf(a.Id)}, {(exact ? "their own camera" : "estimated view")} at {head}, eye {a.CamY:F2} m above the feet, fov {fov:F0}");
        }

        static Camera _specCam;
        static void SetFov(float fov)
        {
            if (!U.Alive(_specCam)) _specCam = _camGo.GetComponent<Camera>();
            if (U.Alive(_specCam) && Mathf.Abs(_specCam.fieldOfView - fov) > 0.05f) _specCam.fieldOfView = fov;
        }

        /// <summary>Where the watched player's eyes are: their head, or the camera of the hiding spot they are in.</summary>
        static void ViewOf(Avatar a, out Vector3 pos, out Quaternion rot)
        {
            pos = a.Root.transform.position + Vector3.up * a.CamY;
            rot = Quaternion.Euler(Mathf.Clamp(a.TPitch, -89f, 89f), a.TYaw, 0f);
            if ((a.Flags & PlayerSync.F_Hiding) != 0 && PlayerSync.TryGetState(a.Id, out var st) && st.Spot >= 1 && st.Spot <= 6)
            {
                var hc = GameRefs.SpotCamera(st.Spot, a.TPos);
                if (U.Alive(hc)) pos = hc.position;
            }
        }

        static bool HasPostProcessing(Component c)
        {
            try
            {
                foreach (var mb in c.GetComponents(Il2CppInterop.Runtime.Il2CppType.Of<MonoBehaviour>()))
                    if (mb != null && U.Il2CppTypeName(mb) == "PostProcessingBehaviour") return true;
            }
            catch { }
            return false;
        }

        /// <summary>The camera the player normally sees through (the one with the game's post-processing).</summary>
        static Camera SourceCamera(Transform root)
        {
            Camera best = null; int bestScore = -1;
            try
            {
                foreach (var c in root.GetComponentsInChildren(Il2CppInterop.Runtime.Il2CppType.Of<Camera>(), true))
                {
                    var cam = c.TryCast<Camera>();
                    if (cam == null || cam.targetTexture != null) continue;
                    int score = (HasPostProcessing(cam) ? 4 : 0) + (cam.enabled ? 2 : 0) + (cam.gameObject.activeInHierarchy ? 1 : 0);
                    if (score > bestScore) { best = cam; bestScore = score; }
                }
            }
            catch (Exception e) { Log.Debug("spectator camera: " + e.Message); }
            return best;
        }

        static void Disable(Behaviour b)
        {
            if (!U.Alive(b) || !b.enabled) return;
            b.enabled = false;
            if (!_disabled.Contains(b)) _disabled.Add(b);
        }

        static void Deactivate(GameObject g)
        {
            if (!U.Alive(g) || !g.activeSelf) return;
            g.SetActive(false);
            if (!_deactivated.Contains(g)) _deactivated.Add(g);
        }

        static bool IsOurs(Component c) => U.Alive(_camGo) && U.IsUnder(c.transform, _camGo.transform);

        /// <summary>
        /// Everything of the dead player that must stay off while spectating: every other screen camera and audio
        /// listener (player camera, hiding-spot cameras), the flashlight / lantern and the hand items – those stayed
        /// where the player died. The game switches some of them back on, so this runs regularly.
        /// </summary>
        static void KeepOff()
        {
            try
            {
                foreach (var c in Camera.allCameras)
                    if (c != null && c.targetTexture == null && !IsOurs(c)) Disable(c);
                foreach (var o in UnityEngine.Object.FindObjectsOfType(Il2CppInterop.Runtime.Il2CppType.Of<AudioListener>()))
                {
                    var l = o.TryCast<AudioListener>();
                    if (l != null && !IsOurs(l)) Disable(l);
                }
                var fps = GameRefs.Fps;
                if (U.Alive(fps)) Disable(fps);
                foreach (var o in UnityEngine.Object.FindObjectsOfType(Il2CppInterop.Runtime.Il2CppType.Of<flashlightController>(), true))
                {
                    var fc = o.TryCast<flashlightController>();
                    if (fc == null) continue;
                    Disable(fc);
                    Deactivate(fc.flashlightPlayer);
                }
                foreach (var o in UnityEngine.Object.FindObjectsOfType(Il2CppInterop.Runtime.Il2CppType.Of<lanternController>(), true))
                {
                    var lc = o.TryCast<lanternController>();
                    if (lc == null) continue;
                    Disable(lc);
                    Deactivate(lc.lanternPlayer);
                }
                if (!U.Alive(_hands)) _hands = GameObject.Find("HandHoldObjects");
                Deactivate(_hands);
                // the "you are hidden" picture in the corner (the player may have died in a hiding spot) – the
                // game switches it itself when the player hides again, so it is not restored
                if (!U.Alive(_hidePics)) _hidePics = GameObject.Find("AllhidePictures");
                if (U.Alive(_hidePics))
                    for (int i = 0; i < _hidePics.transform.childCount; i++)
                    {
                        var ch = _hidePics.transform.GetChild(i).gameObject;
                        if (ch.activeSelf) ch.SetActive(false);
                    }
                // the hiding spots' own flashlight / lantern lights (the player may have died in a hiding spot) – the
                // state from before is restored when spectating ends (the game itself never switches these lights on
                // again: picking up the flashlight / lantern activates them once)
                _spotLocal.Clear();
                if (!U.Alive(_inBed)) _inBed = GameObject.Find("PlayerInBed");
                foreach (var l in SpotLights())
                {
                    _spotLocal.Add(l);
                    int key = l.GetInstanceID(); // stable for the light itself, whichever wrapper the lookup returns
                    if (!_spotOrig.ContainsKey(key)) _spotOrig[key] = l.enabled;
                }
                KeepSpotLightsOff();
                // the catch scenes' cameras (Granny's jumpscare, the spider's mouth) carry their own copy of the
                // player's flashlight / lantern light – in single player the day ends right after, a spectator stays
                if (!U.Alive(_catchScenes)) _catchScenes = GameObject.Find("GameOverScenes");
                if (!U.Alive(_spiderHead)) _spiderHead = GameObject.Find("MomSpiderHeadParent");
                foreach (var holder in new[] { _catchScenes, _spiderHead })
                {
                    if (!U.Alive(holder)) continue;
                    foreach (var c in holder.GetComponentsInChildren(Il2CppInterop.Runtime.Il2CppType.Of<Light>(), true))
                    {
                        var l = c.TryCast<Light>();
                        if (l != null && (holder == _catchScenes || IsPlayerHeadLight(l))) Disable(l);
                    }
                }
                // any light still on the dead player's body
                if (U.Alive(GameRefs.PlayerRoot))
                    foreach (var c in GameRefs.PlayerRoot.GetComponentsInChildren(Il2CppInterop.Runtime.Il2CppType.Of<Light>(), true))
                    {
                        var l = c.TryCast<Light>();
                        if (l != null) Disable(l);
                    }
            }
            catch (Exception e) { Log.Debug("spectator keep-off: " + e.Message); }
        }
        static GameObject _hands, _hidePics, _inBed, _catchScenes, _spiderHead;

        /// <summary>A light under the spider's PlayerHead (the eaten player's flashlight / lantern), not the spider's own.</summary>
        static bool IsPlayerHeadLight(Light l)
        {
            for (var t = l.transform; t != null; t = t.parent) if (t.name == "PlayerHead" || t.name == "PlayerHeadHolder") return true;
            return false;
        }
        static readonly List<Light> _spotLocal = new();
        // their state before spectating (by instance id) – restored afterwards (the game does not switch them on again)
        static readonly Dictionary<int, bool> _spotOrig = new();

        /// <summary>Every light of the hiding spots' cameras (beds, coffins, car) and of the player's under-bed rig.</summary>
        static List<Light> SpotLights()
        {
            var list = new List<Light>();
            var heads = SpotHeads().ToList();
            if (U.Alive(_inBed)) heads.Add(_inBed);
            foreach (var h in heads)
                try
                {
                    foreach (var c in h.GetComponentsInChildren(Il2CppInterop.Runtime.Il2CppType.Of<Light>(), true))
                    {
                        var l = c?.TryCast<Light>();
                        if (l != null) list.Add(l);
                    }
                }
                catch { }
            return list;
        }

        /// <summary>The hiding spots' lights get back the state they had before spectating.</summary>
        static void RestoreSpotLights()
        {
            if (_spotOrig.Count == 0) return;
            int n = 0;
            foreach (var l in SpotLights())
                try
                {
                    if (_spotOrig.TryGetValue(l.GetInstanceID(), out var on) && l.enabled != on) { l.enabled = on; n++; }
                }
                catch { }
            Log.Debug($"spectator: {n} hiding-spot light(s) switched back on ({_spotOrig.Count} remembered)");
            _spotOrig.Clear();
        }
        // every frame: the game turns a hiding spot's light back on while its camera is active
        static void KeepSpotLightsOff()
        {
            foreach (var l in _spotLocal)
                try { if (U.Alive(l) && l.enabled) l.enabled = false; } catch { }
        }

        /// <summary>The faint light the player has without a lamp (LightOnplayerDarker) – copied to the spectator.</summary>
        static Light PlayerAmbientLight()
        {
            try
            {
                var roots = new List<Transform>();
                if (U.Alive(GameRefs.PlayerRoot)) roots.Add(GameRefs.PlayerRoot);
                var hands = GameObject.Find("HandHoldObjects"); if (U.Alive(hands)) roots.Add(hands.transform);
                foreach (var r in roots)
                    foreach (var c in r.GetComponentsInChildren(Il2CppInterop.Runtime.Il2CppType.Of<Light>(), false))
                    {
                        var l = c.TryCast<Light>();
                        if (l != null && l.enabled && l.type == LightType.Point && l.gameObject.name.IndexOf("LightOnplayer", StringComparison.OrdinalIgnoreCase) >= 0) return l;
                    }
            }
            catch { }
            return null;
        }

        static GameObject CloneCamera(Camera src)
        {
            if (src == null) return null;
            try
            {
                var copy = UnityEngine.Object.Instantiate((UnityEngine.Object)src.gameObject)?.TryCast<GameObject>();
                if (copy == null) return null;
                copy.name = "GrannyMP_Spectator";
                copy.transform.SetParent(null, true);
                copy.transform.position = src.transform.position; copy.transform.rotation = src.transform.rotation;
                for (int i = copy.transform.childCount - 1; i >= 0; i--)
                {
                    var ch = copy.transform.GetChild(i).gameObject;
                    ch.SetActive(false);
                    UnityEngine.Object.Destroy(ch);
                }
                // keep only what renders the picture the player sees (camera + the game's post-processing)
                foreach (var c in copy.GetComponents(Il2CppInterop.Runtime.Il2CppType.Of<Component>()))
                {
                    if (c == null) continue;
                    var n = U.Il2CppTypeName(c);
                    if (n == "Transform" || n == "Camera" || n == "FlareLayer" || n == "PostProcessingBehaviour" || n == "AudioListener") continue;
                    try { var b = c.TryCast<Behaviour>(); if (b != null) b.enabled = false; } catch { }
                    try { var a = c.TryCast<AudioSource>(); if (a != null) a.Stop(); } catch { }
                    UnityEngine.Object.Destroy(c);
                }
                var cam = copy.GetComponent<Camera>();
                if (cam == null) { UnityEngine.Object.Destroy(copy); return null; }
                cam.enabled = true; cam.targetTexture = null; cam.fieldOfView = _viewFov;
                cam.nearClipPlane = Mathf.Min(cam.nearClipPlane, 0.05f);
                var al = copy.GetComponent<AudioListener>();
                if (al == null) al = copy.AddComponent<AudioListener>();
                al.enabled = true;
                if (!copy.activeSelf) copy.SetActive(true);
                Log.Info($"Spectator camera: copy of '{src.name}' (post-processing {(HasPostProcessing(cam) ? "on" : "none")})");
                return copy;
            }
            catch (Exception e) { Log.Warn("Spectator camera copy failed: " + e.Message); return null; }
        }

        public static bool MenuOpen { get { try { return U.Alive(GameRefs.Fps) && GameRefs.Fps.menuEnabled; } catch { return false; } } }

        /// <summary>Esc opens the game's pause menu also while spectating (the disabled player controller normally does it).</summary>
        static void UpdateMenu()
        {
            var fps = GameRefs.Fps;
            if (!U.Alive(fps)) return;
            bool menu = MenuOpen, ui = LobbyUI.Open || LobbyUI.ChatOpen;
            if (!menu && !ui && In.KeyDown(KeyCode.Escape))
            {
                try
                {
                    if (U.Alive(fps.inGameOptionMenu))
                    {
                        fps.inGameOptionMenu.SetActive(true);
                        fps.menuEnabled = true;
                        menu = true;
                        Cursor.lockState = CursorLockMode.None; Cursor.visible = true; _cursorLocked = false;
                    }
                }
                catch (Exception e) { Log.Debug("spectator menu: " + e.Message); }
            }
            // the menu's Resume closed it – lock the cursor again (the player's mouse look is off while spectating)
            if (!menu && !ui && (!_cursorLocked || Cursor.lockState != CursorLockMode.Locked))
            {
                Cursor.lockState = CursorLockMode.Locked; Cursor.visible = false; _cursorLocked = true;
            }
            else if (menu || ui) _cursorLocked = false;
        }

        /// <summary>The game's crosshair dot (hidden by the catch sequences) stays visible while spectating.</summary>
        static void KeepCrosshair()
        {
            try
            {
                if (!U.Alive(_crosshair) && U.Alive(GameRefs.BedCatch)) _crosshair = GameRefs.BedCatch.mittPrick;
                if (U.Alive(_crosshair) && !_crosshair.activeSelf) _crosshair.SetActive(true);
            }
            catch { }
        }

        /// <summary>A copy of a hand-holder child (HandHoldObjects/…) without scripts, physics or sounds.</summary>
        static GameObject CopyHandPart(Transform src, Transform parent)
        {
            if (!U.Alive(src)) return null;
            var copy = UnityEngine.Object.Instantiate((UnityEngine.Object)src.gameObject)?.TryCast<GameObject>();
            if (copy == null) return null;
            copy.name = "Spectator_" + src.name;
            foreach (var c in copy.GetComponentsInChildren(Il2CppInterop.Runtime.Il2CppType.Of<Component>(), true))
            {
                if (c == null) continue;
                var n = U.Il2CppTypeName(c);
                if (n == "Transform" || n == "MeshFilter" || n == "MeshRenderer" || n == "SkinnedMeshRenderer" || n == "Light") continue;
                try { var b = c.TryCast<Behaviour>(); if (b != null) b.enabled = false; } catch { }
                UnityEngine.Object.Destroy(c);
            }
            copy.transform.SetParent(parent, false);
            copy.transform.localPosition = src.localPosition;
            copy.transform.localRotation = src.localRotation;
            copy.transform.localScale = src.localScale;
            foreach (var c in copy.GetComponentsInChildren(Il2CppInterop.Runtime.Il2CppType.Of<Light>(), true))
            {
                var l = c.TryCast<Light>();
                if (l != null) l.enabled = true;
            }
            return copy;
        }

        static void BuildRig()
        {
            try
            {
                if (!U.Alive(_hands)) return;
                _rig = new GameObject("Hands");
                _rig.transform.SetParent(_camGo.transform, false);
                _rig.transform.localPosition = _hhPos; _rig.transform.localRotation = _hhRot;
                var src = _hands.transform.Find("LanternLightHand");
                _rigLights = CopyHandPart(src, _rig.transform);
                if (U.Alive(_rigLights))
                {
                    _rigFlash = _rigLights.transform.Find("HoldingLightSourcesAnimation/FlashlightHand")?.gameObject;
                    _rigLantern = _rigLights.transform.Find("HoldingLightSourcesAnimation/LanternHand")?.gameObject;
                    _rigLights.SetActive(false);
                }
                for (int i = 0; i < _hands.transform.childCount; i++)
                {
                    var ch = _hands.transform.GetChild(i);
                    // the faint light the player has without a lamp – only when the game actually uses it
                    if (ch.name.StartsWith("LightOnplayerDarker", StringComparison.OrdinalIgnoreCase) && ch.gameObject.activeSelf) { _rigDark = CopyHandPart(ch, _rig.transform); break; }
                }
                if (U.Alive(_rigDark)) _rigDark.SetActive(false);
                _rigHeldIdx = 254;
                Log.Debug($"spectator hands: lights={U.Alive(_rigLights)} flashlight={U.Alive(_rigFlash)} lantern={U.Alive(_rigLantern)} dark={U.Alive(_rigDark)}");
            }
            catch (Exception e) { Log.Debug("spectator hands: " + e.Message); }
        }

        static void UpdateRig(bool flash, bool lantern, byte held)
        {
            if (!U.Alive(_rig)) return;
            try
            {
                if (U.Alive(_rigLights))
                {
                    bool any = flash || lantern;
                    if (_rigLights.activeSelf != any) _rigLights.SetActive(any);
                    if (U.Alive(_rigFlash) && _rigFlash.activeSelf != flash) _rigFlash.SetActive(flash);
                    if (U.Alive(_rigLantern) && _rigLantern.activeSelf != lantern) _rigLantern.SetActive(lantern);
                }
                // a change of the held item counts only when it lasts (a single state with another value – a packet
                // from a moment ago, the hiding flag flickering – made the item vanish and come back)
                if (held != _heldPending) { _heldPending = held; _heldPendingAt = Time.unscaledTime; }
                bool settled = _rigHeldIdx == 254 || Time.unscaledTime - _heldPendingAt > 0.35f;
                if (held != _rigHeldIdx && settled)
                {
                    _rigHeldIdx = held;
                    if (U.Alive(_rigHeld)) UnityEngine.Object.Destroy(_rigHeld);
                    _rigHeld = null;
                    var model = PlayerSync.HandModel(held);
                    if (U.Alive(model) && U.Alive(_hands) && model.transform.parent == _hands.transform)
                    {
                        _rigHeld = CopyHandPart(model.transform, _rig.transform);
                        SprayFx.Reset(_rigHeld); _rigSprayShown = false;
                        if (U.Alive(_rigHeld)) _rigHeld.SetActive(true);
                    }
                }
            }
            catch (Exception e) { Log.Debug("spectator hands: " + e.Message); }
        }

        /// <summary>The hiding spots' camera objects (Granny's bedCam1-3, coffinHead1/2, carHead) – they carry their own lights.</summary>
        static IEnumerable<GameObject> SpotHeads()
        {
            var g = GameRefs.Granny;
            if (!U.Alive(g)) yield break;
            GameObject[] all = null;
            try { all = new[] { g.bedCam1, g.bedCam2, g.bedCam3, g.coffinHead1, g.coffinHead2, g.carHead }; } catch { }
            if (all == null) yield break;
            foreach (var go in all) if (U.Alive(go)) yield return go;
        }

        /// <summary>The flashlight (kind 1) or lantern (kind 2) light of the camera a player looks through in a hiding spot.</summary>
        static List<Light> SpotSourceLights(Transform cam, int kind)
        {
            var list = new List<Light>();
            // the lights are children of the camera; the parent is searched too in case the camera sits deeper
            foreach (var root in new[] { cam, cam.parent })
            {
                if (root == null || list.Count > 0) continue;
                foreach (var c in root.GetComponentsInChildren(Il2CppInterop.Runtime.Il2CppType.Of<Light>(), true))
                {
                    var l = c.TryCast<Light>();
                    if (l == null) continue;
                    // the whole path below the camera: LanternLight_Car/.../LyktaLight, FlashlightSpotLight_Bed1
                    string n = "";
                    for (var t = l.transform; t != null && t != root; t = t.parent) n += t.name + "/";
                    bool isFlash = n.IndexOf("Flashlight", StringComparison.OrdinalIgnoreCase) >= 0;
                    bool isLantern = n.IndexOf("Lykta", StringComparison.OrdinalIgnoreCase) >= 0 || n.IndexOf("Lantern", StringComparison.OrdinalIgnoreCase) >= 0;
                    if ((kind == 1 && isFlash) || (kind == 2 && isLantern && !isFlash)) list.Add(l);
                }
            }
            return list;
        }

        // the hiding spot's camera has its own post-processing (brighter picture under the bed / in the coffin) –
        // the spectator camera uses that profile while the watched player hides
        static UnityEngine.PostProcessing.PostProcessingBehaviour _specPP;
        static UnityEngine.PostProcessing.PostProcessingProfile _specPPOrig;
        static Transform _spotLookCam; static bool _spotLookSet;
        static void UpdateSpotLook(Transform spotCam)
        {
            if (spotCam == _spotLookCam && (_spotLookSet || spotCam == null)) return;
            _spotLookCam = spotCam;
            try
            {
                if (!U.Alive(_specPP))
                {
                    _specPP = _camGo.GetComponent<UnityEngine.PostProcessing.PostProcessingBehaviour>();
                    if (!U.Alive(_specPP)) return;
                    _specPPOrig = _specPP.profile;
                }
                UnityEngine.PostProcessing.PostProcessingProfile prof = null;
                if (U.Alive(spotCam))
                {
                    var src = spotCam.GetComponent<UnityEngine.PostProcessing.PostProcessingBehaviour>();
                    if (U.Alive(src)) prof = src.profile;
                }
                _specPP.profile = prof != null ? prof : _specPPOrig;
                _spotLookSet = U.Alive(spotCam);
                if (prof != null) Log.Debug("spectator: hiding spot post-processing from " + spotCam.name);
            }
            catch (Exception e) { Log.Debug("spectator post-processing: " + e.Message); _spotLookSet = true; }
        }

        static GameObject _spotLight; static int _spotKey = -1;
        // the lamp each player had in hand before hiding (the game puts it away in a hiding spot, so its flag goes off)
        static readonly Dictionary<byte, int> _lampBefore = new();
        static void TrackLamps()
        {
            if (!Session.Active || !GameRefs.InGameScene) { if (_lampBefore.Count > 0) _lampBefore.Clear(); return; }
            try
            {
                foreach (var p in Session.Players.Values)
                {
                    if (p.IsLocal || !PlayerSync.TryGetState(p.Id, out var s) || (s.Flags & PlayerSync.F_Hiding) != 0) continue;
                    // the lamp is put away for the window jump – the one before still counts
                    if ((s.Flags & (PlayerSync.F_Flashlight | PlayerSync.F_Lantern)) == 0 && WindowCam(s.ViewPos) != null) continue;
                    _lampBefore[p.Id] = (s.Flags & PlayerSync.F_Flashlight) != 0 ? 1 : (s.Flags & PlayerSync.F_Lantern) != 0 ? 2 : 0;
                }
            }
            catch { }
        }
        /// <summary>The light the watched player has in their hiding spot (spot 0 / kind 0 = none).</summary>
        static bool _windowLight;
        static Transform _winIn, _winOut; static float _winFindAt;

        /// <summary>The window jump's camera (JumpOutWindow) the given view position is at, or null.</summary>
        static Transform WindowCam(Vector3 view)
        {
            if (!U.Alive(_winIn) && Time.unscaledTime >= _winFindAt)
            {
                _winFindAt = Time.unscaledTime + 5f;
                try
                {
                    var w = GameObject.Find("JumpOutWindow");
                    if (w != null)
                    {
                        _winIn = w.transform.Find("CamIndoorParent/CameraIndoor");
                        foreach (var t in w.GetComponentsInChildren<Transform>(true))
                            if (t != null && t.name.StartsWith("CameraOutdoor", StringComparison.Ordinal)) { _winOut = t; break; }
                    }
                }
                catch { }
            }
            foreach (var c in new[] { _winIn, _winOut })
            {
                if (!U.Alive(c)) continue;
                var parent = c.parent != null ? c.parent.position : c.position;
                if ((view - c.position).sqrMagnitude < 9f || (view - parent).sqrMagnitude < 9f) return c;
            }
            return null;
        }

        static void UpdateSpotLight(int spot, int kind, Vector3 near)
        {
            int key = kind == 0 ? 0 : spot * 4 + kind;
            if (key == _spotKey && (key == 0 || U.Alive(_spotLight))) return;
            UpdateCamLight(kind == 0 ? null : GameRefs.SpotCamera(spot, near), key, kind);
        }

        /// <summary>Copies of the lights a camera carries (a hiding spot's, the window's) on the spectator camera.</summary>
        static void UpdateCamLight(Transform cam, int key, int kind)
        {
            int spot = key;
            if (key == _spotKey && (key == 0 || U.Alive(_spotLight))) return;
            _spotKey = key;
            if (U.Alive(_spotLight)) UnityEngine.Object.Destroy(_spotLight);
            _spotLight = null;
            if (key == 0 || !Active) return;
            try
            {
                if (!U.Alive(cam)) return;
                var srcs = SpotSourceLights(cam, kind);
                if (srcs.Count == 0) { Log.Debug($"spectator spot light: none for spot {spot} kind {kind}"); return; }
                _spotLight = new GameObject("Spectator_SpotLights");
                _spotLight.transform.SetParent(_camGo.transform, false);
                foreach (var src in srcs)
                {
                    var go = new GameObject("Spectator_" + src.gameObject.name);
                    go.transform.SetParent(_spotLight.transform, false);
                    // offset in the camera's rotation frame – without the camera's scale (InverseTransformPoint divides by it)
                    go.transform.localPosition = Quaternion.Inverse(cam.rotation) * (src.transform.position - cam.position);
                    go.transform.localRotation = Quaternion.Inverse(cam.rotation) * src.transform.rotation;
                    var l = go.AddComponent<Light>();
                    l.type = src.type; l.range = src.range; l.spotAngle = src.spotAngle; l.innerSpotAngle = src.innerSpotAngle;
                    l.intensity = src.intensity; l.color = src.color; l.bounceIntensity = src.bounceIntensity;
                    l.cookie = src.cookie; l.cookieSize = src.cookieSize; l.cullingMask = src.cullingMask; l.renderMode = src.renderMode;
                    l.shadows = src.shadows; l.shadowStrength = src.shadowStrength; l.shadowBias = src.shadowBias;
                    l.shadowNormalBias = src.shadowNormalBias; l.shadowNearPlane = src.shadowNearPlane; l.shadowResolution = src.shadowResolution;
                    l.enabled = true;
                    Log.Debug($"spectator spot light: copy of '{src.gameObject.name}' for spot {spot}: {src.type} range {src.range:F1} angle {src.spotAngle:F0} intensity {src.intensity:F2} at {go.transform.localPosition}");
                }
            }
            catch (Exception e) { Log.Debug("spectator spot light: " + e.Message); }
        }

        static void Begin()
        {
            var root = GameRefs.PlayerRoot;
            var src = SourceCamera(root);
            var ambient = PlayerAmbientLight();
            float ambRange = 0, ambIntensity = 0; Color ambColor = Color.white;
            if (ambient != null) { ambRange = ambient.range; ambIntensity = ambient.intensity; ambColor = ambient.color; }

            // drop the item in hand (the game's own drop) so it is not lost for the others until the next day
            try
            {
                World.AllowLocalSpawnsUntil = Time.unscaledTime + 2f;
                if (U.Alive(GameRefs.Inventory)) GameRefs.Inventory.CheckInventory();
            }
            catch (Exception e) { Log.Debug("spectator drop: " + e.Message); }

            _camGo = CloneCamera(src);
            if (_camGo == null)
            {
                _camGo = new GameObject("GrannyMP_Spectator");
                var sc = _camGo.AddComponent<Camera>();
                if (src != null)
                {
                    sc.fieldOfView = src.fieldOfView; sc.farClipPlane = src.farClipPlane; sc.cullingMask = src.cullingMask;
                    sc.clearFlags = src.clearFlags; sc.backgroundColor = src.backgroundColor; sc.renderingPath = src.renderingPath;
                    _camGo.transform.position = src.transform.position; _camGo.transform.rotation = src.transform.rotation;
                }
                sc.nearClipPlane = 0.05f;
                _camGo.AddComponent<AudioListener>();
            }
            try { _camGo.tag = "MainCamera"; } catch { }
            if (!U.Alive(_hands)) _hands = GameObject.Find("HandHoldObjects");
            KeepOff();
            BuildRig();
            _cursorLocked = false;

            var lg = new GameObject("Light"); lg.transform.SetParent(_camGo.transform, false);
            _dim = lg.AddComponent<Light>();
            _dim.type = LightType.Point; _dim.shadows = LightShadows.None; _dim.renderMode = LightRenderMode.ForcePixel;
            if (ambient != null) { _dim.range = ambRange; _dim.intensity = ambIntensity; _dim.color = ambColor; }
            else { _dim.range = 7f; _dim.intensity = 0.45f; _dim.color = new Color(0.75f, 0.8f, 1f); }
            _watching = 255;
            Log.Info("Spectator mode on");
        }

        public static void Stop()
        {
            if (U.Alive(_camGo)) UnityEngine.Object.Destroy(_camGo);
            _camGo = null;
            for (int i = _deactivated.Count - 1; i >= 0; i--) { try { if (U.Alive(_deactivated[i])) _deactivated[i].SetActive(true); } catch { } }
            foreach (var b in _disabled) { try { if (U.Alive(b)) b.enabled = true; } catch { } }
            bool had = _disabled.Count > 0 || _deactivated.Count > 0;
            _disabled.Clear(); _deactivated.Clear();
            if (U.Alive(_spotLight)) UnityEngine.Object.Destroy(_spotLight);
            _spotLight = null; _spotKey = -1; _spotLocal.Clear();
            RestoreSpotLights();
            _inBed = null;
            _specPP = null; _specPPOrig = null; _spotLookCam = null; _spotLookSet = false;
            _watching = 255; _hands = null; _hidePics = null; _specCam = null; _rig = null; _rigLights = null; _rigFlash = null; _rigLantern = null; _rigDark = null; _rigHeld = null; _rigHeldIdx = 254;
            if (had && GameRefs.InGameScene && !LobbyUI.Open) { Cursor.lockState = CursorLockMode.Locked; Cursor.visible = false; }
            if (had) Log.Info("Spectator mode off");
        }
    }
}
