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

### v1.0.0 — Steam P2P transport (in progress this session)
- [x] Transport abstraction: `IHostTransport`/`IClientTransport` (src/Net/Transport.cs); CoopHost/CoopClient are now transport-agnostic protocol layers
- [x] `UdpTransport` — existing LiteNetLib wire, kept for LAN + solo two-instance testing + MockClient
- [x] `SteamTransport` — SteamNetworking P2P via Cuphead's bundled Steamworks.NET (Assembly-CSharp-firstpass). Session synthesis: HELLO(key)/BYE/PING/PONG/DATA/DATA_SEQ opcodes; latest-wins ushort seq for snapshots; >1150-byte snapshots ride reliable (Steam drops unreliable >1200B); relay fallback enabled (free NAT traversal)
- [x] Config: `[Network] Transport = Steam|Udp` (default Steam), `HostSteamId` (client dials host's SteamID64), `AutoStart = Off|Host|Connect` (no-hotkey startup; client retries until host up)
- [x] Host's SteamID64 shown in overlay + log when hosting (the value the second player needs)
- [x] BepInEx reinstalled into both installs (was missing entirely — only .doorstop_version remained)
- [x] Solo smoke test via Goldberg emu — PASSED headless (-batchmode): HELLO/key handshake, Welcome v11, 30Hz snapshots (484 rx), 60Hz inputs (1382 tx), ping keepalive, kill-client → host timeout → re-accept all verified. UDP regression identical (486/1380).
- [x] v1.0.0 dist bundles built (win zip / mac tar.gz), CLIENT-INSTRUCTIONS updated for Steam flow
- [ ] Real two-PC Steam test (Windows host + Mac client, real Steam both sides) — needs Leif + second player; Mac must update to v1.0.0 and set HostSteamId

### v1.1.0 — first real two-PC session fallout (shipped, awaiting two-PC re-test)
First Windows↔Mac Steam session (2026-07-04): connection clean, gameplay sync "terrible —
animations not synced, deaths, etc." Root causes found by code audit; none architectural.

- [x] Snapshot interpolation (`SnapshotInterpolation.cs`): buffer last 4 StateSnapshots keyed
      by HostTickMs (already on the wire), render ~60ms behind newest, lerp positions + anim
      times for players/entities/projectiles each frame into CoopState before the appliers run.
      Fixes the 30 Hz hard-snap stepping.
- [x] Single position writer: PlayerMotorBypass stops writing transform.position (keeps the
      FixedUpdate skip + Traverse property forcing); ScenePuppetry.ClientApply (LateUpdate,
      +32000) is the only writer, now fed interpolated values.
- [x] Animator scrubbing instead of drift-resync: every frame `Play(hash, 0, interpolatedTime)`.
      Kills two bugs: wrap-around drift (|cur-rx| compared with no loop wrap → rewind stutter
      every loop) and one-shot replay (capture wrapped `t - floor(t)` so finished non-looping
      states re-trigger from the fraction — death poses/attack windups replayed forever).
      Capture now sends `loop ? frac(t) : clamp01(t)` (shared AnimUtil helper, all 3 sites).
- [x] EntitySync: drop the projectile ("p") layer — ProjectileSync owns runtime clones via
      NetworkID. Fixes alive-set SetActive(false) killing client's own live shots + hash-bind
      collisions. Alive-set now only ever touches stable scene entities.
- [x] M8.5 minimal death mirroring (`PlayerDeathSync.cs`, client): hide a player's
      SpriteRenderers when host reports IsDead or Present=false (>0.3s hysteresis, other
      player still present); unhide on revive/respawn. Ghost/parry-revive visuals stay
      host-only for now.
- [x] Diagnostics for the next remote test: client logs a 10s-interval sync summary at Info
      (rx Hz, snapshot age, entity hit/miss, projectile bound/unbound) so Verbose isn't needed.
- [x] Protocol Version 12 (wire format unchanged; version gate so both sides run v1.1.0).
- [x] Build, deploy both installs, solo Goldberg smoke test (UDP headless: handshake v12,
      rx=30.0Hz steady, 0 exceptions both sides), bundles, release.
- [x] BONUS bug found by the new diagnostics: host snapshot accumulator reset to 0 instead of
      subtracting the interval — every session to date actually streamed at ~20 Hz, not 30
      (at 60 fps, 33ms threshold fires every 3rd frame). Fixed with subtract + burst cap.
- [x] Autonomous headless gameplay test (2026-07-04): TestHarness (BlockSaves / AutoLoadLevel /
      AutoPlay / KillP2AfterSec, all [Debug]-gated, default off) drove two headless instances
      into a real Veggies fight with scripted inputs. VERIFIED: scene sync into the level,
      entity sync 1 hit/0 miss, projectiles binding continuously, 30.0Hz / age<=33ms, zero
      exceptions, and death mirroring live — client hid P2's 6 renderers when the boss killed
      P2 on host, restored them when the level ended. Real save files md5-verified untouched
      (BlockSaves). Note: agent shells run in Windows Session 0 — windowed launches are
      impossible from automation; visual runs need the user to start run-visual-test.ps1.
- [ ] Real two-PC re-test (Windows host + Mac client, both on v1.1.0)

### v1.2.0 — full-fidelity client rendering (verified 16/16, releasing)
User verdict on v1.1.0: client cups slide without walk-left/shoot/damage animations, both-dead
shows nothing, lives HUD missing. Directive: batch ALL known gaps, verify autonomously, then one
human test.

- [x] Investigate (3 parallel decompile agents): player-anim pipeline, death/game-over/HUD flow, audio cues
- [x] Player animations: full fidelity on client (walk both dirs, aim/shoot, hit react) — local-driven controllers fed by forced motor state + streamed flags/pulses (protocol v13)
- [x] Game-over + win mirroring (both dead -> retry card on client; KNOCKOUT/results on win)
  - host: LevelEnd.Win/Lose postfix latches; HostLoseWatchdog invokes Level._OnLose() one-shot when the stock 4-frame lose gate deadlocks (ghost-revive cycling), wrapping level-end/lose events so a half-torn-down subscriber can't abort the flow
  - client: authoritative LevelFlagLost consumer (force local deaths + trigger stock game-over) + ClientSaveGuard blocks SaveCurrentFile on the client
- [x] Lives/HP HUD on client — real SetHealth() events + force-join both slots + HudFixup re-init
- [x] Ghost/parry-revive visuals on client — cosmetic PlayerDeathEffect on death edges
- [x] Audio cues on client — host streams AudioManager Play/PlayLoop/Stop keys; client suppresses local gameplay SFX and replays the host stream
- [x] Harness-verified: tools/run-verification.sh — 16/16 PASS (handshake v13, 30Hz, anim parity incl. shoot layer, death + ghost, game-over mirror, save guard, SFX tx/rx, zero exceptions, saves untouched)
- [ ] v1.2.0 bundles + release, then the ONE human test

v1.2.0 debugging notes (for future archaeology):
- Game-over mirror was TWO stacked bugs: (1) host deadlock — the death pause disables the Level
  component and can freeze playerDeathDelayFrames past its window, so re-arming playerIsDead
  can never fire the stock gate; direct _OnLose() invoke is the fix; (2) client receive mask in
  CoopState.ApplyRemoteState only kept Won|Reload bits and silently dropped Lost.
- _OnLose minutes after death hits KeyNotFoundException in a dead player's
  LevelPlayerWeaponManager.OnLevelEnd (weapon dict torn down) — a state the stock 4-frame path
  never sees; per-subscriber try/catch wrapping of the level-end/lose events isolates it.

Known deliberate v1.2.0 limitations (release notes): Chalice/EX/super/parry animation long tail;
Dashing/IsUsingSuperOrEx not forceable client-side (no setters); dynamic BGM phase changes not
forwarded; win-screen scoreboard shows client's stale PlayerData.

### Pause sync (shipped as v0.6.0, unverified)
Host sample → snapshot → client `PauseManager.Pause/Unpause` apply. Local pause input on client is suppressed via Harmony prefix gated on `PauseSync.RemoteDriven`.
