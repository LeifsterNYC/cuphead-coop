using HarmonyLib;
using Rewired;
using UnityEngine;

namespace CupheadCoop.Coop
{
    /// <summary>
    /// Three-layer Rewired input interception. Order matters — gates run as Prefix, override
    /// runs as Postfix.
    ///
    /// 1. <b>Focus gate (Prefix, returns 0 to skip original)</b> — when the Cuphead window
    ///    doesn't have OS focus and FocusGateInput is on, suppress all input. Solo
    ///    two-instance testing on a single PC: stops cross-window input bleed.
    ///
    /// 2. <b>Client mirror gate (Prefix, fills mirrored result and skips original)</b> — when
    ///    Mode == Client, return the host-streamed input mirror (PlayerSnapshot.Buttons /
    ///    Axes) for both Player 1 and Player 2. Without mirroring, client's local sim has
    ///    zero input → P1 never fires weapons → no local AbstractProjectile spawns to bind
    ///    against host's NetworkID stream → projectiles invisible on client. With it, both
    ///    sides run the simulation with the same inputs and produce nearly identical state.
    ///
    /// 3. <b>Host P2 override (Postfix)</b> — on host, when reading P2's Rewired, substitute
    ///    the network-received input from CoopState. Has been there since M3.
    ///
    /// All three gates skip their effect when <see cref="CoopState.IsCapturingLocalInput"/>
    /// is set — that flag is held while <c>Plugin.CaptureLocalInputForUpload</c> and
    /// <c>ScenePuppetry.CaptureInputs</c> read raw local Rewired so we don't recursively
    /// substitute mirrored values back into the upload stream.
    /// </summary>
    [HarmonyPatch]
    internal static class RewiredFocusGate
    {
        // Returns true if focus-gate should suppress (return zero) for any reason that's
        // not "we have a mirrored value to substitute". Mirror substitution is handled
        // separately so the result can carry actual data.
        private static bool ShouldSuppressForFocus()
        {
            if (CoopState.IsCapturingLocalInput) return false;
            if (ModConfig.FocusGateInput.Value && !Application.isFocused) return true;
            return false;
        }

        // Returns 1 if we should fill from P1 mirror, 2 if from P2 mirror, 0 if no mirror
        // applies (Off mode, host mode, or the player id doesn't match a known slot).
        // Skips when IsCapturingLocalInput is set so capture paths read raw local Rewired.
        private static int ClientMirrorSlot(Player p)
        {
            if (CoopState.IsCapturingLocalInput) return 0;
            if (CoopState.Mode != CoopMode.Client) return 0;
            if (p == null) return 0;
            int p1Id = CoopState.RewiredPlayer1Id;
            int p2Id = CoopState.RewiredPlayer2Id;
            if (p1Id >= 0 && p.id == p1Id) return 1;
            if (p2Id >= 0 && p.id == p2Id) return 2;
            return 0;
        }

        [HarmonyPatch(typeof(Player), nameof(Player.GetButton), new[] { typeof(int) })]
        [HarmonyPrefix]
        private static bool GetButton_Prefix(Player __instance, int actionId, ref bool __result)
        {
            if (ShouldSuppressForFocus()) { __result = false; return false; }
            int slot = ClientMirrorSlot(__instance);
            if (slot == 1) { __result = CoopState.IsClientP1ButtonHeld(actionId); return false; }
            if (slot == 2) { __result = CoopState.IsClientP2ButtonHeld(actionId); return false; }
            return true;
        }

        [HarmonyPatch(typeof(Player), nameof(Player.GetButtonDown), new[] { typeof(int) })]
        [HarmonyPrefix]
        private static bool GetButtonDown_Prefix(Player __instance, int actionId, ref bool __result)
        {
            if (ShouldSuppressForFocus()) { __result = false; return false; }
            int slot = ClientMirrorSlot(__instance);
            if (slot == 1) { __result = CoopState.IsClientP1ButtonDown(actionId); return false; }
            if (slot == 2) { __result = CoopState.IsClientP2ButtonDown(actionId); return false; }
            return true;
        }

        [HarmonyPatch(typeof(Player), nameof(Player.GetButtonUp), new[] { typeof(int) })]
        [HarmonyPrefix]
        private static bool GetButtonUp_Prefix(Player __instance, int actionId, ref bool __result)
        {
            if (ShouldSuppressForFocus()) { __result = false; return false; }
            int slot = ClientMirrorSlot(__instance);
            if (slot == 1) { __result = CoopState.IsClientP1ButtonUp(actionId); return false; }
            if (slot == 2) { __result = CoopState.IsClientP2ButtonUp(actionId); return false; }
            return true;
        }

        [HarmonyPatch(typeof(Player), nameof(Player.GetAxis), new[] { typeof(int) })]
        [HarmonyPrefix]
        private static bool GetAxis_Prefix(Player __instance, int actionId, ref float __result)
        {
            if (ShouldSuppressForFocus()) { __result = 0f; return false; }
            int slot = ClientMirrorSlot(__instance);
            if (slot == 1)
            {
                if (actionId == 0) __result = CoopState.MirroredP1AxisX;
                else if (actionId == 1) __result = CoopState.MirroredP1AxisY;
                else __result = 0f;
                return false;
            }
            if (slot == 2)
            {
                if (actionId == 0) __result = CoopState.MirroredP2AxisX;
                else if (actionId == 1) __result = CoopState.MirroredP2AxisY;
                else __result = 0f;
                return false;
            }
            return true;
        }
    }

    /// <summary>
    /// Host-side Player 2 override. When host reads Player 2's Rewired input, substitute
    /// the network-received frame (CoopState.CurrentButtons / Axes) so host's local P2
    /// simulation is driven by the client's keyboard.
    ///
    /// Implemented as Postfix (rather than Prefix-replace) so it stacks cleanly with
    /// RewiredFocusGate's Prefix. On host, FocusGate doesn't suppress (Mode != Client)
    /// and ClientMirrorSlot returns 0 (Mode != Client), so the original GetButton runs,
    /// then this Postfix overrides for P2 only.
    ///
    /// We only patch the integer-id overloads because Cuphead exclusively passes int ids
    /// (the CupheadButton enum). String overloads aren't on the hot path for gameplay.
    /// </summary>
    internal static class RewiredPatches
    {
        public static void Apply(Harmony harmony)
        {
            harmony.PatchAll(typeof(RewiredFocusGate));
            harmony.PatchAll(typeof(RewiredPatches));
            harmony.PatchAll(typeof(PlayerInputInit_Patch));
            harmony.PatchAll(typeof(NatPunchModule_SkipCtor_Patch));
            harmony.PatchAll(typeof(PlayerDamageReceiver_TakeDamage_Patch));
            harmony.PatchAll(typeof(PauseManager_Pause_Patch));
            harmony.PatchAll(typeof(PauseManager_Unpause_Patch));
            harmony.PatchAll(typeof(AnimatorParamPatches));
            harmony.PatchAll(typeof(PlayerAnimControllerUpdateBlock));
            harmony.PatchAll(typeof(ProjectileLifecyclePatches));
            harmony.PatchAll(typeof(PlayerMotorBypass));
        }

        private static bool ShouldOverride(Player __instance)
        {
            if (CupheadCoop.Coop.CoopState.Mode == CoopMode.Off &&
                !ModConfig.DebugForceP2WalkRight.Value) return false;

            if (__instance == null) return false;
            int p2Id = CupheadCoop.Coop.CoopState.RewiredPlayer2Id;
            if (p2Id < 0) return false;
            return __instance.id == p2Id;
        }

        [HarmonyPatch(typeof(Player), nameof(Player.GetButton), new[] { typeof(int) })]
        [HarmonyPostfix]
        private static void GetButton_Postfix(Player __instance, int actionId, ref bool __result)
        {
            if (!ShouldOverride(__instance)) return;

            if (CupheadCoop.Coop.CoopState.Mode == CoopMode.Host &&
                CupheadCoop.Coop.CoopState.HasRemoteInput)
            {
                __result = CupheadCoop.Coop.CoopState.IsButtonHeld(actionId);
                return;
            }

            // Debug fallback: pretend MoveHorizontal axis is positive — handled by GetAxis,
            // but for buttons we leave the default unless explicitly tested. No-op.
        }

        [HarmonyPatch(typeof(Player), nameof(Player.GetButtonDown), new[] { typeof(int) })]
        [HarmonyPostfix]
        private static void GetButtonDown_Postfix(Player __instance, int actionId, ref bool __result)
        {
            if (!ShouldOverride(__instance)) return;
            if (CupheadCoop.Coop.CoopState.Mode == CoopMode.Host &&
                CupheadCoop.Coop.CoopState.HasRemoteInput)
            {
                __result = CupheadCoop.Coop.CoopState.IsButtonDown(actionId);
            }
        }

        [HarmonyPatch(typeof(Player), nameof(Player.GetButtonUp), new[] { typeof(int) })]
        [HarmonyPostfix]
        private static void GetButtonUp_Postfix(Player __instance, int actionId, ref bool __result)
        {
            if (!ShouldOverride(__instance)) return;
            if (CupheadCoop.Coop.CoopState.Mode == CoopMode.Host &&
                CupheadCoop.Coop.CoopState.HasRemoteInput)
            {
                __result = CupheadCoop.Coop.CoopState.IsButtonUp(actionId);
            }
        }

        [HarmonyPatch(typeof(Player), nameof(Player.GetAxis), new[] { typeof(int) })]
        [HarmonyPostfix]
        private static void GetAxis_Postfix(Player __instance, int actionId, ref float __result)
        {
            if (!ShouldOverride(__instance)) return;

            // Cuphead uses actionId 0 = MoveHorizontal, 1 = MoveVertical.
            // (Confirmed via CupheadButton enum: MoveHorizontal=0, MoveVertical=1.)
            if (CupheadCoop.Coop.CoopState.Mode == CoopMode.Host &&
                CupheadCoop.Coop.CoopState.HasRemoteInput)
            {
                if (actionId == 0) __result = CupheadCoop.Coop.CoopState.AxisX;
                else if (actionId == 1) __result = CupheadCoop.Coop.CoopState.AxisY;
                return;
            }

            // Off-mode debug: force walk right when the flag is on. Helps verify the patch is live
            // even before any networking is active.
            if (ModConfig.DebugForceP2WalkRight.Value && actionId == 0)
            {
                __result = 1f;
            }
        }
    }
}
