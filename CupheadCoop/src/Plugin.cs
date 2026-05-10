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
        public const string Version = "0.7.5";

        private Harmony _harmony;
        private CoopHost _host;
        private CoopClient _client;
        private CoopOverlay _overlay;

        private void Awake()
        {
            // Log this BEFORE any other init so testers can confirm Awake actually fires.
            // If a static-init or sceneLoaded subscription failure kills Awake silently,
            // the absence of this line is a definitive signal.
            Logger.LogInfo("CupheadCoop " + Version + " loading…");

            try
            {
                ModConfig.Bind(Config);
                PlayerInputInit_Patch.Log = Logger;
                ScenePuppetry.Log = Logger;
                EntitySync.Log = Logger;
                SceneSync.Log = Logger;
                EntitySync.Wire();
                LogTap.Wire();
                Application.runInBackground = true;
            }
            catch (Exception ex)
            {
                Logger.LogError("Pre-patch init failed: " + ex);
            }

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

            try
            {
                _overlay = gameObject.AddComponent<CoopOverlay>();
            }
            catch (Exception ex)
            {
                Logger.LogError("Overlay attach failed: " + ex);
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
                EntitySync.Tick(Time.unscaledDeltaTime);
                ScenePuppetry.HostCapture();
                PauseSync.HostCapture();
                SceneSync.HostCapture();
                _host?.TickStateSnapshot(Time.unscaledDeltaTime);
            }
            else if (CoopState.Mode == CoopMode.Client)
            {
                // Scene sync FIRST — if we need to load a different scene, do it before
                // touching anything else this frame.
                if (ModConfig.EnableSceneSync.Value)
                    SceneSync.ApplyFromHost(CoopState.RemoteSceneName);
                EntitySync.Tick(Time.unscaledDeltaTime);
                ScenePuppetry.ClientApply();
                if (ModConfig.EnableEntitySync.Value)
                    EntitySync.ApplyToClient(CoopState.RemoteEntities, CoopState.RemoteEntityCount);
                if (ModConfig.EnablePauseSync.Value)
                    PauseSync.ApplyFromHost(CoopState.RemoteIsPaused);
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
            // controls onto the host's Player 2. The gate patches in RewiredFocusGate would
            // otherwise return zero for client-mode reads — we set IsCapturingLocalInput so our
            // calls bypass the gate and see real keyboard state.
            //
            // Critical: skip capture when the client window doesn't have OS focus. Otherwise the
            // client process keeps reading global keyboard state and forwarding to the host, so
            // typing in the focused HOST window also moves host's P2 (via the network roundtrip
            // from the unfocused client). Solo-testing nightmare.
            if (ModConfig.FocusGateInput.Value && !Application.isFocused)
            {
                CoopState.LocalButtons = 0;
                CoopState.LocalAxisX = 0f;
                CoopState.LocalAxisY = 0f;
                return;
            }

            var p1 = CoopState.LocalPlayer1;
            if (p1 == null) return;

            CoopState.IsCapturingLocalInput = true;
            try
            {
                uint buttons = 0;
                for (int actionId = 0; actionId < 28; actionId++)
                {
                    if (p1.GetButton(actionId)) buttons |= (1u << actionId);
                }
                CoopState.LocalButtons = buttons;
                CoopState.LocalAxisX = p1.GetAxis(0);
                CoopState.LocalAxisY = p1.GetAxis(1);
            }
            finally
            {
                CoopState.IsCapturingLocalInput = false;
            }
        }
    }
}
