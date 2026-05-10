# Night 2 result — handoff for testing

## What landed this session

| Layer | Status | Notes |
| --- | --- | --- |
| **M3 transport + input streaming** | ✅ verified | Mock client handshakes, P2 walks right under remote control |
| **NatPunchModule sidestep** | ✅ verified | Harmony-skip the ctor — was the silent F9-fail blocker |
| **Application.runInBackground** | ✅ verified | Snapshots no longer pause when alt-tabbed |
| **M5 animation sync (P1/P2)** | shipped, awaiting in-game verification | Cups should animate matching the host |
| **M6 entity sync (boss + animated scene actors)** | shipped, awaiting in-game verification | Bosses mirror across PCs; runtime spawns deferred to M7 |
| **EntitySync periodic refresh** | shipped | Catches boss phase transitions on a 2s cadence |
| **Overlay entity counts** | shipped | `ents=N` (host captured) and `ents=R/C` (client received / cache size) |
| **Plugin Version constant** | bumped to **0.4.0** | Wire protocol bumped to **v4** |

Full repo at `ccdac6c` (or whatever `git log -1` says when you read this).

## Tomorrow's test plan (single-PC, with mock client)

1. **Quit Cuphead** if it's running (the v0.4.0 DLL has been deployed but the running game still has v0.3.0 cached).
2. **Relaunch Cuphead.** Black BepInEx console should appear. Should log `CupheadCoop 0.4.0 loading…`.
3. **Get into a real run-and-gun stage.** 2P co-op required for P2 capture. Boss fights are best — they have animated entities our cache picks up.
4. **F9 → host.** Overlay top-left flips to `mode=Host`. BepInEx log: `EntitySync: cached N entities for scene 'level_xyz'`.
5. From PowerShell in repo root:
   ```sh
   dotnet run --project tools/MockClient
   ```
6. Watch:
   - **Mock client output**: `entities=N` should now be > 0 (was 0 before M6).
   - **Host's screen**: P2 walks right autonomously (mock client sends walk-right pattern).
   - **Overlay**: `tx state p1=… p2=…  ents=N` showing the captured entity count each frame.
7. **`Ctrl+C` mock client** to stop.

## Likely issues to watch for

- **`ents=0` on host even mid-fight.** Means `FindObjectsOfType<AbstractLevelEntity>` returned nothing or filter killed everything. Check log for the `EntitySync: cached N entities` line; if N=0, the filter (descendant Animator required) is too strict for this level. Fix: relax the filter or remove the Animator requirement.
- **`ents` counts host-side keep changing wildly** between refreshes. Expected during phase transitions; a problem if it happens constantly. May indicate scene contains transient entities that fail the active-in-hierarchy check intermittently.
- **Mock client connection dies after ~5s with `Timeout`.** Means Cuphead's Update stopped firing. With `runInBackground=true`, this should only happen if the game crashes — check log for exceptions.
- **Plugin loads as 0.3.0 instead of 0.4.0.** BepInEx cached the old DLL. Delete `<game>/BepInEx/cache` and relaunch, OR force-redeploy:
  ```sh
  cd CupheadCoop && dotnet build -c Debug --no-incremental
  ```

## Mac side will need re-deploy

Protocol v3 → v4 (entities array added). Mac's plugin must rebuild from source or rerun `setup-mac.sh` against `dist/CupheadCoop-mac-v0.4.0.tar.gz`. Without that, handshake fails with `protocol version mismatch (host=4 self=3)`.

## What's queued (not started)

- **M7 — runtime-spawn entity sync.** Projectiles, summoned mobs, phase-2 boss instantiations. Hardest of the open work — needs spawn-id system or different identification approach (path-hashing won't work for clones).
- **M8 — HP/death/score sync.** Smaller, well-bounded. Adds HP + IsDead to PlayerSnapshot and applies on client.
- **M9 — suppress client-side sim influence.** Most invasive. Patches `PlayerDamageReceiver.TakeDamage` to no-op when `Mode == Client`, plus other surgery to make the host fully authoritative for damage/death. Risk: subtle state machines may break.
- **Pause/menu sync.** When host pauses, client pauses. Easy to wire once we find the pause API.

## Key files to read first when picking this up

- `tasks/todo.md` — full milestone plan with M1–M6 reviews and M7+ list
- `tasks/M6-design.md` — entity sync strategy (has open-questions section relevant to M7)
- `CupheadCoop/src/Coop/EntitySync.cs` — the hot path for M6
- `CupheadCoop/src/Net/Protocol.cs` — wire format, version-bump comment chain
- `tasks/lessons.md` — landmines we already hit; read before debugging anything subtle
