using System;
using BepInEx.Logging;
using CupheadCoop.Coop;
using LiteNetLib;
using LiteNetLib.Utils;

namespace CupheadCoop.Net
{
    /// <summary>
    /// Host-side networking. Listens on the configured port and accepts a single Player 2 client.
    /// Every received <see cref="PacketType.InputFrame"/> is forwarded into <see cref="CoopState"/>.
    /// </summary>
    internal class CoopHost : INetEventListener
    {
        private readonly ManualLogSource _log;
        private NetManager _net;
        private NetPeer _client;

        // M4 snapshot pacing
        private float _stateAccum;
        private uint _stateSeq;
        private readonly System.Diagnostics.Stopwatch _hostClock = System.Diagnostics.Stopwatch.StartNew();

        public bool Running => _net != null && _net.IsRunning;

        public CoopHost(ManualLogSource log) { _log = log; }

        public bool Start(int port, string connectKey)
        {
            _net = new NetManager(this);
            _net.UnconnectedMessagesEnabled = false;
            _net.UpdateTime = 15;
            if (!_net.Start(port))
            {
                _log.LogError("CoopHost: failed to bind UDP port " + port);
                _net = null;
                return false;
            }
            CoopState.Mode = CoopMode.Host;
            CoopState.HasRemoteInput = false;
            _log.LogInfo("CoopHost: listening on UDP " + port + " (key='" + connectKey + "')");
            return true;
        }

        public void Stop()
        {
            if (_net != null) { _net.Stop(); _net = null; }
            _client = null;
            CoopState.Reset();
            _log.LogInfo("CoopHost: stopped");
        }

        public void Pump()
        {
            if (_net != null) _net.PollEvents();
        }

        /// <summary>
        /// Called from <c>Plugin.LateUpdate</c> after <c>ScenePuppetry.HostCapture</c> has sampled
        /// live transforms. Sends an unreliable+sequenced StateSnapshot to the connected peer at
        /// the configured rate. No-ops if no peer is connected.
        /// </summary>
        public void TickStateSnapshot(float dt)
        {
            if (_client == null || _client.ConnectionState != ConnectionState.Connected) return;

            int rate = ModConfig.StateSendRateHz.Value;
            if (rate < 5) rate = 5;
            if (rate > 120) rate = 120;
            float interval = 1f / rate;

            _stateAccum += dt;
            if (_stateAccum < interval) return;
            _stateAccum = 0f;

            _stateSeq++;
            int entityCount;
            EntitySync.CaptureForHost(out entityCount);
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
                    IsDead = ScenePuppetry.LocalP1IsDead
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
                    IsDead = ScenePuppetry.LocalP2IsDead
                },
                IsPaused = PauseSync.LocalIsPaused,
                SceneName = SceneSync.LocalSceneName,
                EntityCount = (byte)entityCount,
                Entities = EntitySync.HostBuffer
            };

            var w = new NetDataWriter();
            snap.Write(w);
            _client.Send(w, DeliveryMethod.Sequenced);

            if (ModConfig.Verbose.Value)
                _log.LogDebug("tx state seq=" + _stateSeq + " p1=" + (snap.P1.Present ? snap.P1.X.ToString("F2") + "," + snap.P1.Y.ToString("F2") : "-") +
                              " p2=" + (snap.P2.Present ? snap.P2.X.ToString("F2") + "," + snap.P2.Y.ToString("F2") : "-"));
        }

        public void OnConnectionRequest(ConnectionRequest request)
        {
            _log.LogInfo("CoopHost: connection request from " + request.RemoteEndPoint);
            if (_client != null)
            {
                _log.LogWarning("CoopHost: rejecting (already have a client at " + _client.EndPoint + ")");
                request.Reject(Encode("server full"));
                return;
            }
            var peer = request.AcceptIfKey(ModConfig.ConnectKey.Value);
            if (peer == null)
                _log.LogWarning("CoopHost: rejected by AcceptIfKey (key mismatch?). Remote=" + request.RemoteEndPoint);
        }

        public void OnPeerConnected(NetPeer peer)
        {
            _client = peer;
            _log.LogInfo("CoopHost: client connected from " + peer.EndPoint);

            var w = new NetDataWriter();
            new WelcomePacket { Version = Protocol.Version, Accepted = true, Reason = "" }.Write(w);
            peer.Send(w, DeliveryMethod.ReliableOrdered);
        }

        public void OnPeerDisconnected(NetPeer peer, DisconnectInfo disconnectInfo)
        {
            _log.LogInfo("CoopHost: client disconnected (" + disconnectInfo.Reason + ")");
            if (peer == _client) _client = null;
            CoopState.HasRemoteInput = false;
            CoopState.CurrentButtons = 0;
            CoopState.AxisX = 0f;
            CoopState.AxisY = 0f;
        }

        public void OnNetworkError(System.Net.IPEndPoint endPoint, System.Net.Sockets.SocketError socketError)
        {
            _log.LogWarning("CoopHost: socket error from " + endPoint + ": " + socketError);
        }

        public void OnNetworkReceiveUnconnected(System.Net.IPEndPoint remoteEndPoint, NetPacketReader reader, UnconnectedMessageType messageType)
        {
            _log.LogInfo("CoopHost: unconnected packet from " + remoteEndPoint + " type=" + messageType + " len=" + reader.AvailableBytes);
            reader.Recycle();
        }

        public void OnNetworkReceive(NetPeer peer, NetPacketReader reader, DeliveryMethod deliveryMethod)
        {
            if (reader.AvailableBytes < 1) { reader.Recycle(); return; }
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
            reader.Recycle();
        }

        public void OnNetworkLatencyUpdate(NetPeer peer, int latency) { }

        private static byte[] Encode(string s)
        {
            var w = new NetDataWriter();
            w.Put(s);
            return w.CopyData();
        }
    }
}
