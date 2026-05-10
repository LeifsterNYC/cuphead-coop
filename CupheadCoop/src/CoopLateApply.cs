using CupheadCoop.Coop;
using UnityEngine;

namespace CupheadCoop
{
    /// <summary>
    /// Hosts the LateUpdate phase of the coop sync. Lives in a separate MonoBehaviour with
    /// <c>[DefaultExecutionOrder(+32000)]</c> so it runs AFTER Cuphead's own animator and
    /// physics writers. Plugin itself runs at -32000 so its Update sees network packets
    /// before PlayerMotor reads input — but that same negative order made Plugin.LateUpdate
    /// also run early, before Cuphead's animator update, which let Cuphead overwrite our
    /// forced animator state and produced visible 2-frame flicker.
    ///
    /// Splitting LateUpdate into a late-order component preserves the input-direction win
    /// while making transform/animator overrides the LAST writes of the frame.
    /// </summary>
    [DefaultExecutionOrder(32000)]
    internal class CoopLateApply : MonoBehaviour
    {
        public Plugin Owner;

        private void LateUpdate()
        {
            float dt = Time.unscaledDeltaTime;
            ProjectileSync.Tick(dt);

            if (CoopState.Mode == CoopMode.Host)
            {
                EntitySync.Tick(dt);
                ScenePuppetry.HostCapture();
                PauseSync.HostCapture();
                SceneSync.HostCapture();
                Owner?.HostInstance?.TickStateSnapshot(dt);
            }
            else if (CoopState.Mode == CoopMode.Client)
            {
                if (ModConfig.EnableSceneSync.Value)
                    SceneSync.ApplyFromHost(CoopState.RemoteSceneName);

                EntitySync.Tick(dt);
                ScenePuppetry.ClientApply();

                if (ModConfig.EnableEntitySync.Value)
                {
                    EntitySync.ApplyAliveSet(CoopState.RemoteAliveHashes, CoopState.RemoteAliveHashCount);
                    EntitySync.ApplyToClient(CoopState.RemoteEntities, CoopState.RemoteEntityCount);
                }
                if (ModConfig.EnableProjectileSync.Value)
                {
                    ProjectileSync.ApplyToClient(CoopState.RemoteProjectiles, CoopState.RemoteProjectileCount);
                }
                if (ModConfig.EnablePauseSync.Value)
                    PauseSync.ApplyFromHost(CoopState.RemoteIsPaused);
            }

            // Edge detection — snapshot at end of frame so next frame's GetButtonDown/Up
            // postfixes can compute deltas. Must happen after the network pump's
            // ApplyRemoteFrame on host (which set CurrentButtons earlier in the frame)
            // and any ApplyMirroredInputs on client (which sets MirroredP1/P2 from
            // host's StateSnapshot earlier in the frame).
            CoopState.AdvanceFrame();
            CoopState.AdvanceClientInputs();
        }
    }
}
