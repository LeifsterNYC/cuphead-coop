using HarmonyLib;

namespace CupheadCoop.Coop
{
    /// <summary>
    /// v0.9.1 architectural pivot — adopted from the Germanized/CupHeads online co-op mod's
    /// `PlayerMotorPatch.cs` (clean-room re-implementation; no code copied). Original architecture
    /// at https://github.com/Germanized/CupHeads/blob/main/CupheadOnline/Patches/PlayerMotorPatch.cs.
    ///
    /// Why: prior architectures (input mirroring, NetworkID-binding, AI-suppression-by-enabled-flag)
    /// all let client run a parallel sim of the player motor and tried to correct via streamed
    /// snapshots. The two sims diverge faster than corrections can keep up — boss attack timing,
    /// jump/dash physics, projectile spawn order all desync.
    ///
    /// Solution: on client, skip the motor's FixedUpdate body entirely for both P1 and P2.
    /// Drive their visible state purely from host's stream:
    ///   - transform.position is written SOLELY by ScenePuppetry.ClientApply, which runs in the
    ///     +32000 LateUpdate off the interpolated snapshot stream. This bypass no longer touches
    ///     position at all (a second FixedUpdate-rate lerp here fought the interpolated LateUpdate
    ///     write and produced jitter).
    ///   - LookDirection / TrueLookDirection / MoveDirection / Grounded forced via Traverse
    ///     (HarmonyLib's reflection helper) so animators and other systems read coherent state
    ///   - Animator state (which is also part of the streamed PlayerSnapshot) gets played by
    ///     ScenePuppetry.ClientApply elsewhere
    /// On host, motor runs unchanged (P1 with local input; P2 with network-forwarded input via
    /// the existing RewiredPatches GetButton/GetAxis postfix on Player 2's id).
    ///
    /// Trilean2 / Trilean: Cuphead's custom -1/0/+1 type used for input-direction state. We have
    /// LookX / LookY as int8 in PlayerSnapshot's Facing already — re-using Facing as LookX for
    /// minimal wire change. LookY isn't streamed yet (would need wire bump); for now we set it to 0
    /// (looking straight). Acceptable approximation for v0.9.1.
    /// </summary>
    [HarmonyPatch]
    internal static class PlayerMotorBypass
    {
        // ---- LevelPlayerMotor (used in boss + run-and-gun gameplay scenes) ----

        [HarmonyPatch(typeof(LevelPlayerMotor), "FixedUpdate")]
        [HarmonyPrefix]
        private static bool LevelMotor_FixedUpdate_Prefix(LevelPlayerMotor __instance)
        {
            if (CoopState.Mode != CoopMode.Client) return true;
            if (__instance == null || __instance.player == null) return true;
            if (!ModConfig.EnableRemoteMotorBypass.Value) return true;

            var id = __instance.player.id;
            ApplyLevelMotorState(__instance, id);
            return false; // skip the original FixedUpdate body
        }

        private static void ApplyLevelMotorState(LevelPlayerMotor motor, global::PlayerId id)
        {
            try
            {
                bool present;
                sbyte facing;
                if (id == global::PlayerId.PlayerOne)
                {
                    present = CoopState.RemoteP1Present;
                    facing = CoopState.RemoteP1Facing;
                }
                else
                {
                    present = CoopState.RemoteP2Present;
                    facing = CoopState.RemoteP2Facing;
                }
                if (!present) return;

                // Position is written by ScenePuppetry.ClientApply (interpolated, +32000 LateUpdate)
                // — deliberately not touched here.

                // Force motor's private-set properties via Traverse so animators / weapon managers
                // / aim logic see coherent direction state. Without this, the motor's internal
                // LookDirection stays at whatever value the (now-skipped) HandleLooking would have
                // set, which is just stale — the cup would face the wrong way.
                int lookX = facing;
                var trav = Traverse.Create(motor);
                trav.Property("LookDirection").SetValue(new global::Trilean2(lookX, 0));
                trav.Property("TrueLookDirection").SetValue(new global::Trilean2(lookX, 0));
                // MoveDirection drives the running animator parameter; mirror look direction
                // when present is true and it's nonzero (very rough proxy — better with full
                // input mirror in PlayerSnapshot but acceptable for v0.9.1).
                trav.Property("MoveDirection").SetValue(new global::Trilean2(lookX, 0));
                // Grounded — without this, the cup is animated as airborne even when host says
                // it's on the floor. We don't have a Grounded bit yet; assume true for now.
                // Adding to wire format would refine.
                trav.Property("Grounded").SetValue(true);
            }
            catch
            {
                // Mid-scene-transition reflection failures shouldn't crash; skip this frame.
            }
        }

        // ---- ArcadePlayerMotor (used in some run-and-gun / arcade gameplay scenes) ----

        [HarmonyPatch(typeof(ArcadePlayerMotor), "FixedUpdate")]
        [HarmonyPrefix]
        private static bool ArcadeMotor_FixedUpdate_Prefix(ArcadePlayerMotor __instance)
        {
            if (CoopState.Mode != CoopMode.Client) return true;
            if (__instance == null || __instance.player == null) return true;
            if (!ModConfig.EnableRemoteMotorBypass.Value) return true;

            var id = __instance.player.id;
            ApplyArcadeMotorState(__instance, id);
            return false;
        }

        private static void ApplyArcadeMotorState(ArcadePlayerMotor motor, global::PlayerId id)
        {
            try
            {
                bool present;
                sbyte facing;
                if (id == global::PlayerId.PlayerOne)
                {
                    present = CoopState.RemoteP1Present;
                    facing = CoopState.RemoteP1Facing;
                }
                else
                {
                    present = CoopState.RemoteP2Present;
                    facing = CoopState.RemoteP2Facing;
                }
                if (!present) return;

                // Position is written by ScenePuppetry.ClientApply (interpolated, +32000 LateUpdate)
                // — deliberately not touched here.

                int lookX = facing;
                var trav = Traverse.Create(motor);
                // ArcadePlayerMotor has the same property names — confirmed from Cuphead decompile.
                trav.Property("LookDirection").SetValue(new global::Trilean2(lookX, 0));
                trav.Property("TrueLookDirection").SetValue(new global::Trilean2(lookX, 0));
                trav.Property("MoveDirection").SetValue(new global::Trilean2(lookX, 0));
                trav.Property("Grounded").SetValue(true);
            }
            catch
            {
                // Same swallowing rationale — scene transition raciness.
            }
        }
    }
}
