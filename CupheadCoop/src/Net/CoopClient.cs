using BepInEx.Logging;
using CupheadCoop.Coop;
using LiteNetLib.Utils;
using UnityEngine;

namespace CupheadCoop.Net
{
    /// <summary>
    /// Client-side protocol layer: connects to a host over whichever transport (UDP or Steam
    /// P2P) it was constructed with, streams local input up at <c>InputSendRateHz</c>, and
    /// applies host StateSnapshots into <see cref="CoopState"/>. Owns the reconnect policy —
    /// transports just report disconnects.
    /// </summary>
    internal class CoopClient
    {
        private readonly ManualLogSource _log;
        private readonly IClientTransport _transport;
        private float _accum;
        private uint _seq;

        // Reconnect state. Auto-retries every ReconnectIntervalSec when disconnected
        // unintentionally (network timeout, host closed, etc). User-initiated Stop sets
        // _userDisconnected so we don't fight against the user pressing F11/K.
        private const float ReconnectIntervalSec = 3f;
        private bool _userDisconnected;
        private float _reconnectTimer;
        public bool Reconnecting { get; private set; }
        public float SecondsUntilReconnect => Reconnecting ? Mathf.Max(0f, ReconnectIntervalSec - _reconnectTimer) : 0f;
        public int PingMs => _transport.PingMs;
        public string Describe => _transport.Describe;

        public bool Connected => _transport.Connected;
        public bool Running => _transport.Running;

        public CoopClient(ManualLogSource log, IClientTransport transport)
        {
            _log = log;
            _transport = transport;
            _transport.ConnectedEvent += OnConnected;
            _transport.Disconnected += OnDisconnected;
            _transport.Received += OnReceived;
        }

        public bool Start()
        {
            _userDisconnected = false;
            if (!_transport.Connect()) return false;
            CoopState.Mode = CoopMode.Client;
            Reconnecting = false;
            _reconnectTimer = 0f;
            return true;
        }

        public void Stop()
        {
            _userDisconnected = true;
            Reconnecting = false;
            _transport.Stop();
            CoopState.Reset();
            _log.LogInfo("CoopClient: stopped");
        }

        public void Pump(float dt)
        {
            _transport.Pump();

            if (Reconnecting)
            {
                _reconnectTimer += dt;
                if (_reconnectTimer >= ReconnectIntervalSec)
                {
                    _reconnectTimer = 0f;
                    if (_transport.Connect()) Reconnecting = false;
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
            _transport.Send(w, NetDelivery.Unreliable);

            if (ModConfig.Verbose.Value)
                _log.LogDebug("tx input seq=" + _seq + " btns=" + CoopState.LocalButtons.ToString("X"));
        }

        private void OnConnected()
        {
            _log.LogInfo("CoopClient: connected to host (" + _transport.Describe + ")");
        }

        private void OnDisconnected(string reason)
        {
            _log.LogWarning("CoopClient: disconnected (" + reason + ")");
            CoopState.HasRemoteInput = false;

            // Auto-reconnect unless the user pressed F11/K. Most disconnect reasons are
            // recoverable: timeout (host paused / lost wifi), host pressed F11, or the host
            // simply wasn't running yet — retry after they start.
            if (!_userDisconnected)
            {
                Reconnecting = true;
                _reconnectTimer = 0f;
                _log.LogInfo("CoopClient: will retry every " + ReconnectIntervalSec + "s until reconnected (press " +
                             ModConfig.KeyDisconnect.Value + " to cancel)");
            }
        }

        private void OnReceived(NetDataReader reader)
        {
            if (reader.AvailableBytes < 1) return;
            var type = (PacketType)reader.GetByte();
            switch (type)
            {
                case PacketType.Welcome:
                {
                    var w = WelcomePacket.Read(reader);
                    if (!w.Accepted)
                    {
                        _log.LogError("CoopClient: rejected by host: " + w.Reason);
                        Stop();
                    }
                    else if (w.Version != Protocol.Version)
                    {
                        _log.LogError("CoopClient: protocol version mismatch (host=" + w.Version +
                                      " self=" + Protocol.Version + "). Disconnecting — both PCs must run the same plugin build.");
                        Stop();
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
                    CoopState.ApplyRemoteState(s.Sequence, s.P1, s.P2, s.IsPaused, s.SceneName, s.Entities, s.EntityCount, s.LevelFlags);
                    CoopState.ApplyRemoteAliveHashes(s.AliveHashes, s.AliveHashCount);
                    CoopState.ApplyRemoteProjectiles(s.Projectiles, s.ProjectileCount);
                    // v1.2.0 wave 2: queue this snapshot's streamed SFX for replay. Done here (not via
                    // the interpolation buffer) so one-shots fire promptly and aren't tied to the
                    // render-in-the-past cursor. AudioSync.Tick drains + replays on the main thread.
                    AudioSync.EnqueueFromHost(s.SfxKinds, s.SfxKeys, s.SfxCount);
                    // v1.1.0: buffer this snapshot for client-side interpolation. The arrays inside
                    // it are freshly allocated by StateSnapshot.Read, so retaining them is safe.
                    SnapshotInterpolation.Push(ref s);
                    if (ModConfig.Verbose.Value)
                        _log.LogDebug("rx state seq=" + s.Sequence + " p1=" + (s.P1.Present ? s.P1.X.ToString("F2") + "," + s.P1.Y.ToString("F2") : "-") +
                                      " p2=" + (s.P2.Present ? s.P2.X.ToString("F2") + "," + s.P2.Y.ToString("F2") : "-") +
                                      " entities=" + s.EntityCount + " proj=" + s.ProjectileCount + " hp=" + s.P1.Hp + "/" + s.P2.Hp);
                    break;
                }
                default:
                    _log.LogWarning("CoopClient: unknown packet type " + type);
                    break;
            }
        }
    }
}
