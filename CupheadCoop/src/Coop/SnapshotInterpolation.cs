using System.Collections.Generic;
using BepInEx.Logging;
using CupheadCoop.Net;
using UnityEngine;

namespace CupheadCoop.Coop
{
    /// <summary>
    /// v1.1.0 client-side snapshot interpolation.
    ///
    /// WHY: before 1.1.0 the client applied the single most-recent StateSnapshot verbatim every
    /// frame. At a 30 Hz snapshot rate rendered on a 60+ fps client, every other frame reused the
    /// same values — visible stepping — and any network jitter that reordered/delayed a packet
    /// produced a snap. This class buffers the last few snapshots and renders a point in the past
    /// (<see cref="ModConfig.InterpDelayMs"/> behind the newest), lerping between the two snapshots
    /// that bracket that render time. Because it writes the interpolated view back INTO the same
    /// <see cref="CoopState"/> fields the raw path used, every downstream applier
    /// (ScenePuppetry / EntitySync / ProjectileSync / PlayerMotorBypass) works unchanged.
    ///
    /// Timing rides the host clock: <see cref="StateSnapshot.HostTickMs"/> (host Stopwatch ms,
    /// 16-bit, already on the wire) is the authoritative timeline; the local
    /// <see cref="Time.realtimeSinceStartup"/> only advances the render cursor between packets.
    /// ushort wraparound is handled with signed-short deltas (same idiom as
    /// SteamTransportUtil.SeqNewer).
    ///
    /// Mirrors/pause/scene/alive-hashes are intentionally NOT touched here — those stay
    /// latest-value as applied by <see cref="CoopState.ApplyRemoteState"/>.
    /// </summary>
    internal static class SnapshotInterpolation
    {
        public static ManualLogSource Log;

        private const int Capacity = 4;

        // A buffered snapshot plus the local time we received it and prebuilt lookup indices.
        // Class (not struct) so the dictionaries live for the object's whole life and get reused
        // via Clear()+refill on each Push — zero steady-state allocation.
        private sealed class Slot
        {
            public StateSnapshot Snap;
            public float ArrivalTime;
            public readonly Dictionary<uint, int> EntIndex = new Dictionary<uint, int>(64);
            public readonly Dictionary<uint, int> ProjIndex = new Dictionary<uint, int>(32);
        }

        // Fixed ring of preallocated slots; _writePos is the next slot to (over)write, _count the
        // number of valid slots (grows to Capacity then stays). Logical order oldest→newest via
        // Entry().
        private static readonly Slot[] _slots = NewSlots();
        private static int _writePos;
        private static int _count;
        private static uint _lastPushedSeq;

        // Diagnostics: snapshots received since last log, and the realtime of the last log.
        private static int _rxCountWindow;
        private static float _lastLogRealtime = -1f;

        private static Slot[] NewSlots()
        {
            var a = new Slot[Capacity];
            for (int i = 0; i < Capacity; i++) a[i] = new Slot();
            return a;
        }

        /// <summary>Physical slot for a logical position (0 = oldest, _count-1 = newest).</summary>
        private static Slot Entry(int logical)
        {
            int phys = ((_writePos - _count + logical) % Capacity + Capacity) % Capacity;
            return _slots[phys];
        }

        private static Slot Newest() { return Entry(_count - 1); }

        /// <summary>
        /// Store a freshly received StateSnapshot. Called by CoopClient right after
        /// CoopState.ApplyRemoteState. Out-of-order/stale packets (same dedup rule as
        /// ApplyRemoteState) are ignored so the ring stays monotonic in host time.
        /// </summary>
        public static void Push(ref StateSnapshot snap)
        {
            if (snap.Sequence != 0 && snap.Sequence <= _lastPushedSeq) return;
            _lastPushedSeq = snap.Sequence;

            var slot = _slots[_writePos];
            slot.Snap = snap;
            slot.ArrivalTime = Time.realtimeSinceStartup;
            RebuildIndex(slot);

            _writePos = (_writePos + 1) % Capacity;
            if (_count < Capacity) _count++;
            _rxCountWindow++;
        }

        // Build PathHash→index and NetworkId→index for this slot once, at receive time, so Apply()
        // never has to scan the older snapshot's arrays per frame.
        private static void RebuildIndex(Slot slot)
        {
            slot.EntIndex.Clear();
            int ec = slot.Snap.EntityCount;
            var ents = slot.Snap.Entities;
            for (int i = 0; i < ec && ents != null && i < ents.Length; i++)
                slot.EntIndex[ents[i].PathHash] = i;

            slot.ProjIndex.Clear();
            int pc = slot.Snap.ProjectileCount;
            var projs = slot.Snap.Projectiles;
            for (int i = 0; i < pc && projs != null && i < projs.Length; i++)
                slot.ProjIndex[projs[i].NetworkId] = i;
        }

        /// <summary>
        /// Compute the render cursor, find the bracketing snapshots, and write the interpolated
        /// view into CoopState. Called once per frame from CoopLateApply BEFORE the appliers run.
        /// </summary>
        public static void Apply()
        {
            if (CoopState.Mode != CoopMode.Client) return;
            if (_count == 0) return;

            float now = Time.realtimeSinceStartup;
            Slot newest = Newest();

            if (_count == 1)
            {
                Write(newest, newest, 1f);
                MaybeLog(now, newest.ArrivalTime);
                return;
            }

            // Render cursor as an offset (ms) from the newest snapshot's host time. Negative =
            // rendering in the past, which is what we want (InterpDelayMs behind newest).
            float renderOffset = (now - newest.ArrivalTime) * 1000f - ModConfig.InterpDelayMs.Value;

            if (renderOffset >= 0f)
            {
                // Cursor is at/after the newest snapshot — clamp, never extrapolate.
                Write(newest, newest, 1f);
                MaybeLog(now, newest.ArrivalTime);
                return;
            }

            ushort refMs = newest.Snap.HostTickMs;
            // Walk adjacent pairs newest→oldest; the first pair whose older bound is at or before
            // the cursor is the bracket.
            for (int i = _count - 1; i >= 1; i--)
            {
                Slot newer = Entry(i);
                Slot older = Entry(i - 1);
                float offNewer = (short)(newer.Snap.HostTickMs - refMs);
                float offOlder = (short)(older.Snap.HostTickMs - refMs);
                if (renderOffset <= offNewer && renderOffset >= offOlder)
                {
                    float span = offNewer - offOlder;
                    float alpha = span > 0.0001f ? (renderOffset - offOlder) / span : 1f;
                    if (alpha < 0f) alpha = 0f;
                    else if (alpha > 1f) alpha = 1f;
                    Write(older, newer, alpha);
                    MaybeLog(now, newest.ArrivalTime);
                    return;
                }
            }

            // Cursor is older than everything buffered (long stall) — clamp to the oldest.
            Slot oldest = Entry(0);
            Write(oldest, oldest, 1f);
            MaybeLog(now, newest.ArrivalTime);
        }

        // Write an interpolated (older→newer @ alpha) view into CoopState. older==newer with
        // alpha==1 degenerates to "use newer verbatim" for the single/clamp cases.
        private static void Write(Slot older, Slot newer, float alpha)
        {
            WritePlayers(older, newer, alpha);
            WriteEntities(older, newer, alpha);
            WriteProjectiles(older, newer, alpha);
        }

        private static void WritePlayers(Slot older, Slot newer, float alpha)
        {
            WritePlayer(true, ref older.Snap.P1, ref newer.Snap.P1, alpha);
            WritePlayer(false, ref older.Snap.P2, ref newer.Snap.P2, alpha);
        }

        private static void WritePlayer(bool p1, ref PlayerSnapshot o, ref PlayerSnapshot n, float alpha)
        {
            // Facing/Hp/IsDead/Present come from the newer snapshot. Only lerp position when both
            // ends are present, otherwise a just-joined player would slide in from the origin.
            bool canLerp = o.Present && n.Present;
            float x = canLerp ? Lerp(o.X, n.X, alpha) : n.X;
            float y = canLerp ? Lerp(o.Y, n.Y, alpha) : n.Y;

            int animHash;
            float animTime;
            if (o.AnimStateHash == n.AnimStateHash)
            {
                animHash = n.AnimStateHash;
                animTime = LerpAnim(o.AnimNormalizedTime, n.AnimNormalizedTime, alpha);
            }
            else
            {
                animHash = n.AnimStateHash;
                animTime = n.AnimNormalizedTime;
            }

            if (p1)
            {
                CoopState.RemoteP1Present = n.Present;
                CoopState.RemoteP1X = x;
                CoopState.RemoteP1Y = y;
                CoopState.RemoteP1Facing = n.Facing;
                CoopState.RemoteP1Hp = n.Hp;
                CoopState.RemoteP1IsDead = n.IsDead;
                CoopState.RemoteP1AnimHash = animHash;
                CoopState.RemoteP1AnimTime = animTime;
            }
            else
            {
                CoopState.RemoteP2Present = n.Present;
                CoopState.RemoteP2X = x;
                CoopState.RemoteP2Y = y;
                CoopState.RemoteP2Facing = n.Facing;
                CoopState.RemoteP2Hp = n.Hp;
                CoopState.RemoteP2IsDead = n.IsDead;
                CoopState.RemoteP2AnimHash = animHash;
                CoopState.RemoteP2AnimTime = animTime;
            }
        }

        private static void WriteEntities(Slot older, Slot newer, float alpha)
        {
            int count = newer.Snap.EntityCount;
            if (count > CoopState.RemoteEntities.Length) count = CoopState.RemoteEntities.Length;
            var nents = newer.Snap.Entities;
            bool pair = older != newer;
            for (int i = 0; i < count; i++)
            {
                EntitySnapshot outE = nents[i];
                int j;
                if (pair && older.EntIndex.TryGetValue(outE.PathHash, out j)
                    && older.Snap.Entities != null && j < older.Snap.Entities.Length)
                {
                    var oe = older.Snap.Entities[j];
                    outE.X = Lerp(oe.X, outE.X, alpha);
                    outE.Y = Lerp(oe.Y, outE.Y, alpha);
                    outE.ScaleX = Lerp(oe.ScaleX, outE.ScaleX, alpha);
                    outE.ScaleY = Lerp(oe.ScaleY, outE.ScaleY, alpha);
                    if (oe.AnimStateHash == outE.AnimStateHash)
                        outE.AnimNormalizedTime = LerpAnim(oe.AnimNormalizedTime, outE.AnimNormalizedTime, alpha);
                }
                CoopState.RemoteEntities[i] = outE;
            }
            CoopState.RemoteEntityCount = count;
        }

        private static void WriteProjectiles(Slot older, Slot newer, float alpha)
        {
            int count = newer.Snap.ProjectileCount;
            if (count > CoopState.RemoteProjectiles.Length) count = CoopState.RemoteProjectiles.Length;
            var nprojs = newer.Snap.Projectiles;
            bool pair = older != newer;
            for (int i = 0; i < count; i++)
            {
                ProjectileSnapshot outP = nprojs[i];
                int j;
                if (pair && older.ProjIndex.TryGetValue(outP.NetworkId, out j)
                    && older.Snap.Projectiles != null && j < older.Snap.Projectiles.Length)
                {
                    var op = older.Snap.Projectiles[j];
                    outP.X = Lerp(op.X, outP.X, alpha);
                    outP.Y = Lerp(op.Y, outP.Y, alpha);
                    outP.ScaleX = Lerp(op.ScaleX, outP.ScaleX, alpha);
                    outP.ScaleY = Lerp(op.ScaleY, outP.ScaleY, alpha);
                    if (op.AnimStateHash == outP.AnimStateHash)
                        outP.AnimNormalizedTime = LerpAnim(op.AnimNormalizedTime, outP.AnimNormalizedTime, alpha);
                }
                CoopState.RemoteProjectiles[i] = outP;
            }
            CoopState.RemoteProjectileCount = count;
        }

        private static float Lerp(float a, float b, float t) { return a + (b - a) * t; }

        // Lerp a normalized animation time, handling a single loop wrap: if the newer sample has
        // wrapped past the loop point below the older one, shift it up by a full cycle before
        // interpolating, then fold the result back into [0,1).
        private static float LerpAnim(float o, float n, float alpha)
        {
            if (n < o - 0.5f) n += 1f;
            float r = o + (n - o) * alpha;
            r -= Mathf.Floor(r);
            return r;
        }

        private static void MaybeLog(float now, float newestArrival)
        {
            if (_lastLogRealtime < 0f) { _lastLogRealtime = now; _rxCountWindow = 0; return; }
            float elapsed = now - _lastLogRealtime;
            if (elapsed < 10f) return;

            float hz = elapsed > 0.0001f ? _rxCountWindow / elapsed : 0f;
            int ageMs = (int)((now - newestArrival) * 1000f);
            Log?.LogInfo("CoopClient sync: rx=" + hz.ToString("0.0") + "Hz age=" + ageMs +
                         "ms ents " + EntitySync.LastApplyHits + " hit/" + EntitySync.LastApplyMisses +
                         " miss proj bound=" + ProjectileSync.LastBoundCount +
                         " unbound=" + ProjectileSync.LastUnboundCandidates);

            _lastLogRealtime = now;
            _rxCountWindow = 0;
        }

        public static void Reset()
        {
            _writePos = 0;
            _count = 0;
            _lastPushedSeq = 0;
            _rxCountWindow = 0;
            _lastLogRealtime = -1f;
            for (int i = 0; i < Capacity; i++)
            {
                _slots[i].Snap = default(StateSnapshot);
                _slots[i].ArrivalTime = 0f;
                _slots[i].EntIndex.Clear();
                _slots[i].ProjIndex.Clear();
            }
        }
    }
}
