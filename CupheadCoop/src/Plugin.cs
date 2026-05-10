using System;
using BepInEx;
using CupheadCoop.Coop;
using CupheadCoop.Net;
using HarmonyLib;
using UnityEngine;

namespace CupheadCoop
{
    [BepInPlugin(GUID, "Cuphead Co-op", Version)]
    public class Plugin : BaseUnityPlugin
    {
        public const string GUID = "leif.cupheadcoop";
        public const string Version = "0.2.0";

        private Harmony _harmony;
        private CoopHost _host;
        private CoopClient _client;
        private CoopOverlay _overlay;

        private void Awake()
        {
            ModConfig.Bind(Config);
            PlayerInputInit_Patch.Log = Logger;
            ScenePuppetry.Log = Logger;
            LogTap.Wire();

            // Keep the simulation, network polling, and snapshot pump running when Cuphead
            // loses focus — otherwise alt-tabbing to a terminal kills the host's Update loop
            // and state snapshots stop until you click back into the game.
            Application.runInBackground = true;

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

            _overlay = gameObject.AddComponent<CoopOverlay>();

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
            HandleHotkeys();

            if (CoopState.Mode == CoopMode.Client)
                CaptureLocalInputForUpload();

            _host?.Pump();
            _client?.Pump(Time.unscaledDeltaTime);
        }

        private void LateUpdate()
        {
            // M4 visual sync runs in LateUpdate — after Cuphead's own Update has moved players.
            // Host: sample P1/P2 transforms now, then ship via the network pump.
            // Client: overwrite local transforms with the host's last-received snapshot so the
            // rendered frame matches what the host sees.
            if (CoopState.Mode == CoopMode.Host)
            {
                ScenePuppetry.HostCapture();
                _host?.TickStateSnapshot(Time.unscaledDeltaTime);
            }
            else if (CoopState.Mode == CoopMode.Client)
            {
                ScenePuppetry.ClientApply();
            }

            // Edge detection lives in CoopState — snapshot at end of frame so next frame's
            // GetButtonDown/Up postfixes can compute deltas.
            CoopState.AdvanceFrame();
        }

        private void HandleHotkeys()
        {
            if (Input.GetKeyDown(ModConfig.KeyHost.Value))
            {
                Logger.LogInfo("Hotkey: Host (" + ModConfig.KeyHost.Value + ") pressed");
                ToggleHost();
            }
            if (Input.GetKeyDown(ModConfig.KeyConnect.Value))
            {
                Logger.LogInfo("Hotkey: Connect (" + ModConfig.KeyConnect.Value + ") pressed");
                ToggleClient();
            }
            if (Input.GetKeyDown(ModConfig.KeyDisconnect.Value))
            {
                Logger.LogInfo("Hotkey: Disconnect (" + ModConfig.KeyDisconnect.Value + ") pressed");
                Disconnect();
            }
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
            var p1 = CoopState.LocalPlayer1;
            if (p1 == null) return;

            uint buttons = 0;
            // Iterate the action ids we care about. CupheadButton enum values 0..27 cover the
            // gameplay surface; we just shovel them all in. (Action ids 0/1 are axes — bits will
            // be ignored by the host's GetAxis postfix path.)
            for (int actionId = 0; actionId < 28; actionId++)
            {
                if (p1.GetButton(actionId)) buttons |= (1u << actionId);
            }

            CoopState.LocalButtons = buttons;
            CoopState.LocalAxisX = p1.GetAxis(0);
            CoopState.LocalAxisY = p1.GetAxis(1);
        }
    }
}
