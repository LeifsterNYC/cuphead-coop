using BepInEx.Configuration;
using UnityEngine;

namespace CupheadCoop
{
    internal static class ModConfig
    {
        public static ConfigEntry<KeyCode> KeyHost;
        public static ConfigEntry<KeyCode> KeyConnect;
        public static ConfigEntry<KeyCode> KeyDisconnect;

        public static ConfigEntry<string> RemoteHost;
        public static ConfigEntry<int> Port;
        public static ConfigEntry<string> ConnectKey;

        public static ConfigEntry<int> InputSendRateHz;
        public static ConfigEntry<int> InputBufferFrames;

        public static ConfigEntry<bool> DebugForceP2WalkRight;
        public static ConfigEntry<bool> Verbose;

        public static void Bind(ConfigFile cfg)
        {
            KeyHost = cfg.Bind("Hotkeys", "Host", KeyCode.F9,
                "Start hosting on the configured port. Press once after launching the game.");
            KeyConnect = cfg.Bind("Hotkeys", "Connect", KeyCode.F10,
                "Connect to the configured RemoteHost:Port as the second-player client.");
            KeyDisconnect = cfg.Bind("Hotkeys", "Disconnect", KeyCode.F11,
                "Tear down the current host or client session.");

            RemoteHost = cfg.Bind("Network", "RemoteHost", "127.0.0.1",
                "IPv4 address of the host. LAN: use the host PC's local IP.");
            Port = cfg.Bind("Network", "Port", 47777,
                "UDP port used for both hosting and connecting.");
            ConnectKey = cfg.Bind("Network", "ConnectKey", "cuphead-coop-v0",
                "Shared key both sides must agree on; prevents stray traffic.");

            InputSendRateHz = cfg.Bind("Input", "SendRateHz", 60,
                "How often the client sends its input snapshot to the host.");
            InputBufferFrames = cfg.Bind("Input", "BufferFrames", 2,
                "Frames of jitter buffer on the host before applying network input. 1 = lowest latency, 3+ = smoother under jitter.");

            DebugForceP2WalkRight = cfg.Bind("Debug", "ForceP2WalkRight", false,
                "If true, with no active session, forces Player 2's MoveHorizontal axis to +1.0. Used to verify the input intercept works without networking.");
            Verbose = cfg.Bind("Debug", "Verbose", false,
                "Log per-frame input traffic. Noisy.");
        }
    }
}
