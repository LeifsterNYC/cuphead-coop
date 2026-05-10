# M7 — runtime-spawn entity sync

Design notes; not yet implemented.

## Why M6 alone isn't enough

M6 catches scene-loaded `AbstractLevelEntity` instances at the path-hash level and refreshes the cache every 2s. That's enough for stationary boss objects, mini-bosses, and animated set pieces. It misses:

- **Projectiles.** Spawned from object pools each time the boss attacks. Path becomes `Pool/Bullet (Clone)`, `Pool/Bullet (Clone) (Clone)`, …
- **Summoned mobs.** E.g., the Dragon's flame sprites, the Devil's minions.
- **Phase-2+ boss instantiations.** Hilda Berg → Moon → UFO replaces the GameObject entirely.
- **Pickups.** Health pickups, parry pickups, coins.

Without M7, the client's local Cuphead simulates these independently. With M5+M6 mirroring the boss's animation state, the local sim's projectile timings are *approximately* aligned to the host's, so the visual divergence is bounded — but each PC ends up with its own projectile world.

M9 currently saves the client from dying to those local projectiles. The remaining gap is **visual fidelity** (projectiles you see on the host should be the projectiles flying at you on the client) and **damage detection on the host** (host's local projectile colliding with the host-streamed-puppet of the client's player → host registers a hit → host streams HP loss → client sees HP drop). The latter already works *if* the client's puppet reaches the same screen position the host sees, which is what M4 does.

## Strategy options

### A. Spawn-event hooking with synthetic IDs (full authoritative)

Patch every `AbstractLevelEntity.Awake` (or a spawn-event hook) on the host to assign an incrementing `NetworkID`. Stream a `SpawnEvent` packet on creation and `DespawnEvent` on destruction. Host's per-tick state stream uses `NetworkID` to mirror.

Client side: on `SpawnEvent`, instantiate a placeholder using a host-supplied prefab key. On `DespawnEvent`, destroy. Client's local sim must NOT spawn locally — that means patching `Object.Instantiate` (or each spawner) to skip when `Mode == Client`.

**Pros:** authoritative, deterministic.
**Cons:** invasive — suppressing local spawns may break boss AI that observes its own children, audio cues, and a thousand other small couplings. High risk of breaking things.

### B. Path-pattern hashing with index disambiguation (extended M6)

Same path-hash cache, but with smarter naming for clones. Strip the `(Clone)` suffix and use parent + index-among-siblings for disambiguation. Both sides walk in the same Unity-internal order, so `Bullet[3]` should be the same projectile on both PCs.

**Pros:** stays in M6's lane; no spawn-event plumbing.
**Cons:** ordering isn't guaranteed across machines if spawn timing drifts even one frame. Brittle.

### C. Don't sync clones; let local sim handle them, and trust M6 + animation alignment

Don't track runtime-spawned objects at all. Trust that M5 (animation state sync) keeps boss attacks in phase, so each PC's local sim spawns its own projectiles at approximately matching times. M9 prevents the client from dying to mismatch.

**Pros:** zero new code. Already works to a useful extent.
**Cons:** any divergence in local sim timing → projectiles shift. Long bossfights drift further apart.

### D. Hybrid: stream just the projectiles (transform-only, no spawn events)

Skip prefab/instantiate logic. Each frame on host, enumerate active `AbstractProjectile` (or whatever the projectile base is) GameObjects and stream their transform-only as a separate compact array. Client looks up local projectiles in stable order by *spatial proximity to host-streamed positions* and overrides. Misses get ignored.

Lighter than (A). Works if projectile counts are roughly equal between host and client (which they should be if animation states match).

**Pros:** non-invasive, no spawn-event patching, leverages local sim for animation/lifetime.
**Cons:** requires identifying the projectile base class (likely `BasicProjectile` or `LevelObjectsProjectile` — needs Inspect dump). Spatial matching can fail if there are many projectiles in flight.

## Recommendation

**Start with (C) and observe.** v0.4–v0.6 might already produce a "good enough" experience — M5 keeps animation phases aligned, M6 keeps boss positions aligned, M9 keeps the client alive. Run a real two-PC test with two real Cuphead instances and SEE how badly things drift before investing in spawn sync.

If drift is unacceptable, escalate to **(D) projectile-only transform stream**:
- Inspect: identify the projectile base class (`BasicProjectile`, `AbstractProjectile`, etc.)
- Add `ProjectileSnapshot[]` to StateSnapshot (separate from EntitySnapshot[])
- Cap at 64 projectiles/snapshot (~64 × 16 bytes = 1 KB per snapshot)
- Client matches local projectiles to host snapshots by nearest-neighbor in (x,y) space; assigns each to the closest unmatched local projectile and overrides its transform
- Stale local projectiles (no matching host snapshot) get despawned via Object.Destroy

If THAT still isn't enough, escalate to **(A) full authoritative spawn sync** — but that's a multi-day project and probably the wrong scope for a private mod.

## What this doesn't fix

- Boss phase transitions where the GameObject is destroyed and a new one instantiated. M6's 2-second refresh catches the new one's path but loses the in-progress animation state of the old. v1 limitation; small fix would be to also stream "phase transition" events from host.
- Audio cues. Each PC plays its own audio. SFX timing diverges with animation drift.
- Score/damage-dealt tracking. Host's local projectiles deal damage to host's local boss; client's local projectiles do the same to client's local boss. Both PCs see the boss's HP go down at similar rates but not identically. UI display isn't synced.

## Out of scope

- HP/damage authority for the boss itself. That's M10 territory: stream boss HP host→client.
- Network compensation (lag interp, dead reckoning). Current impl is snap-to-position. At 30 Hz over LAN/ZeroTier this is fine; over WAN it'd benefit from interpolation.
