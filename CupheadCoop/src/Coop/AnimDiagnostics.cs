using BepInEx.Logging;
using UnityEngine;

namespace CupheadCoop.Coop
{
    /// <summary>
    /// v1.2.0 verification aid, [Debug] DumpAnimState. Once per second, both host and client log the
    /// same one-line-per-player summary of each player's animator (layer 0/1/2 state hash + time,
    /// layer-1 weight, localScale.x sign) plus the flags byte the two ends exchanged. Because the
    /// format is identical on both sides — flags read from ScenePuppetry.LocalP*Flags on the host and
    /// CoopState.RemoteP*Flags on the client — the two logs diff cleanly to confirm the client's
    /// locally-driven animation matches the host's authoritative state (e.g. layer-1 weight = 1 while
    /// the host reports IsShooting).
    /// </summary>
    internal static class AnimDiagnostics
    {
        public static ManualLogSource Log;
        private static float _accum;

        public static void Tick(float dt)
        {
            if (CoopState.Mode == CoopMode.Off) return;
            if (!ModConfig.DumpAnimState.Value) return;
            _accum += dt;
            if (_accum < 1f) return;
            _accum = 0f;

            bool host = CoopState.Mode == CoopMode.Host;
            string side = host ? "HOST" : "CLIENT";
            DumpOne(side, "P1", global::PlayerId.PlayerOne,
                    host ? ScenePuppetry.LocalP1Flags : CoopState.RemoteP1Flags);
            DumpOne(side, "P2", global::PlayerId.PlayerTwo,
                    host ? ScenePuppetry.LocalP2Flags : CoopState.RemoteP2Flags);

            // v1.2.0 wave 2: one LEVELSTATE line per second, same prefix both sides so greps diff
            // cleanly. Host reports its win latch + SFX shipped; client reports the mirrored win,
            // the both-dead condition, live ghost count, and SFX replayed — all since the last line.
            try
            {
                if (host)
                    Log?.LogInfo("LEVELSTATE HOST won=" + HostLevelFlags.Won +
                                 " sfxTx=" + AudioSync.ConsumeTxWindow());
                else
                    Log?.LogInfo("LEVELSTATE CLIENT won=" + CoopState.RemoteLevelWon +
                                 " bothDead=" + LevelEventSync.BothDeadNow +
                                 " ghosts=" + PlayerDeathSync.GhostCount +
                                 " sfxRx=" + AudioSync.ConsumeRxWindow());
            }
            catch
            {
                // Diagnostic only.
            }
        }

        private static void DumpOne(string side, string label, global::PlayerId id, byte flags)
        {
            try
            {
                if (!global::PlayerManager.DoesPlayerExist(id)) return;
                var ctrl = global::PlayerManager.GetPlayer(id);
                if (ctrl == null || ctrl.transform == null) return;
                var anim = ctrl.GetComponentInChildren<Animator>();
                if (anim == null || !anim.isActiveAndEnabled || anim.runtimeAnimatorController == null) return;

                int layers = anim.layerCount;
                var l0 = anim.GetCurrentAnimatorStateInfo(0);
                string s = "ANIMDUMP " + side + " " + label +
                           " L0=0x" + l0.fullPathHash.ToString("X8") + "@" + AnimUtil.SampleTime(l0).ToString("F2");
                if (layers > 1)
                {
                    var l1 = anim.GetCurrentAnimatorStateInfo(1);
                    s += " L1=0x" + l1.fullPathHash.ToString("X8") + " w1=" + anim.GetLayerWeight(1).ToString("F2");
                }
                if (layers > 2)
                {
                    var l2 = anim.GetCurrentAnimatorStateInfo(2);
                    s += " L2=0x" + l2.fullPathHash.ToString("X8") + " w2=" + anim.GetLayerWeight(2).ToString("F2");
                }
                float sx = ctrl.transform.localScale.x;
                s += " sx=" + (sx > 0.001f ? "+" : sx < -0.001f ? "-" : "0") +
                     " flags=0x" + flags.ToString("X2");
                Log?.LogInfo(s);
            }
            catch
            {
                // Diagnostic only — never let a scene-transition race throw.
            }
        }
    }
}
