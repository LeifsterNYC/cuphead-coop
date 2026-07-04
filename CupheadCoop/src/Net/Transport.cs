using System;
using LiteNetLib.Utils;

namespace CupheadCoop.Net
{
    /// <summary>
    /// Delivery semantics the protocol layer asks for; each transport maps them onto whatever
    /// its wire actually supports.
    /// </summary>
    internal enum NetDelivery : byte
    {
        /// <summary>Guaranteed + ordered. Handshake / control traffic.</summary>
        Reliable,
        /// <summary>Latest-wins: stale packets are dropped, nothing is retransmitted. State snapshots.</summary>
        Sequenced,
        /// <summary>Fire-and-forget. Input frames (next one supersedes anyway).</summary>
        Unreliable,
    }

    /// <summary>
    /// Host-side wire. Exactly one client at a time (2-player co-op). All events fire from
    /// <see cref="Pump"/> or the Steam callback dispatcher — both on the Unity main thread.
    /// </summary>
    internal interface IHostTransport
    {
        bool Start();
        void Stop();
        void Pump();
        bool Running { get; }
        bool HasClient { get; }
        int PingMs { get; }
        /// <summary>One-liner for the overlay, e.g. "UDP :47777" or "Steam 7656…".</summary>
        string Describe { get; }
        void Send(NetDataWriter payload, NetDelivery delivery);
        event Action ClientConnected;
        event Action<string> ClientDisconnected;
        event Action<NetDataReader> Received;
    }

    /// <summary>
    /// Client-side wire. <see cref="Connect"/> is also the retry entry point — the reconnect
    /// policy lives in <see cref="CoopClient"/>, not here.
    /// </summary>
    internal interface IClientTransport
    {
        bool Connect();
        void Stop();
        void Pump();
        bool Running { get; }
        bool Connected { get; }
        int PingMs { get; }
        string Describe { get; }
        void Send(NetDataWriter payload, NetDelivery delivery);
        event Action ConnectedEvent;
        event Action<string> Disconnected;
        event Action<NetDataReader> Received;
    }
}
