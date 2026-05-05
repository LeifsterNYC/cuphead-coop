using System;

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

        // Resolved at runtime by sniffing PlayerManager.GetPlayerInput(PlayerId.PlayerTwo).
        // -1 means "not yet captured" — patches must no-op until known.
        public static int RewiredPlayer2Id = -1;

        // Buttons currently held, as seen by the simulation. Bit n = CupheadButton with action id n.
        public static uint CurrentButtons;
        // Buttons held last frame. Used to derive Down/Up edges.
        public static uint PreviousButtons;
        // Latest analog axes for Player 2. Range -1..+1.
        public static float AxisX;
        public static float AxisY;

        // Frame sequence applied. Used to detect stale inputs / log gaps.
        public static uint LastAppliedSequence;

        // Set true on the host once the client has connected and we've started receiving input frames.
        public static bool HasRemoteInput;

        // Local-side: when Mode == Client, holds the most recent local-input frame so the network pump can ship it.
        public static uint LocalButtons;
        public static float LocalAxisX;
        public static float LocalAxisY;
        public static uint LocalSequence;

        public static bool IsButtonHeld(int actionId)
        {
            if (actionId < 0 || actionId >= 32) return false;
            return ((CurrentButtons >> actionId) & 1u) != 0u;
        }

        public static bool IsButtonDown(int actionId)
        {
            if (actionId < 0 || actionId >= 32) return false;
            uint bit = 1u << actionId;
            return (CurrentButtons & bit) != 0u && (PreviousButtons & bit) == 0u;
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
        }

        public static void ApplyRemoteFrame(uint sequence, uint buttons, float axisX, float axisY)
        {
            // Drop out-of-order packets. UDP can reorder.
            if (sequence != 0 && sequence <= LastAppliedSequence) return;
            LastAppliedSequence = sequence;
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
        }

        private static float Clamp(float v, float min, float max)
        {
            if (v < min) return min;
            if (v > max) return max;
            return v;
        }
    }
}
