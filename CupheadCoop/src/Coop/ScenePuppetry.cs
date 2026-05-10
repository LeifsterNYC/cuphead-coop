using BepInEx.Logging;
using UnityEngine;

namespace CupheadCoop.Coop
{
    /// <summary>
    /// M4 visual sync. Two responsibilities:
    /// 1. <see cref="HostCapture"/>: when Mode == Host, sample local P1 and P2 transforms each
    ///    frame so <see cref="Net.CoopHost"/> can ship them as <c>StateSnapshot</c> packets.
    /// 2. <see cref="ClientApply"/>: when Mode == Client and we've received at least one snapshot,
    ///    override the local Cuphead transforms with the host's positions in LateUpdate. The
    ///    client's own Update still runs the simulation (we can't fully suppress it without
    ///    breaking spawn/death/animation logic), but our LateUpdate write happens last so the
    ///    rendered position matches the host. This is the cheap "render-only spectator" path
    ///    discussed in tasks/todo.md M4.
    /// </summary>
    internal static class ScenePuppetry
    {
        public static ManualLogSource Log;

        // Cached samples for the host to ship out. Populated in HostCapture, read by CoopHost.Pump.
        public static bool LocalP1Present;
        public static float LocalP1X;
        public static float LocalP1Y;
        public static sbyte LocalP1Facing;
        public static int LocalP1AnimHash;
        public static float LocalP1AnimTime;
        public static bool LocalP2Present;
        public static float LocalP2X;
        public static float LocalP2Y;
        public static sbyte LocalP2Facing;
        public static int LocalP2AnimHash;
        public static float LocalP2AnimTime;

        // Throttled diagnostic — log "why is P1/P2 not present" once every ~2s while the
        // host has a connected peer, so testers can tell the difference between "wrong scene"
        // and "transform-find broken".
        private static int _diagFrame;
        private static string _lastDiag;

        /// <summary>
        /// Read live P1/P2 positions on the host. Called from <c>Plugin.LateUpdate</c> just before
        /// the network pump fires, so what we send is the simulated end-of-frame state.
        /// </summary>
        public static void HostCapture()
        {
            string p1Why;
            var p1 = SafeGetPlayerSnapshot(global::PlayerId.PlayerOne, out var f1, out var ah1, out var at1, out p1Why);
            if (p1.HasValue)
            {
                LocalP1Present = true;
                LocalP1X = p1.Value.x;
                LocalP1Y = p1.Value.y;
                LocalP1Facing = f1;
                LocalP1AnimHash = ah1;
                LocalP1AnimTime = at1;
            }
            else
            {
                LocalP1Present = false;
            }

            string p2Why;
            var p2 = SafeGetPlayerSnapshot(global::PlayerId.PlayerTwo, out var f2, out var ah2, out var at2, out p2Why);
            if (p2.HasValue)
            {
                LocalP2Present = true;
                LocalP2X = p2.Value.x;
                LocalP2Y = p2.Value.y;
                LocalP2Facing = f2;
                LocalP2AnimHash = ah2;
                LocalP2AnimTime = at2;
            }
            else
            {
                LocalP2Present = false;
            }

            // Diagnostic: while connected, print why one or both players aren't sampled.
            // Throttled to once every ~2s and de-duped so we don't flood the log.
            if (++_diagFrame >= 120)
            {
                _diagFrame = 0;
                if (!LocalP1Present || !LocalP2Present)
                {
                    string diag = "P1: " + (LocalP1Present ? "ok" : p1Why) +
                                  ", P2: " + (LocalP2Present ? "ok" : p2Why);
                    if (diag != _lastDiag)
                    {
                        _lastDiag = diag;
                        Log?.LogInfo("ScenePuppetry capture: " + diag);
                    }
                }
                else if (_lastDiag != "ok")
                {
                    _lastDiag = "ok";
                    Log?.LogInfo("ScenePuppetry capture: both players sampled @ P1=(" +
                                 LocalP1X.ToString("F1") + "," + LocalP1Y.ToString("F1") + ") P2=(" +
                                 LocalP2X.ToString("F1") + "," + LocalP2Y.ToString("F1") + ")");
                }
            }
        }

        /// <summary>
        /// On the client: rewrite local Cuphead transforms to match the host's last-received
        /// snapshot. Runs in LateUpdate so we win against the local simulation's Update writes.
        /// No interpolation in this first cut — at 30 Hz snapshots over LAN you'll see ~33ms of
        /// stepping but it's enough to validate the architecture before we add lerp + dead reckoning.
        /// </summary>
        public static void ClientApply()
        {
            if (CoopState.RemoteStateSequence == 0) return; // never received anything yet

            if (CoopState.RemoteP1Present)
                ApplyTo(global::PlayerId.PlayerOne, CoopState.RemoteP1X, CoopState.RemoteP1Y,
                        CoopState.RemoteP1Facing, CoopState.RemoteP1AnimHash, CoopState.RemoteP1AnimTime);
            if (CoopState.RemoteP2Present)
                ApplyTo(global::PlayerId.PlayerTwo, CoopState.RemoteP2X, CoopState.RemoteP2Y,
                        CoopState.RemoteP2Facing, CoopState.RemoteP2AnimHash, CoopState.RemoteP2AnimTime);
        }

        private static Vector2? SafeGetPlayerSnapshot(global::PlayerId id, out sbyte facing,
                                                      out int animHash, out float animTime, out string why)
        {
            facing = 0;
            animHash = 0;
            animTime = 0f;
            why = null;
            try
            {
                if (!global::PlayerManager.DoesPlayerExist(id)) { why = "DoesPlayerExist=false"; return null; }
                var ctrl = global::PlayerManager.GetPlayer(id);
                if (ctrl == null) { why = "GetPlayer=null"; return null; }
                if (ctrl.transform == null) { why = "transform=null"; return null; }
                var pos = ctrl.transform.position;
                float sx = ctrl.transform.localScale.x;
                facing = sx > 0.01f ? (sbyte)1 : sx < -0.01f ? (sbyte)-1 : (sbyte)0;

                // Animator may live on the controller itself or on a child. Search up to 2 deep.
                var animator = ctrl.GetComponentInChildren<Animator>();
                if (animator != null && animator.isActiveAndEnabled && animator.runtimeAnimatorController != null)
                {
                    var st = animator.GetCurrentAnimatorStateInfo(0);
                    animHash = st.fullPathHash;
                    // normalizedTime can grow unboundedly while a state loops; mod into [0,1).
                    float t = st.normalizedTime;
                    animTime = t - Mathf.Floor(t);
                }
                return new Vector2(pos.x, pos.y);
            }
            catch (System.Exception ex)
            {
                why = "ex:" + ex.GetType().Name;
                return null;
            }
        }

        private static void ApplyTo(global::PlayerId id, float x, float y, sbyte facing, int animHash, float animTime)
        {
            try
            {
                if (!global::PlayerManager.DoesPlayerExist(id)) return;
                var ctrl = global::PlayerManager.GetPlayer(id);
                if (ctrl == null || ctrl.transform == null) return;
                var t = ctrl.transform;
                var p = t.position;
                p.x = x;
                p.y = y;
                t.position = p;
                if (facing != 0)
                {
                    var s = t.localScale;
                    float magnitude = Mathf.Abs(s.x);
                    if (magnitude < 0.001f) magnitude = 1f;
                    s.x = facing * magnitude;
                    t.localScale = s;
                }

                // Animator sync: only act when the host actually captured a hash. Otherwise
                // we'd repeatedly Play(0) and freeze the cup on its idle frame.
                if (animHash != 0)
                {
                    var animator = ctrl.GetComponentInChildren<Animator>();
                    if (animator != null && animator.isActiveAndEnabled && animator.runtimeAnimatorController != null)
                    {
                        var current = animator.GetCurrentAnimatorStateInfo(0);
                        // Only Play() when the state actually differs — calling Play every frame
                        // resets normalizedTime each call and produces a stuttering animation.
                        if (current.fullPathHash != animHash)
                        {
                            animator.Play(animHash, 0, animTime);
                        }
                        // If we're already in the target state, optionally re-sync time to keep
                        // host and client phase-aligned. Threshold avoids constant tiny corrections.
                        else if (Mathf.Abs((current.normalizedTime - Mathf.Floor(current.normalizedTime)) - animTime) > 0.15f)
                        {
                            animator.Play(animHash, 0, animTime);
                        }
                    }
                }
            }
            catch
            {
                // Same logic as capture: swallow during transitions.
            }
        }
    }
}
