using System.Collections.Generic;
using BepInEx.Logging;
using UnityEngine;

namespace CupheadCoop.Coop
{
    /// <summary>
    /// Cross-machine entity-type identification + client-side prefab lookup. Both host and
    /// client compute <see cref="HashType"/> the same way (FNV1a32 of <c>Type.FullName</c>),
    /// so a TypeId calculated on host can be looked up on client without any registry-sync
    /// packet — assuming both PCs run the same Cuphead build (which we already require).
    ///
    /// Why this matters (v0.9.0): when host streams an entity client doesn't have locally
    /// (e.g., a boss-summoned minion that only exists on host because client's AI is
    /// suppressed), client receives the snapshot with a path-hash that misses our cache.
    /// Without a TypeId, we'd just drop the entity — invisible. With it, we look up the
    /// TypeId in <see cref="ClientTypeRegistry"/> (populated as we walk the scene), find a
    /// local instance of the same type, <c>Object.Instantiate</c> from it, and place the
    /// new GameObject at host's transform. The entity becomes a pure render target.
    ///
    /// Population strategy: walk every <see cref="AbstractLevelEntity"/> and
    /// <see cref="AbstractProjectile"/> in the scene, hash its concrete Type.FullName, and
    /// remember the first GameObject we see for each type. We use the live instance as the
    /// "prefab template" for <c>Object.Instantiate</c> — this works because Cuphead's
    /// gameplay objects don't have scene-bound singleton state that would break on cloning.
    /// </summary>
    internal static class TypeRegistry
    {
        public static ManualLogSource Log;

        // Client-side: TypeId (FNV1a32 of Type.FullName) → live GameObject we can clone from.
        // Re-built on every scene load. Multiple instances of the same type just leave the
        // first one we encountered as the template; subsequent finds are skipped.
        private static readonly Dictionary<uint, GameObject> _clientByType = new Dictionary<uint, GameObject>();
        public static int ClientRegistryCount => _clientByType.Count;

        /// <summary>FNV1a32 of <paramref name="typeName"/>. Same algorithm as
        /// EntitySync's path hash — deterministic across .NET versions, stable across
        /// machines.</summary>
        public static uint HashType(string typeName)
        {
            const uint prime = 16777619u;
            uint hash = 2166136261u;
            for (int i = 0; i < typeName.Length; i++)
            {
                hash ^= typeName[i];
                hash *= prime;
            }
            return hash;
        }

        /// <summary>Convenience overload — hashes <paramref name="t"/>.FullName.</summary>
        public static uint HashType(System.Type t) => HashType(t?.FullName ?? "");

        /// <summary>Register a GameObject as a template for its component's runtime type.
        /// Called from <see cref="EntitySync.RefreshCache"/> on client mode. Idempotent —
        /// only the first GameObject seen for each TypeId is kept.</summary>
        public static void RegisterClientTemplate(System.Type t, GameObject go)
        {
            if (t == null || go == null) return;
            uint id = HashType(t);
            if (!_clientByType.ContainsKey(id))
                _clientByType[id] = go;
        }

        /// <summary>Look up a previously-registered template GameObject for a given TypeId.
        /// Returns null if no local instance of that type has been seen yet.</summary>
        public static GameObject GetClientTemplate(uint typeId)
        {
            _clientByType.TryGetValue(typeId, out var go);
            return go;
        }

        /// <summary>Drop the current registry. Called on scene unload + client disconnect so
        /// stale references don't survive into the next scene/session.</summary>
        public static void ClearClient()
        {
            _clientByType.Clear();
        }
    }
}
