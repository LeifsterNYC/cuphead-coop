using HarmonyLib;
using Rewired;

namespace CupheadCoop.Coop
{
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
            harmony.PatchAll(typeof(RewiredPatches));
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
