using HarmonyLib;
using UnityEngine;

namespace CupheadCoop.Coop
{
    /// <summary>
    /// v0.9.1 architectural pivot — adopted from the Germanized/CupHeads online co-op mod's
    /// `PlayerMotorPatch.cs` (clean-room re-implementation; no code copied). Original architecture
    /// at https://github.com/Germanized/CupHeads/blob/main/CupheadOnline/Patches/PlayerMotorPatch.cs.
    ///
    /// Why: prior architectures (input mirroring, NetworkID-binding, AI-suppression-by-enabled-flag)
    /// all let client run a parallel sim of the player motor and tried to correct via streamed
    /// snapshots. The two sims diverge faster than corrections can keep up.
    ///
    /// Solution: on client, skip the motor's FixedUpdate body entirely for both P1 and P2, and drive
    /// the motor's *polled* state so the game's own animation controller (which keeps running on the
    /// client as of v1.2.0) renders the player faithfully:
    ///   - transform.position is written SOLELY by ScenePuppetry.ClientApply (interpolated, +32000
    ///     LateUpdate). This bypass never touches position.
    ///   - LookDirection / TrueLookDirection / MoveDirection / Grounded / Locked are forced via
    ///     Traverse from the streamed input mirror + <see cref="CoopState.RemoteP1Flags"/> so the
    ///     animation controller's Update reads coherent state (run/idle/turn/aim, grounded/air).
    ///   - weaponManager.IsShooting is forced from the Shooting flag so the shoot-layer weights the
    ///     controller sets each frame render the shooting pose.
    ///   - Edge events the controller drives off game events (which never fire on the client because
    ///     FixedUpdate is skipped) are synthesized from the streamed Pulses: WeaponFired →
    ///     OnShotFired(); DamageTaken → play Hit + set hitAnimation. The Invulnerable flag is mirrored
    ///     onto damageReceiver.state so the controller's flash_cr blinks the cup.
    ///
    /// v1.2.0 removed the old approach of scrubbing the player animator to a streamed hash every
    /// frame — layer-0 scrubbing couldn't render the weighted shoot layers and fought the controller's
    /// own facing flip. Local-driven animation replaces it. NOT forced (no setter exists / out of
    /// v1.2.0 scope): Dashing, DashDirection, IsUsingSuperOrEx — Chalice/super/EX/dash are a known
    /// visual limitation this wave; the flags are still on the wire for a later wave.
    ///
    /// On host, the motor runs unchanged (P1 with local input; P2 with network-forwarded input).
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

            ApplyLevelMotorState(__instance, __instance.player.id);
            return false; // skip the original FixedUpdate body
        }

        private static void ApplyLevelMotorState(LevelPlayerMotor motor, global::PlayerId id)
        {
            try
            {
                bool isP1 = id == global::PlayerId.PlayerOne;
                bool present = isP1 ? CoopState.RemoteP1Present : CoopState.RemoteP2Present;
                if (!present) return;
                byte flags = isP1 ? CoopState.RemoteP1Flags : CoopState.RemoteP2Flags;

                bool grounded = (flags & CoopState.FlagGrounded) != 0;
                bool locked = (flags & CoopState.FlagLocked) != 0;
                bool superEx = (flags & CoopState.FlagSuperEx) != 0;

                int lookX, lookY;
                DeriveLook(isP1, grounded, locked, superEx, out lookX, out lookY);

                var trav = Traverse.Create(motor);
                int trueX = lookX != 0 ? lookX : motor.TrueLookDirection.x;
                trav.Property("LookDirection").SetValue(new global::Trilean2(lookX, lookY));
                trav.Property("TrueLookDirection").SetValue(new global::Trilean2(trueX, lookY));
                trav.Property("MoveDirection").SetValue(new global::Trilean2(lookX, lookY));
                trav.Property("Grounded").SetValue(grounded);
                trav.Property("Locked").SetValue(locked);

                if (ModConfig.EnableAnimationSync.Value)
                {
                    var ctrl = motor.player;
                    var wm = ctrl.weaponManager;
                    bool shooting = (flags & CoopState.FlagShooting) != 0;
                    if (wm != null && wm.IsShooting != shooting) wm.IsShooting = shooting;

                    ForceInvulnerable(ctrl.damageReceiver, (flags & CoopState.FlagInvulnerable) != 0);
                    SynthesizeEdges(isP1, grounded, ctrl.animationController, ctrl);
                }
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

            ApplyArcadeMotorState(__instance, __instance.player.id);
            return false;
        }

        private static void ApplyArcadeMotorState(ArcadePlayerMotor motor, global::PlayerId id)
        {
            try
            {
                bool isP1 = id == global::PlayerId.PlayerOne;
                bool present = isP1 ? CoopState.RemoteP1Present : CoopState.RemoteP2Present;
                if (!present) return;
                byte flags = isP1 ? CoopState.RemoteP1Flags : CoopState.RemoteP2Flags;

                bool grounded = (flags & CoopState.FlagGrounded) != 0;
                bool locked = (flags & CoopState.FlagLocked) != 0;
                bool superEx = (flags & CoopState.FlagSuperEx) != 0;

                int lookX, lookY;
                DeriveLook(isP1, grounded, locked, superEx, out lookX, out lookY);

                var trav = Traverse.Create(motor);
                int trueX = lookX != 0 ? lookX : motor.TrueLookDirection.x;
                trav.Property("LookDirection").SetValue(new global::Trilean2(lookX, lookY));
                trav.Property("TrueLookDirection").SetValue(new global::Trilean2(trueX, lookY));
                trav.Property("MoveDirection").SetValue(new global::Trilean2(lookX, lookY));
                trav.Property("Grounded").SetValue(grounded);
                trav.Property("Locked").SetValue(locked);

                if (ModConfig.EnableAnimationSync.Value)
                {
                    var ctrl = motor.player;
                    var wm = ctrl.weaponManager;
                    bool shooting = (flags & CoopState.FlagShooting) != 0;
                    if (wm != null && wm.IsShooting != shooting) wm.IsShooting = shooting;

                    ForceInvulnerable(ctrl.damageReceiver, (flags & CoopState.FlagInvulnerable) != 0);
                    SynthesizeArcadeEdges(isP1, grounded, ctrl.animationController, ctrl);
                }
            }
            catch
            {
                // Same swallowing rationale — scene transition raciness.
            }
        }

        // Digitize the mirrored analog axes into the same -1/0/+1 Trilean the motor's HandleLooking
        // would produce, using GetAxisInt's exact thresholds (magnitude 0.375; direction cos 0.38268
        // for X, 0.5 crampedDiagonal for Y; duck override -0.705). Reading the mirror directly (rather
        // than routing through Rewired/GetAxisInt) keeps this independent of the client window's OS
        // focus, which the input focus-gate would otherwise zero. Allocation-free (Vector2 is a struct).
        private static void DeriveLook(bool isP1, bool grounded, bool locked, bool superEx,
                                       out int lookX, out int lookY)
        {
            float ax = isP1 ? CoopState.MirroredP1AxisX : CoopState.MirroredP2AxisX;
            float ay = isP1 ? CoopState.MirroredP1AxisY : CoopState.MirroredP2AxisY;
            var v = new Vector2(ax, ay);
            bool duckMod = grounded && !locked && !superEx;
            lookX = AxisToInt(v, false, false, false);
            lookY = AxisToInt(v, true, true, duckMod);
        }

        // Mirrors PlayerInput.GetAxisInt's core (post-camera-rotate branch, which is off in-level).
        private static int AxisToInt(Vector2 v, bool wantY, bool crampedDiagonal, bool duckMod)
        {
            float magnitude = v.magnitude;
            if (magnitude < 0.375f) return 0;
            float threshold = crampedDiagonal ? 0.5f : 0.38268f;
            float component = (wantY ? v.y : v.x) / magnitude;
            if (component > threshold) return 1;
            if (component < (duckMod ? -0.705f : -threshold)) return -1;
            return 0;
        }

        // Force damageReceiver.state to Invulnerable during the streamed i-frame window so the
        // controller's flash_cr coroutine (Flashing => state == Invulnerable) blinks the cup, and
        // back to Vulnerable when the window clears. Never stomps a non-Vulnerable/Invulnerable
        // state (e.g. death), so we don't interfere with states this wave doesn't own.
        private static void ForceInvulnerable(global::PlayerDamageReceiver dr, bool invuln)
        {
            if (dr == null) return;
            var st = dr.state;
            if (invuln && st != global::PlayerDamageReceiver.State.Invulnerable)
                Traverse.Create(dr).Property("state").SetValue(global::PlayerDamageReceiver.State.Invulnerable);
            else if (!invuln && st == global::PlayerDamageReceiver.State.Invulnerable)
                Traverse.Create(dr).Property("state").SetValue(global::PlayerDamageReceiver.State.Vulnerable);
        }

        private static void SynthesizeEdges(bool isP1, bool grounded,
                                            global::LevelPlayerAnimationController animCtrl,
                                            global::LevelPlayerController ctrl)
        {
            if (animCtrl == null) return;
            if (ConsumePulse(isP1, CoopState.PulseWeaponFired))
                animCtrl.OnShotFired();
            if (ConsumePulse(isP1, CoopState.PulseDamageTaken))
            {
                var anim = ctrl.GetComponentInChildren<Animator>();
                if (anim != null) anim.Play(grounded ? "Hit.Hit_Ground" : "Hit.Hit_Air", 0);
                Traverse.Create(animCtrl).Field("hitAnimation").SetValue(true);
            }
        }

        private static void SynthesizeArcadeEdges(bool isP1, bool grounded,
                                                  global::ArcadePlayerAnimationController animCtrl,
                                                  global::ArcadePlayerController ctrl)
        {
            if (animCtrl == null) return;
            if (ConsumePulse(isP1, CoopState.PulseWeaponFired))
                animCtrl.OnShotFired();
            if (ConsumePulse(isP1, CoopState.PulseDamageTaken))
            {
                var anim = ctrl.GetComponentInChildren<Animator>();
                if (anim != null) anim.Play(grounded ? "Hit.Hit_Ground" : "Hit.Hit_Air", 0);
                Traverse.Create(animCtrl).Field("hitAnimation").SetValue(true);
            }
        }

        private static bool ConsumePulse(bool isP1, byte bit)
        {
            return isP1 ? CoopState.ConsumeP1Pulse(bit) : CoopState.ConsumeP2Pulse(bit);
        }
    }
}
