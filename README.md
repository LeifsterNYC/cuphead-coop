# Cuphead Online Co-op

A BepInEx mod that adds real online 2-player co-op to Cuphead, replacing Steam Remote Play. Host-authoritative — the host runs the simulation; the client streams input and receives world snapshots.

**Status:** alpha. LAN/ZeroTier only. No NAT traversal yet, no in-game menu (hotkeys + IMGUI overlay), no save-state sync between sessions. Tested on Windows host + macOS client.

## Install

Both PCs need:
- Cuphead (Steam, Windows or native macOS)
- BepInEx 5.x
- The `CupheadCoop` plugin (this repo)

### Windows
1. Download BepInEx 5.4.23.5 `win_x64`, drop the contents into your Cuphead folder.
2. Launch Cuphead once, then quit.
3. Drop `dist/CupheadCoop-vX.Y.Z.zip`'s `CupheadCoop/` folder into `<game>/BepInEx/plugins/`.

### macOS
Run the installer:
```sh
./setup/setup-mac.sh <host-LAN-ip>
```
It installs BepInEx 5.4.22 (5.4.23.5's macOS preloader has a libc.so.6 bug), patches Cuphead's bundled Mono dllmap, drops the plugin in, and pre-seeds the config. Then set Steam → Cuphead → Properties → Launch Options to:
```
"<full-path-to-Cuphead>/run_bepinex.sh" %command%
```

## Play

Both PCs must run the same plugin version (the wire protocol is versioned and refuses cross-version handshakes).

**Host PC:**
1. Launch Cuphead, start local 2-player co-op (you need both cups in the level).
2. Press `F9`. The overlay flips to `mode=Host` and the log shows `CoopHost: listening on UDP 47777`.

**Client PC:**
1. Launch Cuphead, start single-player or local 2-player.
2. Press the connect hotkey (`F10` on Windows, `J` on macOS — see hotkey notes below).
3. Overlay shows `mode=Client` and log shows `CoopClient: handshake ok`.

The client's local input now drives the host's Player 2. Press the disconnect hotkey to tear down (`F11` on Windows, `K` on macOS).

### Hotkeys

| Action | Windows | macOS |
| --- | --- | --- |
| Host | `F9` | `F9` |
| Connect | `F10` | `J` |
| Disconnect | `F11` | `K` |
| Toggle overlay | `O` | `O` |

(macOS intercepts `F10`/`F11` even with `Fn` held, so the macOS defaults are letter keys. All bindings are configurable in `BepInEx/config/leif.cupheadcoop.cfg`.)

## Configuration

`<game>/BepInEx/config/leif.cupheadcoop.cfg`:

| Section | Key | Notes |
| --- | --- | --- |
| `[Network]` | `RemoteHost` | Host PC's LAN/ZeroTier IPv4 (client only). |
| `[Network]` | `Port` | UDP port; must match on both ends (default 47777). |
| `[Network]` | `ConnectKey` | Shared key; must match on both ends. |
| `[Input]` | `SendRateHz` | Client → host input rate (default 60). |
| `[State]` | `SendRateHz` | Host → client state-snapshot rate (default 30). |
| `[Hotkeys]` | `Host` / `Connect` / `Disconnect` / `ToggleOverlay` | Any Unity `KeyCode` name. |
| `[Debug]` | `ForceP2WalkRight` | Bypasses networking; auto-walks Player 2 right. Useful for verifying the input intercept on a single PC. |
| `[Debug]` | `Verbose` | Per-frame log spam. |

## Build

Requires .NET SDK 6+ (8 recommended).

```sh
cd CupheadCoop
dotnet build -c Debug      # auto-deploys to <game>/BepInEx/plugins/CupheadCoop/
dotnet build -c Release    # clean DLLs in bin/Release/
```

The `.csproj` auto-detects Cuphead at `F:\SteamLibrary\steamapps\common\Cuphead`, the default Steam path on Windows, and `~/Library/Application Support/Steam/steamapps/common/Cuphead` on macOS. Override with `-p:CupheadDir=...` or the `CupheadDir` env var.

Targets `net35` to match Cuphead's Unity Mono runtime. LiteNetLib `0.9.5.2` is the last version with a `net35` build target — don't bump it without retargeting the framework.

## How it works

- **Input intercept:** Harmony postfixes `Rewired.Player.GetButton/GetButtonDown/GetButtonUp/GetAxis(int)`. When `Mode == Host` and a remote frame is buffered, the result for Cuphead's Player 2 is replaced with the network value. Cuphead's `PlayerInput` delegates to `Rewired.Player` for everything, so patching at the Rewired layer is enough.
- **Discovery:** Harmony postfix on `PlayerInput.Init` captures both Rewired player IDs and `Rewired.Player` references the moment Cuphead binds them — no polling.
- **Transport:** LiteNetLib over UDP. Input frames are unreliable (60 Hz, latest-wins). State snapshots are unreliable+sequenced (30 Hz). Connection handshake is reliable+ordered.
- **Visual sync (host → client):** Host samples `PlayerManager.GetPlayer(PlayerOne|PlayerTwo).transform.position` each LateUpdate and ships a `StateSnapshot`. Client overrides local Cuphead transforms in LateUpdate, after the local sim's Update has run, so the rendered frame matches the host. No interpolation in this first cut — visible stepping at 30 Hz, fine for validation.

## Why not rollback?

Cuphead bosses are non-deterministic across machines (RNG, particle timings, audio cues). Rollback netcode would require state-syncing every random source, which is a much bigger lift than what 2-player co-op needs. Host-authoritative + client-side rendering is the standard pattern (Terraria, Don't Starve, Risk of Rain 2). Tradeoff: ~50ms perceived input lag for the remote player. Acceptable for co-op; precision-platforming bosses still feel responsive.
