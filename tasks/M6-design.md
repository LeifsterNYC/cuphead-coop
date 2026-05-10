# M6 — entity sync (boss + scene actors)

Design notes; not yet implemented.

## Problem

After M5, the host streams P1 + P2 (position + facing + animator state). The client's cups now move and animate matching the host. But everything else — boss, projectiles, particles, level events — runs independently in each PC's local sim. Result: client and host visually diverge as soon as a boss does anything.

**Goal:** mirror the host's gameplay-relevant scene actors on the client so both screens show the same fight.

## Why this is hard

- Cuphead has no single "boss" base class. Each level has its own type (`RumRunnersLevelMobBoss`, `ChessBOldBLevelBoss`, …).
- Entity instance IDs (`GetInstanceID()`) are per-process; useless across machines.
- Many entities are runtime-spawned, so naming is unstable (`Bullet(Clone)4`).
- Multi-part bosses transform across phases; child objects appear/disappear.
- Bandwidth: at 30 Hz, even 50 entities × 24 bytes = ~36 KB/s. Acceptable for LAN/ZeroTier; would matter over WAN.

## Strategy: scene-path identification, scoped to scene-loaded entities

Skip dynamic spawns for v1. Sync only entities that exist in the scene at level-load time. Bosses qualify (loaded with the level scene). Projectiles/spawned enemies do not.

1. **Hook scene load.** `UnityEngine.SceneManagement.SceneManager.sceneLoaded += OnSceneLoaded;` rebuilds the entity cache.
2. **Build cache:** enumerate `Object.FindObjectsOfType<AbstractLevelEntity>()` in the loaded scene. For each, compute a stable ID:
   ```
   pathHash = FNV1a32(scene.name + "/" + transform.GetFullPath())
   ```
   where `GetFullPath` walks parents up to root. FNV1a is deterministic across .NET versions, unlike `string.GetHashCode`.
3. **Filter:** keep only entities with at least one descendant `Animator` component (these are the visually-animated ones — bosses, set pieces). Cap at 32 entities per snapshot.
4. **Capture (host, every snapshot tick):** for each cached entity, emit `(pathHash, x, y, scaleX, scaleY, animHash, animTime)`.
5. **Apply (client, every render frame after a snapshot arrives):** look up local entity by `pathHash` from the same cache, override its transform + animator. Hash misses are silently ignored (handles brief desyncs during scene transitions).

## Wire format additions

```
StateSnapshot {
  uint   Sequence
  ushort HostTickMs
  PlayerSnapshot P1, P2
  byte           EntityCount        // <= 32
  EntitySnapshot Entities[EntityCount]
}

EntitySnapshot {
  uint  PathHash
  float X, Y
  float ScaleX, ScaleY
  int   AnimStateHash
  float AnimNormalizedTime
}
```

Per-entity payload: 24 bytes. Bump `Protocol.Version` 3 → 4.

## Open questions

- **Multi-part bosses.** Some bosses have child objects whose transforms move independently of the parent (e.g., a boss head + tentacles). Need to verify those children also have their own paths picked up by the enumeration.
- **Scene-static vs respawned.** What happens on player death/retry? Cuphead may reload the scene. Cache rebuild is triggered by `sceneLoaded`, so should be OK. Worth verifying.
- **Phase transitions.** Cuphead bosses morph through phases — sometimes destroying the current GameObject and instantiating a new one. The new phase object is a runtime spawn → not in our cache. v1 will fail to sync phase-2/3.
- **Filtering.** The `has descendant Animator` filter may include too much (every projectile pool entry, every coin) or too little. May need a per-level allow-list once we hit specific cases.

## Out of scope for v1

- Spawned projectile/enemy sync. (Future M7.)
- Boss HP / damage / phase events. (Future M8.)
- Audio cue sync. (Future M9.)
- Suppressing the client's local sim entirely. Right now the client still simulates locally and our host writes are overrides on top. A bullet hit landed locally on the client may damage the local cup based on stale local positions; the host's damage is what counts but the client's HP UI may briefly desync. (Future M10.)

## Implementation skeleton

```
Coop/
  EntitySync.cs       // RefreshCache(), CaptureForHost(out arr), ApplyToClient(arr)
                      // Internal: Dictionary<uint, EntityRef> _byPath; FNV1a32 helper
Plugin.cs LateUpdate:
  if (Mode == Host) { ScenePuppetry.HostCapture(); EntitySync.CaptureForHost(out hostEntitiesBuf); CoopHost.TickStateSnapshot(...); }
  if (Mode == Client) { ScenePuppetry.ClientApply(); EntitySync.ApplyToClient(CoopState.RemoteEntities); }
```

Sized to land in roughly the same scope as the M4 → M5 work: ~3 new files, edits to 4 existing.
