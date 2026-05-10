// CupheadCoop mock client — pretends to be a real CoopClient so you can exercise the
// network stack on a single PC without launching two Cuphead instances.
//
// Usage:
//   dotnet run --project tools/MockClient -- [host] [port] [key] [pattern]
//   dotnet run --project tools/MockClient                                     # localhost defaults, walk-right pattern
//   dotnet run --project tools/MockClient -- 127.0.0.1 47777 cuphead-coop-v0  # explicit
//   dotnet run --project tools/MockClient -- 192.168.0.4 47777 cuphead-coop-v0 jump
//
// Patterns: walk-right (default), walk-left, jump (every ~1s), idle.
//
// What it does:
//  - LiteNetLib UDP connect + handshake (mirrors CoopClient.Start in the plugin)
//  - Sends InputFrame at 60 Hz with synthetic buttons + axes
//  - Prints any StateSnapshot the host streams back (proves M4 round-trip)

using System.Net;
using System.Net.Sockets;
using LiteNetLib;
using LiteNetLib.Utils;

string host        = args.Length > 0 ? args[0] : "127.0.0.1";
int    port        = args.Length > 1 ? int.Parse(args[1]) : 47777;
string connectKey  = args.Length > 2 ? args[2] : "cuphead-coop-v0";
string patternName = args.Length > 3 ? args[3] : "walk-right";

Console.WriteLine($"MockClient v0.1 → {host}:{port}  key='{connectKey}'  pattern={patternName}");

var listener = new Listener();
var net = new NetManager(listener) { UnconnectedMessagesEnabled = false, UpdateTime = 15 };
if (!net.Start()) { Console.Error.WriteLine("NetManager.Start() failed"); return 1; }

var peer = net.Connect(host, port, connectKey);
Console.WriteLine($"dialing… (peer endpoint will resolve once handshake completes)");

var inputPattern = patternName switch
{
    "walk-left"  => InputPattern.WalkLeft,
    "jump"       => InputPattern.Jump,
    "idle"       => InputPattern.Idle,
    _            => InputPattern.WalkRight,
};

var pacer = new InputPacer(60, inputPattern);
uint seq = 0;
var sw = System.Diagnostics.Stopwatch.StartNew();
long lastReportMs = 0;

Console.CancelKeyPress += (_, e) => { e.Cancel = true; listener.QuitRequested = true; };

while (!listener.QuitRequested)
{
    net.PollEvents();

    if (peer.ConnectionState == ConnectionState.Connected && listener.HandshakeOk)
    {
        var (buttons, axisX, axisY) = pacer.Tick(sw.Elapsed.TotalSeconds);
        if (pacer.ShouldSendThisTick)
        {
            seq++;
            var w = new NetDataWriter();
            w.Put((byte)10);          // PacketType.InputFrame
            w.Put(seq);               // sequence
            w.Put(buttons);           // buttons bitmask
            w.Put((sbyte)Math.Clamp((int)(axisX * 100f), -100, 100)); // axisX
            w.Put((sbyte)Math.Clamp((int)(axisY * 100f), -100, 100)); // axisY
            peer.Send(w, DeliveryMethod.Unreliable);
        }

        if (sw.ElapsedMilliseconds - lastReportMs >= 1000)
        {
            lastReportMs = sw.ElapsedMilliseconds;
            Console.WriteLine($"[{sw.Elapsed:mm\\:ss}] tx seq={seq}  rx state seq={listener.LastStateSeq}  " +
                              $"p1={(listener.LastP1Present ? $"{listener.LastP1X:F1},{listener.LastP1Y:F1}" : "-")}  " +
                              $"p2={(listener.LastP2Present ? $"{listener.LastP2X:F1},{listener.LastP2Y:F1}" : "-")}  " +
                              $"hp={listener.LastP1Hp}/{listener.LastP2Hp}  entities={listener.LastEntityCount}");
        }
    }

    Thread.Sleep(2);
}

Console.WriteLine("disconnecting…");
peer.Disconnect();
net.Stop();
return 0;

/// <summary>Wire-protocol version we claim. Must match Net/Protocol.cs in the plugin.</summary>
static class Proto { public const int Version = 11; }

enum InputPattern { WalkRight, WalkLeft, Jump, Idle }

class InputPacer
{
    public bool ShouldSendThisTick { get; private set; }
    private readonly double _interval;
    private double _accum;
    private readonly InputPattern _pattern;

    public InputPacer(int hz, InputPattern pattern)
    {
        _interval = 1.0 / hz;
        _pattern = pattern;
    }

    public (uint buttons, float axisX, float axisY) Tick(double t)
    {
        _accum += 0.002;
        ShouldSendThisTick = false;
        if (_accum >= _interval) { _accum -= _interval; ShouldSendThisTick = true; }

        // CupheadButton enum (from decompile): MoveHorizontal=0, MoveVertical=1, Jump=2,
        // ShootHorizontal=3, ShootVertical=4, Shoot=5, Dash=6, Lock=7, ...
        // We emit axes via axisX/axisY for movement, and bits 2/5 for jump/shoot if needed.
        return _pattern switch
        {
            InputPattern.WalkRight => (0u, 1f, 0f),
            InputPattern.WalkLeft  => (0u, -1f, 0f),
            // Jump every ~1.2s for a brief window — pulse bit 2.
            InputPattern.Jump      => (((t % 1.2) < 0.08 ? (1u << 2) : 0u), 0f, 0f),
            _                      => (0u, 0f, 0f),
        };
    }
}

class Listener : INetEventListener
{
    public bool QuitRequested;
    public bool HandshakeOk;
    public uint LastStateSeq;
    public bool LastP1Present;
    public float LastP1X, LastP1Y;
    public bool LastP2Present;
    public float LastP2X, LastP2Y;
    public sbyte LastP1Hp = -1, LastP2Hp = -1;
    public int LastEntityCount;

    public void OnPeerConnected(NetPeer peer) =>
        Console.WriteLine($"connected to {peer.EndPoint}, awaiting Welcome…");

    public void OnPeerDisconnected(NetPeer peer, DisconnectInfo info)
    {
        Console.WriteLine($"disconnected: {info.Reason}  add'l: {info.AdditionalData?.AvailableBytes ?? 0} bytes");
        QuitRequested = true;
    }

    public void OnNetworkError(IPEndPoint endPoint, SocketError socketError) =>
        Console.WriteLine($"socket error from {endPoint}: {socketError}");

    public void OnNetworkReceive(NetPeer peer, NetPacketReader reader, DeliveryMethod deliveryMethod)
    {
        if (reader.AvailableBytes < 1) { reader.Recycle(); return; }
        byte type = reader.GetByte();
        switch (type)
        {
            case 2: // Welcome
            {
                int hostV = reader.GetInt();
                bool accepted = reader.GetBool();
                string reason = reader.GetString();
                if (!accepted) { Console.WriteLine($"REJECTED by host: {reason}"); QuitRequested = true; }
                else if (hostV != Proto.Version) { Console.WriteLine($"PROTOCOL MISMATCH: host=v{hostV}, mock=v{Proto.Version}"); QuitRequested = true; }
                else { HandshakeOk = true; Console.WriteLine($"handshake ok (v{hostV})"); }
                break;
            }
            case 20: // StateSnapshot
            {
                LastStateSeq      = reader.GetUInt();
                /* hostTickMs */ reader.GetUShort();
                LastP1Present     = reader.GetBool();
                LastP1X           = reader.GetFloat();
                LastP1Y           = reader.GetFloat();
                /* p1Facing */    reader.GetSByte();
                /* p1Anim */      reader.GetInt();
                /* p1AnimT */     reader.GetFloat();
                LastP1Hp          = reader.GetSByte();
                /* p1IsDead */    reader.GetBool();
                /* p1Buttons */   reader.GetUInt();   // v10
                /* p1AxisX_q */   reader.GetSByte();  // v10
                /* p1AxisY_q */   reader.GetSByte();  // v10
                LastP2Present     = reader.GetBool();
                LastP2X           = reader.GetFloat();
                LastP2Y           = reader.GetFloat();
                /* p2Facing */    reader.GetSByte();
                /* p2Anim */      reader.GetInt();
                /* p2AnimT */     reader.GetFloat();
                LastP2Hp          = reader.GetSByte();
                /* p2IsDead */    reader.GetBool();
                /* p2Buttons */   reader.GetUInt();   // v10
                /* p2AxisX_q */   reader.GetSByte();  // v10
                /* p2AxisY_q */   reader.GetSByte();  // v10
                /* isPaused */    reader.GetBool();
                /* sceneName */   reader.GetString();
                LastEntityCount   = reader.GetByte();
                for (int i = 0; i < LastEntityCount; i++)
                {
                    /* pathHash */ reader.GetUInt();
                    /* typeId */   reader.GetUInt();   // v11
                    /* x */        reader.GetFloat();
                    /* y */        reader.GetFloat();
                    /* scaleX */   reader.GetFloat();
                    /* scaleY */   reader.GetFloat();
                    /* animHash */ reader.GetInt();
                    /* animT */    reader.GetFloat();
                }
                int aliveCount = reader.GetUShort();
                for (int i = 0; i < aliveCount; i++) reader.GetUInt();
                int projCount = reader.GetByte();
                for (int i = 0; i < projCount; i++)
                {
                    /* networkId */ reader.GetUInt();
                    /* typeId */    reader.GetUInt();    // v11
                    /* x */         reader.GetFloat();
                    /* y */         reader.GetFloat();
                    /* scaleX */    reader.GetFloat();
                    /* scaleY */    reader.GetFloat();
                    /* animHash */  reader.GetInt();
                    /* animT */     reader.GetFloat();
                }
                break;
            }
        }
        reader.Recycle();
    }

    public void OnNetworkReceiveUnconnected(IPEndPoint remoteEndPoint, NetPacketReader reader, UnconnectedMessageType type) =>
        reader.Recycle();
    public void OnNetworkLatencyUpdate(NetPeer peer, int latency) { }
    public void OnConnectionRequest(ConnectionRequest request) => request.Reject();
}
