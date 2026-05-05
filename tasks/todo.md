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

