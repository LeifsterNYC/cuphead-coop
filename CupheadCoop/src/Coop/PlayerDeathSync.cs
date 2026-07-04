using System.Collections.Generic;
using UnityEngine;

namespace CupheadCoop.Coop
{
    /// <summary>
    /// v1.1.0 M8.5 — minimal client-side death mirroring.
    ///
    /// WHY: with the motor bypass driving the client's cups purely from the host stream, a cup
    /// that dies on the host stops receiving position updates but its local GameObject stays put,
    /// leaving a frozen, alive-looking cup standing in the level. This hides that cup's sprites
    /// while the host reports it dead (or reports it absent while the other player is still
    /// present — the death-then-despawn window). It is deliberately the smallest possible cut:
    /// the ghost/parry-revive visuals of a downed player are host-only and are NOT mirrored yet;
    /// this just avoids showing a wrong "still alive" cup. Revive/respawn restores exactly the
    /// renderers we disabled.
    /// </summary>
    internal static class PlayerDeathSync
    {
        // How long a player must be continuously absent (while the other is present) before we
        // treat it as dead/despawned rather than a momentary scene-transition blip.
        private const float AbsentGraceSec = 0.3f;

        private static float _p1AbsentSince = -1f;
        private static float _p2AbsentSince = -1f;
        private static bool _p1Hidden;
        private static bool _p2Hidden;
        private static readonly List<SpriteRenderer> _p1Hid = new List<SpriteRenderer>();
        private static readonly List<SpriteRenderer> _p2Hid = new List<SpriteRenderer>();

        public static void Tick()
        {
            if (!ModConfig.EnableDeathSync.Value) return;
            if (CoopState.RemoteStateSequence == 0) return;
            try
            {
                float now = Time.realtimeSinceStartup;
                bool p1Hide = ShouldHide(true, now);
                bool p2Hide = ShouldHide(false, now);
                ApplyHide(global::PlayerId.PlayerOne, p1Hide, ref _p1Hidden, _p1Hid);
                ApplyHide(global::PlayerId.PlayerTwo, p2Hide, ref _p2Hidden, _p2Hid);
            }
            catch
            {
                // Scene transition raciness — try again next frame.
            }
        }

        private static bool ShouldHide(bool p1, float now)
        {
            bool isDead = p1 ? CoopState.RemoteP1IsDead : CoopState.RemoteP2IsDead;
            bool present = p1 ? CoopState.RemoteP1Present : CoopState.RemoteP2Present;
            bool otherPresent = p1 ? CoopState.RemoteP2Present : CoopState.RemoteP1Present;

            if (isDead)
            {
                if (p1) _p1AbsentSince = -1f; else _p2AbsentSince = -1f;
                return true;
            }

            // Absent while the other player is still present → likely died and despawned. Require
            // it to persist past a short grace so we don't hide during a shared scene transition
            // (where both go absent together — handled by the otherPresent guard).
            if (!present && otherPresent)
            {
                float since = p1 ? _p1AbsentSince : _p2AbsentSince;
                if (since < 0f)
                {
                    if (p1) _p1AbsentSince = now; else _p2AbsentSince = now;
                    return false;
                }
                return (now - since) > AbsentGraceSec;
            }

            // Present again, or both absent — clear the timer and show.
            if (p1) _p1AbsentSince = -1f; else _p2AbsentSince = -1f;
            return false;
        }

        private static void ApplyHide(global::PlayerId id, bool hide, ref bool hidden,
                                      List<SpriteRenderer> hidList)
        {
            if (hide == hidden) return; // no state change this frame

            if (hide)
            {
                // Level players only — map players don't die, so we simply find nothing on the map.
                if (!global::PlayerManager.DoesPlayerExist(id)) return;
                var ctrl = global::PlayerManager.GetPlayer(id);
                if (ctrl == null || ctrl.transform == null) return;

                var srs = ctrl.GetComponentsInChildren<SpriteRenderer>(true);
                hidList.Clear();
                for (int i = 0; i < srs.Length; i++)
                {
                    if (srs[i] != null && srs[i].enabled)
                    {
                        srs[i].enabled = false;
                        hidList.Add(srs[i]); // remember exactly what we disabled, to restore later
                    }
                }
                hidden = true;
            }
            else
            {
                Unhide(hidList);
                hidden = false;
            }
        }

        private static void Unhide(List<SpriteRenderer> hidList)
        {
            for (int i = 0; i < hidList.Count; i++)
                if (hidList[i] != null) hidList[i].enabled = true;
            hidList.Clear();
        }

        public static void Reset()
        {
            try { Unhide(_p1Hid); Unhide(_p2Hid); } catch { }
            _p1Hidden = false;
            _p2Hidden = false;
            _p1AbsentSince = -1f;
            _p2AbsentSince = -1f;
        }
    }
}
