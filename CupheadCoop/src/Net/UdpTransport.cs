using System;
using BepInEx.Logging;
using LiteNetLib;
using LiteNetLib.Utils;

namespace CupheadCoop.Net
{
    /// <summary>
    /// LiteNetLib UDP transport — the original v0.x wire. Kept alongside the Steam transport
    /// because (a) solo two-instance testing on one PC can't use Steam P2P (one account can't
    /// dial itself), (b) the MockClient tool speaks it, (c) it's a fallback if Steam relay
    /// behaves badly on some network.
    /// </summary>
    internal sealed class UdpHostTransport : IHostTransport, INetEventListener
    {
        private readonly ManualLogSource _log;
        private readonly int _port;
        private readonly string _connectKey;
        private NetManager _net;
        private NetPeer _client;

        public bool Running => _net != null && _net.IsRunning;
        public bool HasClient => _client != null && _client.ConnectionState == ConnectionState.Connected;
        public int PingMs { get; private set; }
        public string Describe => "UDP :" + _port;

        public event Action ClientConnected;
        public event Action<string> ClientDisconnected;
        public event Action<NetDataReader> Received;

        public UdpHostTransport(ManualLogSource log, int port, string connectKey)
        {
            _log = log;
            _port = port;
            _connectKey = connectKey;
        }

        public bool Start()
        {
            _net = new NetManager(this);
            _net.UnconnectedMessagesEnabled = false;
            _net.UpdateTime = 15;
            if (!_net.Start(_port))
            {
                _log.LogError("CoopHost: failed to bind UDP port " + _port);
                _net = null;
                return false;
            }
            _log.LogInfo("CoopHost: listening on UDP " + _port + " (key='" + _connectKey + "')");
            return true;
        }

        public void Stop()
        {
            if (_net != null) { _net.Stop(); _net = null; }
            _client = null;
        }

        public void Pump()
        {
            if (_net != null) _net.PollEvents();
        }

        public void Send(NetDataWriter payload, NetDelivery delivery)
        {
            if (!HasClient) return;
            _client.Send(payload, Map(delivery));
        }

        private static DeliveryMethod Map(NetDelivery d)
        {
            switch (d)
            {
                case NetDelivery.Reliable: return DeliveryMethod.ReliableOrdered;
                case NetDelivery.Sequenced: return DeliveryMethod.Sequenced;
                default: return DeliveryMethod.Unreliable;
            }
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
            var peer = request.AcceptIfKey(_connectKey);
            if (peer == null)
                _log.LogWarning("CoopHost: rejected by AcceptIfKey (key mismatch?). Remote=" + request.RemoteEndPoint);
        }

        public void OnPeerConnected(NetPeer peer)
        {
            _client = peer;
            _log.LogInfo("CoopHost: client connected from " + peer.EndPoint);
            ClientConnected?.Invoke();
        }

        public void OnPeerDisconnected(NetPeer peer, DisconnectInfo disconnectInfo)
        {
            if (peer == _client) _client = null;
            ClientDisconnected?.Invoke(disconnectInfo.Reason.ToString());
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
            Received?.Invoke(reader);
            reader.Recycle();
        }

        public void OnNetworkLatencyUpdate(NetPeer peer, int latency) { PingMs = latency; }

        private static byte[] Encode(string s)
        {
            var w = new NetDataWriter();
            w.Put(s);
            return w.CopyData();
        }
    }

    internal sealed class UdpClientTransport : IClientTransport, INetEventListener
    {
        private readonly ManualLogSource _log;
        private readonly string _host;
        private readonly int _port;
        private readonly string _connectKey;
        private NetManager _net;
        private NetPeer _peer;
        private bool _dialedOnce;

        public bool Running => _net != null && _net.IsRunning;
        public bool Connected => _peer != null && _peer.ConnectionState == ConnectionState.Connected;
        public int PingMs { get; private set; }
        public string Describe => "UDP " + _host + ":" + _port;

        public event Action ConnectedEvent;
        public event Action<string> Disconnected;
        public event Action<NetDataReader> Received;

        public UdpClientTransport(ManualLogSource log, string host, int port, string connectKey)
        {
            _log = log;
            _host = host;
            _port = port;
            _connectKey = connectKey;
        }

        public bool Connect()
        {
            try
            {
                if (_net == null)
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
                }
                _log.LogInfo("CoopClient: " + (_dialedOnce ? "retrying" : "dialing") + " " + _host + ":" + _port);
                _dialedOnce = true;
                _peer = _net.Connect(_host, _port, _connectKey);
                return true;
            }
            catch (Exception ex)
            {
                _log.LogError("CoopClient: Connect failed: " + ex.GetType().Name + ": " + ex.Message);
                _peer = null;
                return false;
            }
        }

        public void Stop()
        {
            if (_net != null) { _net.Stop(); _net = null; }
            _peer = null;
        }

        public void Pump()
        {
            if (_net != null) _net.PollEvents();
        }

        public void Send(NetDataWriter payload, NetDelivery delivery)
        {
            if (!Connected) return;
            switch (delivery)
            {
                case NetDelivery.Reliable: _peer.Send(payload, DeliveryMethod.ReliableOrdered); break;
                case NetDelivery.Sequenced: _peer.Send(payload, DeliveryMethod.Sequenced); break;
                default: _peer.Send(payload, DeliveryMethod.Unreliable); break;
            }
        }

        public void OnPeerConnected(NetPeer peer)
        {
            _log.LogInfo("CoopClient: connected to host " + peer.EndPoint);
            ConnectedEvent?.Invoke();
        }

        public void OnPeerDisconnected(NetPeer peer, DisconnectInfo disconnectInfo)
        {
            _peer = null;
            Disconnected?.Invoke(disconnectInfo.Reason.ToString());
        }

        public void OnNetworkError(System.Net.IPEndPoint endPoint, System.Net.Sockets.SocketError socketError)
        {
            _log.LogWarning("CoopClient: socket error from " + endPoint + ": " + socketError);
        }

        public void OnNetworkReceive(NetPeer peer, NetPacketReader reader, DeliveryMethod deliveryMethod)
        {
            Received?.Invoke(reader);
            reader.Recycle();
        }

        public void OnNetworkReceiveUnconnected(System.Net.IPEndPoint remoteEndPoint, NetPacketReader reader, UnconnectedMessageType messageType)
        {
            reader.Recycle();
        }

        public void OnNetworkLatencyUpdate(NetPeer peer, int latency) { PingMs = latency; }

        public void OnConnectionRequest(ConnectionRequest request)
        {
            request.Reject();
        }
    }
}
