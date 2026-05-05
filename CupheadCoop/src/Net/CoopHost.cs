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

        public void OnConnectionRequest(ConnectionRequest request)
        {
            if (_client != null)
            {
                request.Reject(Encode("server full"));
                return;
            }
            request.AcceptIfKey(ModConfig.ConnectKey.Value);
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

        public void OnNetworkReceiveUnconnected(System.Net.IPEndPoint remoteEndPoint, NetPacketReader reader, UnconnectedMessageType messageType)
        {
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
