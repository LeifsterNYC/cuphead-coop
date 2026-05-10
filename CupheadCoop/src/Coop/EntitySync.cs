using System.Collections.Generic;
using System.Text;
using BepInEx.Logging;
using CupheadCoop.Net;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace CupheadCoop.Coop
{
    /// <summary>
    /// M6: scene-actor synchronization. Mirrors host-side <see cref="AbstractLevelEntity"/>
    /// instances onto the client by streaming their transforms + animator state. Identifies
    /// each entity by FNV1a32 hash of its scene-relative hierarchy path so both sides agree
    /// on "which entity is this" without any spawn-id system.
    ///
    /// Scope of v1:
    ///  • Only entities present in the scene at <c>sceneLoaded</c> time are tracked. Runtime
    ///    spawns (projectiles, summoned enemies, phase-2 boss instantiations) are ignored.
    ///  • Cap of <see cref="MaxSyncedEntities"/> per snapshot. Keeps packet size predictable.
    ///  • Filter: only entities whose subtree contains an <c>Animator</c> are tracked. Bosses
    ///    qualify; passive scenery doesn't.
    ///
    /// Limitations documented in tasks/M6-design.md.
    /// </summary>
    internal static class EntitySync
    {
        public const int MaxSyncedEntities = 64;

        public static ManualLogSource Log;

        private struct EntityRef
        {
            public Transform Transform;
            public Animator Animator;
            public string Path; // kept for debug logging
        }

        // Path-hash → live reference. Rebuilt on every scene load.
        private static readonly Dictionary<uint, EntityRef> _byPath = new Dictionary<uint, EntityRef>();
        private static bool _cacheValid;
        // Last-logged signature so periodic refreshes only chatter when something actually changes.
        private static int _lastLoggedCount = -1;
        private static string _lastLoggedScene;

        // Scratch buffer used by Plugin.LateUpdate when handing snapshots to CoopHost. Reused
        // across frames to avoid per-frame allocations on the hot path.
        public static readonly EntitySnapshot[] HostBuffer = new EntitySnapshot[MaxSyncedEntities];

        // Larger buffer for "alive but maybe not position-tracked" entity hashes. Sent to
        // client so it can deactivate locally any entity not in this set.
        public const int MaxAliveHashes = 256;
        public static readonly uint[] AliveHashesBuffer = new uint[MaxAliveHashes];
        public static int AliveHashesCount;

        // Diagnostic counters surfaced by the in-game overlay.
        public static int LastCapturedCount;
        public static int CacheSize => _byPath.Count;

        // Periodic re-walk cadence. Tightened from 2s to 0.5s now that we also track
        // AbstractProjectile — projectiles are short-lived (sub-second), so a 2s interval
        // would miss most of them. Cost: ~30ms × 2/s = ~6% of one frame budget. Acceptable.
        private const float RefreshIntervalSec = 0.5f;
        private static float _secondsSinceRefresh;

        public static void Wire()
        {
            SceneManager.sceneLoaded += OnSceneLoaded;
            // Also try once at startup in case a scene is already loaded (e.g., we attached
            // mid-game, which BepInEx supports via "config reload" workflows).
            RefreshCache();
        }

        private static void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            RefreshCache();
            _secondsSinceRefresh = 0f;
        }

        /// <summary>
        /// Called every frame from <c>Plugin.LateUpdate</c>. Refreshes the cache on a fixed
        /// cadence so we pick up entities that didn't exist at scene-load time (boss phase 2,
        /// shopkeepers spawning, intro-cutscene cleanup leaving new objects behind).
        /// </summary>
        public static void Tick(float dt)
        {
            _secondsSinceRefresh += dt;
            if (_secondsSinceRefresh < RefreshIntervalSec) return;
            _secondsSinceRefresh = 0f;
            RefreshCache();
        }

        /// <summary>
        /// Walk every active <c>AbstractLevelEntity</c> in the loaded scenes, hash their
        /// hierarchy path, and stash a Transform + Animator reference in the cache. Cheap
        /// to call multiple times — fully overwrites the cache each invocation.
        /// </summary>
        public static void RefreshCache()
        {
            _byPath.Clear();
            try
            {
                int kept = 0, skipped = 0;

                // Layer 1: scene-loaded gameplay entities. Catches bosses, scenery animations,
                // mini-bosses, set pieces. Path-hash works because these are stable scene objects.
                var entities = Object.FindObjectsOfType<AbstractLevelEntity>();
                foreach (var ent in entities) { if (TryAdd(ent?.gameObject, ref kept, ref skipped, "le")) {} }

                // Layer 2: projectiles (boss attacks, player shots, mob shots). Runtime-spawned
                // clones — path-hash uses sibling index instead of name to disambiguate, so the
                // first projectile fired by a given parent on host hashes to the same value as
                // the first projectile fired by the same parent on client (assuming spawn order
                // is deterministic, which it is when boss AI animation states match).
                var projectiles = Object.FindObjectsOfType<AbstractProjectile>();
                foreach (var p in projectiles) { if (TryAdd(p?.gameObject, ref kept, ref skipped, "p")) {} }

                string scene = SceneManager.GetActiveScene().name;
                // Only log when the count or scene changed — periodic refresh in a stable scene
                // would otherwise dump an identical line into the overlay tail every 2s.
                if (kept != _lastLoggedCount || scene != _lastLoggedScene)
                {
                    Log?.LogInfo("EntitySync: cached " + kept + " entities (" + skipped + " skipped) for scene '"
                                 + scene + "'");
                    // Dump short names of what we caught so the tester can see what's actually
                    // being synced. Throttled to scene-change events; one line for everything.
                    if (kept > 0 && kept <= 16)
                    {
                        var names = new List<string>(kept);
                        foreach (var v in _byPath.Values)
                        {
                            if (v.Transform != null) names.Add(v.Transform.name);
                        }
                        Log?.LogInfo("EntitySync: tracking " + string.Join(", ", names.ToArray()));
                    }
                    _lastLoggedCount = kept;
                    _lastLoggedScene = scene;
                }
            }
            catch (System.Exception ex)
            {
                _cacheValid = false;
                Log?.LogWarning("EntitySync RefreshCache failed: " + ex.Message);
            }
        }

        /// <summary>
        /// Fill <see cref="HostBuffer"/> with up to <see cref="MaxSyncedEntities"/> snapshots
        /// sampled from the live cache. Caller writes <paramref name="count"/> into the
        /// outgoing packet header.
        /// </summary>
        public static void CaptureForHost(out int count)
        {
            count = 0;
            AliveHashesCount = 0;
            if (!_cacheValid) { LastCapturedCount = 0; return; }

            foreach (var kvp in _byPath)
            {
                var er = kvp.Value;
                if (er.Transform == null) continue; // entity destroyed since last refresh
                if (!er.Transform.gameObject.activeInHierarchy) continue;

                // Track alive hash regardless of whether we can fit positions in the snapshot.
                if (AliveHashesCount < MaxAliveHashes)
                {
                    AliveHashesBuffer[AliveHashesCount++] = kvp.Key;
                }

                if (count >= MaxSyncedEntities) continue; // alive-tracked but no position slot

                int animHash = 0;
                float animTime = 0f;
                if (er.Animator != null && er.Animator.isActiveAndEnabled && er.Animator.runtimeAnimatorController != null)
                {
                    var st = er.Animator.GetCurrentAnimatorStateInfo(0);
                    animHash = st.fullPathHash;
                    float t = st.normalizedTime;
                    animTime = t - Mathf.Floor(t);
                }

                var pos = er.Transform.position;
                var scale = er.Transform.localScale;
                HostBuffer[count] = new EntitySnapshot
                {
                    PathHash = kvp.Key,
                    X = pos.x,
                    Y = pos.y,
                    ScaleX = scale.x,
                    ScaleY = scale.y,
                    AnimStateHash = animHash,
                    AnimNormalizedTime = animTime
                };
                count++;
            }
            LastCapturedCount = count;
        }

        /// <summary>
        /// On the client: walk the snapshot array, look each entity up in the local cache by
        /// path hash, and override its transform + animator state. Hash misses are silently
        /// dropped — that handles brief desyncs during scene transitions and runtime-spawned
        /// entities the host can sample but we can't.
        /// </summary>
        public static void ApplyAliveSet(uint[] aliveHashes, int aliveCount)
        {
            if (!_cacheValid || aliveHashes == null || aliveCount == 0) return;

            // Build a lookup set of host-alive hashes.
            var alive = new HashSet<uint>();
            for (int i = 0; i < aliveCount; i++) alive.Add(aliveHashes[i]);

            // For each cached entity, if its hash isn't in the alive set AND its GameObject is
            // currently active, deactivate it. This collapses host-killed enemies / despawned
            // projectiles on the client immediately rather than waiting for the next refresh.
            foreach (var kvp in _byPath)
            {
                var er = kvp.Value;
                if (er.Transform == null) continue;
                var go = er.Transform.gameObject;
                if (go == null) continue;
                if (alive.Contains(kvp.Key))
                {
                    // Should be alive — re-activate if local sim turned it off.
                    if (!go.activeSelf) go.SetActive(true);
                }
                else
                {
                    if (go.activeSelf) go.SetActive(false);
                }
            }
        }

        public static void ApplyToClient(EntitySnapshot[] snapshots, int count)
        {
            if (!_cacheValid) return;

            for (int i = 0; i < count; i++)
            {
                var s = snapshots[i];
                if (!_byPath.TryGetValue(s.PathHash, out var er)) continue;
                if (er.Transform == null) continue;

                try
                {
                    var t = er.Transform;
                    var p = t.position;
                    p.x = s.X;
                    p.y = s.Y;
                    t.position = p;

                    var sc = t.localScale;
                    sc.x = s.ScaleX;
                    sc.y = s.ScaleY;
                    t.localScale = sc;

                    if (s.AnimStateHash != 0 && er.Animator != null && er.Animator.isActiveAndEnabled
                        && er.Animator.runtimeAnimatorController != null)
                    {
                        var current = er.Animator.GetCurrentAnimatorStateInfo(0);
                        float curT = current.normalizedTime - Mathf.Floor(current.normalizedTime);
                        if (current.fullPathHash != s.AnimStateHash
                            || Mathf.Abs(curT - s.AnimNormalizedTime) > 0.15f)
                        {
                            er.Animator.Play(s.AnimStateHash, 0, s.AnimNormalizedTime);
                        }
                    }
                }
                catch
                {
                    // entity went away mid-apply (scene unload race) — skip
                }
            }
        }

        /// <summary>
        /// Hierarchy path from scene root. For stable scene objects, uses GameObject names.
        /// For runtime clones (anything whose name contains "(Clone)"), substitutes the
        /// sibling-index of the GameObject under its parent, so two same-name clones can be
        /// disambiguated. Both host and client compute paths the same way; spawn order on each
        /// side is deterministic when M5/M6 keep animator states aligned, so the sibling
        /// indices line up and same-projectile-on-both-sides gets the same hash.
        /// </summary>
        private static string ComputePath(Transform t)
        {
            var stack = new Stack<string>(8);
            var cur = t;
            while (cur != null)
            {
                if (cur.name.IndexOf("(Clone)", System.StringComparison.Ordinal) >= 0 && cur.parent != null)
                {
                    // Use sibling index; this is the projectile/clone case.
                    stack.Push("[" + cur.GetSiblingIndex() + "]");
                }
                else
                {
                    stack.Push(cur.name);
                }
                cur = cur.parent;
            }
            var sb = new StringBuilder(64);
            sb.Append(SceneManager.GetActiveScene().name);
            while (stack.Count > 0)
            {
                sb.Append('/');
                sb.Append(stack.Pop());
            }
            return sb.ToString();
        }

        // Helper used by RefreshCache layers. Adds a GameObject to the cache if it has a
        // descendant Animator. Returns true if added.
        private static bool TryAdd(GameObject go, ref int kept, ref int skipped, string layerTag)
        {
            if (go == null) return false;
            if (!go.activeInHierarchy) { skipped++; return false; }
            var animator = go.GetComponentInChildren<Animator>();
            if (animator == null) { skipped++; return false; }
            string path = ComputePath(go.transform);
            uint hash = Fnv1a32(path);
            _byPath[hash] = new EntityRef { Transform = go.transform, Animator = animator, Path = path };
            if (kept < MaxSyncedEntities) kept++;
            return true;
        }

        /// <summary>
        /// FNV-1a 32-bit. Deterministic across .NET versions (unlike string.GetHashCode),
        /// which is the property we need for host/client agreement.
        /// </summary>
        private static uint Fnv1a32(string s)
        {
            const uint prime = 16777619u;
            uint hash = 2166136261u;
            for (int i = 0; i < s.Length; i++)
            {
                hash ^= s[i];
                hash *= prime;
            }
            return hash;
        }
    }
}
