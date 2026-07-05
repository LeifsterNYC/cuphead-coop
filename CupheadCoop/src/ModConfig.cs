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

        public static ConfigEntry<string> Transport;
        public static ConfigEntry<string> RemoteHost;
        public static ConfigEntry<int> Port;
        public static ConfigEntry<string> ConnectKey;
        public static ConfigEntry<string> HostSteamId;
        public static ConfigEntry<string> AutoStart;

        public static ConfigEntry<int> InputSendRateHz;
        public static ConfigEntry<int> InputBufferFrames;
        public static ConfigEntry<int> StateSendRateHz;
        public static ConfigEntry<int> InterpDelayMs;

        public static ConfigEntry<bool> DebugForceP2WalkRight;
        public static ConfigEntry<bool> Verbose;
        public static ConfigEntry<bool> DumpAnimState;

        // Automated-test harness. All default OFF — see TestHarness.cs. These are for driving two
        // instances from a script; they have no place in normal play.
        public static ConfigEntry<bool> BlockSaves;
        public static ConfigEntry<string> AutoLoadLevel;
        public static ConfigEntry<bool> AutoPlay;
        public static ConfigEntry<int> KillP2AfterSec;

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
        public static ConfigEntry<bool> EnableClientEntityAISuppress;
        public static ConfigEntry<bool> EnableSpawnFromHost;
        public static ConfigEntry<bool> EnableRemoteMotorBypass;
        public static ConfigEntry<bool> EnableDeathSync;
        public static ConfigEntry<bool> EnableLevelEvents;
        public static ConfigEntry<bool> EnableAudioSync;

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

            Transport = cfg.Bind("Network", "Transport", "Steam",
                "Wire transport: 'Steam' (Steam P2P — no port forwarding or ZeroTier needed; client sets HostSteamId) " +
                "or 'Udp' (LiteNetLib direct UDP — for LAN/ZeroTier and solo two-instance testing on one PC, " +
                "where a single Steam account can't connect to itself). Both ends must use the same transport.");
            RemoteHost = cfg.Bind("Network", "RemoteHost", "127.0.0.1",
                "Udp transport only: IPv4 address of the host. LAN: use the host PC's local IP.");
            Port = cfg.Bind("Network", "Port", 47777,
                "UDP port used for both hosting and connecting.");
            ConnectKey = cfg.Bind("Network", "ConnectKey", "cuphead-coop-v0",
                "Shared key both sides must agree on; prevents stray traffic.");
            HostSteamId = cfg.Bind("Network", "HostSteamId", "",
                "Steam transport only, client side: the host's SteamID64 (a 17-digit number). " +
                "The host's ID is shown in their overlay and BepInEx log when they press Host.");
            AutoStart = cfg.Bind("Network", "AutoStart", "Off",
                "Automatically take a role a few seconds after the game boots: 'Host', 'Connect', or 'Off'. " +
                "With 'Connect', the client keeps retrying until the host is up, so launch order doesn't matter. " +
                "Lets both players just start the game and meet in a level — no hotkeys needed.");

            InputSendRateHz = cfg.Bind("Input", "SendRateHz", 60,
                "How often the client sends its input snapshot to the host.");
            InputBufferFrames = cfg.Bind("Input", "BufferFrames", 2,
                "Frames of jitter buffer on the host before applying network input. 1 = lowest latency, 3+ = smoother under jitter.");
            StateSendRateHz = cfg.Bind("State", "SendRateHz", 30,
                "How often the host sends a world-state snapshot (P1/P2 transforms) to the client. 30 Hz is a good balance for LAN; bump to 60 if jitter is visible.");
            InterpDelayMs = cfg.Bind("State", "InterpDelayMs", 60,
                "How far behind the newest host snapshot the client renders, in ms. Higher = smoother under jitter, lower = less visual latency. 2 snapshot intervals is a good floor.");

            DebugForceP2WalkRight = cfg.Bind("Debug", "ForceP2WalkRight", false,
                "If true, with no active session, forces Player 2's MoveHorizontal axis to +1.0. Used to verify the input intercept works without networking.");
            Verbose = cfg.Bind("Debug", "Verbose", false,
                "Log per-frame input traffic. Noisy.");
            DumpAnimState = cfg.Bind("Debug", "DumpAnimState", false,
                "v1.2.0 verification: once per second, both host and client log each player's animator " +
                "layer 0/1/2 state hash + normalized time, layer weights, localScale.x sign and the exchanged " +
                "flags byte in an identical format, so the two logs diff cleanly. Off for normal play.");

            BlockSaves = cfg.Bind("Debug", "BlockSaves", false,
                "AUTOMATED TESTING ONLY. Blocks every .sav disk write (OnlineInterfaceSteam.SaveCloudData, the sole " +
                "path for save-progress AND settings-cloud writes) so the harness can never touch the SHARED real save " +
                "in %AppData%\\Cuphead. The game is told the save succeeded. Leave OFF for normal play.");
            AutoLoadLevel = cfg.Bind("Debug", "AutoLoadLevel", "",
                "AUTOMATED TESTING ONLY. When non-empty and hosting with a client connected, auto-loads the named boss " +
                "level a few seconds after the session is ready (e.g. 'Veggies', 'Slime', 'Flower'). Case-insensitive. One-shot.");
            AutoPlay = cfg.Bind("Debug", "AutoPlay", false,
                "AUTOMATED TESTING ONLY. Drives scripted move/jump/shoot input so the cups act without a human. On host it " +
                "drives Player 1; on client it drives the uploaded input (host's Player 2), phase-shifted so the two cups differ.");
            KillP2AfterSec = cfg.Bind("Debug", "KillP2AfterSec", 0,
                "AUTOMATED TESTING ONLY. 0 = off. When > 0 and hosting inside a boss level (scene_level*), kills Player 2 " +
                "through the game's own damage path after this many seconds — to verify death mirroring. One-shot.");

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
            EnableClientEntityAISuppress = cfg.Bind("Sync", "EnableClientEntityAISuppress", true,
                "v0.9.0 HKMP-style enemy AI suppression: when caching AbstractLevelEntity instances on client, set their MonoBehaviour.enabled = false so Unity stops calling Update/FixedUpdate on the AI script. Transforms + animators + sprite renderers still work — host's stream drives them. Without this, client's local boss AI runs in parallel with host's and the two sims drift (different RNG, different attack timing). Disable if a specific boss visually freezes or behaves wrong on client.");
            EnableSpawnFromHost = cfg.Bind("Sync", "EnableSpawnFromHost", true,
                "v0.9.0 spawn-from-host: when host streams an entity or projectile that client doesn't have locally (boss-summoned minion, host-fired projectile that client's suppressed AI didn't fire), Object.Instantiate from a local prefab template (built at scene-load by walking FindObjectsOfType + hashing Type.FullName). Required for full visual sync once enemy AI is suppressed. Disable if Instantiate'd clones cause Unity errors / NREs in your version of Cuphead.");
            EnableRemoteMotorBypass = cfg.Bind("Sync", "EnableRemoteMotorBypass", true,
                "v0.9.1 architectural pivot inspired by Germanized/CupHeads' PlayerMotorPatch: on client, prefix-skip LevelPlayerMotor.FixedUpdate and ArcadePlayerMotor.FixedUpdate for both player slots. Their position is written by ScenePuppetry from the interpolated host stream + Traverse-set motor properties (LookDirection, MoveDirection, Grounded). Eliminates the parallel-sim divergence that input-mirroring couldn't fully fix. Disable if both cups go visually static or jitter weirdly.");
            EnableDeathSync = cfg.Bind("Sync", "EnableDeathSync", true,
                "v1.1.0 M8.5 minimal death mirroring: on client, hide a cup's sprite renderers while the host reports that player dead (or absent while the other player is still present). Prevents a frozen, alive-looking cup being left standing when a player dies on the host. v1.2.0 also spawns the game's own floating death ghost for the downed cup, destroyed on revive/scene change. Disable if players go invisible when they shouldn't.");
            EnableLevelEvents = cfg.Bind("Sync", "EnableLevelEvents", true,
                "v1.2.0 wave 2: on client, mirror host-authoritative level lifecycle from the stream — both-players-dead shows the stock game-over retry card (Level.playerIsDead), a host Retry reloads the client's level (SceneLoader.ReloadLevel), and a host win plays the cosmetic LevelKOAnimation. The client NEVER takes any save-writing path (PlayerData.SaveCurrentFile is blocked in client mode). Disable to bisect if the game-over/win card or reload misbehaves.");
            EnableAudioSync = cfg.Bind("Sync", "EnableAudioSync", true,
                "v1.2.0 wave 2: host streams gameplay SFX (AudioManager.Play/PlayLoop/Stop keys) to the client, which suppresses its own local gameplay SFX and replays the host's inside synced level scenes (scene_level*). Fixes the near-silent client fight (boss/enemy SFX come from AI coroutines that are dead on the client). BGM + menu/map SFX are untouched. When off, the host stops capturing AND the client stops suppressing — both play their own SFX locally.");
        }
    }
}
