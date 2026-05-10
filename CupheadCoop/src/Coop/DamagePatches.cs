using HarmonyLib;

namespace CupheadCoop.Coop
{
    /// <summary>
    /// M9: when running as a Client, the host owns gameplay simulation and damage state. The
    /// client's local Cuphead still simulates collisions/projectiles independently, so without
    /// this patch the client's cup can take damage and die from local-sim hits that never
    /// occurred on the host. Result: the two PCs diverge, the client sees a "Game Over" screen
    /// while the host plays on.
    ///
    /// Fix: short-circuit <c>PlayerDamageReceiver.TakeDamage</c> when <see cref="CoopState.Mode"/>
    /// is <see cref="CoopMode.Client"/>. M8 streams the host's authoritative HP back so the
    /// client's HUD reflects what the host sees.
    ///
    /// This patch is universal across players — both P1 and P2 are suppressed on the client.
    /// On the host, the prefix returns true (continue) and damage processes normally for both
    /// local controllers.
    /// </summary>
    [HarmonyPatch(typeof(PlayerDamageReceiver), nameof(PlayerDamageReceiver.TakeDamage))]
    internal static class PlayerDamageReceiver_TakeDamage_Patch
    {
        [HarmonyPrefix]
        private static bool Prefix()
        {
            return CoopState.Mode != CoopMode.Client;
        }
    }

    /// <summary>
    /// Pause-input suppression on the client. Without this, the client pressing Pause locally
    /// would freeze their game; the next host snapshot says "not paused" and our PauseSync
    /// applies Unpause, producing a single-frame flicker. Cleaner: just block client-initiated
    /// pause requests and let host be the only authority.
    ///
    /// We allow PauseManager.Pause to run when <see cref="PauseSync.RemoteDriven"/> is true —
    /// that's the path our own ApplyFromHost takes when echoing the host's pause state.
    /// </summary>
    [HarmonyPatch(typeof(PauseManager), nameof(PauseManager.Pause))]
    internal static class PauseManager_Pause_Patch
    {
        [HarmonyPrefix]
        private static bool Prefix()
        {
            if (CoopState.Mode != CoopMode.Client) return true;
            return PauseSync.RemoteDriven;
        }
    }

    [HarmonyPatch(typeof(PauseManager), nameof(PauseManager.Unpause))]
    internal static class PauseManager_Unpause_Patch
    {
        [HarmonyPrefix]
        private static bool Prefix()
        {
            if (CoopState.Mode != CoopMode.Client) return true;
            return PauseSync.RemoteDriven;
        }
    }
}
