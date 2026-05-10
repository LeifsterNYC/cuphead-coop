using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace CupheadCoop.Coop
{
    /// <summary>
    /// Targeted Animator parameter-setter block. v0.7.6 blanket-blocked ALL Animator setters
    /// when Mode == Client to fix the player-cup run/duck flicker. That over-shot — bosses
    /// also stopped transitioning between attack states because their parameter-driven state
    /// machines couldn't progress, which is why projectiles stopped appearing.
    ///
    /// v0.7.7: maintain a registry of Animator instances we know belong to puppet'd player
    /// cups (registered by ScenePuppetry when it locates them). Setters are blocked ONLY for
    /// those. Boss / enemy / projectile / UI animators run their state machines normally so
    /// gameplay continues to evolve locally — only the position+animator-state of player cups
    /// is forced from snapshots.
    /// </summary>
    internal static class AnimatorParamPatches
    {
        private static readonly HashSet<int> _suppressedAnimatorIds = new HashSet<int>();

        /// <summary>Mark this animator as one whose parameter-setters should be no-op'd
        /// while in client mode. Idempotent.</summary>
        public static void RegisterSuppressed(Animator a)
        {
            if (a != null) _suppressedAnimatorIds.Add(a.GetInstanceID());
        }

        public static void ClearSuppressed() => _suppressedAnimatorIds.Clear();

        private static bool ShouldBlock(Animator a)
        {
            if (CoopState.Mode != CoopMode.Client) return false;
            if (a == null) return false;
            return _suppressedAnimatorIds.Contains(a.GetInstanceID());
        }

        // ----- SetFloat -----
        [HarmonyPatch(typeof(Animator), nameof(Animator.SetFloat), new[] { typeof(string), typeof(float) })]
        [HarmonyPrefix]
        private static bool SetFloat_StringFloat(Animator __instance) => !ShouldBlock(__instance);

        [HarmonyPatch(typeof(Animator), nameof(Animator.SetFloat), new[] { typeof(int), typeof(float) })]
        [HarmonyPrefix]
        private static bool SetFloat_IntFloat(Animator __instance) => !ShouldBlock(__instance);

        [HarmonyPatch(typeof(Animator), nameof(Animator.SetFloat), new[] { typeof(string), typeof(float), typeof(float), typeof(float) })]
        [HarmonyPrefix]
        private static bool SetFloat_StringFloatDamp(Animator __instance) => !ShouldBlock(__instance);

        [HarmonyPatch(typeof(Animator), nameof(Animator.SetFloat), new[] { typeof(int), typeof(float), typeof(float), typeof(float) })]
        [HarmonyPrefix]
        private static bool SetFloat_IntFloatDamp(Animator __instance) => !ShouldBlock(__instance);

        // ----- SetInteger -----
        [HarmonyPatch(typeof(Animator), nameof(Animator.SetInteger), new[] { typeof(string), typeof(int) })]
        [HarmonyPrefix]
        private static bool SetInteger_StringInt(Animator __instance) => !ShouldBlock(__instance);

        [HarmonyPatch(typeof(Animator), nameof(Animator.SetInteger), new[] { typeof(int), typeof(int) })]
        [HarmonyPrefix]
        private static bool SetInteger_IntInt(Animator __instance) => !ShouldBlock(__instance);

        // ----- SetBool -----
        [HarmonyPatch(typeof(Animator), nameof(Animator.SetBool), new[] { typeof(string), typeof(bool) })]
        [HarmonyPrefix]
        private static bool SetBool_StringBool(Animator __instance) => !ShouldBlock(__instance);

        [HarmonyPatch(typeof(Animator), nameof(Animator.SetBool), new[] { typeof(int), typeof(bool) })]
        [HarmonyPrefix]
        private static bool SetBool_IntBool(Animator __instance) => !ShouldBlock(__instance);

        // ----- SetTrigger -----
        [HarmonyPatch(typeof(Animator), nameof(Animator.SetTrigger), new[] { typeof(string) })]
        [HarmonyPrefix]
        private static bool SetTrigger_String(Animator __instance) => !ShouldBlock(__instance);

        [HarmonyPatch(typeof(Animator), nameof(Animator.SetTrigger), new[] { typeof(int) })]
        [HarmonyPrefix]
        private static bool SetTrigger_Int(Animator __instance) => !ShouldBlock(__instance);

        // ----- ResetTrigger -----
        [HarmonyPatch(typeof(Animator), nameof(Animator.ResetTrigger), new[] { typeof(string) })]
        [HarmonyPrefix]
        private static bool ResetTrigger_String(Animator __instance) => !ShouldBlock(__instance);

        [HarmonyPatch(typeof(Animator), nameof(Animator.ResetTrigger), new[] { typeof(int) })]
        [HarmonyPrefix]
        private static bool ResetTrigger_Int(Animator __instance) => !ShouldBlock(__instance);
    }
}
