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

        // M6 entity sync. Fixed-size buffer to avoid GC churn; RemoteEntityCount tracks valid slots.
        public static readonly EntitySnapshot[] RemoteEntities = new EntitySnapshot[EntitySync.MaxSyncedEntities];
        public static int RemoteEntityCount;
        // M7 v8: full alive-hash list from host (separate from position-tracked entities).
        public static readonly uint[] RemoteAliveHashes = new uint[EntitySync.MaxAliveHashes];
        public static int RemoteAliveHashCount;

        // True only while Plugin.CaptureLocalInputForUpload is reading Rewired. The input gate
        // patches let the call through unchanged when this is set, but otherwise return zero
        // for client-mode Cupheads so the local sim doesn't react to keyboard input that's
        // meant for upload only.
        public static bool IsCapturingLocalInput;

        // Host's pause state. Client's PauseSync.ApplyFromHost converges to this each frame.
        public static bool RemoteIsPaused;

        // Host's active scene name. Client's SceneSync.ApplyFromHost converges to this.
        public static string RemoteSceneName = "";

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
            RemoteEntityCount = 0;
            RemoteIsPaused = false;
            RemoteSceneName = "";
        }

        /// <summary>
        /// Apply a host-streamed world snapshot. Out-of-order packets are dropped on the floor.
        /// </summary>
        public static void ApplyRemoteState(uint sequence,
                                            PlayerSnapshot p1, PlayerSnapshot p2,
                                            bool isPaused, string sceneName,
                                            EntitySnapshot[] entities, int entityCount)
        {
            if (sequence != 0 && sequence <= RemoteStateSequence) return;
            RemoteStateSequence = sequence;
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
        }

        public static void ApplyRemoteAliveHashes(uint[] hashes, int count)
        {
            int n = count;
            if (n > RemoteAliveHashes.Length) n = RemoteAliveHashes.Length;
            for (int i = 0; i < n; i++) RemoteAliveHashes[i] = hashes[i];
            RemoteAliveHashCount = n;
        }

        private static float Clamp(float v, float min, float max)
        {
            if (v < min) return min;
            if (v > max) return max;
            return v;
        }
    }
}
