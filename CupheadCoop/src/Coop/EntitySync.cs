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
        public const int MaxSyncedEntities = 32;

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

        // Scratch buffer used by Plugin.LateUpdate when handing snapshots to CoopHost. Reused
        // across frames to avoid per-frame allocations on the hot path.
        public static readonly EntitySnapshot[] HostBuffer = new EntitySnapshot[MaxSyncedEntities];

        // Diagnostic counters surfaced by the in-game overlay.
        public static int LastCapturedCount;
        public static int CacheSize => _byPath.Count;

        // Periodic re-walk cadence. Phase-transition bosses, mid-level spawns of stationary
        // animated objects, and any add/remove the sceneLoaded callback misses get picked up
        // here. ~2s is short enough that desyncs are brief and long enough that the cost is
        // negligible (~25ms × 0.5/s).
        private const float RefreshIntervalSec = 2f;
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
                // FindObjectsOfType is allocation-heavy but only runs at scene transitions.
                var entities = Object.FindObjectsOfType<AbstractLevelEntity>();
                int kept = 0, skipped = 0;
                foreach (var ent in entities)
                {
                    if (ent == null) continue;
                    var go = ent.gameObject;
                    if (go == null || !go.activeInHierarchy) { skipped++; continue; }

                    // Filter to entities with at least one Animator anywhere below them.
                    // Cuts out coins, parry-pickups, audio-only objects, etc.
                    var animator = ent.GetComponentInChildren<Animator>();
                    if (animator == null) { skipped++; continue; }

                    var t = ent.transform;
                    string path = ComputePath(t);
                    uint hash = Fnv1a32(path);

                    // Collisions: last write wins. Both sides will agree if both walk the scene
                    // in the same Unity-internal order, which is deterministic per build.
                    _byPath[hash] = new EntityRef { Transform = t, Animator = animator, Path = path };
                    kept++;
                }
                _cacheValid = true;
                Log?.LogInfo("EntitySync: cached " + kept + " entities (" + skipped + " skipped) for scene '"
                             + SceneManager.GetActiveScene().name + "'");
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
            if (!_cacheValid) { LastCapturedCount = 0; return; }

            foreach (var kvp in _byPath)
            {
                if (count >= MaxSyncedEntities) break;
                var er = kvp.Value;
                if (er.Transform == null) continue; // entity destroyed since last refresh

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

        /// <summary>Hierarchy path from scene root: "Root/Child/Grandchild".</summary>
        private static string ComputePath(Transform t)
        {
            // Walk up to the root, collecting names, then join in reverse.
            var stack = new Stack<string>(8);
            var cur = t;
            while (cur != null)
            {
                stack.Push(cur.name);
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
