using System;
using BepInEx;
using BepInEx.Logging;
using CupheadCoop.Coop;
using CupheadCoop.Net;
using HarmonyLib;
using UnityEngine;

namespace CupheadCoop
{
    [BepInPlugin(GUID, "Cuphead Co-op", Version)]
    // Run our Update before any other MonoBehaviour's. Critical for input forwarding: if
    // Cuphead's PlayerMotor.Update runs first, it reads actions.GetButtonDown BEFORE we've
    // pumped network packets, so the just-arrived jump press is invisible to the motor.
    // Next frame, AdvanceFrame has already moved Current → Previous so it never counts as
    // "down" anymore. Symptom: client presses jump, host's P2 doesn't jump.
    [DefaultExecutionOrder(-32000)]
    public class Plugin : BaseUnityPlugin
    {
        public const string GUID = "leif.cupheadcoop";
        public const string Version = "0.9.1";

        private Harmony _harmony;
        private CoopHost _host;
        private CoopClient _client;
        private CoopOverlay _overlay;
        private CoopLateApply _lateApply;

        // Exposed so the overlay + late-apply component can reach in for diagnostic data
        // without us building a sprawling read-only properties layer.
        internal CoopHost HostInstance => _host;
        internal CoopClient ClientInstance => _client;
        internal static BepInEx.Logging.ManualLogSource LogStatic;

        private void Awake()
        {
            // Log this BEFORE any other init so testers can confirm Awake actually fires.
            // If a static-init or sceneLoaded subscription failure kills Awake silently,
            // the absence of this line is a definitive signal.
            Logger.LogInfo("CupheadCoop " + Version + " loading…");
            LogStatic = Logger;

            try
            {
                ModConfig.Bind(Config);
                PlayerInputInit_Patch.Log = Logger;
                ScenePuppetry.Log = Logger;
                EntitySync.Log = Logger;
                SceneSync.Log = Logger;
                P2AutoJoin.Log = Logger;
                ProjectileSync.Log = Logger;
                TypeRegistry.Log = Logger;
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

            try
            {
                _lateApply = gameObject.AddComponent<CoopLateApply>();
                _lateApply.Owner = this;
            }
            catch (Exception ex)
            {
                Logger.LogError("CoopLateApply attach failed: " + ex);
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

            // Run any deferred main-thread reflection actions queued by the network layer.
            // Both host and client may have queued a P2 auto-join.
            if (CoopState.Mode != CoopMode.Off)
                P2AutoJoin.TickIfPending();
        }

        // CRITICAL: also pump the host network in FixedUpdate. Cuphead's ArcadePlayerMotor.HandleInput
        // runs from FixedUpdate, and FixedUpdate fires BEFORE Update in a Unity frame. If we only
        // pumped from Update, packets that arrived between LateUpdate(N) and FixedUpdate(N+1) would
        // sit unprocessed when HandleInput reads actions.GetButtonDown(2) in FixedUpdate(N+1) —
        // the down-edge would be visible only in Update(N+1), one phase too late. AdvanceFrame
        // (LateUpdate, end of frame) then moves CurrentButtons → PreviousButtons, eating the edge.
        // Symptom: holding jump on client never registers on host's P2 even though btns shows the
        // bit set. Pumping in FixedUpdate fixes the input timing for edge-triggered actions.
        // Held buttons (Shoot uses GetButton, not GetButtonDown) didn't have this problem because
        // CurrentButtons stays valid across frames once set.
        private void FixedUpdate()
        {
            if (CoopState.Mode == CoopMode.Host)
                _host?.Pump();
        }

        // LateUpdate moved into CoopLateApply (separate component with [DefaultExecutionOrder(+32000)])
        // so it runs AFTER Cuphead's animator and physics writers, instead of before. Plugin keeps
        // its negative execution order purely so Update gets the network packets first.

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
