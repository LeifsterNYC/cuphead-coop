using System;
using CupheadCoop.Net;

namespace CupheadCoop.Coop
{
    internal enum CoopMode { Off, Host, Client }

    /// <summary>
    /// Process-wide state visible to Harmony patches and the network layer.
    /// One instance lives for the lifetime of the BepInEx plugin.
    /// </summary>
    internal static class CoopState
    {
        public static CoopMode Mode = CoopMode.Off;

        // v13 PlayerSnapshot.Flags bit layout. Shared by host capture (ScenePuppetry/HostCaptureHooks)
        // and client consume (PlayerMotorBypass) so the two ends can never disagree on a bit.
        public const byte FlagGrounded = 1 << 0;
        public const byte FlagLocked = 1 << 1;
        public const byte FlagDashing = 1 << 2;
        public const byte FlagShooting = 1 << 3;
        public const byte FlagSuperEx = 1 << 4;
        public const byte FlagInvulnerable = 1 << 5;
        public const byte FlagDashDirPos = 1 << 6; // 1 = DashDirection >= 0

        // v13 PlayerSnapshot.Pulses bit layout (host sets on the edge, client consumes once).
        public const byte PulseWeaponFired = 1 << 0;
        public const byte PulseDamageTaken = 1 << 1;

        // v13 StateSnapshot.LevelFlags bit layout.
        public const byte LevelFlagWon = 1 << 0;    // latch while host win sequence runs
        public const byte LevelFlagReload = 1 << 1; // pulse on SceneLoader.ReloadLevel()
        public const byte LevelFlagLost = 1 << 2;   // latch while host game-over (lose) flow runs

        // Resolved by Harmony patch on PlayerInput.Init the moment Cuphead binds Rewired.Player
        // to a PlayerId. -1 means "not yet captured" — patches no-op until known.
        public static int RewiredPlayer1Id = -1;
        public static int RewiredPlayer2Id = -1;

        // Live Rewired.Player references captured at the same time. Held so the client side can
        // sample local input without re-querying PlayerManager every frame.
        public static Rewired.Player LocalPlayer1;
        public static Rewired.Player LocalPlayer2;

        // Buttons currently held, as seen by the simulation. Bit n = CupheadButton with action id n.
        public static uint CurrentButtons;
        // Buttons held last frame. Used to derive Down/Up edges.
        public static uint PreviousButtons;
        // Latest analog axes for Player 2. Range -1..+1.
        public static float AxisX;
        public static float AxisY;

        // Frame sequence applied. Used to detect stale inputs / log gaps.
        public static uint LastAppliedSequence;

        // Bits that went 0→1 since the last AdvanceFrame, accumulated across all received
        // packets within a single host frame. Lets IsButtonDown report a press even if multiple
        // packets land in one frame (the latest-overwrite behavior of CurrentButtons would
        // otherwise lose transient presses), and is order-independent — Cuphead's PlayerMotor
        // can read it BEFORE or AFTER our network pump in any given frame and it still works.
        public static uint PressedThisFrame;
        private static uint _prevSeenButtons; // last raw buttons we received, for newly-down detection

        // Set true on the host once the client has connected and we've started receiving input frames.
        public static bool HasRemoteInput;

        // Local-side: when Mode == Client, holds the most recent local-input frame so the network pump can ship it.
        public static uint LocalButtons;
        public static float LocalAxisX;
        public static float LocalAxisY;
        public static uint LocalSequence;

        // M4: latest world-state snapshot received from the host. Used by ScenePuppetry on the
        // client to override the local Cuphead transforms each frame. Sequence is kept so we can
        // drop out-of-order UDP packets and so the overlay can show "is the host still streaming?".
        public static uint RemoteStateSequence;
        public static bool RemoteP1Present;
        public static float RemoteP1X;
        public static float RemoteP1Y;
        public static sbyte RemoteP1Facing;
        public static int RemoteP1AnimHash;
        public static float RemoteP1AnimTime;
        public static sbyte RemoteP1Hp;
        public static bool RemoteP1IsDead;
        public static bool RemoteP2Present;
        public static float RemoteP2X;
        public static float RemoteP2Y;
        public static sbyte RemoteP2Facing;
        public static int RemoteP2AnimHash;
        public static float RemoteP2AnimTime;
        public static sbyte RemoteP2Hp;
        public static bool RemoteP2IsDead;

        // v13 per-player motor/weapon state (newest snapshot wins — not interpolated). Read each
        // frame by PlayerMotorBypass to drive the game's own animation controller on the client.
        public static byte RemoteP1Flags;
        public static byte RemoteP2Flags;
        // v13 per-player edge pulses. Sticky-ORed across every received snapshot (so a pulse in a
        // snapshot the interpolation window skips over is not lost) and consumed exactly once by
        // the client's edge synthesis via the Consume* accessors below.
        public static byte RemoteP1Pulses;
        public static byte RemoteP2Pulses;
        // v13 level-lifecycle bits. LevelWon is a latch (newest); LevelReload is a sticky pulse.
        public static byte RemoteLevelFlags;

        // M6 entity sync. Fixed-size buffer to avoid GC churn; RemoteEntityCount tracks valid slots.
        public static readonly EntitySnapshot[] RemoteEntities = new EntitySnapshot[EntitySync.MaxSyncedEntities];
        public static int RemoteEntityCount;
        // M7 v8: full alive-hash list from host (separate from position-tracked entities).
        public static readonly uint[] RemoteAliveHashes = new uint[EntitySync.MaxAliveHashes];
        public static int RemoteAliveHashCount;
        // M7 v9: per-projectile snapshots with NetworkIDs. Pre-allocated buffer.
        public static readonly ProjectileSnapshot[] RemoteProjectiles = new ProjectileSnapshot[ProjectileSync.MaxSyncedProjectiles];
        public static int RemoteProjectileCount;

        // v10 input mirror — per-player buttons + axes streamed by host so client's local
        // Rewired reads can return host's actual inputs instead of zero. P1 mirror is what
        // host's local P1 was pressing this frame; P2 mirror is what host's P2 was pressing
        // (which on host's side is the round-tripped client keyboard input).
        // Edge state: to support GetButtonDown/Up correctly on client, we maintain
        // PressedThisFrame accumulator + Previous-frame snapshot, mirroring the existing
        // CurrentButtons/PreviousButtons/PressedThisFrame pattern but per-player.
        public static uint MirroredP1Buttons;
        public static uint PreviousMirroredP1Buttons;
        public static uint PressedP1ThisFrame;
        public static float MirroredP1AxisX;
        public static float MirroredP1AxisY;
        private static uint _prevSeenP1Buttons;
        public static uint MirroredP2Buttons;
        public static uint PreviousMirroredP2Buttons;
        public static uint PressedP2ThisFrame;
        public static float MirroredP2AxisX;
        public static float MirroredP2AxisY;
        private static uint _prevSeenP2Buttons;

        // True only while Plugin.CaptureLocalInputForUpload is reading Rewired. The input gate
        // patches let the call through unchanged when this is set, but otherwise return zero
        // for client-mode Cupheads so the local sim doesn't react to keyboard input that's
        // meant for upload only.
        public static bool IsCapturingLocalInput;

        // Host's pause state. Client's PauseSync.ApplyFromHost converges to this each frame.
        public static bool RemoteIsPaused;

        // Host's active scene name. Client's SceneSync.ApplyFromHost converges to this.
        public static string RemoteSceneName = "";

        // v13 wave 2 audio: set true only while AudioSync is replaying a host-streamed SFX through
        // the real AudioManager, so the client-side suppression prefix lets that one call through
        // instead of eating it. Every other AudioManager.Play/Stop on the client is a local gameplay
        // SFX and stays suppressed while a host stream is active in a synced level scene.
        public static bool ReplayingFromHost;

        public static bool IsButtonHeld(int actionId)
        {
            if (actionId < 0 || actionId >= 32) return false;
            return ((CurrentButtons >> actionId) & 1u) != 0u;
        }

        public static bool IsButtonDown(int actionId)
        {
            if (actionId < 0 || actionId >= 32) return false;
            uint bit = 1u << actionId;
            // A press reported by either: bits accumulated this frame from newly-arrived
            // packets, OR the last-vs-current-frame transition (covers the case where the
            // user is holding a button across many frames).
            return ((PressedThisFrame & bit) != 0u)
                || ((CurrentButtons & bit) != 0u && (PreviousButtons & bit) == 0u);
        }

        public static bool IsButtonUp(int actionId)
        {
            if (actionId < 0 || actionId >= 32) return false;
            uint bit = 1u << actionId;
            return (CurrentButtons & bit) == 0u && (PreviousButtons & bit) != 0u;
        }

        public static void AdvanceFrame()
        {
            PreviousButtons = CurrentButtons;
            PressedThisFrame = 0;
        }

        /// <summary>
        /// v10 mirror edge advance — same idea as <see cref="AdvanceFrame"/> but for the
        /// per-player input mirrors used by client's local sim. Called once per frame from
        /// CoopLateApply alongside AdvanceFrame so PressedP1/P2ThisFrame are zeroed and
        /// Previous tracks Current.
        /// </summary>
        public static void AdvanceClientInputs()
        {
            PreviousMirroredP1Buttons = MirroredP1Buttons;
            PreviousMirroredP2Buttons = MirroredP2Buttons;
            PressedP1ThisFrame = 0;
            PressedP2ThisFrame = 0;
        }

        /// <summary>
        /// Apply a fresh per-player input snapshot received from the host. Mirrors the
        /// <see cref="ApplyRemoteFrame"/> "newly-pressed" accumulator pattern so multiple
        /// snapshots arriving within a single frame don't lose transient down-edges.
        /// </summary>
        public static void ApplyMirroredInputs(uint p1Buttons, float p1AxisX, float p1AxisY,
                                                uint p2Buttons, float p2AxisX, float p2AxisY)
        {
            uint newlyP1 = p1Buttons & ~_prevSeenP1Buttons;
            PressedP1ThisFrame |= newlyP1;
            _prevSeenP1Buttons = p1Buttons;
            MirroredP1Buttons = p1Buttons;
            MirroredP1AxisX = Clamp(p1AxisX, -1f, 1f);
            MirroredP1AxisY = Clamp(p1AxisY, -1f, 1f);

            uint newlyP2 = p2Buttons & ~_prevSeenP2Buttons;
            PressedP2ThisFrame |= newlyP2;
            _prevSeenP2Buttons = p2Buttons;
            MirroredP2Buttons = p2Buttons;
            MirroredP2AxisX = Clamp(p2AxisX, -1f, 1f);
            MirroredP2AxisY = Clamp(p2AxisY, -1f, 1f);
        }

        // Per-player mirror queries used by RewiredPatches when Mode == Client. Mirrors the
        // existing IsButtonHeld/Down/Up signatures but read from the P1/P2 mirror slots
        // instead of the global Current/Previous/PressedThisFrame.
        public static bool IsClientP1ButtonHeld(int actionId)
        {
            if (actionId < 0 || actionId >= 32) return false;
            return ((MirroredP1Buttons >> actionId) & 1u) != 0u;
        }
        public static bool IsClientP1ButtonDown(int actionId)
        {
            if (actionId < 0 || actionId >= 32) return false;
            uint bit = 1u << actionId;
            return ((PressedP1ThisFrame & bit) != 0u)
                || ((MirroredP1Buttons & bit) != 0u && (PreviousMirroredP1Buttons & bit) == 0u);
        }
        public static bool IsClientP1ButtonUp(int actionId)
        {
            if (actionId < 0 || actionId >= 32) return false;
            uint bit = 1u << actionId;
            return (MirroredP1Buttons & bit) == 0u && (PreviousMirroredP1Buttons & bit) != 0u;
        }
        public static bool IsClientP2ButtonHeld(int actionId)
        {
            if (actionId < 0 || actionId >= 32) return false;
            return ((MirroredP2Buttons >> actionId) & 1u) != 0u;
        }
        public static bool IsClientP2ButtonDown(int actionId)
        {
            if (actionId < 0 || actionId >= 32) return false;
            uint bit = 1u << actionId;
            return ((PressedP2ThisFrame & bit) != 0u)
                || ((MirroredP2Buttons & bit) != 0u && (PreviousMirroredP2Buttons & bit) == 0u);
        }
        public static bool IsClientP2ButtonUp(int actionId)
        {
            if (actionId < 0 || actionId >= 32) return false;
            uint bit = 1u << actionId;
            return (MirroredP2Buttons & bit) == 0u && (PreviousMirroredP2Buttons & bit) != 0u;
        }

        // ---- v13 pulse accessors (client side). Flags need no accessor — consumers test the
        // RemoteP*Flags byte directly against the Flag* constants. ----

        /// <summary>Return whether the given pulse bit is pending for a player, clearing it so the
        /// edge fires exactly once. Called from the client's edge synthesis each frame.</summary>
        public static bool ConsumeP1Pulse(byte bit)
        {
            bool v = (RemoteP1Pulses & bit) != 0;
            RemoteP1Pulses &= (byte)~bit;
            return v;
        }
        public static bool ConsumeP2Pulse(byte bit)
        {
            bool v = (RemoteP2Pulses & bit) != 0;
            RemoteP2Pulses &= (byte)~bit;
            return v;
        }

        /// <summary>Latched host win state — read (not consumed) by the client's win-cosmetic path (wave 2).</summary>
        public static bool RemoteLevelWon => (RemoteLevelFlags & LevelFlagWon) != 0;

        /// <summary>Latched host game-over state. Authoritative both-dead signal: the per-player
        /// IsDead stream is racy (Cuphead removes a dead player's controller the same frame IsDead
        /// flips, so a 30 Hz snapshot usually never carries it) — the host latches its own
        /// LevelEnd.Lose instead.</summary>
        public static bool RemoteLevelLost => (RemoteLevelFlags & LevelFlagLost) != 0;

        /// <summary>Consume the one-shot LevelReload pulse (wave 2 calls SceneLoader.ReloadLevel).</summary>
        public static bool ConsumeLevelReload()
        {
            bool v = (RemoteLevelFlags & LevelFlagReload) != 0;
            RemoteLevelFlags &= unchecked((byte)~LevelFlagReload);
            return v;
        }

        public static void ApplyRemoteFrame(uint sequence, uint buttons, float axisX, float axisY)
        {
            if (sequence != 0 && sequence <= LastAppliedSequence) return;
            LastAppliedSequence = sequence;

            // Detect newly-pressed bits relative to the last raw packet seen. This catches
            // transient presses that would otherwise be squashed by a same-frame second packet.
            uint newlyPressed = buttons & ~_prevSeenButtons;
            PressedThisFrame |= newlyPressed;
            _prevSeenButtons = buttons;

            CurrentButtons = buttons;
            AxisX = Clamp(axisX, -1f, 1f);
            AxisY = Clamp(axisY, -1f, 1f);
            HasRemoteInput = true;
        }

        public static void Reset()
        {
            Mode = CoopMode.Off;
            CurrentButtons = 0;
            PreviousButtons = 0;
            AxisX = 0f;
            AxisY = 0f;
            LastAppliedSequence = 0;
            HasRemoteInput = false;
            LocalButtons = 0;
            LocalAxisX = 0f;
            LocalAxisY = 0f;
            LocalSequence = 0;
            RemoteStateSequence = 0;
            RemoteP1Present = false;
            RemoteP1X = 0f;
            RemoteP1Y = 0f;
            RemoteP1Facing = 0;
            RemoteP1AnimHash = 0;
            RemoteP1AnimTime = 0f;
            RemoteP1Hp = -1;
            RemoteP1IsDead = false;
            RemoteP2Present = false;
            RemoteP2X = 0f;
            RemoteP2Y = 0f;
            RemoteP2Facing = 0;
            RemoteP2AnimHash = 0;
            RemoteP2AnimTime = 0f;
            RemoteP2Hp = -1;
            RemoteP2IsDead = false;
            RemoteP1Flags = 0;
            RemoteP2Flags = 0;
            RemoteP1Pulses = 0;
            RemoteP2Pulses = 0;
            RemoteLevelFlags = 0;
            RemoteEntityCount = 0;
            RemoteIsPaused = false;
            RemoteSceneName = "";
            ReplayingFromHost = false;
            RemoteProjectileCount = 0;
            MirroredP1Buttons = 0;
            PreviousMirroredP1Buttons = 0;
            PressedP1ThisFrame = 0;
            MirroredP1AxisX = 0f;
            MirroredP1AxisY = 0f;
            _prevSeenP1Buttons = 0;
            MirroredP2Buttons = 0;
            PreviousMirroredP2Buttons = 0;
            PressedP2ThisFrame = 0;
            MirroredP2AxisX = 0f;
            MirroredP2AxisY = 0f;
            _prevSeenP2Buttons = 0;
            ProjectileSync.Reset();
            // v1.1.0: clear the interpolation ring + restore any death-hidden renderers.
            SnapshotInterpolation.Reset();
            PlayerDeathSync.Reset();
            // v1.2.0 wave 2: clear the audio capture/replay rings + loop tracking, and the level-event
            // one-shots, so a fresh session doesn't inherit stale SFX or a latched game-over/win.
            AudioSync.Reset();
            LevelEventSync.Reset();
            // v0.9.0: re-enable any AbstractLevelEntity AI components we disabled while in
            // client mode + drop the prefab template registry. Otherwise after F11 the user's
            // single-player session has dead bosses (AI disabled).
            EntitySync.RestoreClientDisabled();
            TypeRegistry.ClearClient();
            // v13: clear host-side edge accumulators + win/reload latch so a new session starts clean.
            HostPlayerPulses.Reset();
            HostLevelFlags.Reset();
            HostLoseWatchdog.Reset();
        }

        /// <summary>
        /// Apply a host-streamed world snapshot. Out-of-order packets are dropped on the floor.
        /// </summary>
        public static void ApplyRemoteState(uint sequence,
                                            PlayerSnapshot p1, PlayerSnapshot p2,
                                            bool isPaused, string sceneName,
                                            EntitySnapshot[] entities, int entityCount,
                                            byte levelFlags)
        {
            if (sequence != 0 && sequence <= RemoteStateSequence) return;
            RemoteStateSequence = sequence;

            // v13: flags take newest (no interpolation); pulses sticky-OR so an edge in a snapshot
            // the interpolation window skips over still reaches the client. This is the receive
            // path (one call per in-order snapshot), so each snapshot's pulses OR in exactly once.
            RemoteP1Flags = p1.Flags;
            RemoteP2Flags = p2.Flags;
            RemoteP1Pulses |= p1.Pulses;
            RemoteP2Pulses |= p2.Pulses;
            // LevelWon/LevelLost = newest latch; LevelReload = sticky pulse ORed across snapshots.
            RemoteLevelFlags = (byte)((levelFlags & (LevelFlagWon | LevelFlagLost))
                                      | ((RemoteLevelFlags | levelFlags) & LevelFlagReload));

            RemoteP1Present = p1.Present;
            RemoteP1X = p1.X;
            RemoteP1Y = p1.Y;
            RemoteP1Facing = p1.Facing;
            RemoteP1AnimHash = p1.AnimStateHash;
            RemoteP1AnimTime = p1.AnimNormalizedTime;
            RemoteP1Hp = p1.Hp;
            RemoteP1IsDead = p1.IsDead;
            RemoteP2Present = p2.Present;
            RemoteP2X = p2.X;
            RemoteP2Y = p2.Y;
            RemoteP2Facing = p2.Facing;
            RemoteP2AnimHash = p2.AnimStateHash;
            RemoteP2AnimTime = p2.AnimNormalizedTime;
            RemoteP2Hp = p2.Hp;
            RemoteP2IsDead = p2.IsDead;

            // Copy entities into our pre-allocated buffer to avoid retaining the network-side
            // array (which gets recycled by LiteNetLib).
            int n = entityCount;
            if (n > RemoteEntities.Length) n = RemoteEntities.Length;
            for (int i = 0; i < n; i++) RemoteEntities[i] = entities[i];
            RemoteEntityCount = n;
            RemoteIsPaused = isPaused;
            RemoteSceneName = sceneName ?? "";

            // v10: extract per-player input mirrors so client's local Rewired reads can
            // return host's actual inputs.
            ApplyMirroredInputs(p1.Buttons, p1.UnpackAxisX, p1.UnpackAxisY,
                                p2.Buttons, p2.UnpackAxisX, p2.UnpackAxisY);
        }

        public static void ApplyRemoteAliveHashes(uint[] hashes, int count)
        {
            int n = count;
            if (n > RemoteAliveHashes.Length) n = RemoteAliveHashes.Length;
            for (int i = 0; i < n; i++) RemoteAliveHashes[i] = hashes[i];
            RemoteAliveHashCount = n;
        }

        public static void ApplyRemoteProjectiles(ProjectileSnapshot[] projectiles, int count)
        {
            int n = count;
            if (projectiles == null) n = 0;
            if (n > RemoteProjectiles.Length) n = RemoteProjectiles.Length;
            for (int i = 0; i < n; i++) RemoteProjectiles[i] = projectiles[i];
            RemoteProjectileCount = n;
        }

        private static float Clamp(float v, float min, float max)
        {
            if (v < min) return min;
            if (v > max) return max;
            return v;
        }
    }
}
