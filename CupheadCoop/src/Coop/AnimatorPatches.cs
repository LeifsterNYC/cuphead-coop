using HarmonyLib;
using UnityEngine;

namespace CupheadCoop.Coop
{
    /// <summary>
    /// On the client, Cuphead's local sim sets Animator parameters every Update (Speed,
    /// IsGrounded, attack-flags, etc). Those parameters drive state-machine transitions.
    /// Result: even though we force-Play the host's animator hash in LateUpdate, the
    /// local-sim parameters re-transition the state machine on the next animation update,
    /// and we re-force the hash on the frame after that. Visible as a 2-frame flicker
    /// between the host-state and whatever the local state-machine wants to be in.
    ///
    /// Fix: short-circuit ALL Animator setter methods when <c>Mode == Client</c>. Then no
    /// parameter-driven transitions fire. The only thing that ever changes an Animator's
    /// state is our explicit <c>Animator.Play()</c> call from snapshot apply paths.
    ///
    /// Risk: client-side UI animators might use parameters too (menu transitions, etc) and
    /// will break. Acceptable trade — client is meant to be a render-only mirror of host.
    /// If something specific breaks badly, can be reverted via <c>[Sync] EnablePlayerSync</c>
    /// (the kill switch covers this layer too since the symptom is animation-related).
    /// </summary>
    internal static class AnimatorParamPatches
    {
        private static bool ShouldBlock() => CoopState.Mode == CoopMode.Client;

        // ----- SetFloat overloads -----
        [HarmonyPatch(typeof(Animator), nameof(Animator.SetFloat), new[] { typeof(string), typeof(float) })]
        [HarmonyPrefix]
        private static bool SetFloat_StringFloat() => !ShouldBlock();

        [HarmonyPatch(typeof(Animator), nameof(Animator.SetFloat), new[] { typeof(int), typeof(float) })]
        [HarmonyPrefix]
        private static bool SetFloat_IntFloat() => !ShouldBlock();

        [HarmonyPatch(typeof(Animator), nameof(Animator.SetFloat), new[] { typeof(string), typeof(float), typeof(float), typeof(float) })]
        [HarmonyPrefix]
        private static bool SetFloat_StringFloatDamp() => !ShouldBlock();

        [HarmonyPatch(typeof(Animator), nameof(Animator.SetFloat), new[] { typeof(int), typeof(float), typeof(float), typeof(float) })]
        [HarmonyPrefix]
        private static bool SetFloat_IntFloatDamp() => !ShouldBlock();

        // ----- SetInteger overloads -----
        [HarmonyPatch(typeof(Animator), nameof(Animator.SetInteger), new[] { typeof(string), typeof(int) })]
        [HarmonyPrefix]
        private static bool SetInteger_StringInt() => !ShouldBlock();

        [HarmonyPatch(typeof(Animator), nameof(Animator.SetInteger), new[] { typeof(int), typeof(int) })]
        [HarmonyPrefix]
        private static bool SetInteger_IntInt() => !ShouldBlock();

        // ----- SetBool overloads -----
        [HarmonyPatch(typeof(Animator), nameof(Animator.SetBool), new[] { typeof(string), typeof(bool) })]
        [HarmonyPrefix]
        private static bool SetBool_StringBool() => !ShouldBlock();

        [HarmonyPatch(typeof(Animator), nameof(Animator.SetBool), new[] { typeof(int), typeof(bool) })]
        [HarmonyPrefix]
        private static bool SetBool_IntBool() => !ShouldBlock();

        // ----- SetTrigger overloads -----
        [HarmonyPatch(typeof(Animator), nameof(Animator.SetTrigger), new[] { typeof(string) })]
        [HarmonyPrefix]
        private static bool SetTrigger_String() => !ShouldBlock();

        [HarmonyPatch(typeof(Animator), nameof(Animator.SetTrigger), new[] { typeof(int) })]
        [HarmonyPrefix]
        private static bool SetTrigger_Int() => !ShouldBlock();

        // ----- ResetTrigger overloads -----
        [HarmonyPatch(typeof(Animator), nameof(Animator.ResetTrigger), new[] { typeof(string) })]
        [HarmonyPrefix]
        private static bool ResetTrigger_String() => !ShouldBlock();

        [HarmonyPatch(typeof(Animator), nameof(Animator.ResetTrigger), new[] { typeof(int) })]
        [HarmonyPrefix]
        private static bool ResetTrigger_Int() => !ShouldBlock();
    }
}
