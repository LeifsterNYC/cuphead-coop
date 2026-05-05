using System;
using BepInEx;
using BepInEx.Logging;
using CupheadCoop.Coop;
using CupheadCoop.Net;
using HarmonyLib;
using Rewired;
using UnityEngine;

namespace CupheadCoop
{
    [BepInPlugin(GUID, "Cuphead Co-op", Version)]
    public class Plugin : BaseUnityPlugin
    {
        public const string GUID = "leif.cupheadcoop";
        public const string Version = "0.1.0";

        private Harmony _harmony;
        private CoopHost _host;
        private CoopClient _client;

        // Player 2 Rewired id is discovered lazily because PlayerManager populates its
        // dictionary only after the game has loaded a scene where players exist.
        private int _p2DiscoveryThrottle;

        // Cached Rewired.Player 1 reference for client-side local-input capture.
        private Player _localP1;

        private void Awake()
        {
            ModConfig.Bind(Config);

            Logger.LogInfo("CupheadCoop " + Version + " loading…");

            _harmony = new Harmony(GUID);
            try
            {
                RewiredPatches.Apply(_harmony);
                int patchCount = 0;
                foreach (var _ in _harmony.GetPatchedMethods()) patchCount++;
                Logger.LogInfo("Harmony patches applied: " + patchCount);
            }
            catch (Exception ex)
            {
                Logger.LogError("Harmony patch failure: " + ex);
            }

            Logger.LogInfo("Press " + ModConfig.KeyHost.Value + " to host, " + ModConfig.KeyConnect.Value +
                           " to connect to " + ModConfig.RemoteHost.Value + ":" + ModConfig.Port.Value +
                           ", " + ModConfig.KeyDisconnect.Value + " to disconnect.");
        }

        private void OnDestroy()
        {
            try { _host?.Stop(); } catch { }
            try { _client?.Stop(); } catch { }
            if (_harmony != null) _harmony.UnpatchSelf();
        }

        private void Update()
        {
            DiscoverRewiredPlayers();

            HandleHotkeys();

            if (CoopState.Mode == CoopMode.Client)
                CaptureLocalInputForUpload();

            _host?.Pump();
            _client?.Pump(Time.unscaledDeltaTime);
        }

        private void LateUpdate()
        {
            // Edge detection lives in CoopState — snapshot at end of frame so next frame's
            // GetButtonDown/Up postfixes can compute deltas.
            CoopState.AdvanceFrame();
        }

        private void DiscoverRewiredPlayers()
        {
            // Try every ~30 frames until we find player 2. Cheap.
            if (CoopState.RewiredPlayer2Id >= 0 && _localP1 != null) return;
            if (++_p2DiscoveryThrottle < 30) return;
            _p2DiscoveryThrottle = 0;

            try
            {
                if (CoopState.RewiredPlayer2Id < 0)
                {
                    var p2 = PlayerManager.GetPlayerInput(PlayerId.PlayerTwo);
                    if (p2 != null)
                    {
                        CoopState.RewiredPlayer2Id = p2.id;
                        Logger.LogInfo("Resolved Rewired Player 2 id = " + p2.id +
                                       " (name=" + p2.name + ")");
                    }
                }
                if (_localP1 == null)
                {
                    _localP1 = PlayerManager.GetPlayerInput(PlayerId.PlayerOne);
                    if (_localP1 != null)
                        Logger.LogInfo("Resolved Rewired Player 1 id = " + _localP1.id +
                                       " (name=" + _localP1.name + ")");
                }
            }
            catch (Exception ex)
            {
                // PlayerManager may not have its dictionary populated yet — silent retry.
                if (ModConfig.Verbose.Value) Logger.LogDebug("Rewired discovery: " + ex.Message);
            }
        }

        private void HandleHotkeys()
        {
            if (Input.GetKeyDown(ModConfig.KeyHost.Value)) ToggleHost();
            if (Input.GetKeyDown(ModConfig.KeyConnect.Value)) ToggleClient();
            if (Input.GetKeyDown(ModConfig.KeyDisconnect.Value)) Disconnect();
        }

        private void ToggleHost()
        {
            if (_host != null && _host.Running) { Disconnect(); return; }
            if (_client != null) { Disconnect(); }

            _host = new CoopHost(Logger);
            if (!_host.Start(ModConfig.Port.Value, ModConfig.ConnectKey.Value))
            {
                _host = null;
                Logger.LogError("Host start failed.");
            }
        }

        private void ToggleClient()
        {
            if (_client != null && _client.Running) { Disconnect(); return; }
            if (_host != null) { Disconnect(); }

            _client = new CoopClient(Logger);
            if (!_client.Start(ModConfig.RemoteHost.Value, ModConfig.Port.Value, ModConfig.ConnectKey.Value))
            {
                _client = null;
                Logger.LogError("Client start failed.");
            }
        }

        private void Disconnect()
        {
            if (_host != null) { _host.Stop(); _host = null; }
            if (_client != null) { _client.Stop(); _client = null; }
        }

        private void CaptureLocalInputForUpload()
        {
            // On the client, the user is the only person at this PC. We map their LOCAL Player 1
            // controls (whatever Rewired binds them to — keyboard or controller) onto the host's
            // Player 2. This is the natural UX: client uses the most familiar controls.
            if (_localP1 == null) return;

            uint buttons = 0;
            // Iterate the action ids we care about. CupheadButton enum values 0..27 cover the
            // gameplay surface; we just shovel them all in.
            for (int actionId = 0; actionId < 28; actionId++)
            {
                if (_localP1.GetButton(actionId)) buttons |= (1u << actionId);
            }

            CoopState.LocalButtons = buttons;
            CoopState.LocalAxisX = _localP1.GetAxis(0);
            CoopState.LocalAxisY = _localP1.GetAxis(1);
        }
    }
}
