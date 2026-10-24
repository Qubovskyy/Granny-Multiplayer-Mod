using System;
using System.Collections.Generic;
using UnityEngine;

namespace GrannyMP
{
    /// <summary>
    /// The big spider in the spider cellar (MomSpiderHead on MomSpiderHeadParent). It runs only on the host; its
    /// position and animations go to the clients as an AI root ([Advanced] AiTypes). The game's spider knows one
    /// player – the host's ("player" / "playerHead"). So before each of its Update steps the host points those fields
    /// at the player the spider should hunt: the nearest one it can see (eye range, view angle, no wall). When the
    /// spider then starts eating a remote player, the eat sequence is stopped on the host and that player is caught
    /// like by Granny – their own game plays the spider's eat sequence (Caught "spider").
    /// </summary>
    internal static class MomSpider
    {
        static MomSpiderHead _s; static MomSpiderEyes _eyes; static float _nextFind;
        static byte _victim = 255; static float _victimSeenAt, _nextPick, _holdUntil;
        static int _mode; // during the spider's Update: 0 its own logic, 1 the host must not be hunted, 2 hunting a remote player
        static bool _eatBlocked;
        static Transform _origPlayer, _origHead; static bool _origHiding;
        const float LostTime = 3f;

        public static MomSpiderHead Spider
        {
            get
            {
                if (U.Alive(_s)) return _s;
                if (Time.unscaledTime < _nextFind) return null;
                _nextFind = Time.unscaledTime + 2f;
                try
                {
                    var o = UnityEngine.Object.FindObjectOfType(Il2CppInterop.Runtime.Il2CppType.Of<MomSpiderHead>(), true);
                    _s = o?.TryCast<MomSpiderHead>();
                    var e = UnityEngine.Object.FindObjectOfType(Il2CppInterop.Runtime.Il2CppType.Of<MomSpiderEyes>(), true);
                    _eyes = e?.TryCast<MomSpiderEyes>();
                }
                catch { }
                return _s;
            }
        }

        public static void OnSceneChanged() { _s = null; _eyes = null; _nextFind = 0; _victim = 255; _mode = 0; _eatBlocked = false; _holdUntil = 0; }

        // ---------------- host: before / after MomSpiderHead.Update ----------------
        public static bool PreUpdate(MomSpiderHead s)
        {
            if (!Session.Active || !Session.IsHost || !GameRefs.InGameScene) return true;
            if (GameFlow.InTransition || GameFlow.StartHolding) return false;
            float now = Time.unscaledTime;
            try
            {
                if (_holdUntil > 0)
                {
                    if (now < _holdUntil) return false; // the catch: the spider stays where it caught the player
                    _holdUntil = 0;
                    try { if (U.Alive(s.agent) && s.agent.enabled) s.agent.isStopped = false; } catch { }
                }
                if (now >= _nextPick) { _nextPick = now + 0.25f; Pick(s, now); }
                bool hostGone = (Session.Local?.Ghost ?? false) || Pvp.IsGranny(Session.LocalId) || Session.GodMode;
                if (_victim == 255 || _victim == Session.LocalId)
                {
                    // nobody remote to hunt: the game's own logic (the host) – but never a spectating host or the PvP Granny
                    if (hostGone) { _origHiding = s.playerInHidingSpot; s.playerInHidingSpot = true; s.seePlayer = false; _mode = 1; }
                    return true;
                }
                if (!PlayerSync.TryGetAvatar(_victim, out var a)) { _victim = 255; return true; }
                _origPlayer = s.player; _origHead = s.playerHead; _origHiding = s.playerInHidingSpot;
                s.player = a.Root.transform;
                s.playerHead = U.Alive(a.Head) ? a.Head : a.Root.transform;
                s.playerInHidingSpot = false;
                if (now - _victimSeenAt < 0.3f) s.seePlayer = true;
                _mode = 2;
            }
            catch (Exception e) { Log.Debug("spider pre: " + e.Message); }
            return true;
        }

        public static void PostUpdate(MomSpiderHead s)
        {
            int mode = _mode;
            if (mode == 0) return;
            _mode = 0;
            try
            {
                if (mode == 2) { s.player = _origPlayer; s.playerHead = _origHead; }
                s.playerInHidingSpot = _origHiding;
                _origPlayer = null; _origHead = null;
                if (_eatBlocked)
                {
                    _eatBlocked = false;
                    byte victim = _victim;
                    s.playerCaught = false; s.Hunting = false; s.seePlayer = false;
                    ToNavPoint(s);
                    if (mode == 2) _victim = 255;
                    if (mode == 2 && victim != 255 && victim != Session.LocalId && GameFlow.CanCatch(victim))
                    {
                        _holdUntil = Time.unscaledTime + 3.5f;
                        try { if (U.Alive(s.agent) && s.agent.enabled) { s.agent.isStopped = true; s.agent.velocity = Vector3.zero; } } catch { }
                        GameFlow.PlayerCaught(victim, "spider");
                    }
                }
            }
            catch (Exception e) { Log.Debug("spider post: " + e.Message); }
        }

        /// <summary>The spider's eat sequence. Host: refused when it was started against a remote player (that player is caught instead).</summary>
        public static bool EatMoveNext()
        {
            if (!Session.Active) return true;
            // started inside the spider's Update while it hunted a remote player (or must not hunt the host)
            if (Session.IsHost && _mode != 0) { _eatBlocked = true; return false; }
            return true;
        }

        static void ToNavPoint(MomSpiderHead s)
        {
            try
            {
                var nav = s.navigations;
                if (nav != null && nav.Length > 0) { var t = nav[Mathf.Clamp(s.index, 0, nav.Length - 1)]; if (t != null) s.target = t; }
            }
            catch { }
        }

        /// <summary>Who the spider hunts: the nearest player it sees; the last one for a few seconds after losing sight.</summary>
        static void Pick(MomSpiderHead s, float now)
        {
            Transform eye = U.Alive(s.spiderMomEye) ? s.spiderMomEye : s.transform;
            float range = 20f, angle = 80f;
            try { if (U.Alive(_eyes)) { if (_eyes.seeRange > 1f) range = _eyes.seeRange; if (_eyes.SeeAngle > 5f) angle = _eyes.SeeAngle; } } catch { }
            byte best = 255; float bestD = float.MaxValue;
            foreach (var p in Session.Players.Values)
            {
                if (!AiControl.Valid(p.Id) || Pvp.IsGranny(p.Id) || (p.Id != Session.LocalId && AiControl.IsHiddenRemote(p.Id))) continue;
                if (!AiControl.HeadOf(p.Id, out var head)) continue;
                var d = head - eye.position; float dist = d.magnitude;
                bool close = dist < 3.5f; // the spider's own "player close" trigger
                if (!close && (dist > range || Vector3.Angle(s.transform.forward, d) > angle)) continue;
                if (!AiControl.ClearLine(eye.position, head, s.transform)) continue;
                if (p.Id == _victim) dist -= 1.5f; // keep the chased one unless another is clearly nearer
                if (dist < bestD) { bestD = dist; best = p.Id; }
            }
            if (best != 255)
            {
                if (best != _victim)
                {
                    Log.Debug($"spider target -> {Session.NameOf(best)}");
                    // it was walking to a remote player's avatar – the host's own target comes from the game's logic
                    try { if (U.Alive(s.target) && PlayerSync.IsAvatarPart(s.target)) ToNavPoint(s); } catch { }
                }
                _victim = best; _victimSeenAt = now;
            }
            else if (_victim != 255 && now - _victimSeenAt > LostTime)
            {
                // lost: back to its rounds (unless it was the host – the game's own logic handles that)
                if (_victim != Session.LocalId) { try { s.Hunting = false; s.seePlayer = false; ToNavPoint(s); } catch { } }
                _victim = 255;
            }
        }

        // ---------------- client: caught by the spider ----------------
        public static void PlayLocalEat()
        {
            var s = Spider;
            if (!U.Alive(s)) return;
            try
            {
                s.StartCoroutine(s.momSpiderEatPlayer());
                Log.Info("Jumpscare: the spider's eat sequence");
            }
            catch (Exception e) { Log.Warn("Spider jumpscare: " + e.Message); }
        }
    }
}
