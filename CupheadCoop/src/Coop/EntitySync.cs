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
            public uint TypeId; // v11: FNV1a32(Type.FullName) — used by host's CaptureForHost
                                // to send the right TypeId to client even if the entity instance
                                // changes; precomputed once at register time.
        }

        // v0.9.0: track which AbstractLevelEntity components we've disabled on client so we
        // can re-enable them when the session ends (otherwise re-entering the level in
        // single-player keeps a dead boss). Keyed by Component InstanceID for fast removal.
        private static readonly Dictionary<int, AbstractLevelEntity> _clientDisabled
            = new Dictionary<int, AbstractLevelEntity>();

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
        // Client-side: counters from last ApplyToClient + ApplyAliveSet pass. Lets us tell the
        // difference between "host didn't send anything" vs "host sent N entities but only K
        // matched our cache" (path-hash drift between host and client).
        public static int LastApplyHits;
        public static int LastApplyMisses;
        public static int LastDeactivated;
        public static int LastSpawnedFromHost; // v11: how many of the hits this tick came via Instantiate-from-template

        // Periodic re-walk cadence. Back to 2s now that projectiles are owned entirely by
        // ProjectileSync — this refresh only needs to catch late-spawned scene entities
        // (boss phase 2, shopkeepers, cutscene cleanup), which are not sub-second-lived.
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
                int kept = 0, skipped = 0;

                // Scene-loaded gameplay entities. Catches bosses, scenery animations,
                // mini-bosses, set pieces. Path-hash works because these are stable scene objects.
                // Projectiles are NOT walked here — they're handled entirely by ProjectileSync
                // (host-assigned NetworkIDs), which survives divergent client spawn order that
                // path-hashing can't.
                var entities = Object.FindObjectsOfType<AbstractLevelEntity>();
                foreach (var ent in entities) { if (TryAdd(ent?.gameObject, ref kept, ref skipped, "le")) {} }

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
                // CRITICAL: gates in CaptureForHost/ApplyToClient/ApplyAliveSet check
                // _cacheValid before doing anything. Forgetting this flip in v0.4.0–v0.8.0
                // silently dead-stopped the entire entity sync layer (host's tx ents=0,
                // client's bosses/projectiles/death-sync all no-op).
                _cacheValid = true;
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
                    animTime = AnimUtil.SampleTime(st);
                }

                var pos = er.Transform.position;
                var scale = er.Transform.localScale;
                HostBuffer[count] = new EntitySnapshot
                {
                    PathHash = kvp.Key,
                    TypeId = er.TypeId,
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

            int deactivated = 0;
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
                    if (go.activeSelf) { go.SetActive(false); deactivated++; }
                }
            }
            LastDeactivated = deactivated;
        }

        public static void ApplyToClient(EntitySnapshot[] snapshots, int count)
        {
            if (!_cacheValid) { LastApplyHits = 0; LastApplyMisses = 0; return; }

            int hits = 0, misses = 0, spawned = 0;
            for (int i = 0; i < count; i++)
            {
                var s = snapshots[i];
                if (!_byPath.TryGetValue(s.PathHash, out var er) || er.Transform == null)
                {
                    // v11: try to instantiate from local prefab registry. Host has an entity
                    // we don't — most commonly a boss-summoned minion that spawned only on
                    // host because client's AI is suppressed. Look up by TypeId, clone, place.
                    GameObject template = (s.TypeId != 0 && ModConfig.EnableSpawnFromHost.Value)
                        ? TypeRegistry.GetClientTemplate(s.TypeId)
                        : null;
                    if (template == null) { misses++; continue; }
                    GameObject newGo;
                    try
                    {
                        newGo = Object.Instantiate(template);
                    }
                    catch (System.Exception ex)
                    {
                        Log?.LogWarning("EntitySync: spawn-from-host Instantiate failed for typeId=" +
                                         s.TypeId.ToString("X") + ": " + ex.Message);
                        misses++;
                        continue;
                    }
                    var t = newGo.transform;
                    t.position = new Vector3(s.X, s.Y, t.position.z);
                    t.localScale = new Vector3(s.ScaleX, s.ScaleY, t.localScale.z);
                    var newAnim = newGo.GetComponentInChildren<Animator>();
                    var newAi = newGo.GetComponent<AbstractLevelEntity>();
                    // Disable AI on the spawned-from-host clone so it stays a pure render
                    // target — same policy as native-cached entities on client.
                    if (newAi != null && ModConfig.EnableClientEntityAISuppress.Value)
                    {
                        newAi.enabled = false;
                        _clientDisabled[newAi.GetInstanceID()] = newAi;
                    }
                    er = new EntityRef
                    {
                        Transform = t,
                        Animator = newAnim,
                        Path = "<spawned-from-host>",
                        TypeId = s.TypeId
                    };
                    _byPath[s.PathHash] = er;
                    spawned++;
                    hits++;
                    // fall through to apply transform/animator below — newly spawned entity
                    // is at host's position, ready for the rest of the normal apply path.
                }
                else hits++;

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
                        AnimUtil.Scrub(er.Animator, s.AnimStateHash, s.AnimNormalizedTime);
                    }
                }
                catch
                {
                    // entity went away mid-apply (scene unload race) — skip
                }
            }
            LastApplyHits = hits;
            LastApplyMisses = misses;
            LastSpawnedFromHost = spawned;
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
        //
        // v0.9.0 client-side responsibilities also handled here:
        //   - Hash the entity's runtime Type.FullName and store in TypeRegistry as a prefab
        //     template (so we can Instantiate later if host streams a not-yet-cached entity).
        //   - If EnableClientEntityAISuppress is on AND this is an AbstractLevelEntity (not
        //     a projectile, which has its own lifecycle), set the AI component's
        //     MonoBehaviour.enabled = false so Unity stops calling Update/FixedUpdate on it.
        //     Track the disabled component in _clientDisabled so we can re-enable on disconnect.
        private static bool TryAdd(GameObject go, ref int kept, ref int skipped, string layerTag)
        {
            if (go == null) return false;
            if (!go.activeInHierarchy) { skipped++; return false; }
            var animator = go.GetComponentInChildren<Animator>();
            if (animator == null) { skipped++; return false; }
            string path = ComputePath(go.transform);
            uint hash = Fnv1a32(path);

            uint typeId = 0;
            // Get the most-derived component on this GameObject for the type registry. Only the
            // "le" layer exists now (AbstractLevelEntity / its concrete subclass); projectiles
            // are handled by ProjectileSync, not here.
            if (layerTag == "le")
            {
                var ai = go.GetComponent<AbstractLevelEntity>();
                if (ai != null) typeId = TypeRegistry.HashType(ai.GetType());
                if (CoopState.Mode == CoopMode.Client)
                {
                    if (ai != null) TypeRegistry.RegisterClientTemplate(ai.GetType(), go);
                    if (ai != null && ModConfig.EnableClientEntityAISuppress.Value && ai.enabled)
                    {
                        ai.enabled = false;
                        _clientDisabled[ai.GetInstanceID()] = ai;
                    }
                }
            }

            _byPath[hash] = new EntityRef { Transform = go.transform, Animator = animator, Path = path, TypeId = typeId };
            if (kept < MaxSyncedEntities) kept++;
            return true;
        }

        /// <summary>Re-enable any AbstractLevelEntity AI components we disabled on client so
        /// next single-player session has working bosses. Called from CoopState.Reset.</summary>
        public static void RestoreClientDisabled()
        {
            foreach (var kvp in _clientDisabled)
            {
                if (kvp.Value != null) kvp.Value.enabled = true;
            }
            _clientDisabled.Clear();
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
