# Night 3 result — overnight work + tomorrow's test plan

## What landed since you went to bed

| Layer | Change | Status |
|---|---|---|
| **M12 P2 auto-join** | Force-joins P2 via reflection on PlayerManager when client connects, on BOTH host and client. Host so its P2 has a body to drive from network input. Client so the local sim has a P2 cup to puppet against host's streamed transform. | shipped, awaiting verification |
| **Reconnect** | Client auto-retries every 3s on disconnect (Timeout / RemoteConnectionClose). Cancellable with disconnect hotkey. Overlay shows reconnect countdown. | shipped |
| **Animator param block — targeted** | v0.7.6 blanket-blocked all Animator setters in client mode, which broke boss attacks (bosses' state machines couldn't transition → no projectiles fired). v0.8.0 only blocks setters on registered player-cup animators (registered when ScenePuppetry first finds them). Bosses run normally. | shipped, almost certainly fixes "no projectiles" |
| **Split execution order** | Plugin keeps `[DefaultExecutionOrder(-32000)]` so its Update sees network packets before PlayerMotor reads input. New `CoopLateApply` MonoBehaviour with `[DefaultExecutionOrder(+32000)]` hosts the LateUpdate phase — transform/animator overrides now happen AFTER Cuphead's animator writers, so we win the LateUpdate race. Should resolve the cup-snaps-between-2-frames flicker that survived the v0.7.6 block. | shipped, animation flicker should be gone |
| **EntitySync expanded** | Now tracks `AbstractProjectile` in addition to `AbstractLevelEntity`. Cap raised 32 → 64. Refresh interval tightened 2s → 0.5s (projectiles are sub-second-lived). | shipped |
| **Sibling-index path-hash** | For runtime `(Clone)` GameObjects, path-hash uses the GameObject's sibling-index instead of name, so projectile #3 spawned by a given parent on host hashes to the same value as projectile #3 on client. Both sides produce the same hash for the same logical projectile when spawn order is deterministic. | shipped |
| **Alive-hash list** | StateSnapshot v8 now carries up to 256 alive entity hashes (separate from position-tracked entities). Client `SetActive(false)` on any cached entity whose hash isn't in the list. Fixes "enemies don't die on client" — host-killed mobs / despawned projectiles disappear on client immediately. | shipped |
| **Multi-animator registration** | When ScenePuppetry locates a player cup's animator, it registers ALL animators in the cup's hierarchy (body, weapon, fx) with the param-block patches. Without this, secondary animators on the cup could still drift independently. | shipped |
| **Overlay enhancements** | Now shows ping (latency), reconnect countdown, dialing/waiting state, host's HasClient indicator. | shipped |
| **Launcher** | Kills any existing Cuphead processes before launching, so re-running the script doesn't stack windows. | shipped |
| **Wire protocol bump** | v7 → v8. AliveHashes added to StateSnapshot. Both PCs need v0.8.0 to handshake. | shipped |

Released as **v0.8.0** at https://github.com/LeifsterNYC/cuphead-coop/releases/tag/v0.8.0

## Test plan when you wake up

Sleep + coffee then:

1. `.\setup\launch-coop-both.ps1` — both Cupheads start, side-by-side, windowed.
2. **Host window**: focus, plug in your controller, get to a level (controller auto-joins P2 on host). Press F9. The new auto-join code shouldn't be needed since you're using a controller — but it'll make sure P2 stays joined if it ever drops.
3. **Client window**: focus, press F10. Should see in the overlay:
   - `mode=Client  ping=Nms`
   - `seq=` ticking up
   - `p1=` and `p2=` showing real coordinates
   - `ents=N/M` non-zero
4. **Critical test**: when you press jump on the client window keyboard (Z by default in Cuphead), does host's P2 jump on the host's screen? With the v0.7.6 execution-order fix this should work, but verify.
5. **Animation test**: focus client, watch P1 cup (which is mirroring host's P1 via stream). Cuphead-on-client should animate (run, jump, dash, shoot) matching host. No 2-frame flicker.
6. **Enemy death test**: in a boss fight or run-and-gun stage, when host kills an enemy, the same enemy should disappear on client via the alive-hash deactivation path.
7. **Pause test** still works.
8. **Map sync test** still works.
9. **Reconnect test**: kill client (Ctrl+C powershell), wait, restart. Or stop host (F11) and restart F9. Client should auto-reconnect.

Specifically watch:
- **Both cups visible on client?** The new client-side P2 auto-join should fix the "only one player on client" issue.
- **Projectiles visible during boss attacks?** The v0.8.0 targeted-block + EntitySync of `AbstractProjectile` should make boss attacks render on client.

## Likely gotchas to watch for

- **First connect after launching** might need the host to be in a level scene, not at title/file-select/map. The auto-join only fires when client connects, not at scene transitions, so if host is on title screen when client connects, the join state lands on title screen's PlayerManager and might be reset by file-select transition. Mitigation: get host into a level BEFORE pressing F9.
- **The plugin DLL got bigger** (~54 KB) due to all the patches. Mac BepInEx might have different behavior with bigger plugins; if Justin reports load failure, check his log first.
- **WIRE PROTOCOL v8** — Justin needs to update his Mac to v0.8.0. Old v0.7.x won't handshake.

## Still open / deferred

- **M8.5 death animation mirror**: when host's P2 dies and becomes a ghost, client's local P2 cup stays alive visually. We capture IsDead but don't apply it. Real fix would mirror Cuphead's death sequence (hard — touches a state machine spread across multiple classes). Acceptable for now since combat is mostly working.
- **Map state sync** (level clears, flags, coins): saves are stored in user appdata. Out of scope for transform sync.
- **Audio sync**: each PC plays its own SFX. Acceptable.
- **In-game menu UI**: still cfg-file driven. Could add an IMGUI panel for IP/port/key entry.
- **Spawn-event hooking** (proper M7 with explicit network IDs): the path-hash + alive-list approach should be "good enough" for most cases. Real implementation hooks AbstractProjectile.Awake and assigns synthetic IDs.

## Files to read first if picking this up

- `tasks/todo.md` — milestone plan with M1–M12 status
- `tasks/lessons.md` — landmines we already hit
- `tasks/M7-design.md` — design notes for runtime spawn sync (now partially shipped via path-hash + alive list)
- `CupheadCoop/src/CoopLateApply.cs` — new in v0.8.0, hosts the late-LateUpdate phase
- `CupheadCoop/src/Coop/AnimatorPatches.cs` — targeted parameter block via registry
- `CupheadCoop/src/Coop/P2AutoJoin.cs` — reflection-based force-join

Repo at the v0.8.0 tag.
