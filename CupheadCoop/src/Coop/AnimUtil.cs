using UnityEngine;

namespace CupheadCoop.Coop
{
    /// <summary>
    /// v1.1.0 animator scrubbing helpers, shared by every client-side applier
    /// (ScenePuppetry / EntitySync / ProjectileSync).
    ///
    /// WHY this exists: the pre-1.1.0 appliers each had their own "if hash differs OR drift &gt;
    /// 0.15 then Animator.Play" block. That drift-resync had two problems: (1) it let the client
    /// free-run the animation between resyncs, so a looping state that wrapped past 1.0 on the
    /// client but not yet on the host produced a large spurious "drift" and a visible snap; and
    /// (2) the 0.15 tolerance meant the client's timeline was never actually equal to the host's,
    /// just "close". <see cref="Scrub"/> replaces that with an unconditional per-frame
    /// <c>Animator.Play(hash, 0, time)</c> so the client is an exact timeline puppet of the host —
    /// no free-running, no wrap-around drift. The single exception is a finished non-looping
    /// one-shot, which we must not re-trigger (re-playing at time≈1 restarts some transitions).
    /// </summary>
    internal static class AnimUtil
    {
        /// <summary>
        /// Normalize an animator state's normalizedTime for the wire: looping states wrap into
        /// [0,1); one-shots clamp into [0,1] (so a finished one-shot reports ~1.0, not 3.7).
        /// </summary>
        public static float SampleTime(AnimatorStateInfo st)
        {
            float t = st.normalizedTime;
            return st.loop ? t - Mathf.Floor(t) : Mathf.Clamp01(t);
        }

        /// <summary>
        /// Force the animator onto the host's exact state+time this frame. Called every frame by
        /// the appliers — deliberate: the client is a pure puppet. The one guard: if we're already
        /// on the target non-looping state and it (and the host) have finished (≥0.995), leave it
        /// alone so we don't re-trigger a completed one-shot.
        /// </summary>
        public static void Scrub(Animator a, int hash, float time)
        {
            if (a == null) return;
            var current = a.GetCurrentAnimatorStateInfo(0);
            if (current.fullPathHash == hash && !current.loop
                && time >= 0.995f && current.normalizedTime >= 0.995f)
                return; // finished one-shot — don't restart it
            a.Play(hash, 0, time);
        }
    }
}
