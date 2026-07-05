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
            // v1.2.0 wave 2: refresh the audio scene gate (both modes) and, on the client, replay
            // this frame's host-streamed SFX. Runs before the mode branches so the cached scene gate
            // is fresh for any AudioManager patch that fires later this frame.
            AudioSync.Tick();

            if (CoopState.Mode == CoopMode.Host)
            {
                EntitySync.Tick(dt);
                ScenePuppetry.HostCapture();
                PauseSync.HostCapture();
                SceneSync.HostCapture();
                // Re-arm the stock lose gate if the game deadlocked past its own 4-frame both-dead
                // window (ghost-revive cycling) — must run after HostCapture/SceneSync so it reads
                // this frame's scene name, before the snapshot send so the Lost latch ships promptly.
                HostLoseWatchdog.Tick(dt);
                Owner?.HostInstance?.TickStateSnapshot(dt);
            }
            else if (CoopState.Mode == CoopMode.Client)
            {
                // v1.1.0: interpolate the buffered snapshot stream into CoopState BEFORE any
                // applier reads it, so every downstream applier sees the smoothed view.
                SnapshotInterpolation.Apply();

                if (ModConfig.EnableSceneSync.Value)
                    SceneSync.ApplyFromHost(CoopState.RemoteSceneName);

                EntitySync.Tick(dt);
                ScenePuppetry.ClientApply();
                PlayerDeathSync.Tick();
                // v1.2.0 wave 2: drive the stock game-over/win/reload UI from the host stream.
                LevelEventSync.Tick();
                // v1.2.0: rebuild the level HUD if its one-shot init raced the force-join.
                HudFixup.Tick();

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
