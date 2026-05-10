using BepInEx.Configuration;
using UnityEngine;

namespace CupheadCoop
{
    internal static class ModConfig
    {
        public static ConfigEntry<KeyCode> KeyHost;
        public static ConfigEntry<KeyCode> KeyConnect;
        public static ConfigEntry<KeyCode> KeyDisconnect;
        public static ConfigEntry<KeyCode> KeyToggleOverlay;

        public static ConfigEntry<string> RemoteHost;
        public static ConfigEntry<int> Port;
        public static ConfigEntry<string> ConnectKey;

        public static ConfigEntry<int> InputSendRateHz;
        public static ConfigEntry<int> InputBufferFrames;
        public static ConfigEntry<int> StateSendRateHz;

        public static ConfigEntry<bool> DebugForceP2WalkRight;
        public static ConfigEntry<bool> Verbose;

        // Per-feature kill switches. Default ON; flip OFF to bisect which sync layer is causing
        // visible problems on the client. The wire format is unchanged — host always sends, client
        // chooses whether to apply.
        public static ConfigEntry<bool> EnablePlayerSync;
        public static ConfigEntry<bool> EnableAnimationSync;
        public static ConfigEntry<bool> EnableEntitySync;
        public static ConfigEntry<bool> EnableHpSync;
        public static ConfigEntry<bool> EnablePauseSync;
        public static ConfigEntry<bool> EnableSceneSync;
        public static ConfigEntry<bool> FocusGateInput;
        public static ConfigEntry<bool> EnableAutoP2Join;
        public static ConfigEntry<bool> EnableProjectileSync;

        public static void Bind(ConfigFile cfg)
        {
            KeyHost = cfg.Bind("Hotkeys", "Host", KeyCode.F9,
                "Start hosting on the configured port. Press once after launching the game.");
            KeyConnect = cfg.Bind("Hotkeys", "Connect", KeyCode.F10,
                "Connect to the configured RemoteHost:Port as the second-player client.");
            KeyDisconnect = cfg.Bind("Hotkeys", "Disconnect", KeyCode.F11,
                "Tear down the current host or client session.");
            KeyToggleOverlay = cfg.Bind("Hotkeys", "ToggleOverlay", KeyCode.O,
                "Show/hide the in-game CupheadCoop status overlay (default O).");

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
            StateSendRateHz = cfg.Bind("State", "SendRateHz", 30,
                "How often the host sends a world-state snapshot (P1/P2 transforms) to the client. 30 Hz is a good balance for LAN; bump to 60 if jitter is visible.");

            DebugForceP2WalkRight = cfg.Bind("Debug", "ForceP2WalkRight", false,
                "If true, with no active session, forces Player 2's MoveHorizontal axis to +1.0. Used to verify the input intercept works without networking.");
            Verbose = cfg.Bind("Debug", "Verbose", false,
                "Log per-frame input traffic. Noisy.");

            // Kill switches let the tester flip individual sync layers off without rebuilding,
            // to bisect which layer is breaking the in-game experience. All default ON.
            EnablePlayerSync = cfg.Bind("Sync", "EnablePlayerSync", true,
                "Client overrides local P1/P2 transforms with host-streamed positions. Disable to see whether ScenePuppetry is interfering with Cuphead's own player movement.");
            EnableAnimationSync = cfg.Bind("Sync", "EnableAnimationSync", true,
                "Client also calls Animator.Play to match host's animation state. Disable if cup animations look wrong but transforms are fine.");
            EnableEntitySync = cfg.Bind("Sync", "EnableEntitySync", true,
                "Client overrides local AbstractLevelEntity transforms (boss, animated set pieces) with host-streamed positions. Disable to verify M6 vs not.");
            EnableHpSync = cfg.Bind("Sync", "EnableHpSync", true,
                "Client mirrors host's HP via PlayerStatsManager.Health setter. Disable if the HP UI is glitching.");
            EnablePauseSync = cfg.Bind("Sync", "EnablePauseSync", true,
                "Host's pause state freezes the client. Disable to confirm pause-related freezes aren't from us.");
            EnableSceneSync = cfg.Bind("Sync", "EnableSceneSync", true,
                "Client auto-LoadScene's to host's active scene. Disable if menu navigation is producing infinite loops or wrong scene loads.");
            FocusGateInput = cfg.Bind("Sync", "FocusGateInput", true,
                "Suppress all Rewired input reads when this Cuphead window doesn't have focus. Required when running two instances on the same PC for solo testing — without this, both windows read the same keyboard simultaneously and every keypress moves both cups. Harmless to leave on for normal multi-PC play.");
            EnableAutoP2Join = cfg.Bind("Sync", "EnableAutoP2Join", true,
                "On both host and client: when a coop session establishes, force-join P2 via reflection on PlayerManager. Required for Justin-style \"keyboard on remote machine\" coop where host has no local controller to do the natural join. Disable if it interferes with single-player or causes weird state in your version of Cuphead.");
            EnableProjectileSync = cfg.Bind("Sync", "EnableProjectileSync", true,
                "v0.8.2 M7 v2: host assigns synthetic NetworkIDs to AbstractProjectile instances at spawn; client binds local AbstractProjectile instances to those IDs by closest-position match. Replaces the broken path-hash approach for runtime clones. Disable if projectiles glitch out badly in some level.");
        }
    }
}
