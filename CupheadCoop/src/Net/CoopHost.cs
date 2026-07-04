using BepInEx.Logging;
using CupheadCoop.Coop;
using LiteNetLib.Utils;

namespace CupheadCoop.Net
{
    /// <summary>
    /// Host-side protocol layer: accepts a single Player 2 client over whichever transport
    /// (UDP or Steam P2P) it was constructed with, forwards received InputFrames into
    /// <see cref="CoopState"/>, and streams StateSnapshots back at the configured rate.
    /// </summary>
    internal class CoopHost
    {
        private readonly ManualLogSource _log;
        private readonly IHostTransport _transport;

        // M4 snapshot pacing
        private float _stateAccum;
        private uint _stateSeq;
        private readonly System.Diagnostics.Stopwatch _hostClock = System.Diagnostics.Stopwatch.StartNew();

        public bool Running => _transport.Running;
        public bool HasClient => _transport.HasClient;
        public int PingMs => _transport.PingMs;
        public string Describe => _transport.Describe;

        public CoopHost(ManualLogSource log, IHostTransport transport)
        {
            _log = log;
            _transport = transport;
            _transport.ClientConnected += OnClientConnected;
            _transport.ClientDisconnected += OnClientDisconnected;
            _transport.Received += OnReceived;
        }

        public bool Start()
        {
            if (!_transport.Start()) return false;
            CoopState.Mode = CoopMode.Host;
            CoopState.HasRemoteInput = false;
            return true;
        }

        public void Stop()
        {
            _transport.Stop();
            CoopState.Reset();
            _log.LogInfo("CoopHost: stopped");
        }

        public void Pump() => _transport.Pump();

        /// <summary>
        /// Called from <c>Plugin.LateUpdate</c> after <c>ScenePuppetry.HostCapture</c> has sampled
        /// live transforms. Sends a latest-wins StateSnapshot to the connected peer at the
        /// configured rate. No-ops if no peer is connected.
        /// </summary>
        public void TickStateSnapshot(float dt)
        {
            if (!_transport.HasClient) return;

            int rate = ModConfig.StateSendRateHz.Value;
            if (rate < 5) rate = 5;
            if (rate > 120) rate = 120;
            float interval = 1f / rate;

            _stateAccum += dt;
            if (_stateAccum < interval) return;
            // Subtract instead of resetting to zero — the reset discarded the overshoot,
            // which at 60 fps quantized "30 Hz" down to an effective 20 Hz (every 3rd frame).
            // Cap the carried remainder at one interval so a long hitch can't cause a burst.
            _stateAccum -= interval;
            if (_stateAccum > interval) _stateAccum = interval;

            _stateSeq++;
            int entityCount;
            EntitySync.CaptureForHost(out entityCount);
            int projectileCount;
            ProjectileSync.CaptureForHost(out projectileCount);
            var snap = new StateSnapshot
            {
                Sequence = _stateSeq,
                HostTickMs = (ushort)(_hostClock.ElapsedMilliseconds & 0xFFFF),
                P1 = new PlayerSnapshot
                {
                    Present = ScenePuppetry.LocalP1Present,
                    X = ScenePuppetry.LocalP1X,
                    Y = ScenePuppetry.LocalP1Y,
                    Facing = ScenePuppetry.LocalP1Facing,
                    AnimStateHash = ScenePuppetry.LocalP1AnimHash,
                    AnimNormalizedTime = ScenePuppetry.LocalP1AnimTime,
                    Hp = ScenePuppetry.LocalP1Hp,
                    IsDead = ScenePuppetry.LocalP1IsDead,
                    Buttons = ScenePuppetry.LocalP1Buttons,
                    AxisX_q = ScenePuppetry.LocalP1AxisX_q,
                    AxisY_q = ScenePuppetry.LocalP1AxisY_q
                },
                P2 = new PlayerSnapshot
                {
                    Present = ScenePuppetry.LocalP2Present,
                    X = ScenePuppetry.LocalP2X,
                    Y = ScenePuppetry.LocalP2Y,
                    Facing = ScenePuppetry.LocalP2Facing,
                    AnimStateHash = ScenePuppetry.LocalP2AnimHash,
                    AnimNormalizedTime = ScenePuppetry.LocalP2AnimTime,
                    Hp = ScenePuppetry.LocalP2Hp,
                    IsDead = ScenePuppetry.LocalP2IsDead,
                    Buttons = ScenePuppetry.LocalP2Buttons,
                    AxisX_q = ScenePuppetry.LocalP2AxisX_q,
                    AxisY_q = ScenePuppetry.LocalP2AxisY_q
                },
                IsPaused = PauseSync.LocalIsPaused,
                SceneName = SceneSync.LocalSceneName,
                EntityCount = (byte)entityCount,
                Entities = EntitySync.HostBuffer,
                AliveHashCount = (ushort)EntitySync.AliveHashesCount,
                AliveHashes = EntitySync.AliveHashesBuffer,
                ProjectileCount = (byte)projectileCount,
                Projectiles = ProjectileSync.HostBuffer
            };

            var w = new NetDataWriter();
            snap.Write(w);
            _transport.Send(w, NetDelivery.Sequenced);

            if (ModConfig.Verbose.Value)
                _log.LogDebug("tx state seq=" + _stateSeq + " p1=" + (snap.P1.Present ? snap.P1.X.ToString("F2") + "," + snap.P1.Y.ToString("F2") : "-") +
                              " p2=" + (snap.P2.Present ? snap.P2.X.ToString("F2") + "," + snap.P2.Y.ToString("F2") : "-"));
        }

        private void OnClientConnected()
        {
            var w = new NetDataWriter();
            new WelcomePacket { Version = Protocol.Version, Accepted = true, Reason = "" }.Write(w);
            _transport.Send(w, NetDelivery.Reliable);

            // Auto-join P2 on host so the client's input can drive a real cup. Without this,
            // host's P2 doesn't exist (no controller plugged in on host's PC) and our network
            // input has no recipient. Deferred to next main-thread tick — see P2AutoJoin.
            P2AutoJoin.Trigger();
        }

        private void OnClientDisconnected(string reason)
        {
            _log.LogInfo("CoopHost: client disconnected (" + reason + ")");
            CoopState.HasRemoteInput = false;
            CoopState.CurrentButtons = 0;
            CoopState.AxisX = 0f;
            CoopState.AxisY = 0f;
        }

        private void OnReceived(NetDataReader reader)
        {
            if (reader.AvailableBytes < 1) return;
            var type = (PacketType)reader.GetByte();
            switch (type)
            {
                case PacketType.InputFrame:
                {
                    var f = InputFrame.Read(reader);
                    CoopState.ApplyRemoteFrame(f.Sequence, f.Buttons, f.UnpackAxisX, f.UnpackAxisY);
                    if (ModConfig.Verbose.Value)
                        _log.LogDebug("rx input seq=" + f.Sequence + " btns=" + f.Buttons.ToString("X") + " ax=" + f.UnpackAxisX + " ay=" + f.UnpackAxisY);
                    break;
                }
                case PacketType.Heartbeat:
                    break;
                default:
                    _log.LogWarning("CoopHost: unknown packet type " + type);
                    break;
            }
        }
    }
}
