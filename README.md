# Cuphead Online Co-op (BepInEx mod)

Replaces Steam Remote Play with real client-server networked 2-player co-op for Cuphead. Host-authoritative — the host runs the simulation, the client streams input.

**Status: M3 in progress.** Plugin builds and deploys. Input intercept and network transport are wired end-to-end. Not yet verified in-game (next session).

## What works (intended)
- BepInEx plugin `CupheadCoop.dll` loads on game start.
- Hotkeys: `F9` host, `F10` connect, `F11` disconnect.
- Host's local Player 2 input is overridden by network input from a connected client.
- Client captures its local Player 1 controls and ships them to the host at 60 Hz.

## What is **not** done yet
- Visual sync from host back to client. The client currently still runs its own simulation locally — it sees its own copy of the game, not what the host sees. (Milestone M4.)
- NAT traversal. LAN-only for now.
- In-game UI (menu, status overlay). Hotkeys only.

## Install

Already installed on this machine:
- BepInEx 5.4.23.5 at `F:\SteamLibrary\steamapps\common\Cuphead\BepInEx\`
- The plugin auto-deploys to `<game>/BepInEx/plugins/CupheadCoop/` when built in Debug mode.

For a second (client) machine:
1. Install BepInEx 5.4.23.5 (win_x64 on Windows, macos_universal on macOS) into the Cuphead folder. On Windows, drop the contents of the BepInEx zip into `<game>/`. On macOS, use `setup/setup-mac.sh`.
2. Launch Cuphead once so BepInEx generates its `BepInEx/config/` and `BepInEx/plugins/` folders. Quit.
3. Copy `dist/CupheadCoop-v0.1.0.zip` to the client. Extract its `CupheadCoop/` folder into `<game>/BepInEx/plugins/`. Final layout:
   ```
   <game>/BepInEx/plugins/CupheadCoop/CupheadCoop.dll
   <game>/BepInEx/plugins/CupheadCoop/LiteNetLib.dll
   ```
4. Edit `<game>/BepInEx/config/leif.cupheadcoop.cfg` (auto-generated on first run) and set:
   - `[Network] RemoteHost = <host PC's LAN IP>` — only the client needs this.
   - `[Network] Port = 47777` — must match on both ends.
   - `[Network] ConnectKey = cuphead-coop-v0` — must match on both ends.

## Test plan (M3 verification)

Goal: prove client keyboard drives host's Player 2 cup.

1. **Host PC**: launch Cuphead. Start local 2-player co-op (any level). With the BepInEx console window visible, press `F9`. Console should log `CoopHost: listening on UDP 47777`.
2. **Client PC**: launch Cuphead. Start a single-player game. Press `F10`. Console should log `CoopClient: dialing <host>:47777` then `CoopClient: handshake ok`.
3. On the host, the second cup should now be remote-controlled by the client's keyboard/controller. The client's local Player 1 is also moving their own copy of the game — that's expected, it's the M4 visual sync that fixes the asymmetric view.
4. Press `F11` on either side to disconnect.

## Diagnostic without networking

If the input intercept fails, set `[Debug] ForceP2WalkRight = true` in `leif.cupheadcoop.cfg` and restart. Player 2 should walk right autonomously. If it does, the Harmony patch landed; if not, the patch didn't apply (check BepInEx log for errors).

## Layout

```
dev/cuphead/
├── CupheadCoop/                 # the BepInEx plugin
│   ├── CupheadCoop.csproj
│   └── src/
│       ├── Plugin.cs            # entry, hotkeys, frame loop
│       ├── ModConfig.cs         # BepInEx config bindings
│       ├── Coop/
│       │   ├── CoopState.cs     # process-wide state shared with patches
│       │   └── RewiredPatches.cs# Harmony patches on Rewired.Player
│       └── Net/
│           ├── Protocol.cs      # wire format
│           ├── CoopHost.cs      # LiteNetLib server
│           └── CoopClient.cs    # LiteNetLib client
├── tools/Inspect/               # Mono.Cecil-based assembly inspector
├── tasks/                       # plan + lessons
└── dist/                        # packaged drop-in zips
```

## Build

```sh
cd CupheadCoop
dotnet build -c Debug      # auto-deploys to game folder
dotnet build -c Release    # produces clean DLLs in bin/Release
```

Targets `net35` to match Cuphead's Unity Mono runtime.

## Architecture notes

- **Why Rewired.Player and not Cuphead.PlayerInput?** Cuphead's `PlayerInput.GetButton` delegates to `Rewired.Player` via the `actions` property. But edge-detection methods (`GetButtonDown`, `GetButtonUp`) live only on `Rewired.Player`, and game logic calls them directly through `actions`. Patching at the Rewired layer covers everything in one place.
- **Why host-authoritative, not rollback?** Cuphead bosses are non-deterministic across machines (RNG, particle timings). True rollback would require state-sync of every random source. Host-authoritative + client-side prediction is the standard 2-player co-op pattern (Terraria, Don't Starve, Risk of Rain 2). Tradeoff: ~50ms perceived input lag for the remote player; far better than streaming a video frame.
- **Why `net35`?** Cuphead's Assembly-CSharp references mscorlib v2 — the legacy Mono runtime. BepInEx 5 plugins target `net35` to match. LiteNetLib 0.9.5.2 is the last version with a `net35` build target.
