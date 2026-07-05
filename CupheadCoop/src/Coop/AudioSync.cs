using System.Collections.Generic;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine.SceneManagement;

namespace CupheadCoop.Coop
{
    /// <summary>
    /// v1.2.0 wave 2 (design section D) — host-authoritative streamed SFX.
    ///
    /// WHY: <c>AudioManager.Play/PlayLoop/Stop(string)</c> is the process-wide chokepoint for every
    /// gameplay sound effect. Boss/enemy SFX come from AI coroutines that are dead on the client
    /// (their <c>AbstractLevelEntity.enabled</c> is false), and player jump/dash/hit/death SFX come
    /// from motor/damage code paths that are skipped on the client — so without this the client's
    /// fight is nearly silent. BGM + the announcer intro already play locally for free (they ride
    /// AudioManagerComponent's autoplay, not gameplay code), so those are deliberately NOT touched.
    ///
    /// Host: Harmony postfixes on the three string methods queue (kind, key) into a preallocated
    /// ring, coalesced + deduped per snapshot tick and capped at <see cref="MaxSfxPerTick"/>.
    /// <see cref="Net.CoopHost.TickStateSnapshot"/> drains the ring into the outgoing StateSnapshot.
    /// Client: prefixes on the same three methods return false (suppress the local gameplay SFX);
    /// the received entries are replayed through the real AudioManager under
    /// <see cref="CoopState.ReplayingFromHost"/> so our own suppression prefix lets the replay through.
    ///
    /// Scope gate: both capture and suppression only apply inside a synced gameplay scene
    /// (name starts with <c>scene_level</c>). Front-end / map / menu SFX stay entirely local on
    /// both ends.
    /// </summary>
    internal static class AudioSync
    {
        public static ManualLogSource Log;

        // SFX entry kinds — mirrored on the wire (StateSnapshot.SfxKinds) and by the replay switch.
        public const byte KindPlay = 0;
        public const byte KindPlayLoop = 1;
        public const byte KindStop = 2;

        // Per-tick cap: at 30 Hz snapshots a boss fight rarely fires more than a handful of distinct
        // SFX per 33 ms, so 16 is generous headroom while bounding the packet size.
        public const int MaxSfxPerTick = 16;

        // Host capture ring. Preallocated; _hostCount tracks valid slots. Deduped by (kind,key) so a
        // sound fired twice in one tick ships once. Drained by CoopHost.TickStateSnapshot.
        public static readonly byte[] HostSfxKinds = new byte[MaxSfxPerTick];
        public static readonly string[] HostSfxKeys = new string[MaxSfxPerTick];
        private static int _hostCount;

        // Client replay inbox. Filled by CoopClient.OnReceived (main thread — LiteNetLib polls during
        // Pump), drained + replayed once per frame by Tick(). Sized larger than one tick's worth to
        // absorb two snapshots landing in one frame; overflow drops the newest (rare, one-shots only).
        private const int InboxCap = 32;
        private static readonly byte[] _rxKinds = new byte[InboxCap];
        private static readonly string[] _rxKeys = new string[InboxCap];
        private static int _rxCount;

        // Loops the host has started on us (KindPlayLoop). Tracked so a dropped Stop snapshot can't
        // strand a loop forever: we Stop them all on scene change / disconnect. Loop starts are rare
        // events, so the HashSet's steady-state churn is negligible.
        private static readonly HashSet<string> _trackedLoops = new HashSet<string>();

        // Cached "am I in a synced gameplay scene?" — refreshed once per frame in Tick() so the hot
        // Play/Stop patch bodies don't each allocate a scene-name string. Used by both host capture
        // and client suppression via IsInLevelScene.
        private static bool _inLevelScene;
        private static string _lastScene = "";

        // Diagnostics (item 6): SFX sent/replayed since the last read, for the LEVELSTATE dump line.
        private static int _txWindow;
        private static int _rxWindow;

        public static bool IsInLevelScene => _inLevelScene;

        // ---- Host capture (called from the Harmony postfixes) ----

        public static void HostCapture(byte kind, string key)
        {
            if (CoopState.Mode != CoopMode.Host) return;
            if (!ModConfig.EnableAudioSync.Value) return;
            if (!_inLevelScene) return;
            if (string.IsNullOrEmpty(key)) return;

            // Dedupe identical (kind,key) already queued this tick.
            for (int i = 0; i < _hostCount; i++)
                if (_hostSfxKindsEq(i, kind, key)) return;

            if (_hostCount >= MaxSfxPerTick) return; // tick is full — drop (latest-wins channel anyway)
            HostSfxKinds[_hostCount] = kind;
            HostSfxKeys[_hostCount] = key;
            _hostCount++;
        }

        private static bool _hostSfxKindsEq(int i, byte kind, string key)
        {
            return HostSfxKinds[i] == kind && HostSfxKeys[i] == key;
        }

        /// <summary>Snapshot the queued host SFX for the outgoing packet and clear the ring. The
        /// backing arrays keep their contents (only the count is reset), so the caller's Write over
        /// [0..count) reads valid data. Called once per outgoing snapshot from CoopHost.</summary>
        public static int DrainForSnapshot()
        {
            int n = _hostCount;
            _hostCount = 0;
            _txWindow += n;
            return n;
        }

        // ---- Client replay inbox (called from CoopClient.OnReceived) ----

        public static void EnqueueFromHost(byte[] kinds, string[] keys, int count)
        {
            if (CoopState.Mode != CoopMode.Client) return;
            if (!ModConfig.EnableAudioSync.Value) return;
            if (kinds == null || keys == null) return;
            for (int i = 0; i < count; i++)
            {
                if (_rxCount >= InboxCap) return;
                _rxKinds[_rxCount] = kinds[i];
                _rxKeys[_rxCount] = keys[i];
                _rxCount++;
            }
        }

        // ---- Per-frame driver (called from CoopLateApply for both modes) ----

        public static void Tick()
        {
            if (CoopState.Mode == CoopMode.Off) return;

            // Refresh the cached scene gate once per frame. Also drives the client's loop reconcile:
            // a scene change Stops any tracked loops so a lost Stop can't bleed across levels.
            string scene;
            try { scene = SceneManager.GetActiveScene().name ?? ""; }
            catch { scene = _lastScene; }
            bool sceneChanged = scene != _lastScene;
            _lastScene = scene;
            _inLevelScene = scene.StartsWith("scene_level");

            if (CoopState.Mode != CoopMode.Client) return; // host does nothing further here

            if (sceneChanged)
            {
                StopTrackedLoops();
                _rxCount = 0; // stale SFX from the previous scene are meaningless now
            }

            if (_rxCount == 0) return;
            if (!ModConfig.EnableAudioSync.Value || !_inLevelScene)
            {
                _rxCount = 0; // drop rather than replay outside a synced gameplay scene
                return;
            }

            // Replay under the ReplayingFromHost flag so our own suppression prefix lets these through.
            CoopState.ReplayingFromHost = true;
            try
            {
                for (int i = 0; i < _rxCount; i++)
                {
                    string key = _rxKeys[i];
                    if (string.IsNullOrEmpty(key)) continue;
                    switch (_rxKinds[i])
                    {
                        case KindPlay:
                            global::AudioManager.Play(key);
                            break;
                        case KindPlayLoop:
                            global::AudioManager.PlayLoop(key);
                            _trackedLoops.Add(key);
                            break;
                        case KindStop:
                            global::AudioManager.Stop(key);
                            _trackedLoops.Remove(key);
                            break;
                    }
                    _rxWindow++;
                }
            }
            finally
            {
                CoopState.ReplayingFromHost = false;
                _rxCount = 0;
            }
        }

        private static void StopTrackedLoops()
        {
            if (_trackedLoops.Count == 0) return;
            bool prev = CoopState.ReplayingFromHost;
            CoopState.ReplayingFromHost = true;
            try
            {
                foreach (var key in _trackedLoops)
                {
                    try { global::AudioManager.Stop(key); } catch { }
                }
            }
            finally { CoopState.ReplayingFromHost = prev; }
            _trackedLoops.Clear();
        }

        // ---- Diagnostics (item 6) ----

        public static int ConsumeTxWindow() { int v = _txWindow; _txWindow = 0; return v; }
        public static int ConsumeRxWindow() { int v = _rxWindow; _rxWindow = 0; return v; }

        public static void Reset()
        {
            _hostCount = 0;
            _rxCount = 0;
            _txWindow = 0;
            _rxWindow = 0;
            _inLevelScene = false;
            _lastScene = "";
            // Don't route through StopTrackedLoops() on teardown — the AudioManager may be gone.
            _trackedLoops.Clear();
        }
    }

    /// <summary>
    /// Harmony patches feeding <see cref="AudioSync"/>. Host postfixes capture; client prefixes
    /// suppress. Both are gated inside the sinks by <see cref="CoopState.Mode"/>, so single-player
    /// and the off-scene case no-op. The client suppression returns false ONLY when a real host
    /// stream is active in a synced gameplay scene and we're not mid-replay — otherwise the game's
    /// own SFX play normally (menus, disconnected, replay path).
    /// </summary>
    [HarmonyPatch]
    internal static class AudioSyncPatches
    {
        // Suppress the local gameplay SFX on the client. Returning false skips AudioManager's event
        // dispatch entirely. The replay path (ReplayingFromHost) and any non-level scene fall through.
        private static bool ClientSuppress()
        {
            if (CoopState.Mode != CoopMode.Client) return true;      // host / single-player: real SFX
            if (!ModConfig.EnableAudioSync.Value) return true;        // kill switch: real SFX
            if (CoopState.ReplayingFromHost) return true;             // our own replay: let it play
            if (CoopState.RemoteStateSequence == 0) return true;      // no host stream yet
            if (!AudioSync.IsInLevelScene) return true;               // menu / map SFX stay local
            return false;                                             // suppress the local gameplay SFX
        }

        [HarmonyPatch(typeof(global::AudioManager), nameof(global::AudioManager.Play), new[] { typeof(string) })]
        [HarmonyPrefix]
        private static bool Play_Prefix(string key) { return ClientSuppress(); }

        [HarmonyPatch(typeof(global::AudioManager), nameof(global::AudioManager.PlayLoop), new[] { typeof(string) })]
        [HarmonyPrefix]
        private static bool PlayLoop_Prefix(string key) { return ClientSuppress(); }

        [HarmonyPatch(typeof(global::AudioManager), nameof(global::AudioManager.Stop), new[] { typeof(string) })]
        [HarmonyPrefix]
        private static bool Stop_Prefix(string key) { return ClientSuppress(); }

        // Host capture postfixes. The `key` parameter is the lowercased value AudioManager reassigned
        // at method entry (Harmony reads the final parameter slot), which matches what a replayed
        // AudioManager.Play(key) would re-lowercase — so capture and replay agree on case.
        [HarmonyPatch(typeof(global::AudioManager), nameof(global::AudioManager.Play), new[] { typeof(string) })]
        [HarmonyPostfix]
        private static void Play_Postfix(string key) { AudioSync.HostCapture(AudioSync.KindPlay, key); }

        [HarmonyPatch(typeof(global::AudioManager), nameof(global::AudioManager.PlayLoop), new[] { typeof(string) })]
        [HarmonyPostfix]
        private static void PlayLoop_Postfix(string key) { AudioSync.HostCapture(AudioSync.KindPlayLoop, key); }

        [HarmonyPatch(typeof(global::AudioManager), nameof(global::AudioManager.Stop), new[] { typeof(string) })]
        [HarmonyPostfix]
        private static void Stop_Postfix(string key) { AudioSync.HostCapture(AudioSync.KindStop, key); }
    }
}
