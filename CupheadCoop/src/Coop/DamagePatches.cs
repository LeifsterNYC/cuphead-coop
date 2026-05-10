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
    /// is <see cref="CoopMode.Client"/>. Authoritative HP/death will be streamed from the host
    /// in M8 once the wire format carries it; until then the client's HP UI will show stale data
    /// (always full) but at least the cup won't die spuriously.
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
}
