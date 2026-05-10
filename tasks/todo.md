# Cuphead Online Co-op Mod — Plan

## Goal
Replace Steam Remote Play with real client-server networked co-op for 2 players.
Host runs the simulation; client streams inputs and receives state snapshots.
Target: precision-platformer-acceptable feel (~50ms perceived lag for remote player, predicted locally).

## Architecture (decided)
- **Engine target**: Cuphead (Steam, with Delicious Last Course DLC). Unity Mono build at `F:\SteamLibrary\steamapps\common\Cuphead`.
- **Input library in game**: Rewired (`Rewired_Core.dll`). Intercept at Rewired Player 1 (Cuphead's Player 2) via Harmony patches on `Rewired.Player.GetButton*` / `GetAxis*`.
- **Loader**: BepInEx 5.x (Mono-flavor). Harmony 2.x for patches.
- **Transport**: LiteNetLib (UDP, reliable+unreliable channels). Bundled as a DLL into the plugin folder.
- **Topology**: Host-authoritative P2P. Host runs full sim. Client sends inputs at fixed rate, receives snapshots.
- **Scope**: 2 players only. LAN first, then NAT-traversal (LiteNetLib has built-in NAT punch).
- **Determinism**: Not required — host is authoritative. Client predicts only its own avatar locally and reconciles to host snapshots.

## Milestones

### M1 — Hello World + Input Hook (CURRENT)
- [x] Confirm install path and Mono build
- [ ] Install BepInEx 5 and verify it loads (BepInEx log appears in game folder)
- [ ] Scaffold `CupheadCoop` BepInEx plugin csproj
- [ ] Decompile Assembly-CSharp to identify Rewired wiring + Cuphead's player class
- [ ] Plugin loads in Cuphead, logs Rewired player count + player names to BepInEx console
- [ ] Harmony-patch Player 2 button reads to hardcoded "always jumping" — visual proof input is intercepted
- **Verification**: Launch Cuphead local co-op. Without touching P2 controls, the cup should jump every frame. If yes, the intercept point is right.

### M2 — Network Transport
- [ ] Bundle LiteNetLib.dll into plugin
- [ ] Add `CoopHost` + `CoopClient` services started by BepInEx config flag
- [ ] Define wire protocol v0: handshake, input frame (16 bits of buttons + axes), heartbeat
- [ ] CLI keys (F9 host, F10 connect to 127.0.0.1) for dev iteration
- **Verification**: Two Cuphead instances on same machine connect; logs show input frames flowing client → host.

### M3 — Networked Input End-to-End
- [ ] Host's Player 2 input source = network input buffer (latest received frame)
- [ ] Client's local Player 2 input is captured and sent to host every fixed-update tick
- [ ] Buffer compensates for jitter (~2-frame buffer; tunable)
- **Verification**: Two instances. Host opens local co-op. Client connects. Client's keyboard drives the second cup on host's screen.

### M4 — Spectator Sync (client sees what host sees)
- [ ] Decide minimum-viable visual sync: stream host camera + key transforms? Or full simulation replay?
- [ ] Likely path: render-only client — host sends transforms of P1, P2, bosses, projectiles, hit events. Client doesn't simulate.
- [ ] Alternative: client also runs sim, host sends RNG seeds + corrections.
- *Decision deferred to after M3 — will pick based on observed perf.*

### M5 — UX & Stability
- [ ] In-game menu (BepInEx ConfigEntry-driven hotkeys, then later IMGUI overlay)
- [ ] Disconnect/reconnect handling
- [ ] NAT punch via LiteNetLib's NatPunchModule

## Open questions / risks
- **Cuphead's update loop**: Is gameplay logic on FixedUpdate or Update? Affects input-tick timing. (To answer in M1 decompile.)
- **State sync scope (M4)**: Full simulation mirroring will require touching boss RNG, particle managers, audio cues. Could balloon scope. Render-only client is safer first cut.
- **Anti-cheat / online**: None to worry about — Cuphead is offline; modding is benign here.
- **DLC compatibility**: Should "just work" since DLC is content, not engine changes. Will verify in M1.

## Review (per milestone, append below)

### M1 — input intercept (verified)
ForceP2WalkRight test: P2's cup walked right autonomously in local 2P co-op. Harmony postfixes on `Rewired.Player.GetAxis(int)` land cleanly. Rewired discovery via `PlayerInput.Init` postfix is deterministic — both P1 and P2 ids resolved on first level entry.

### M2/M3 — transport + input streaming (verified)
Single-PC mock-client test: handshake completes (`handshake ok (v3)`), input frames flow at 60 Hz, mock client's walk-right pattern moves the host's P2. Sidestepped LiteNetLib NatPunchModule's System.Runtime.Serialization dependency by Harmony-skipping its constructor.

### M4 — visual sync v1 (partially verified)
StateSnapshot stream from host to client works (mock client receives `rx state seq=N`). When window loses focus, Update pauses — fixed by `Application.runInBackground = true`. ScenePuppetry's player-position capture only succeeds in actual gameplay levels (not menus / file select); diagnostic logging now reports `DoesPlayerExist=false` etc. clearly.

### M5 — animation sync (shipped, awaiting in-game verification)
PlayerSnapshot extended with Animator state hash + normalized time. Protocol bumped to v3. Client calls `Animator.Play(hash, 0, time)` only on state changes or significant phase drift, avoiding per-frame stutter resets.

### M6 — entity sync (shipped as v0.4.0, awaiting in-game verification)
- `EntitySync` cache: `Object.FindObjectsOfType<AbstractLevelEntity>` → filter by descendant `Animator` → FNV1a32 hash of scene-relative hierarchy path
- Wire format: `EntitySnapshot[]` appended to `StateSnapshot`; cap 32 per packet (~24 KB/s @ 30Hz)
- Periodic refresh every 2s catches phase-transitions and runtime spawns the `sceneLoaded` callback misses
- Overlay shows `ents=N` (host: captured) and `ents=R/C` (client: received / cache size)
- Hash misses are silently dropped — covers transition races and runtime-spawned objects v1 doesn't track

### Test plan for M5+M6 (next session)
1. Get into a real run-and-gun stage (Bootleg Boy, Hilda Berg, etc.). 2P co-op required for P2 capture.
2. F9. Overlay flips to `mode=Host`.
3. Run mock client — should print `entities=N` with N > 0 once the boss is in the scene.
4. BepInEx log should show `EntitySync: cached N entities for scene 'level_xyz'` after each scene load.
5. With a real Mac client, the boss should mirror visually.

### Open after M6
- M7: runtime-spawn entity sync (projectiles, summoned mobs, phase-2 boss instantiations). See `tasks/M7-design.md`. Recommendation: try option (C) — do nothing, see if it's good enough — before investing in spawn-event plumbing.
- M8.5: apply IsDead on client (mirror death animation). Not as simple as calling `OnDeath()` — see lesson in `tasks/lessons.md`.
- M10: stream boss HP / phase events.
- Polish: reconnect handling, in-game UI, audio cue events, level-end mirror.

### M7 — entity sync (shipped as v0.4.0, partially verified — Veggies/Potato visible)

### M8 — HP capture (shipped as v0.5.0, verified host-side via mock client `hp=N/M`)
Client-side HP application unverified — needs real host streaming.

### M8 + M9 — client damage suppression (shipped as v0.5.0, **VERIFIED** in-game)
F10-without-host trick: press F10, take damage in local 2P. Projectile collides, despawns, HP unchanged. M9's Harmony prefix on `PlayerDamageReceiver.TakeDamage` is live and effective.

### Pause sync (shipped as v0.6.0, unverified)
Host sample → snapshot → client `PauseManager.Pause/Unpause` apply. Local pause input on client is suppressed via Harmony prefix gated on `PauseSync.RemoteDriven`.
