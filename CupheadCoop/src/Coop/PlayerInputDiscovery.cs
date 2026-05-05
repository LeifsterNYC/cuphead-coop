using BepInEx.Logging;
using HarmonyLib;
using Rewired;

namespace CupheadCoop.Coop
{
    /// <summary>
    /// Captures the Rewired.Player ids the moment Cuphead's PlayerInput.Init runs, instead of
    /// polling PlayerManager.GetPlayerInput from Update(). PlayerInput.Init is the canonical
    /// hand-off point: <c>actions = PlayerManager.GetPlayerInput(playerId)</c>.
    /// </summary>
    [HarmonyPatch(typeof(PlayerInput), nameof(PlayerInput.Init))]
    internal static class PlayerInputInit_Patch
    {
        public static ManualLogSource Log;

        [HarmonyPostfix]
        private static void Postfix(PlayerInput __instance, PlayerId playerId)
        {
            if (__instance == null || __instance.actions == null) return;

            int rewiredId = __instance.actions.id;
            if (playerId == PlayerId.PlayerTwo)
            {
                if (CoopState.RewiredPlayer2Id != rewiredId)
                {
                    CoopState.RewiredPlayer2Id = rewiredId;
                    CoopState.LocalPlayer2 = __instance.actions;
                    Log?.LogInfo("PlayerInput.Init: P2 -> Rewired id " + rewiredId);
                }
            }
            else if (playerId == PlayerId.PlayerOne)
            {
                if (CoopState.RewiredPlayer1Id != rewiredId)
                {
                    CoopState.RewiredPlayer1Id = rewiredId;
                    CoopState.LocalPlayer1 = __instance.actions;
                    Log?.LogInfo("PlayerInput.Init: P1 -> Rewired id " + rewiredId);
                }
            }
        }
    }
}
