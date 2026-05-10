using System;
using BepInEx.Logging;
using CupheadCoop.Coop;
using LiteNetLib;
using LiteNetLib.Utils;
using UnityEngine;

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

        // Reconnect state. Auto-retries every ReconnectIntervalSec when disconnected
        // unintentionally (network timeout, host closed, etc). User-initiated Stop sets
        // _userDisconnected so we don't fight against the user pressing F11/K.
        private const float ReconnectIntervalSec = 3f;
        private string _lastHost;
        private int _lastPort;
        private string _lastConnectKey;
        private bool _userDisconnected;
        private float _reconnectTimer;
        public bool Reconnecting { get; private set; }
        public float SecondsUntilReconnect => Reconnecting ? Mathf.Max(0f, ReconnectIntervalSec - _reconnectTimer) : 0f;
        public int PingMs { get; private set; }

        public bool Connected => _peer != null && _peer.ConnectionState == ConnectionState.Connected;
        public bool Running => _net != null && _net.IsRunning;

        public CoopClient(ManualLogSource log) { _log = log; }

        public bool Start(string host, int port, string connectKey)
        {
            _lastHost = host; _lastPort = port; _lastConnectKey = connectKey;
            _userDisconnected = false;
            return InternalConnect(initial: true);
        }

        private bool InternalConnect(bool initial)
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
                _log.LogInfo("CoopClient: " + (initial ? "dialing" : "retrying") + " " + _lastHost + ":" + _lastPort);
                _peer = _net.Connect(_lastHost, _lastPort, _lastConnectKey);
                CoopState.Mode = CoopMode.Client;
                Reconnecting = false;
                _reconnectTimer = 0f;
                return true;
            }
            catch (Exception ex)
            {
                _log.LogError("CoopClient: Connect failed: " + ex.GetType().Name + ": " + ex.Message);
                _peer = null;
                if (initial)
                {
                    if (_net != null) { try { _net.Stop(); } catch { } _net = null; }
                    CoopState.Reset();
                }
                return false;
            }
        }

        public void Stop()
        {
            _userDisconnected = true;
            Reconnecting = false;
            if (_net != null) { _net.Stop(); _net = null; }
            _peer = null;
            CoopState.Reset();
            _log.LogInfo("CoopClient: stopped");
        }

        public void Pump(float dt)
        {
            if (_net == null) return;
            _net.PollEvents();

            if (Reconnecting)
            {
                _reconnectTimer += dt;
                if (_reconnectTimer >= ReconnectIntervalSec)
                {
                    _reconnectTimer = 0f;
                    InternalConnect(initial: false);
                }
                return;
            }

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
            CoopState.HasRemoteInput = false;

            // Auto-reconnect unless the user pressed F11/K. Most disconnect reasons are
            // recoverable: Timeout (host paused / lost wifi), RemoteConnectionClose (host
            // pressed F11), ConnectionFailed (host wasn't running yet — retry after host
            // F9s).
            if (!_userDisconnected && _net != null)
            {
                Reconnecting = true;
                _reconnectTimer = 0f;
                _log.LogInfo("CoopClient: will retry every " + ReconnectIntervalSec + "s until reconnected (press " +
                             ModConfig.KeyDisconnect.Value + " to cancel)");
            }
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
                        // Auto-join P2 on the local sim too. Without this, client's local
                        // PlayerManager only has P1; there's no P2 GameObject to be the
                        // target of host's streamed P2 transform, so the second cup is
                        // invisible on client. Force-joining puts a P2 avatar in the scene.
                        P2AutoJoin.Trigger();
                    }
                    break;
                }
                case PacketType.StateSnapshot:
                {
                    var s = StateSnapshot.Read(reader);
                    CoopState.ApplyRemoteState(s.Sequence, s.P1, s.P2, s.IsPaused, s.SceneName, s.Entities, s.EntityCount);
                    CoopState.ApplyRemoteAliveHashes(s.AliveHashes, s.AliveHashCount);
                    if (ModConfig.Verbose.Value)
                        _log.LogDebug("rx state seq=" + s.Sequence + " p1=" + (s.P1.Present ? s.P1.X.ToString("F2") + "," + s.P1.Y.ToString("F2") : "-") +
                                      " p2=" + (s.P2.Present ? s.P2.X.ToString("F2") + "," + s.P2.Y.ToString("F2") : "-") +
                                      " entities=" + s.EntityCount + " hp=" + s.P1.Hp + "/" + s.P2.Hp);
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

        public void OnNetworkLatencyUpdate(NetPeer peer, int latency) { PingMs = latency; }

        public void OnConnectionRequest(ConnectionRequest request)
        {
            request.Reject();
        }
    }
}
