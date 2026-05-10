using System.Collections.Generic;
using BepInEx.Logging;
using CupheadCoop.Net;
using HarmonyLib;
using UnityEngine;

namespace CupheadCoop.Coop
{
    /// <summary>
    /// M7 v2 — projectile sync via host-assigned synthetic NetworkIDs.
    ///
    /// Why a separate layer (vs the path-hash EntitySync used for AbstractLevelEntity):
    /// Cuphead spawns AbstractProjectile instances at runtime as Clone GameObjects. Their
    /// hierarchy paths look like "scene/.../BaronessLevelBaronessProjectile (Clone)" and the
    /// sibling-index disambiguation only works if both host and client spawn projectiles in
    /// the same order — which they don't, because RNG, animator timing drift, and client
    /// having gated input mean spawn order diverges. Path-hash misses, projectiles invisible
    /// on client.
    ///
    /// Approach:
    ///  - Host: Harmony postfix on <c>AbstractProjectile.Awake</c> → assign monotonically
    ///    increasing uint NetworkID, store in <see cref="_hostTracked"/>. Prefix on
    ///    <c>AbstractProjectile.OnDestroy</c> → remove. Each snapshot tick we walk
    ///    <see cref="_hostTracked"/> and serialize (id, x, y, sx, sy, anim) per active
    ///    projectile (capped at <see cref="MaxSyncedProjectiles"/>).
    ///  - Client: same Awake/OnDestroy hooks register the local projectile in
    ///    <see cref="_clientUnbound"/>. On each StateSnapshot, for each incoming
    ///    ProjectileSnapshot we either update the already-bound local instance, or pick the
    ///    closest unbound candidate within a position threshold and bind it. If host has
    ///    stopped reporting a NetworkID we previously saw, we destroy the bound local.
    ///
    /// Limits:
    ///  - Best-match-by-position binding can mis-pair if two projectiles spawn within a few
    ///    pixels of each other in the same frame. Acceptable jitter; rare in practice.
    ///  - Doesn't handle bosses spawning projectiles client's local boss never spawns (e.g.,
    ///    if animator-state mirroring isn't perfect). Those will just fail to bind and the
    ///    client won't see them. Future work: instantiate from a prefab registry.
    /// </summary>
    internal static class ProjectileSync
    {
        public const int MaxSyncedProjectiles = 32;
        // Position-match threshold for binding unbound local projectiles to incoming
        // NetworkIDs. Cuphead's world-space units are pixels-ish; ~80 covers a generous
        // jitter window without matching across projectile clusters.
        public const float BindMatchRadius = 80f;
        // If we don't see a NetworkID for this long, the host has destroyed it; wipe ours too.
        public const float StaleSeconds = 0.4f;

        public static ManualLogSource Log;

        private struct HostEntry
        {
            public uint NetworkId;
            public uint TypeId; // v11: precomputed FNV1a32(Type.FullName)
            public AbstractProjectile Proj;
        }
        private struct ClientBound
        {
            public AbstractProjectile Proj;
            public float LastSeenTime;
        }

        // Host-side: every live AbstractProjectile gets a NetworkID. Keyed by InstanceID for
        // fast OnDestroy removal.
        private static readonly Dictionary<int, HostEntry> _hostTracked = new Dictionary<int, HostEntry>();
        private static uint _nextNetworkId = 1;

        // Client-side: bound = "this local projectile follows host's NetworkID". Unbound =
        // "freshly spawned local projectile, available for binding". Both keyed/listed by
        // local InstanceID so OnDestroy can clean up either.
        private static readonly Dictionary<uint, ClientBound> _clientBound = new Dictionary<uint, ClientBound>();
        private static readonly List<AbstractProjectile> _clientUnbound = new List<AbstractProjectile>();
        // Reverse map: local InstanceID → bound NetworkID, so OnDestroy can remove from
        // _clientBound without scanning.
        private static readonly Dictionary<int, uint> _clientInstToId = new Dictionary<int, uint>();

        // Scratch buffer for outgoing snapshots — reused each tick to avoid GC churn.
        public static readonly ProjectileSnapshot[] HostBuffer = new ProjectileSnapshot[MaxSyncedProjectiles];

        // Diagnostic counters for the overlay.
        public static int LastHostCount;
        public static int LastBoundCount;
        public static int LastUnboundCandidates;
        public static int LastBindsThisTick;
        public static int LastDestroyedThisTick;

        // Wall-clock seconds since plugin load. Used to age out stale NetworkIDs.
        private static float _now;

        public static void Tick(float dt) { _now += dt; }

        // ==== HOST SIDE ====

        public static void OnProjectileAwakeHost(AbstractProjectile p)
        {
            if (p == null) return;
            int iid = p.GetInstanceID();
            if (_hostTracked.ContainsKey(iid)) return; // already registered — Awake re-fired
            uint id = _nextNetworkId++;
            uint typeId = TypeRegistry.HashType(p.GetType());
            _hostTracked[iid] = new HostEntry { NetworkId = id, TypeId = typeId, Proj = p };
        }

        public static void OnProjectileDestroyHost(AbstractProjectile p)
        {
            if (p == null) return;
            _hostTracked.Remove(p.GetInstanceID());
        }

        public static void CaptureForHost(out int count)
        {
            count = 0;
            // Walk a snapshot of keys so we can prune stale entries (shouldn't happen if
            // OnDestroy fires reliably, but defensive).
            var stale = new List<int>();
            foreach (var kvp in _hostTracked)
            {
                var e = kvp.Value;
                if (e.Proj == null) { stale.Add(kvp.Key); continue; }
                if (!e.Proj.gameObject.activeInHierarchy) continue;
                if (count >= MaxSyncedProjectiles) break;

                int animHash = 0;
                float animTime = 0f;
                var animator = e.Proj.GetComponentInChildren<Animator>();
                if (animator != null && animator.isActiveAndEnabled && animator.runtimeAnimatorController != null)
                {
                    var st = animator.GetCurrentAnimatorStateInfo(0);
                    animHash = st.fullPathHash;
                    float t = st.normalizedTime;
                    animTime = t - Mathf.Floor(t);
                }

                var pos = e.Proj.transform.position;
                var scale = e.Proj.transform.localScale;
                HostBuffer[count] = new ProjectileSnapshot
                {
                    NetworkId = e.NetworkId,
                    TypeId = e.TypeId,
                    X = pos.x,
                    Y = pos.y,
                    ScaleX = scale.x,
                    ScaleY = scale.y,
                    AnimStateHash = animHash,
                    AnimNormalizedTime = animTime
                };
                count++;
            }
            for (int i = 0; i < stale.Count; i++) _hostTracked.Remove(stale[i]);
            LastHostCount = count;
        }

        // ==== CLIENT SIDE ====

        public static void OnProjectileAwakeClient(AbstractProjectile p)
        {
            if (p == null) return;
            // New local projectile, available for binding. We don't know which host
            // NetworkID it corresponds to yet — that decision happens in ApplyToClient when
            // the next snapshot arrives.
            _clientUnbound.Add(p);
            // Also register this type as a prefab template for spawn-from-host fallback.
            // Idempotent — TypeRegistry only keeps the first GameObject seen per type.
            TypeRegistry.RegisterClientTemplate(p.GetType(), p.gameObject);
        }

        public static void OnProjectileDestroyClient(AbstractProjectile p)
        {
            if (p == null) return;
            int iid = p.GetInstanceID();
            // Remove from bound dict if it was bound to a NetworkID.
            if (_clientInstToId.TryGetValue(iid, out var nid))
            {
                _clientBound.Remove(nid);
                _clientInstToId.Remove(iid);
            }
            // Remove from unbound list (linear, but the list is small).
            for (int i = _clientUnbound.Count - 1; i >= 0; i--)
                if (_clientUnbound[i] == null || _clientUnbound[i].GetInstanceID() == iid)
                    _clientUnbound.RemoveAt(i);
        }

        /// <summary>
        /// Apply the latest projectile snapshot list from host. For each incoming NetworkID
        /// we either update the bound local instance or bind the closest unbound match.
        /// Bound projectiles whose NetworkID hasn't been seen for <see cref="StaleSeconds"/>
        /// get destroyed locally.
        /// </summary>
        public static void ApplyToClient(ProjectileSnapshot[] snapshots, int count)
        {
            int binds = 0;
            int destroyed = 0;

            // Mark which NetworkIDs appeared this snapshot so we can reap stale ones below.
            var seen = new HashSet<uint>();

            for (int i = 0; i < count; i++)
            {
                var s = snapshots[i];
                seen.Add(s.NetworkId);

                if (_clientBound.TryGetValue(s.NetworkId, out var b))
                {
                    if (b.Proj == null)
                    {
                        // Bound local was destroyed underneath us; drop the binding so the
                        // next iteration treats this NetworkID as new and can re-bind.
                        _clientBound.Remove(s.NetworkId);
                        // fall through to unbound-match logic below
                    }
                    else
                    {
                        ApplyTransform(b.Proj, s);
                        b.LastSeenTime = _now;
                        _clientBound[s.NetworkId] = b;
                        continue;
                    }
                }

                // Not bound: try to claim the closest unbound local within radius.
                int bestIdx = -1;
                float bestDist = BindMatchRadius * BindMatchRadius;
                for (int j = _clientUnbound.Count - 1; j >= 0; j--)
                {
                    var cand = _clientUnbound[j];
                    if (cand == null) { _clientUnbound.RemoveAt(j); continue; }
                    var p = cand.transform.position;
                    float dx = p.x - s.X;
                    float dy = p.y - s.Y;
                    float d2 = dx * dx + dy * dy;
                    if (d2 < bestDist)
                    {
                        bestDist = d2;
                        bestIdx = j;
                    }
                }
                if (bestIdx >= 0)
                {
                    var claimed = _clientUnbound[bestIdx];
                    _clientUnbound.RemoveAt(bestIdx);
                    _clientBound[s.NetworkId] = new ClientBound { Proj = claimed, LastSeenTime = _now };
                    _clientInstToId[claimed.GetInstanceID()] = s.NetworkId;
                    ApplyTransform(claimed, s);
                    binds++;
                    continue;
                }

                // v11: no local unbound candidate — try to instantiate from prefab template.
                // Most common case: host's boss fired a projectile that client's local boss
                // didn't fire (because client's enemy AI is suppressed in v0.9.0). Look up
                // by TypeId, clone, place at host's transform, bind to NetworkID.
                if (s.TypeId != 0 && ModConfig.EnableSpawnFromHost.Value)
                {
                    var template = TypeRegistry.GetClientTemplate(s.TypeId);
                    if (template != null)
                    {
                        try
                        {
                            var newGo = Object.Instantiate(template);
                            var newProj = newGo.GetComponent<AbstractProjectile>();
                            if (newProj != null)
                            {
                                // Note: AbstractProjectile.Awake on the clone will fire
                                // OnProjectileAwakeClient, which adds it to _clientUnbound.
                                // We immediately remove it from there since we're binding it
                                // right now to host's NetworkID.
                                for (int j = _clientUnbound.Count - 1; j >= 0; j--)
                                    if (_clientUnbound[j] == newProj) { _clientUnbound.RemoveAt(j); break; }
                                _clientBound[s.NetworkId] = new ClientBound { Proj = newProj, LastSeenTime = _now };
                                _clientInstToId[newProj.GetInstanceID()] = s.NetworkId;
                                ApplyTransform(newProj, s);
                                binds++;
                                continue;
                            }
                            else
                            {
                                Object.Destroy(newGo);
                            }
                        }
                        catch (System.Exception ex)
                        {
                            Log?.LogWarning("ProjectileSync: spawn-from-host Instantiate failed for typeId="
                                             + s.TypeId.ToString("X") + ": " + ex.Message);
                        }
                    }
                }
                // No match and no template — host has a projectile we can't reproduce locally.
                // Skip; better luck next time.
            }

            // Reap NetworkIDs we previously bound but haven't seen in this snapshot for
            // a while — host destroyed those projectiles, so nuke ours too.
            var stale = new List<uint>();
            foreach (var kvp in _clientBound)
            {
                if (!seen.Contains(kvp.Key) && (_now - kvp.Value.LastSeenTime) > StaleSeconds)
                    stale.Add(kvp.Key);
            }
            for (int i = 0; i < stale.Count; i++)
            {
                if (_clientBound.TryGetValue(stale[i], out var b))
                {
                    if (b.Proj != null)
                    {
                        _clientInstToId.Remove(b.Proj.GetInstanceID());
                        Object.Destroy(b.Proj.gameObject);
                        destroyed++;
                    }
                    _clientBound.Remove(stale[i]);
                }
            }

            // Cap the unbound candidates list — projectiles that never got bound live forever
            // otherwise. Anything older than StaleSeconds without a host match is discarded:
            // we don't know its spawn time directly, so the list cap acts as a backstop.
            if (_clientUnbound.Count > MaxSyncedProjectiles * 2)
            {
                _clientUnbound.RemoveRange(0, _clientUnbound.Count - MaxSyncedProjectiles * 2);
            }

            LastBoundCount = _clientBound.Count;
            LastUnboundCandidates = _clientUnbound.Count;
            LastBindsThisTick = binds;
            LastDestroyedThisTick = destroyed;
        }

        private static void ApplyTransform(AbstractProjectile p, ProjectileSnapshot s)
        {
            try
            {
                var t = p.transform;
                var pos = t.position;
                pos.x = s.X;
                pos.y = s.Y;
                t.position = pos;
                var sc = t.localScale;
                sc.x = s.ScaleX;
                sc.y = s.ScaleY;
                t.localScale = sc;

                if (s.AnimStateHash != 0)
                {
                    var animator = p.GetComponentInChildren<Animator>();
                    if (animator != null && animator.isActiveAndEnabled
                        && animator.runtimeAnimatorController != null)
                    {
                        var current = animator.GetCurrentAnimatorStateInfo(0);
                        float curT = current.normalizedTime - Mathf.Floor(current.normalizedTime);
                        if (current.fullPathHash != s.AnimStateHash
                            || Mathf.Abs(curT - s.AnimNormalizedTime) > 0.15f)
                        {
                            animator.Play(s.AnimStateHash, 0, s.AnimNormalizedTime);
                        }
                    }
                }
            }
            catch
            {
                // Projectile destroyed mid-apply — caller will reap on next tick.
            }
        }

        public static void Reset()
        {
            _hostTracked.Clear();
            _clientBound.Clear();
            _clientUnbound.Clear();
            _clientInstToId.Clear();
            _nextNetworkId = 1;
            LastHostCount = 0;
            LastBoundCount = 0;
            LastUnboundCandidates = 0;
            LastBindsThisTick = 0;
            LastDestroyedThisTick = 0;
        }

        /// <summary>
        /// True if this projectile (on client) is currently bound to a host NetworkID.
        /// Used by the lifecycle patches to decide whether to suppress local-sim movement:
        /// bound projectiles get their position from host's stream (suppress local move to
        /// avoid jitter); unbound projectiles run their local Update/FixedUpdate so they can
        /// self-destruct via lifetime/distance (otherwise they'd accumulate as visible
        /// floating debris when client's sim spawns extras host doesn't have).
        /// </summary>
        public static bool IsBoundClient(AbstractProjectile p)
        {
            if (p == null) return false;
            return _clientInstToId.ContainsKey(p.GetInstanceID());
        }
    }

    /// <summary>
    /// Harmony hooks on AbstractProjectile lifecycle so we can register/unregister
    /// projectiles with <see cref="ProjectileSync"/>.
    /// </summary>
    [HarmonyPatch]
    internal static class ProjectileLifecyclePatches
    {
        [HarmonyPatch(typeof(AbstractProjectile), "Awake")]
        [HarmonyPostfix]
        private static void Awake_Postfix(AbstractProjectile __instance)
        {
            if (CoopState.Mode == CoopMode.Host)
                ProjectileSync.OnProjectileAwakeHost(__instance);
            else if (CoopState.Mode == CoopMode.Client)
                ProjectileSync.OnProjectileAwakeClient(__instance);
        }

        [HarmonyPatch(typeof(AbstractProjectile), "OnDestroy")]
        [HarmonyPrefix]
        private static void OnDestroy_Prefix(AbstractProjectile __instance)
        {
            if (CoopState.Mode == CoopMode.Host)
                ProjectileSync.OnProjectileDestroyHost(__instance);
            else if (CoopState.Mode == CoopMode.Client)
                ProjectileSync.OnProjectileDestroyClient(__instance);
        }

        // Suppress Update + FixedUpdate + Move on client only for projectiles BOUND to a host
        // NetworkID. Bound projectiles get their position from host's stream every LateUpdate,
        // so letting local sim move them would cause 1-frame jitter snapping to host pos.
        //
        // UNBOUND projectiles (client's local sim spawned them but host doesn't have a
        // matching NetworkID) need their normal Update/FixedUpdate so AbstractProjectile.Update's
        // lifetime/distance tracking can self-destruct them. Without this, they'd accumulate as
        // visible "floating debris" — the bug seen in v0.8.3 where unbound peashooter shots
        // froze in place around the cup.
        //
        // Only blocks the base AbstractProjectile body — subclasses that override these
        // methods need their own patches (BasicProjectile.Move below covers the common
        // straight-line movement case).
        [HarmonyPatch(typeof(AbstractProjectile), "Update")]
        [HarmonyPrefix]
        private static bool Update_Prefix(AbstractProjectile __instance)
        {
            if (CoopState.Mode != CoopMode.Client) return true;
            return !ProjectileSync.IsBoundClient(__instance);
        }

        [HarmonyPatch(typeof(AbstractProjectile), "FixedUpdate")]
        [HarmonyPrefix]
        private static bool FixedUpdate_Prefix(AbstractProjectile __instance)
        {
            if (CoopState.Mode != CoopMode.Client) return true;
            return !ProjectileSync.IsBoundClient(__instance);
        }

        // BasicProjectile.FixedUpdate calls base (which we just decided to skip on client for
        // bound only) then conditionally calls Move() — Move is the actual position-mutating
        // call for straight-line projectiles. Same gate: block only when the instance is bound
        // so unbound BasicProjectiles can fly their local trajectory and self-reap.
        [HarmonyPatch(typeof(BasicProjectile), "Move")]
        [HarmonyPrefix]
        private static bool BasicProjectile_Move_Prefix(AbstractProjectile __instance)
        {
            if (CoopState.Mode != CoopMode.Client) return true;
            return !ProjectileSync.IsBoundClient(__instance);
        }
    }
}
