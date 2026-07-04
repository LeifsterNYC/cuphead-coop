using System.Collections.Generic;
using BepInEx.Logging;
using CupheadCoop.Coop;
using UnityEngine;

namespace CupheadCoop
{
    /// <summary>
    /// Top-left IMGUI status panel + recent-events tail. Always visible while the plugin is loaded
    /// so the tester gets immediate visual feedback (mode, Rewired discovery, hotkey presses,
    /// network handshake) without alt-tabbing to read LogOutput.log.
    ///
    /// The events tail is fed by <see cref="LogTap"/> which subscribes to BepInEx's log events
    /// and keeps the last N lines in a ring buffer.
    /// </summary>
    internal class CoopOverlay : MonoBehaviour
    {
        private GUIStyle _style;
        private GUIStyle _bgStyle;
        private Texture2D _bgTex;
        // Visibility toggled by ModConfig.KeyToggleOverlay. Defaults to visible so a fresh
        // launch shows feedback immediately; once the tester knows the layout they can hide it.
        public static bool Visible = true;

        private void Update()
        {
            if (Input.GetKeyDown(ModConfig.KeyToggleOverlay.Value))
                Visible = !Visible;
        }

        private void EnsureStyles()
        {
            if (_style != null) return;
            _bgTex = new Texture2D(1, 1);
            _bgTex.SetPixel(0, 0, new Color(0f, 0f, 0f, 0.7f));
            _bgTex.Apply();
            _bgStyle = new GUIStyle { normal = { background = _bgTex } };
            _style = new GUIStyle
            {
                fontSize = 12,
                normal = { textColor = Color.white },
                padding = new RectOffset(6, 6, 4, 4),
                richText = false
            };
        }

        private void OnGUI()
        {
            if (!Visible) return;
            EnsureStyles();

            // Find Plugin singleton via scene to reach into host/client diagnostics.
            var plugin = Object.FindObjectOfType<Plugin>();

            string mode = CoopState.Mode.ToString();
            string p1 = CoopState.RewiredPlayer1Id < 0 ? "?" : CoopState.RewiredPlayer1Id.ToString();
            string p2 = CoopState.RewiredPlayer2Id < 0 ? "?" : CoopState.RewiredPlayer2Id.ToString();
            string remote = CoopState.HasRemoteInput ? "rx" : "--";
            string seq = CoopState.LastAppliedSequence.ToString();
            string btns = CoopState.CurrentButtons.ToString("X");
            string ax = CoopState.AxisX.ToString("0.00") + "," + CoopState.AxisY.ToString("0.00");
            string forced = ModConfig.DebugForceP2WalkRight.Value ? "  [DEBUG: P2 walks right]" : "";

            // Connection/health summary on the right side of line 1
            string netHealth = "";
            if (plugin?.ClientInstance != null && CoopState.Mode == CoopMode.Client)
            {
                if (plugin.ClientInstance.Reconnecting)
                    netHealth = "  [RECONNECTING in " + plugin.ClientInstance.SecondsUntilReconnect.ToString("0.0") + "s]";
                else if (plugin.ClientInstance.Connected)
                    netHealth = "  ping=" + plugin.ClientInstance.PingMs + "ms";
                else
                    netHealth = "  [DIALING]";
            }
            else if (plugin?.HostInstance != null && CoopState.Mode == CoopMode.Host)
            {
                // Describe includes the host's SteamID64 on the Steam transport — that's the
                // value the second player needs for [Network] HostSteamId, so keep it on screen.
                netHealth = (plugin.HostInstance.HasClient
                                ? "  ping=" + plugin.HostInstance.PingMs + "ms"
                                : "  [waiting for client]") +
                            "  (" + plugin.HostInstance.Describe + ")";
            }

            string line1 = "CupheadCoop v" + Plugin.Version + "  mode=" + mode + netHealth +
                           "  hotkeys: host=" + ModConfig.KeyHost.Value + " connect=" + ModConfig.KeyConnect.Value +
                           " disc=" + ModConfig.KeyDisconnect.Value + " hide=" + ModConfig.KeyToggleOverlay.Value + forced;
            string line2 = "rewired p1=" + p1 + " p2=" + p2 + "   net=" + remote +
                          " seq=" + seq + " btns=" + btns + " axes=" + ax;
            // Line 3: M4 visual sync. Host: what we last sampled. Client: what we last received.
            string line3 = null;
            if (CoopState.Mode == CoopMode.Host)
            {
                string hp1 = ScenePuppetry.LocalP1Present
                    ? ScenePuppetry.LocalP1X.ToString("0.0") + "," + ScenePuppetry.LocalP1Y.ToString("0.0") +
                      " hp=" + ScenePuppetry.LocalP1Hp + (ScenePuppetry.LocalP1IsDead ? " DEAD" : "")
                    : "-";
                string hp2 = ScenePuppetry.LocalP2Present
                    ? ScenePuppetry.LocalP2X.ToString("0.0") + "," + ScenePuppetry.LocalP2Y.ToString("0.0") +
                      " hp=" + ScenePuppetry.LocalP2Hp + (ScenePuppetry.LocalP2IsDead ? " DEAD" : "")
                    : "-";
                line3 = "tx state p1=" + hp1 + "  p2=" + hp2 + "  ents=" + EntitySync.LastCapturedCount +
                       "  proj=" + ProjectileSync.LastHostCount +
                       (PauseSync.LocalIsPaused ? "  [PAUSED]" : "");
            }
            else if (CoopState.Mode == CoopMode.Client)
            {
                string cp1 = CoopState.RemoteP1Present
                    ? CoopState.RemoteP1X.ToString("0.0") + "," + CoopState.RemoteP1Y.ToString("0.0") +
                      " hp=" + CoopState.RemoteP1Hp + (CoopState.RemoteP1IsDead ? " DEAD" : "")
                    : "-";
                string cp2 = CoopState.RemoteP2Present
                    ? CoopState.RemoteP2X.ToString("0.0") + "," + CoopState.RemoteP2Y.ToString("0.0") +
                      " hp=" + CoopState.RemoteP2Hp + (CoopState.RemoteP2IsDead ? " DEAD" : "")
                    : "-";
                // ents=rx/cache  hits=apply-hit  miss=apply-miss  off=alive-deactivated
                // hit/miss tells us if path-hashes line up between host and client. Persistent
                // miss > 0 with hit == 0 means the host's path-hash space and the client's are
                // disjoint (likely runtime-spawn order differs, scene differs, or scene-name
                // prefix differs).
                line3 = "rx state seq=" + CoopState.RemoteStateSequence + " p1=" + cp1 + "  p2=" + cp2 +
                       "  ents=" + CoopState.RemoteEntityCount + "/" + EntitySync.CacheSize +
                       " hit=" + EntitySync.LastApplyHits + " miss=" + EntitySync.LastApplyMisses +
                       " spawn=" + EntitySync.LastSpawnedFromHost +
                       " off=" + EntitySync.LastDeactivated +
                       " types=" + TypeRegistry.ClientRegistryCount +
                       "  proj=" + CoopState.RemoteProjectileCount + " bound=" + ProjectileSync.LastBoundCount +
                       " unb=" + ProjectileSync.LastUnboundCandidates +
                       (CoopState.RemoteIsPaused ? "  [PAUSED]" : "");
            }

            // Line 4+: tail of recent BepInEx log events. Capped to LogTap.MaxLines.
            var tail = LogTap.GetSnapshot();

            const float pad = 8f;
            const float w = 640f;
            const float lineH = 16f;
            int header = 2 + (line3 != null ? 1 : 0);
            int totalLines = header + tail.Count;
            float h = totalLines * lineH + 8f;
            var rect = new Rect(pad, pad, w, h);
            GUI.Box(rect, GUIContent.none, _bgStyle);

            float y = rect.y + 2f;
            GUI.Label(new Rect(rect.x, y, w, lineH + 2), line1, _style); y += lineH;
            GUI.Label(new Rect(rect.x, y, w, lineH + 2), line2, _style); y += lineH;
            if (line3 != null) { GUI.Label(new Rect(rect.x, y, w, lineH + 2), line3, _style); y += lineH; }
            for (int i = 0; i < tail.Count; i++, y += lineH)
                GUI.Label(new Rect(rect.x, y, w, lineH + 2), tail[i], _style);
        }
    }

    /// <summary>
    /// Subscribes to BepInEx log events and keeps the last <see cref="MaxLines"/> messages in a
    /// ring buffer for the overlay to display. Initialised once from <see cref="Plugin.Awake"/>.
    /// </summary>
    internal static class LogTap
    {
        public const int MaxLines = 8;
        private static readonly Queue<string> _buf = new Queue<string>();
        private static readonly object _lock = new object();
        private static bool _wired;

        public static void Wire()
        {
            if (_wired) return;
            _wired = true;
            BepInEx.Logging.Logger.Listeners.Add(new TailListener());
        }

        public static List<string> GetSnapshot()
        {
            lock (_lock) return new List<string>(_buf);
        }

        private class TailListener : ILogListener
        {
            public LogLevel LogLevelFilter => LogLevel.All;
            public void LogEvent(object sender, LogEventArgs eventArgs)
            {
                // Only mirror our own plugin's lines into the in-game tail; BepInEx infra noise
                // (chainloader bootstrap, harmonyx warnings) belongs only in the disk log.
                var src = eventArgs.Source?.SourceName;
                if (src != "Cuphead Co-op") return;
                string line = "[" + eventArgs.Level.ToString().Substring(0, 1) + "] " + eventArgs.Data;
                lock (_lock)
                {
                    if (_buf.Count >= MaxLines) _buf.Dequeue();
                    _buf.Enqueue(line);
                }
            }
            public void Dispose() { }
        }
    }
}
