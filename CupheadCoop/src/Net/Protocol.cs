using System;
using LiteNetLib.Utils;

namespace CupheadCoop.Net
{
    /// <summary>
    /// Wire protocol v0. Bump <see cref="Version"/> on any breaking change so peers can refuse
    /// connections from incompatible builds.
    /// </summary>
    internal static class Protocol
    {
        public const int Version = 1;
    }

    internal enum PacketType : byte
    {
        Welcome = 2,      // host -> client: handshake ack (carries protocol version)
        InputFrame = 10,  // client -> host: per-tick input snapshot
        Heartbeat = 99,   // both: idle keepalive
    }

    internal struct InputFrame
    {
        public uint Sequence;
        public uint Buttons; // bitmask over CupheadButton action ids 0..27 (only low bits used)
        public sbyte AxisX;  // -100..+100 fixed-point
        public sbyte AxisY;

        public float UnpackAxisX => AxisX / 100f;
        public float UnpackAxisY => AxisY / 100f;

        public static InputFrame Pack(uint seq, uint buttons, float axisX, float axisY)
        {
            return new InputFrame
            {
                Sequence = seq,
                Buttons = buttons,
                AxisX = ToFixed(axisX),
                AxisY = ToFixed(axisY)
            };
        }

        private static sbyte ToFixed(float v)
        {
            int x = (int)(v * 100f);
            if (x < -100) x = -100;
            if (x > 100) x = 100;
            return (sbyte)x;
        }

        public void Write(NetDataWriter w)
        {
            w.Put((byte)PacketType.InputFrame);
            w.Put(Sequence);
            w.Put(Buttons);
            w.Put(AxisX);
            w.Put(AxisY);
        }

        public static InputFrame Read(NetDataReader r)
        {
            var f = new InputFrame
            {
                Sequence = r.GetUInt(),
                Buttons = r.GetUInt(),
                AxisX = r.GetSByte(),
                AxisY = r.GetSByte()
            };
            return f;
        }
    }

    internal struct WelcomePacket
    {
        public int Version;
        public bool Accepted;
        public string Reason;

        public void Write(NetDataWriter w)
        {
            w.Put((byte)PacketType.Welcome);
            w.Put(Version);
            w.Put(Accepted);
            w.Put(Reason ?? "");
        }

        public static WelcomePacket Read(NetDataReader r)
        {
            return new WelcomePacket
            {
                Version = r.GetInt(),
                Accepted = r.GetBool(),
                Reason = r.GetString()
            };
        }
    }
}
