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
        public static sbyte LocalP1Hp;
        public static bool LocalP1IsDead;
        public static bool LocalP2Present;
        public static float LocalP2X;
        public static float LocalP2Y;
        public static sbyte LocalP2Facing;
        public static int LocalP2AnimHash;
        public static float LocalP2AnimTime;
        public static sbyte LocalP2Hp;
        public static bool LocalP2IsDead;

        // v13: per-player motor/weapon flags (bit layout = CoopState.Flag*). Sampled every frame in
        // HostCapture; CoopHost.TickStateSnapshot ships the newest into PlayerSnapshot.Flags.
        public static byte LocalP1Flags;
        public static byte LocalP2Flags;

        // v10: per-player Rewired input snapshot, sampled by HostCapture and shipped in
        // PlayerSnapshot so client's local sim can read what host was reading. P1 = host's
        // local controller/keyboard. P2 = the network input forwarded from client (which
        // host has already applied to CoopState.CurrentButtons via ApplyRemoteFrame, so we
        // re-read it from there rather than re-deriving from P2's Rewired (which on host
        // is overridden by our own postfix anyway).
        public static uint LocalP1Buttons;
        public static sbyte LocalP1AxisX_q;
        public static sbyte LocalP1AxisY_q;
        public static uint LocalP2Buttons;
        public static sbyte LocalP2AxisX_q;
        public static sbyte LocalP2AxisY_q;

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
            CaptureInputs();
            string p1Why;
            var p1 = SafeGetPlayerSnapshot(global::PlayerId.PlayerOne, out var f1, out var ah1, out var at1,
                                           out var hp1, out var d1, out var fl1, out p1Why);
            if (p1.HasValue)
            {
                LocalP1Present = true;
                LocalP1X = p1.Value.x;
                LocalP1Y = p1.Value.y;
                LocalP1Facing = f1;
                LocalP1AnimHash = ah1;
                LocalP1AnimTime = at1;
                LocalP1Hp = hp1;
                LocalP1IsDead = d1;
                LocalP1Flags = fl1;
            }
            else
            {
                LocalP1Present = false;
                LocalP1Flags = 0;
            }

            string p2Why;
            var p2 = SafeGetPlayerSnapshot(global::PlayerId.PlayerTwo, out var f2, out var ah2, out var at2,
                                           out var hp2, out var d2, out var fl2, out p2Why);
            if (p2.HasValue)
            {
                LocalP2Present = true;
                LocalP2X = p2.Value.x;
                LocalP2Y = p2.Value.y;
                LocalP2Facing = f2;
                LocalP2AnimHash = ah2;
                LocalP2AnimTime = at2;
                LocalP2Hp = hp2;
                LocalP2IsDead = d2;
                LocalP2Flags = fl2;
            }
            else
            {
                LocalP2Present = false;
                LocalP2Flags = 0;
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
            if (CoopState.RemoteStateSequence == 0) return;
            if (!ModConfig.EnablePlayerSync.Value) return; // kill switch

            int animHashP1 = ModConfig.EnableAnimationSync.Value ? CoopState.RemoteP1AnimHash : 0;
            int animHashP2 = ModConfig.EnableAnimationSync.Value ? CoopState.RemoteP2AnimHash : 0;
            sbyte hpP1 = ModConfig.EnableHpSync.Value ? CoopState.RemoteP1Hp : (sbyte)-1;
            sbyte hpP2 = ModConfig.EnableHpSync.Value ? CoopState.RemoteP2Hp : (sbyte)-1;

            if (CoopState.RemoteP1Present)
                ApplyTo(global::PlayerId.PlayerOne, CoopState.RemoteP1X, CoopState.RemoteP1Y,
                        CoopState.RemoteP1Facing, animHashP1, CoopState.RemoteP1AnimTime, hpP1);
            if (CoopState.RemoteP2Present)
                ApplyTo(global::PlayerId.PlayerTwo, CoopState.RemoteP2X, CoopState.RemoteP2Y,
                        CoopState.RemoteP2Facing, animHashP2, CoopState.RemoteP2AnimTime, hpP2);
        }

        private static Vector2? SafeGetPlayerSnapshot(global::PlayerId id, out sbyte facing,
                                                      out int animHash, out float animTime,
                                                      out sbyte hp, out bool isDead, out byte flags, out string why)
        {
            facing = 0;
            animHash = 0;
            animTime = 0f;
            hp = -1;
            isDead = false;
            flags = 0;
            why = null;
            try
            {
                // Level case — AbstractPlayerController.
                if (global::PlayerManager.DoesPlayerExist(id))
                {
                    var ctrl = global::PlayerManager.GetPlayer(id);
                    if (ctrl != null && ctrl.transform != null)
                    {
                        var pos = ctrl.transform.position;
                        float sx = ctrl.transform.localScale.x;
                        facing = sx > 0.01f ? (sbyte)1 : sx < -0.01f ? (sbyte)-1 : (sbyte)0;

                        var animator = ctrl.GetComponentInChildren<Animator>();
                        if (animator != null && animator.isActiveAndEnabled && animator.runtimeAnimatorController != null)
                        {
                            var st = animator.GetCurrentAnimatorStateInfo(0);
                            animHash = st.fullPathHash;
                            animTime = AnimUtil.SampleTime(st);
                        }

                        if (ctrl.stats != null)
                        {
                            int h = ctrl.stats.Health;
                            if (h < -128) h = -128;
                            else if (h > 127) h = 127;
                            hp = (sbyte)h;
                        }
                        isDead = ctrl.IsDead;
                        flags = SampleFlags(ctrl);
                        return new Vector2(pos.x, pos.y);
                    }
                }

                // Map case — fall through to MapPlayerController which is a separate hierarchy
                // used on the world-map scenes. Doesn't have HP/IsDead/PlayerStatsManager.
                var mapCtrl = FindMapPlayer(id);
                if (mapCtrl != null && mapCtrl.transform != null)
                {
                    var pos = mapCtrl.transform.position;
                    float sx = mapCtrl.transform.localScale.x;
                    facing = sx > 0.01f ? (sbyte)1 : sx < -0.01f ? (sbyte)-1 : (sbyte)0;

                    var animator = mapCtrl.GetComponentInChildren<Animator>();
                    if (animator != null && animator.isActiveAndEnabled && animator.runtimeAnimatorController != null)
                    {
                        var st = animator.GetCurrentAnimatorStateInfo(0);
                        animHash = st.fullPathHash;
                        animTime = AnimUtil.SampleTime(st);
                    }
                    return new Vector2(pos.x, pos.y);
                }

                why = "DoesPlayerExist=false (no MapPlayerController either)";
                return null;
            }
            catch (System.Exception ex)
            {
                why = "ex:" + ex.GetType().Name;
                return null;
            }
        }

        /// <summary>
        /// v13: pack the motor/weapon/damage state the client needs to drive the game's own player
        /// animation controller. Level and Arcade controllers only — map players (no motor) return 0.
        /// Bit layout is <see cref="CoopState.Flag"/>* so host and client agree by construction.
        /// Host-only path (called from HostCapture), so it also doubles as the registration point
        /// for the per-player DamageTaken pulse subscription — the receiver reference is fresh here
        /// every frame, so a scene load that recreates it re-hooks automatically.
        /// </summary>
        private static byte SampleFlags(global::AbstractPlayerController ctrl)
        {
            byte f = 0;
            try
            {
                HostPlayerPulses.EnsureDamageHook(ctrl.id, ctrl.damageReceiver);

                var lvl = ctrl as global::LevelPlayerController;
                if (lvl != null)
                {
                    var m = lvl.motor;
                    if (m != null)
                    {
                        if (m.Grounded) f |= CoopState.FlagGrounded;
                        if (m.Locked) f |= CoopState.FlagLocked;
                        if (m.Dashing) f |= CoopState.FlagDashing;
                        if (m.IsUsingSuperOrEx) f |= CoopState.FlagSuperEx;
                        if (m.DashDirection >= 0) f |= CoopState.FlagDashDirPos;
                    }
                    if (lvl.weaponManager != null && lvl.weaponManager.IsShooting) f |= CoopState.FlagShooting;
                    if (ctrl.damageReceiver != null
                        && ctrl.damageReceiver.state == global::PlayerDamageReceiver.State.Invulnerable)
                        f |= CoopState.FlagInvulnerable;
                    return f;
                }

                var arc = ctrl as global::ArcadePlayerController;
                if (arc != null)
                {
                    var m = arc.motor;
                    if (m != null)
                    {
                        if (m.Grounded) f |= CoopState.FlagGrounded;
                        if (m.Locked) f |= CoopState.FlagLocked;
                        if (m.Dashing) f |= CoopState.FlagDashing;
                        if (m.IsUsingSuperOrEx) f |= CoopState.FlagSuperEx;
                        if (m.DashDirection >= 0) f |= CoopState.FlagDashDirPos;
                    }
                    if (arc.weaponManager != null && arc.weaponManager.IsShooting) f |= CoopState.FlagShooting;
                    if (ctrl.damageReceiver != null
                        && ctrl.damageReceiver.state == global::PlayerDamageReceiver.State.Invulnerable)
                        f |= CoopState.FlagInvulnerable;
                    return f;
                }
            }
            catch
            {
                // Mid-transition: a manager may be half-wired. Zero flags is a safe idle.
            }
            return f;
        }

        // Cached lookup so we're not enumerating every frame. Refreshed when the cached
        // reference is destroyed (scene transition).
        private static global::MapPlayerController _cachedMapP1;
        private static global::MapPlayerController _cachedMapP2;

        private static global::MapPlayerController FindMapPlayer(global::PlayerId id)
        {
            // Quick path: cached and still alive.
            if (id == global::PlayerId.PlayerOne && _cachedMapP1 != null && _cachedMapP1.id == id)
                return _cachedMapP1;
            if (id == global::PlayerId.PlayerTwo && _cachedMapP2 != null && _cachedMapP2.id == id)
                return _cachedMapP2;

            // Slow path: enumerate. World maps have at most 2 active map players.
            var all = Object.FindObjectsOfType<global::MapPlayerController>();
            for (int i = 0; i < all.Length; i++)
            {
                if (all[i] == null) continue;
                if (all[i].id == global::PlayerId.PlayerOne) _cachedMapP1 = all[i];
                else if (all[i].id == global::PlayerId.PlayerTwo) _cachedMapP2 = all[i];
            }
            if (id == global::PlayerId.PlayerOne) return _cachedMapP1;
            if (id == global::PlayerId.PlayerTwo) return _cachedMapP2;
            return null;
        }

        // v10: sample host-side Rewired inputs once per snapshot tick. Packs into the
        // LocalP1/P2 input fields above which CoopHost.TickStateSnapshot then ships in
        // PlayerSnapshot.Buttons/Axes.
        // - P1: host's local controller / keyboard. Read directly from
        //   CoopState.LocalPlayer1 (the captured Rewired.Player from PlayerInput.Init).
        //   Set IsCapturingLocalInput so RewiredFocusGate's read-side gates don't apply
        //   here (we want raw local input, not anything mirrored from elsewhere).
        // - P2: on host, P2's Rewired reads are OVERRIDDEN by our own RewiredPatches
        //   postfix to return CoopState.CurrentButtons. So we re-read from CoopState
        //   directly rather than going through Rewired.Player and risking circular
        //   override resolution. Same for axes.
        private static void CaptureInputs()
        {
            try
            {
                var p1 = CoopState.LocalPlayer1;
                if (p1 != null)
                {
                    bool prevFlag = CoopState.IsCapturingLocalInput;
                    CoopState.IsCapturingLocalInput = true;
                    try
                    {
                        uint btns = 0;
                        for (int actionId = 0; actionId < 28; actionId++)
                            if (p1.GetButton(actionId)) btns |= (1u << actionId);
                        LocalP1Buttons = btns;
                        LocalP1AxisX_q = ToFixed(p1.GetAxis(0));
                        LocalP1AxisY_q = ToFixed(p1.GetAxis(1));
                    }
                    finally { CoopState.IsCapturingLocalInput = prevFlag; }
                }
                else
                {
                    LocalP1Buttons = 0;
                    LocalP1AxisX_q = 0;
                    LocalP1AxisY_q = 0;
                }

                // P2 = the network-applied client input. CoopState already holds it from
                // CoopHost.OnNetworkReceive → ApplyRemoteFrame. Just forward it.
                LocalP2Buttons = CoopState.CurrentButtons;
                LocalP2AxisX_q = ToFixed(CoopState.AxisX);
                LocalP2AxisY_q = ToFixed(CoopState.AxisY);
            }
            catch
            {
                // If anything throws (e.g., scene transition with player not yet wired), zero
                // out and try again next tick.
                LocalP1Buttons = 0;
                LocalP1AxisX_q = 0;
                LocalP1AxisY_q = 0;
                LocalP2Buttons = 0;
                LocalP2AxisX_q = 0;
                LocalP2AxisY_q = 0;
            }
        }

        private static sbyte ToFixed(float v)
        {
            int x = (int)(v * 100f);
            if (x < -100) x = -100;
            if (x > 100) x = 100;
            return (sbyte)x;
        }

        private static void ApplyTo(global::PlayerId id, float x, float y, sbyte facing,
                                    int animHash, float animTime, sbyte hp)
        {
            try
            {
                // v13: for Level/Arcade players the game's own animation controller now runs locally
                // (see PlayerMotorBypass), so it owns localScale (facing) and the animator entirely.
                // We only write the interpolated position and push the host's authoritative HP.
                if (global::PlayerManager.DoesPlayerExist(id))
                {
                    var ctrl = global::PlayerManager.GetPlayer(id);
                    if (ctrl != null && ctrl.transform != null)
                    {
                        var pos = ctrl.transform.position;
                        pos.x = x;
                        pos.y = y;
                        ctrl.transform.position = pos;

                        // Push HP through the public setter so OnHealthChanged fires and the HUD
                        // updates (the raw backing-field setter skipped that plumbing). The != guard
                        // avoids re-firing the event when nothing changed.
                        var targetStats = ctrl.stats;
                        if (hp >= 0 && targetStats != null && targetStats.Health != hp)
                            targetStats.SetHealth(hp);
                        return;
                    }
                }

                // Map case — no motor bypass and no animation controller takeover on the world map,
                // so we keep the pre-v13 puppet path: write position + facing + scrub the animator.
                var mapCtrl = FindMapPlayer(id);
                if (mapCtrl == null || mapCtrl.transform == null) return;
                var t = mapCtrl.transform;
                var mp = t.position;
                mp.x = x;
                mp.y = y;
                t.position = mp;
                if (facing != 0)
                {
                    var s = t.localScale;
                    float magnitude = Mathf.Abs(s.x);
                    if (magnitude < 0.001f) magnitude = 1f;
                    s.x = facing * magnitude;
                    t.localScale = s;
                }

                var mapAnim = mapCtrl.GetComponentInChildren<Animator>();
                if (animHash != 0 && mapAnim != null && mapAnim.isActiveAndEnabled
                    && mapAnim.runtimeAnimatorController != null)
                {
                    var allAnims = t.GetComponentsInChildren<Animator>();
                    for (int i = 0; i < allAnims.Length; i++)
                        AnimatorParamPatches.RegisterSuppressed(allAnims[i]);

                    AnimUtil.Scrub(mapAnim, animHash, animTime);
                }
            }
            catch
            {
                // Same logic as capture: swallow during transitions.
            }
        }
    }
}
