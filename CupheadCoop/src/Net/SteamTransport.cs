using System;
using System.Reflection;
using BepInEx.Logging;
using LiteNetLib.Utils;
using Steamworks;

namespace CupheadCoop.Net
{
    /// <summary>
    /// Steam P2P transport over Cuphead's own bundled Steamworks.NET (Assembly-CSharp-firstpass).
    /// Why: SteamNetworking gives NAT traversal for free (direct connect, falls back to Valve's
    /// relay) — no ZeroTier / port forwarding / IP exchange. The client dials the host's SteamID64.
    ///
    /// Steam P2P is connectionless, so this layer synthesizes a session: a 1-byte opcode header
    /// wraps every packet (HELLO carries the connect key, PING/PONG measure RTT and act as
    /// keepalive, DATA/DATA_SEQ carry the app protocol; DATA_SEQ adds a ushort latest-wins
    /// sequence because Steam has no native sequenced-unreliable mode).
    ///
    /// The game's own SteamManager calls SteamAPI.Init()/RunCallbacks(), so our Callback&lt;T&gt;
    /// registrations dispatch on the main thread with zero extra plumbing.
    /// </summary>
    internal static class SteamOp
    {
        public const byte Hello = 1;    // client -> host: [op][string connectKey]
        public const byte Bye = 2;      // either dir:    [op][string reason]
        public const byte Ping = 3;     // either dir:    [op][int senderTickMs]
        public const byte Pong = 4;     // either dir:    [op][int echoedTickMs]
        public const byte Data = 0x10;  // either dir:    [op][app payload]
        public const byte DataSeq = 0x11; // either dir:  [op][ushort seq][app payload]
    }

    internal static class SteamTransportUtil
    {
        // Steam caps unreliable P2P messages at 1200 bytes. Anything bigger goes reliable
        // (Steam fragments/reassembles reliable messages up to 1 MB). Header is <= 3 bytes.
        public const int UnreliableMax = 1150;
        public const int PingIntervalMs = 1000;
        public const int TimeoutMs = 8000;
        public const int HelloRetryMs = 2000;
        public const int DialTimeoutMs = 15000;

        private static bool _checked;
        private static bool _ready;

        /// <summary>
        /// True once the game's SteamManager reports SteamAPI.Init() succeeded. SteamManager is
        /// internal to Assembly-CSharp, hence reflection. Reading Initialized also lazily creates
        /// the SteamManager GameObject if the game somehow hasn't yet.
        /// </summary>
        public static bool SteamReady(ManualLogSource log)
        {
            if (_checked) return _ready;
            _checked = true;
            try
            {
                var t = Type.GetType("SteamManager, Assembly-CSharp");
                if (t == null)
                {
                    log.LogError("SteamTransport: SteamManager type not found in Assembly-CSharp");
                    return false;
                }
                var p = t.GetProperty("Initialized", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                _ready = p != null && (bool)p.GetValue(null, null);
                if (!_ready)
                    log.LogError("SteamTransport: SteamAPI not initialized (game not launched through Steam?)");
                return _ready;
            }
            catch (Exception ex)
            {
                log.LogError("SteamTransport: Steam readiness check failed: " + ex);
                return false;
            }
        }

        /// <summary>Allows re-probing after a failed check (e.g. Steam started late).</summary>
        public static void InvalidateReadyCache() { _checked = false; }

        public static void Send(CSteamID to, NetDataWriter framed, NetDelivery delivery)
        {
            EP2PSend mode;
            switch (delivery)
            {
                case NetDelivery.Reliable:
                    mode = EP2PSend.k_EP2PSendReliable;
                    break;
                case NetDelivery.Sequenced:
                    // Latest-wins wants unreliable, but Steam drops unreliable messages over
                    // ~1200 bytes outright. Big snapshots (entity-heavy scenes) ride reliable;
                    // the DATA_SEQ header still lets the receiver drop stale ones.
                    mode = framed.Length > UnreliableMax ? EP2PSend.k_EP2PSendReliable : EP2PSend.k_EP2PSendUnreliableNoDelay;
                    break;
                default:
                    mode = EP2PSend.k_EP2PSendUnreliableNoDelay;
                    break;
            }
            SteamNetworking.SendP2PPacket(to, framed.Data, (uint)framed.Length, mode, 0);
        }

        public static NetDataWriter Frame(NetDataWriter payload, NetDelivery delivery, ref ushort txSeq)
        {
            var w = new NetDataWriter();
            if (delivery == NetDelivery.Sequenced)
            {
                w.Put(SteamOp.DataSeq);
                w.Put(++txSeq);
            }
            else
            {
                w.Put(SteamOp.Data);
            }
            w.Put(payload.Data, 0, payload.Length);
            return w;
        }

        /// <summary>Wraparound-safe "is a newer than b" for ushort sequence numbers.</summary>
        public static bool SeqNewer(ushort a, ushort b) => (short)(a - b) > 0;
    }

    internal sealed class SteamHostTransport : IHostTransport
    {
        private readonly ManualLogSource _log;
        private readonly string _connectKey;
        private Callback<P2PSessionRequest_t> _cbSessionRequest;
        private Callback<P2PSessionConnectFail_t> _cbConnectFail;
        private CSteamID _peer;
        private bool _running;
        private bool _hasClient;
        private ushort _txSeq;
        private ushort _rxSeq;
        private int _lastRxMs;
        private int _lastPingMs;
        private byte[] _buf = new byte[4096];

        public bool Running => _running;
        public bool HasClient => _hasClient;
        public int PingMs { get; private set; }
        public string Describe => "Steam id " + OwnId();

        public event Action ClientConnected;
        public event Action<string> ClientDisconnected;
        public event Action<NetDataReader> Received;

        public SteamHostTransport(ManualLogSource log, string connectKey)
        {
            _log = log;
            _connectKey = connectKey;
        }

        public static ulong OwnId()
        {
            try { return SteamUser.GetSteamID().m_SteamID; } catch { return 0; }
        }

        public bool Start()
        {
            SteamTransportUtil.InvalidateReadyCache();
            if (!SteamTransportUtil.SteamReady(_log)) return false;
            SteamNetworking.AllowP2PPacketRelay(true);
            _cbSessionRequest = Callback<P2PSessionRequest_t>.Create(OnSessionRequest);
            _cbConnectFail = Callback<P2PSessionConnectFail_t>.Create(OnConnectFail);
            _running = true;
            _hasClient = false;
            _log.LogInfo("CoopHost: Steam P2P listening. Your SteamID64 = " + OwnId() +
                         " — the second player puts this in [Network] HostSteamId and presses Connect.");
            return true;
        }

        public void Stop()
        {
            if (_hasClient) SendControlString(SteamOp.Bye, "host stopped");
            DropPeer();
            if (_cbSessionRequest != null) { _cbSessionRequest.Unregister(); _cbSessionRequest = null; }
            if (_cbConnectFail != null) { _cbConnectFail.Unregister(); _cbConnectFail = null; }
            _running = false;
        }

        public void Pump()
        {
            if (!_running) return;
            uint size;
            while (SteamNetworking.IsP2PPacketAvailable(out size, 0))
            {
                if (size > _buf.Length) _buf = new byte[Math.Max((int)size, _buf.Length * 2)];
                uint read;
                CSteamID remote;
                if (!SteamNetworking.ReadP2PPacket(_buf, (uint)_buf.Length, out read, out remote, 0)) break;
                HandlePacket(remote, (int)read);
                if (!_running) return; // a handler may have called Stop()
            }
            TickKeepalive();
        }

        public void Send(NetDataWriter payload, NetDelivery delivery)
        {
            if (!_hasClient) return;
            var framed = SteamTransportUtil.Frame(payload, delivery, ref _txSeq);
            SteamTransportUtil.Send(_peer, framed, delivery);
        }

        private void OnSessionRequest(P2PSessionRequest_t req)
        {
            if (!_running) return;
            // Accept anyone while unclaimed — the connect-key gate happens at HELLO, and we
            // can't read their HELLO without accepting the session first.
            if (!_hasClient || req.m_steamIDRemote == _peer)
            {
                _log.LogInfo("CoopHost: P2P session request from " + req.m_steamIDRemote.m_SteamID + " — accepting");
                SteamNetworking.AcceptP2PSessionWithUser(req.m_steamIDRemote);
            }
            else
            {
                _log.LogWarning("CoopHost: P2P session request from " + req.m_steamIDRemote.m_SteamID + " ignored (already have a client)");
            }
        }

        private void OnConnectFail(P2PSessionConnectFail_t fail)
        {
            if (_hasClient && fail.m_steamIDRemote == _peer)
            {
                _log.LogWarning("CoopHost: P2P session to client failed (err=" + fail.m_eP2PSessionError + ")");
                var reason = "steam p2p error " + fail.m_eP2PSessionError;
                DropPeer();
                ClientDisconnected?.Invoke(reason);
            }
        }

        private void HandlePacket(CSteamID remote, int len)
        {
            if (len < 1) return;
            var r = new NetDataReader(_buf, 0, len);
            byte op = r.GetByte();

            if (op == SteamOp.Hello)
            {
                string key;
                try { key = r.GetString(); } catch { return; }
                if (_hasClient && remote != _peer)
                {
                    SendControlStringTo(remote, SteamOp.Bye, "server full");
                    SteamNetworking.CloseP2PSessionWithUser(remote);
                    return;
                }
                if (key != _connectKey)
                {
                    _log.LogWarning("CoopHost: HELLO from " + remote.m_SteamID + " with wrong connect key — rejecting");
                    SendControlStringTo(remote, SteamOp.Bye, "key mismatch");
                    SteamNetworking.CloseP2PSessionWithUser(remote);
                    return;
                }
                _lastRxMs = Environment.TickCount;
                if (_hasClient) return; // duplicate HELLO while our Welcome is in flight — ignore
                _peer = remote;
                _hasClient = true;
                _rxSeq = 0;
                _log.LogInfo("CoopHost: client connected via Steam (id " + remote.m_SteamID + ")");
                ClientConnected?.Invoke();
                return;
            }

            if (!_hasClient || remote != _peer) return;
            _lastRxMs = Environment.TickCount;

            switch (op)
            {
                case SteamOp.Ping:
                {
                    int stamp = r.GetInt();
                    var w = new NetDataWriter();
                    w.Put(SteamOp.Pong);
                    w.Put(stamp);
                    SteamTransportUtil.Send(_peer, w, NetDelivery.Unreliable);
                    break;
                }
                case SteamOp.Pong:
                    PingMs = Math.Max(0, Environment.TickCount - r.GetInt());
                    break;
                case SteamOp.Bye:
                {
                    string reason;
                    try { reason = r.GetString(); } catch { reason = "bye"; }
                    DropPeer();
                    ClientDisconnected?.Invoke(reason);
                    break;
                }
                case SteamOp.Data:
                    Received?.Invoke(r);
                    break;
                case SteamOp.DataSeq:
                {
                    ushort seq = r.GetUShort();
                    if (_rxSeq != 0 && !SteamTransportUtil.SeqNewer(seq, _rxSeq)) break;
                    _rxSeq = seq;
                    Received?.Invoke(r);
                    break;
                }
            }
        }

        private void TickKeepalive()
        {
            if (!_hasClient) return;
            int now = Environment.TickCount;
            if (now - _lastPingMs >= SteamTransportUtil.PingIntervalMs)
            {
                _lastPingMs = now;
                var w = new NetDataWriter();
                w.Put(SteamOp.Ping);
                w.Put(now);
                SteamTransportUtil.Send(_peer, w, NetDelivery.Unreliable);
            }
            if (now - _lastRxMs > SteamTransportUtil.TimeoutMs)
            {
                _log.LogWarning("CoopHost: client timed out");
                DropPeer();
                ClientDisconnected?.Invoke("timeout");
            }
        }

        private void SendControlString(byte op, string s) => SendControlStringTo(_peer, op, s);

        private static void SendControlStringTo(CSteamID to, byte op, string s)
        {
            var w = new NetDataWriter();
            w.Put(op);
            w.Put(s);
            SteamTransportUtil.Send(to, w, NetDelivery.Reliable);
        }

        private void DropPeer()
        {
            if (_hasClient) SteamNetworking.CloseP2PSessionWithUser(_peer);
            _hasClient = false;
            _peer = default(CSteamID);
        }
    }

    internal sealed class SteamClientTransport : IClientTransport
    {
        private readonly ManualLogSource _log;
        private readonly string _connectKey;
        private readonly string _hostIdRaw;
        private Callback<P2PSessionConnectFail_t> _cbConnectFail;
        private CSteamID _host;
        private bool _running;
        private bool _connected;
        private bool _dialing;
        private int _dialStartMs;
        private int _lastHelloMs;
        private int _lastRxMs;
        private int _lastPingMs;
        private ushort _txSeq;
        private ushort _rxSeq;
        private byte[] _buf = new byte[4096];

        public bool Running => _running;
        public bool Connected => _connected;
        public int PingMs { get; private set; }
        public string Describe => "Steam -> " + _hostIdRaw;

        public event Action ConnectedEvent;
        public event Action<string> Disconnected;
        public event Action<NetDataReader> Received;

        public SteamClientTransport(ManualLogSource log, string hostSteamId64, string connectKey)
        {
            _log = log;
            _hostIdRaw = (hostSteamId64 ?? "").Trim();
            _connectKey = connectKey;
        }

        public bool Connect()
        {
            SteamTransportUtil.InvalidateReadyCache();
            if (!SteamTransportUtil.SteamReady(_log)) return false;

            ulong id;
            if (!ulong.TryParse(_hostIdRaw, out id) || id == 0)
            {
                _log.LogError("CoopClient: [Network] HostSteamId is not a valid SteamID64 (got '" + _hostIdRaw +
                              "'). The host's ID is printed in their log/overlay when they start hosting.");
                return false;
            }
            _host = new CSteamID(id);
            SteamNetworking.AllowP2PPacketRelay(true);
            if (_cbConnectFail == null)
                _cbConnectFail = Callback<P2PSessionConnectFail_t>.Create(OnConnectFail);

            _running = true;
            _connected = false;
            _dialing = true;
            _dialStartMs = _lastHelloMs = Environment.TickCount;
            _rxSeq = 0;
            _log.LogInfo("CoopClient: dialing Steam id " + id + " (direct, relay fallback)");
            SendHello();
            return true;
        }

        public void Stop()
        {
            if (_connected || _dialing)
            {
                var w = new NetDataWriter();
                w.Put(SteamOp.Bye);
                w.Put("client stopped");
                SteamTransportUtil.Send(_host, w, NetDelivery.Reliable);
                SteamNetworking.CloseP2PSessionWithUser(_host);
            }
            if (_cbConnectFail != null) { _cbConnectFail.Unregister(); _cbConnectFail = null; }
            _running = false;
            _connected = false;
            _dialing = false;
        }

        public void Pump()
        {
            if (!_running) return;
            uint size;
            while (SteamNetworking.IsP2PPacketAvailable(out size, 0))
            {
                if (size > _buf.Length) _buf = new byte[Math.Max((int)size, _buf.Length * 2)];
                uint read;
                CSteamID remote;
                if (!SteamNetworking.ReadP2PPacket(_buf, (uint)_buf.Length, out read, out remote, 0)) break;
                if (remote != _host) continue; // stray peer — never accepted, but be safe
                HandlePacket((int)read);
                if (!_running) return; // a handler may have called Stop()
            }
            TickDialAndKeepalive();
        }

        public void Send(NetDataWriter payload, NetDelivery delivery)
        {
            if (!_connected) return;
            var framed = SteamTransportUtil.Frame(payload, delivery, ref _txSeq);
            SteamTransportUtil.Send(_host, framed, delivery);
        }

        private void SendHello()
        {
            var w = new NetDataWriter();
            w.Put(SteamOp.Hello);
            w.Put(_connectKey);
            SteamTransportUtil.Send(_host, w, NetDelivery.Reliable);
        }

        private void OnConnectFail(P2PSessionConnectFail_t fail)
        {
            if (!_running || fail.m_steamIDRemote != _host) return;
            _log.LogWarning("CoopClient: Steam P2P session failed (err=" + fail.m_eP2PSessionError +
                            " — 1=NotRunningApp 2=NoRightsToApp 3=UserNotLoggedIn 4=Timeout)");
            bool wasUp = _connected || _dialing;
            _connected = false;
            _dialing = false;
            if (wasUp) Disconnected?.Invoke("steam p2p error " + fail.m_eP2PSessionError);
        }

        private void HandlePacket(int len)
        {
            if (len < 1) return;
            _lastRxMs = Environment.TickCount;
            if (!_connected)
            {
                // First packet from the host = session is live both ways. The app-level Welcome
                // (protocol version check) rides in right behind — usually in this same packet.
                _connected = true;
                _dialing = false;
                _log.LogInfo("CoopClient: Steam P2P session established with " + _host.m_SteamID);
                ConnectedEvent?.Invoke();
            }

            var r = new NetDataReader(_buf, 0, len);
            byte op = r.GetByte();
            switch (op)
            {
                case SteamOp.Ping:
                {
                    int stamp = r.GetInt();
                    var w = new NetDataWriter();
                    w.Put(SteamOp.Pong);
                    w.Put(stamp);
                    SteamTransportUtil.Send(_host, w, NetDelivery.Unreliable);
                    break;
                }
                case SteamOp.Pong:
                    PingMs = Math.Max(0, Environment.TickCount - r.GetInt());
                    break;
                case SteamOp.Bye:
                {
                    string reason;
                    try { reason = r.GetString(); } catch { reason = "bye"; }
                    _connected = false;
                    _dialing = false;
                    SteamNetworking.CloseP2PSessionWithUser(_host);
                    Disconnected?.Invoke(reason);
                    break;
                }
                case SteamOp.Data:
                    Received?.Invoke(r);
                    break;
                case SteamOp.DataSeq:
                {
                    ushort seq = r.GetUShort();
                    if (_rxSeq != 0 && !SteamTransportUtil.SeqNewer(seq, _rxSeq)) break;
                    _rxSeq = seq;
                    Received?.Invoke(r);
                    break;
                }
            }
        }

        private void TickDialAndKeepalive()
        {
            int now = Environment.TickCount;
            if (_dialing)
            {
                if (now - _dialStartMs > SteamTransportUtil.DialTimeoutMs)
                {
                    _dialing = false;
                    _log.LogWarning("CoopClient: Steam dial timed out (host not hosting, or not friends and neither end reachable)");
                    Disconnected?.Invoke("connect timeout");
                    return;
                }
                if (now - _lastHelloMs >= SteamTransportUtil.HelloRetryMs)
                {
                    _lastHelloMs = now;
                    SendHello();
                }
                return;
            }
            if (!_connected) return;

            if (now - _lastPingMs >= SteamTransportUtil.PingIntervalMs)
            {
                _lastPingMs = now;
                var w = new NetDataWriter();
                w.Put(SteamOp.Ping);
                w.Put(now);
                SteamTransportUtil.Send(_host, w, NetDelivery.Unreliable);
            }
            if (now - _lastRxMs > SteamTransportUtil.TimeoutMs)
            {
                _connected = false;
                _log.LogWarning("CoopClient: host timed out");
                SteamNetworking.CloseP2PSessionWithUser(_host);
                Disconnected?.Invoke("timeout");
            }
        }
    }
}
