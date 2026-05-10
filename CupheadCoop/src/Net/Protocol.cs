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
        // v1 = M3 input streaming only (host runs sim, client uploads inputs)
        // v2 = M4 added StateSnapshot (host streams P1/P2 positions back to client)
        // v3 = M5 PlayerSnapshot extended with animator state hash + normalized time
        // v4 = M6 StateSnapshot now carries an entity array (boss + scene actors)
        // v5 = M8 PlayerSnapshot extended with Hp + IsDead
        // v6 = pause sync — StateSnapshot now carries an IsPaused bit
        // v7 = M11 scene sync — StateSnapshot carries the host's active scene name
        // v8 = M7 alive-hash list (client can SetActive(false) on entities host has killed)
        public const int Version = 8;
    }

    internal enum PacketType : byte
    {
        Welcome = 2,        // host -> client: handshake ack (carries protocol version)
        InputFrame = 10,    // client -> host: per-tick input snapshot
        StateSnapshot = 20, // host -> client: world-state snapshot for spectator-view rendering
        Heartbeat = 99,     // both: idle keepalive
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

    /// <summary>
    /// Per-player slice of a world-state snapshot. Position + facing + animator state.
    /// AnimStateHash is Animator.GetCurrentAnimatorStateInfo(0).fullPathHash — a stable
    /// integer derived from the state's full path inside the Animator Controller. The
    /// hashing function is part of the Unity Animation system's stable contract, so the
    /// hash is the same on host and client as long as both run the same Cuphead build.
    /// </summary>
    internal struct PlayerSnapshot
    {
        public bool Present;
        public float X;
        public float Y;
        public sbyte Facing;
        public int AnimStateHash;
        public float AnimNormalizedTime;
        public sbyte Hp;       // -1 = unknown; 0+ = current HP. Cuphead's max is single digits so 1 byte suffices.
        public bool IsDead;

        public void Write(NetDataWriter w)
        {
            w.Put(Present);
            w.Put(X);
            w.Put(Y);
            w.Put(Facing);
            w.Put(AnimStateHash);
            w.Put(AnimNormalizedTime);
            w.Put(Hp);
            w.Put(IsDead);
        }

        public static PlayerSnapshot Read(NetDataReader r)
        {
            return new PlayerSnapshot
            {
                Present = r.GetBool(),
                X = r.GetFloat(),
                Y = r.GetFloat(),
                Facing = r.GetSByte(),
                AnimStateHash = r.GetInt(),
                AnimNormalizedTime = r.GetFloat(),
                Hp = r.GetSByte(),
                IsDead = r.GetBool()
            };
        }
    }

    /// <summary>
    /// Per-entity slice (M6): scene actor identified by FNV1a32 of its scene/path. Carries
    /// transform + animator state. Spawned-at-runtime objects aren't included by the host's
    /// capture path in v1, so this slice mirrors scene-loaded entities only.
    /// </summary>
    internal struct EntitySnapshot
    {
        public uint PathHash;
        public float X;
        public float Y;
        public float ScaleX;
        public float ScaleY;
        public int AnimStateHash;
        public float AnimNormalizedTime;

        public void Write(NetDataWriter w)
        {
            w.Put(PathHash);
            w.Put(X);
            w.Put(Y);
            w.Put(ScaleX);
            w.Put(ScaleY);
            w.Put(AnimStateHash);
            w.Put(AnimNormalizedTime);
        }

        public static EntitySnapshot Read(NetDataReader r)
        {
            return new EntitySnapshot
            {
                PathHash = r.GetUInt(),
                X = r.GetFloat(),
                Y = r.GetFloat(),
                ScaleX = r.GetFloat(),
                ScaleY = r.GetFloat(),
                AnimStateHash = r.GetInt(),
                AnimNormalizedTime = r.GetFloat()
            };
        }
    }

    /// <summary>
    /// Host -> client world-state packet. Sent at <see cref="ModConfig.StateSendRateHz"/> Hz over an
    /// unreliable+sequenced channel: stale packets are dropped by the client based on Sequence,
    /// and lost packets just cause one missed interpolation frame, which is fine.
    /// </summary>
    internal struct StateSnapshot
    {
        public uint Sequence;
        public ushort HostTickMs;
        public PlayerSnapshot P1;
        public PlayerSnapshot P2;
        public bool IsPaused;
        public string SceneName;
        public byte EntityCount;
        public EntitySnapshot[] Entities;
        // v8: list of all path-hashes the host considers alive THIS frame. Client deactivates
        // any cached entity whose hash isn't in here, so projectiles that despawned and
        // enemies that died on the host disappear on the client without waiting for the next
        // 500ms cache refresh. Up to 256 hashes (1 KB) — projectile count under fire can be
        // higher than the 64 we track positions for.
        public ushort AliveHashCount;
        public uint[] AliveHashes;

        public void Write(NetDataWriter w)
        {
            w.Put((byte)PacketType.StateSnapshot);
            w.Put(Sequence);
            w.Put(HostTickMs);
            P1.Write(w);
            P2.Write(w);
            w.Put(IsPaused);
            w.Put(SceneName ?? "");
            w.Put(EntityCount);
            for (int i = 0; i < EntityCount; i++) Entities[i].Write(w);
            w.Put(AliveHashCount);
            for (int i = 0; i < AliveHashCount; i++) w.Put(AliveHashes[i]);
        }

        public static StateSnapshot Read(NetDataReader r)
        {
            var s = new StateSnapshot
            {
                Sequence = r.GetUInt(),
                HostTickMs = r.GetUShort(),
                P1 = PlayerSnapshot.Read(r),
                P2 = PlayerSnapshot.Read(r),
                IsPaused = r.GetBool(),
                SceneName = r.GetString(),
                EntityCount = r.GetByte()
            };
            s.Entities = s.EntityCount > 0 ? new EntitySnapshot[s.EntityCount] : null;
            for (int i = 0; i < s.EntityCount; i++) s.Entities[i] = EntitySnapshot.Read(r);
            s.AliveHashCount = r.GetUShort();
            s.AliveHashes = s.AliveHashCount > 0 ? new uint[s.AliveHashCount] : null;
            for (int i = 0; i < s.AliveHashCount; i++) s.AliveHashes[i] = r.GetUInt();
            return s;
        }
    }
}
