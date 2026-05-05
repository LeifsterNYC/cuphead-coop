using System;
using BepInEx.Logging;
using CupheadCoop.Coop;
using LiteNetLib;
using LiteNetLib.Utils;

namespace CupheadCoop.Net
{
    /// <summary>
    /// Client-side networking. Connects to a host and streams local input snapshots up at
    /// <c>InputSendRateHz</c> Hz. Does not receive game state in M3 — host runs the simulation.
    /// </summary>
    internal class CoopClient : INetEventListener
    {
        private readonly ManualLogSource _log;
        private NetManager _net;
        private NetPeer _peer;
        private float _accum;
        private uint _seq;

        public bool Connected => _peer != null && _peer.ConnectionState == ConnectionState.Connected;
        public bool Running => _net != null && _net.IsRunning;

        public CoopClient(ManualLogSource log) { _log = log; }

        public bool Start(string host, int port, string connectKey)
        {
            _net = new NetManager(this);
            _net.UnconnectedMessagesEnabled = false;
            _net.UpdateTime = 15;
            if (!_net.Start())
            {
                _log.LogError("CoopClient: failed to start NetManager");
                _net = null;
                return false;
            }
            _peer = _net.Connect(host, port, connectKey);
            CoopState.Mode = CoopMode.Client;
            _log.LogInfo("CoopClient: dialing " + host + ":" + port);
            return true;
        }

        public void Stop()
        {
            if (_net != null) { _net.Stop(); _net = null; }
            _peer = null;
            CoopState.Reset();
            _log.LogInfo("CoopClient: stopped");
        }

        public void Pump(float dt)
        {
            if (_net == null) return;
            _net.PollEvents();

            if (!Connected) return;

            int rate = ModConfig.InputSendRateHz.Value;
            if (rate < 10) rate = 10;
            if (rate > 240) rate = 240;
            float interval = 1f / rate;

            _accum += dt;
            while (_accum >= interval)
            {
                _accum -= interval;
                SendInputFrame();
            }
        }

        private void SendInputFrame()
        {
            _seq++;
            CoopState.LocalSequence = _seq;

            var f = InputFrame.Pack(_seq, CoopState.LocalButtons, CoopState.LocalAxisX, CoopState.LocalAxisY);
            var w = new NetDataWriter();
            f.Write(w);
            _peer.Send(w, DeliveryMethod.Unreliable);

            if (ModConfig.Verbose.Value)
                _log.LogDebug("tx input seq=" + _seq + " btns=" + CoopState.LocalButtons.ToString("X"));
        }

        public void OnPeerConnected(NetPeer peer)
        {
            _log.LogInfo("CoopClient: connected to host " + peer.EndPoint);
        }

        public void OnPeerDisconnected(NetPeer peer, DisconnectInfo disconnectInfo)
        {
            _log.LogWarning("CoopClient: disconnected (" + disconnectInfo.Reason + ")");
            _peer = null;
        }

        public void OnNetworkError(System.Net.IPEndPoint endPoint, System.Net.Sockets.SocketError socketError)
        {
            _log.LogWarning("CoopClient: socket error from " + endPoint + ": " + socketError);
        }

        public void OnNetworkReceive(NetPeer peer, NetPacketReader reader, DeliveryMethod deliveryMethod)
        {
            if (reader.AvailableBytes < 1) { reader.Recycle(); return; }
            var type = (PacketType)reader.GetByte();
            switch (type)
            {
                case PacketType.Welcome:
                {
                    var w = WelcomePacket.Read(reader);
                    if (!w.Accepted)
                    {
                        _log.LogError("CoopClient: rejected by host: " + w.Reason);
                        peer.Disconnect();
                    }
                    else if (w.Version != Protocol.Version)
                    {
                        _log.LogError("CoopClient: protocol version mismatch (host=" + w.Version +
                                      " self=" + Protocol.Version + "). Disconnecting — both PCs must run the same plugin build.");
                        peer.Disconnect();
                    }
                    else
                    {
                        _log.LogInfo("CoopClient: handshake ok (v" + w.Version + ")");
                    }
                    break;
                }
                default:
                    _log.LogWarning("CoopClient: unknown packet type " + type);
                    break;
            }
            reader.Recycle();
        }

        public void OnNetworkReceiveUnconnected(System.Net.IPEndPoint remoteEndPoint, NetPacketReader reader, UnconnectedMessageType messageType)
        {
            reader.Recycle();
        }

        public void OnNetworkLatencyUpdate(NetPeer peer, int latency) { }

        public void OnConnectionRequest(ConnectionRequest request)
        {
            request.Reject();
        }
    }
}
