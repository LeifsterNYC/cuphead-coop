using System.Reflection;
using System.Collections.Generic;
using BepInEx.Logging;
using UnityEngine;

namespace CupheadCoop.Coop
{
    /// <summary>
    /// v1.1.0 M8.5 — minimal client-side death mirroring, extended in v1.2.0 wave 2 with the cosmetic
    /// death ghost.
    ///
    /// WHY (hide): with the motor bypass driving the client's cups purely from the host stream, a cup
    /// that dies on the host stops receiving position updates but its local GameObject stays put,
    /// leaving a frozen, alive-looking cup standing in the level. This hides that cup's sprites
    /// while the host reports it dead (or reports it absent while the other player is still
    /// present — the death-then-despawn window). Revive/respawn restores exactly the renderers we
    /// disabled.
    ///
    /// WHY (ghost, v1.2.0): the hidden cup is replaced with the game's own floating death ghost so the
    /// downed player still reads as "waiting for a parry-revive". We spawn it from the local
    /// LevelPlayerController's own <c>deathEffect</c> template (reflection) at the streamed death
    /// position and let its <c>float_cr</c> run autonomously; a living local player's parry can even
    /// revive it locally. The ghost is additive to the hide-renderers cut, and is destroyed on
    /// revive / scene change.
    /// </summary>
    internal static class PlayerDeathSync
    {
        public static ManualLogSource Log;

        // How long a player must be continuously absent (while the other is present) before we
        // treat it as dead/despawned rather than a momentary scene-transition blip.
        private const float AbsentGraceSec = 0.3f;

        private static float _p1AbsentSince = -1f;
        private static float _p2AbsentSince = -1f;
        private static bool _p1Hidden;
        private static bool _p2Hidden;
        private static readonly List<SpriteRenderer> _p1Hid = new List<SpriteRenderer>();
        private static readonly List<SpriteRenderer> _p2Hid = new List<SpriteRenderer>();

        // v1.2.0 ghost. Tracked per player so we spawn exactly one on the death edge and destroy it
        // on revive / scene change. LevelPlayerController.deathEffect is private — reflected once.
        private static global::PlayerDeathEffect _p1Ghost;
        private static global::PlayerDeathEffect _p2Ghost;
        private static FieldInfo _deathEffectField;

        /// <summary>Diagnostics (item 6): number of ghosts currently spawned (0..2).</summary>
        public static int GhostCount
        {
            get { return (_p1Ghost != null ? 1 : 0) + (_p2Ghost != null ? 1 : 0); }
        }

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
                Log?.LogInfo("PlayerDeathSync: hid " + hidList.Count + " renderers for " + id
                             + " (host reports dead/absent)");
                SpawnGhost(id); // additive cosmetic ghost for the downed cup
            }
            else
            {
                Log?.LogInfo("PlayerDeathSync: restored " + hidList.Count + " renderers for " + id
                             + " (revived/respawned)");
                Unhide(hidList);
                hidden = false;
                CleanupGhost(id);
            }
        }

        // Spawn the game's own death ghost from the local controller's deathEffect template. Level
        // players only — arcade/map players have no deathEffect, so the cast simply yields null and
        // we skip. Driven off the streamed death position; float_cr then runs autonomously.
        private static void SpawnGhost(global::PlayerId id)
        {
            try
            {
                bool isP1 = id == global::PlayerId.PlayerOne;
                if ((isP1 ? _p1Ghost : _p2Ghost) != null) return; // already have one

                if (!global::PlayerManager.DoesPlayerExist(id)) return;
                var lvl = global::PlayerManager.GetPlayer(id) as global::LevelPlayerController;
                if (lvl == null) return; // not a level player (arcade/map) — no ghost this wave

                if (_deathEffectField == null)
                {
                    _deathEffectField = typeof(global::LevelPlayerController).GetField("deathEffect",
                        BindingFlags.NonPublic | BindingFlags.Instance);
                    if (_deathEffectField == null)
                    {
                        Log?.LogWarning("PlayerDeathSync: LevelPlayerController.deathEffect not found — ghost disabled");
                        return;
                    }
                }

                var template = _deathEffectField.GetValue(lvl) as global::PlayerDeathEffect;
                if (template == null) return;

                float x = isP1 ? CoopState.RemoteP1X : CoopState.RemoteP2X;
                float y = isP1 ? CoopState.RemoteP1Y : CoopState.RemoteP2Y;
                int deaths = lvl.stats != null ? lvl.stats.Deaths : 0;

                var ghost = template.Create(id, lvl.input, new Vector2(x, y), deaths,
                                            global::PlayerMode.Level, true);
                if (isP1) _p1Ghost = ghost; else _p2Ghost = ghost;
                Log?.LogInfo("PlayerDeathSync: spawned death ghost for " + id + " at (" +
                             x.ToString("F1") + "," + y.ToString("F1") + ")");
            }
            catch (System.Exception ex)
            {
                Log?.LogWarning("PlayerDeathSync: SpawnGhost(" + id + ") failed: " + ex.Message);
            }
        }

        private static void CleanupGhost(global::PlayerId id)
        {
            bool isP1 = id == global::PlayerId.PlayerOne;
            var ghost = isP1 ? _p1Ghost : _p2Ghost;
            if (isP1) _p1Ghost = null; else _p2Ghost = null;
            // Unity's overloaded == treats a destroyed object as null, so a ghost the player already
            // parry-revived (which destroys itself) is skipped here — no double-destroy.
            if (ghost != null)
            {
                try { Object.Destroy(ghost.gameObject); } catch { }
                Log?.LogInfo("PlayerDeathSync: cleaned up death ghost for " + id);
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
            try { CleanupGhost(global::PlayerId.PlayerOne); CleanupGhost(global::PlayerId.PlayerTwo); } catch { }
            _p1Hidden = false;
            _p2Hidden = false;
            _p1AbsentSince = -1f;
            _p2AbsentSince = -1f;
        }
    }
}
