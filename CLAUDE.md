# CLAUDE.md — repo notes for Claude sessions

> Self-contained context for any Claude session opening this repo, especially a
> Mac-side session that won't see the Windows machine's user-memory files.

## What this is

A BepInEx mod that adds real client-server networked 2-player co-op to Cuphead, replacing Steam Remote Play. **Host-authoritative** — the host runs the simulation; the client streams its inputs and the host's Player 2 reads from the network.

Originally built on a Windows machine where the host plays. The Mac side is the client (second player). Both PCs run their own Cuphead instance with this same plugin loaded.

## Status

> This section goes stale fast — `tasks/todo.md` (milestone log) and `git log` are the
> source of truth. Snapshot as of v1.0.0 (July 2026):

- **Input intercept, transport, input streaming, visual sync (players/animators/entities/projectiles/HP/pause/scene), HKMP-style client AI suppression, remote-motor bypass** — all shipped through v0.9.1; parts verified in-game, parts still awaiting a real two-PC session.
- **v1.0.0: transport is now selectable** — `[Network] Transport = Steam|Udp`. Steam P2P rides Cuphead's own bundled Steamworks.NET (NAT traversal via Steam relay — no IP/ZeroTier setup; client dials the host's SteamID64 from `HostSteamId`). LiteNetLib UDP remains for LAN and solo two-instance testing. `AutoStart = Off|Host|Connect` removes the hotkey dance. Steam + UDP paths both smoke-tested solo via Goldberg emu, headless `-batchmode`.
- **Next**: real two-PC Steam test (Windows host + Mac client on v1.0.0), then in-game gameplay-sync verification of the v0.9.x layers.

What's NOT done: in-game UI (hotkeys + IMGUI overlay only), death-sequence mirroring (M8.5), state persistence between sessions.

## Repo map

```
dev/cuphead/
├── CupheadCoop/              # the BepInEx plugin csproj
│   ├── CupheadCoop.csproj    # path resolution lives here, see "Building"
│   └── src/
│       ├── Plugin.cs                # BepInEx entry, hotkeys, frame loop
│       ├── ModConfig.cs             # config bindings
│       ├── CoopOverlay.cs           # IMGUI status panel (top-left)
│       ├── Coop/
│       │   ├── CoopState.cs         # process-wide shared state
│       │   ├── RewiredPatches.cs    # Harmony postfixes on Rewired.Player
│       │   └── PlayerInputDiscovery.cs  # captures Rewired ids on PlayerInput.Init
│       └── Net/
│           ├── Protocol.cs          # wire format
│           ├── CoopHost.cs          # LiteNetLib server-side
│           └── CoopClient.cs        # LiteNetLib client-side
├── tools/Inspect/            # Mono.Cecil-based assembly inspector (build time only)
├── setup/setup-mac.sh        # macOS installer for the second player's PC
├── dist/                     # shipped artifacts (Win zip + Mac tar.gz)
├── tasks/                    # plan + lessons + handoff doc
└── README.md                 # public-facing project description
```

## Architecture decisions (why, not what)

- **Loader: BepInEx 5.4.23.5.** Cuphead's CLR is `2.0.50727` (legacy Mono runtime); BepInEx 5 (not 6) is the right line.
- **Plugin target: net35.** Matches Cuphead's `Assembly-CSharp` references (mscorlib 2.0). `LiteNetLib 0.9.5.2` is the last version with a `net35` build target — don't bump unless you also bump the framework target.
- **Intercept point: `Rewired.Player.GetButton` / `GetButtonDown` / `GetButtonUp` / `GetAxis` (int overloads).** Cuphead's `PlayerInput.GetButton` etc. delegate to `actions.GetXxx` (a `Rewired.Player`); patching at the Rewired layer covers both `PlayerInput.*` callers and game code that uses `actions.GetButtonDown` directly.
- **Discovery: Harmony postfix on `PlayerInput.Init`.** That method assigns `actions = PlayerManager.GetPlayerInput(playerId)` — capturing it there yields both `Rewired.Player` references the moment they're bound, no polling.
- **Topology: host-authoritative P2P.** Host runs the full sim; client streams inputs at 60 Hz. Edge detection (Down/Up) is derived host-side from the diff between consecutive button bitmasks. Rollback netcode is intentionally avoided — Cuphead bosses aren't deterministic across machines, and 2-player co-op tolerates ~50ms perceived input lag fine (same pattern as Terraria, Don't Starve, RoR2).

## Building

The csproj resolves `CupheadDir` in this order:

1. `-p:CupheadDir=...` on the command line, or `CupheadDir` env var
2. Auto-detect: Windows `F:\SteamLibrary\...`, Windows `C:\Program Files (x86)\Steam\...`, Mac `~/Library/Application Support/Steam/steamapps/common/Cuphead`

`ManagedDir` derives from that, handling both Windows (`Cuphead_Data/Managed`) and Mac (`Cuphead.app/Contents/Resources/Data/Managed`) layouts.

If auto-detect fails, the build errors out with a clear message. To override:

```sh
# from the project's CupheadCoop/ folder:
dotnet build -c Release   # auto-detect
# or
CupheadDir="/path/to/Cuphead" dotnet build -c Release
```

`Debug` config has an `AfterBuild` target that copies the DLLs straight into `<game>/BepInEx/plugins/CupheadCoop/`. `Release` does not — use `Release` if you don't want auto-deploy.

## Testing on this Mac (likely the M3 client side)

1. Run `setup/setup-mac.sh <host-ip>` if BepInEx isn't already installed for Cuphead. The script also pre-seeds `BepInEx/config/leif.cupheadcoop.cfg` with the host IP.
2. Set Steam → Cuphead → Properties → Launch Options to `"<full-path>/Cuphead/run_bepinex.sh" %command%`. macOS-only, one-time. Reason: Mac BepInEx uses `DYLD_INSERT_LIBRARIES` injection which has to be set in the launching shell — Steam launches the game directly otherwise and BepInEx never loads.
3. Launch Cuphead. Look for the IMGUI overlay in the top-left whenever a mode is active. Logs go to `<game>/BepInEx/LogOutput.log` plus the BepInEx Terminal window.
4. To verify the Harmony patch lands on Mac (independent of the host being up), edit `BepInEx/config/leif.cupheadcoop.cfg`, set `[Debug] ForceP2WalkRight = true`, start local 2-player co-op. Player 2 should walk right autonomously. Same test the Windows side passed — establishes the patch isn't broken by Unity-version drift on Mac.
5. For the actual coop test: start single-player on Mac, press F10. Console should log `CoopClient: dialing <host>` → `CoopClient: handshake ok`. Overlay shows `mode=Client`. The host's Player 2 should now respond to your local Player 1 controls.

## Common-failure cheat sheet

- **Plugin doesn't load** → check `BepInEx/LogOutput.log`. If `MissingMethodException` on a `Rewired.Player.*` or `UnityEngine.*` call, the Mac assemblies have a different signature than Windows; rebuild from source on the Mac (CupheadDir auto-detect should handle it).
- **Plugin loads but P2 input not overridden** → overlay shows `p2=?` because `PlayerInput.Init` for `PlayerId.PlayerTwo` never ran. Confirm host actually started local 2-player co-op (not single-player). On the client, only P1 is needed, so `p2=?` is fine there.
- **F10 does nothing** → some Mac keyboards bind F-keys to media controls. Hold `Fn+F10`, or change `[Hotkeys] Connect` to a different key in `leif.cupheadcoop.cfg`.
- **Handshake never completes** → `ConnectKey` mismatch (must match on both ends), or firewall/router dropping UDP 47777. ZeroTier `10.x` addresses bypass NAT issues if both PCs are on the same ZeroTier network.

## User collaboration style (from prior session memory)

- Driver/tester split: Claude writes the code; Leif tests in-game and reports.
- Comfortable with auto mode and overnight autonomous execution.
- Terse, lowercase, action-first. Doesn't want long preambles.
- Defers technical choices to Claude when prompted ("fine", "yup"). Speaks up when something feels wrong; respect that immediately.
- Does not want save files or Steam config touched.
- Off-limits language: don't refer to the second player as "the friend" — use "the client" or "the second player".

## Lessons captured

See `tasks/lessons.md` — append every time a course-correction lands.

## What to work on next

In rough priority order:

1. **Verify on Mac that the prebuilt DLL loads.** That's the cheapest signal of cross-platform compatibility. If it does, M3 testing can proceed without rebuilding.
2. **Run the M1 ForceP2WalkRight test on Mac** — confirms Harmony patches against the Mac-side Rewired/PlayerInput classes work the same.
3. **Two-PC connection test** — F9 on Windows host, F10 on Mac client. Watch logs both sides. This is the M3 acceptance test.
4. **Start M4** — host streams transforms (P1, P2, projectiles, boss) back to client; client interpolates and overrides its local Cuphead's transforms instead of running the sim. See `tasks/todo.md` for the deferred design choice (render-only vs deterministic mirror — the cheaper render-only path is recommended).
