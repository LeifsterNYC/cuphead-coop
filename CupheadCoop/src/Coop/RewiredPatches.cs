using HarmonyLib;
using Rewired;
using UnityEngine;

namespace CupheadCoop.Coop
{
    /// <summary>
    /// Two stacked input gates on Rewired.Player.GetButton/GetButtonDown/GetButtonUp/GetAxis:
    ///
    /// 1. <b>Focus gate</b> — when this Cuphead window doesn't have OS focus, return zero. Stops
    ///    cross-window input bleed when running two Cuphead instances on a single PC for solo
    ///    testing. Harmless on multi-PC setups.
    ///
    /// 2. <b>Client gate</b> — when <see cref="CoopMode.Client"/> is active, return zero so the
    ///    local Cuphead simulation doesn't act on keyboard input. Without this, the client's
    ///    local cup runs/ducks/jumps based on local input AND those animation states fight with
    ///    the host-streamed transforms+animator overrides every frame, producing the run↔duck
    ///    flicker the tester saw. With it, the local cup is a pure renderer driven entirely by
    ///    snapshots.
    ///
    /// The gates skip their effect when <see cref="CoopState.IsCapturingLocalInput"/> is true —
    /// that's set inside <c>Plugin.CaptureLocalInputForUpload</c> so our own input read can
    /// still see real keypresses for shipping to the host.
    /// </summary>
    [HarmonyPatch]
    internal static class RewiredFocusGate
    {
        private static bool ShouldSuppress()
        {
            if (CoopState.IsCapturingLocalInput) return false;
            if (ModConfig.FocusGateInput.Value && !Application.isFocused) return true;
            if (CoopState.Mode == CoopMode.Client) return true;
            return false;
        }

        [HarmonyPatch(typeof(Player), nameof(Player.GetButton), new[] { typeof(int) })]
        [HarmonyPrefix]
        private static bool GetButton_Prefix(ref bool __result)
        {
            if (!ShouldSuppress()) return true;
            __result = false; return false;
        }

        [HarmonyPatch(typeof(Player), nameof(Player.GetButtonDown), new[] { typeof(int) })]
        [HarmonyPrefix]
        private static bool GetButtonDown_Prefix(ref bool __result)
        {
            if (!ShouldSuppress()) return true;
            __result = false; return false;
        }

        [HarmonyPatch(typeof(Player), nameof(Player.GetButtonUp), new[] { typeof(int) })]
        [HarmonyPrefix]
        private static bool GetButtonUp_Prefix(ref bool __result)
        {
            if (!ShouldSuppress()) return true;
            __result = false; return false;
        }

        [HarmonyPatch(typeof(Player), nameof(Player.GetAxis), new[] { typeof(int) })]
        [HarmonyPrefix]
        private static bool GetAxis_Prefix(ref float __result)
        {
            if (!ShouldSuppress()) return true;
            __result = 0f; return false;
        }
    }

    /// <summary>
    /// Patches Rewired.Player.GetButton/GetButtonDown/GetButtonUp/GetAxis so that, when the
    /// instance corresponds to Cuphead's Player 2 AND we have a remote input source active,
    /// the value comes from the network frame rather than the local input device.
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
