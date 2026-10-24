using System;
using UnityEngine;

namespace GrannyMP
{
    /// <summary>
    /// The small spider in the attic (spiderControll). Its area trigger (spiderTrigger, forwarded from clients) sets
    /// huntPlayer, and spiderControll.Update then walks it to PlayerPos – always the host's player – and its bite is a
    /// host-only trigger that only meets the host's body. So the host remembers who is in the spider's area (the last
    /// player whose stay arrived), points PlayerPos at that player's avatar during the spider's Update, and bites a
    /// remote player itself when the spider reaches them; that player's own game plays the bite (playerCaught.spiderBitePlayer).
    /// </summary>
    internal static class AtticSpider
    {
        static byte _victim = 255; static float _victimAt;
        static Transform _savedPos; static bool _swapped;
        const float BiteReach = 1.2f;

        public static void OnSceneChanged() { _victim = 255; _swapped = false; _savedPos = null; }

        /// <summary>Host: spiderTrigger.OnTriggerStay ran – the host's player, or a client's (forwarded).</summary>
        public static void AreaStay(Collider c)
        {
            if (!Session.Active || !Session.IsHost) return;
            byte who = 255;
            if (Events.Forwarding) who = Events.ActionBy;
            else if (c != null && U.Alive(GameRefs.PlayerRoot) && U.IsUnder(c.transform, GameRefs.PlayerRoot)) who = Session.LocalId;
            if (who == 255 || Pvp.IsGranny(who)) return;
            float now = Time.unscaledTime;
            // the chased player stays the target while they are in the area
            if (_victim != 255 && _victim != who && now - _victimAt < 1.5f) return;
            if (_victim != who) Log.Debug($"attic spider target -> {Session.NameOf(who)}");
            _victim = who; _victimAt = now;
        }

        public static void PreUpdate(spiderControll s)
        {
            if (!Session.Active || !Session.IsHost) return;
            try
            {
                if (_victim == 255 || _victim == Session.LocalId) return;
                if (Time.unscaledTime - _victimAt > 3f) { _victim = 255; return; } // left the area
                if (!s.huntPlayer || s.playerCaught || s.spiderDead) return;
                if (!AiControl.Valid(_victim) || !PlayerSync.TryGetAvatar(_victim, out var a)) return;
                _savedPos = s.PlayerPos;
                s.PlayerPos = a.Root.transform;
                _swapped = true;
            }
            catch (Exception e) { Log.Debug("attic spider pre: " + e.Message); }
        }

        public static void PostUpdate(spiderControll s)
        {
            if (!_swapped) return;
            _swapped = false;
            try
            {
                byte victim = _victim;
                var target = s.PlayerPos;
                s.PlayerPos = _savedPos; _savedPos = null;
                if (!U.Alive(target) || !s.huntPlayer || s.playerCaught || s.spiderDead || GameFlow.InTransition) return;
                Vector3 sp = s.transform.position, tp = target.position;
                if (U.FlatDist(sp, tp) > BiteReach || Mathf.Abs(sp.y - tp.y) > 2.5f || !GameFlow.CanCatch(victim)) return;
                // the bite: the spider goes back to its nest, the player is caught
                s.huntPlayer = false; s.spiderResetNow = true;
                _victim = 255;
                GameFlow.PlayerCaught(victim, "attic spider");
            }
            catch (Exception e) { Log.Debug("attic spider post: " + e.Message); }
        }

        /// <summary>Client: bitten by the attic spider – the game's own bite (the camera turns to the spider, then the end of the day).</summary>
        public static void PlayLocalBite()
        {
            try
            {
                var o = UnityEngine.Object.FindObjectOfType(Il2CppInterop.Runtime.Il2CppType.Of<playerCaught>(), true);
                var pc = o?.TryCast<playerCaught>();
                if (pc == null) { Log.Warn("Spider bite: the player's catch script was not found"); return; }
                pc.spiderBitePlayer = true;
                Log.Info("Jumpscare: the attic spider's bite");
            }
            catch (Exception e) { Log.Warn("Spider bite: " + e.Message); }
        }
    }
}
