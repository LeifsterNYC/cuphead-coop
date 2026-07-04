# CupheadCoop — your setup (macOS client)

Hi! You're the **client**. The other PC runs the game's simulation; your PC streams its inputs over and renders what the host sends back. One-time setup, ~5 min.

## What you need
- Cuphead (Steam, native macOS — not Whisky/Wine/Crossover)
- These files (download from the GitHub release):
  - `CupheadCoop-v1.0.0.zip` — the plugin
  - `setup-mac.sh` — the installer (also in the release attachments)
- **The host's SteamID64** — a 17-digit number the host reads off their overlay/log when they press Host. (As of v1.0.0 the connection goes over Steam's network: no IPs, no ZeroTier, no port forwarding.)

## Install

1. Open Terminal and `cd` to wherever you saved the files. Then:
   ```sh
   chmod +x setup-mac.sh
   ./setup-mac.sh <host-ip-or-anything>
   ```
   (The IP argument only matters for the legacy Udp transport; any placeholder works if you're using Steam.)

   The script:
   - Auto-finds Cuphead in `~/Library/Application Support/Steam/steamapps/common/Cuphead/`
   - Installs BepInEx 5.4.22
   - Patches Cuphead's bundled Mono dllmap (BepInEx's macOS bootstrap is broken without this)
   - Drops the plugin into `BepInEx/plugins/`
   - Pre-seeds the config

2. **Set the host's Steam ID in the config.** Edit
   `~/Library/Application Support/Steam/steamapps/common/Cuphead/BepInEx/config/leif.cupheadcoop.cfg`:
   ```ini
   [Network]
   Transport = Steam
   HostSteamId = <the 17-digit number from the host>
   ```
   Optional: set `AutoStart = Connect` and the plugin dials the host automatically a few
   seconds after the game boots (it keeps retrying until the host is up, so launch order
   doesn't matter) — no hotkey needed at all.

3. **Set Cuphead's Steam launch options.** Open Steam → right-click Cuphead → Properties → Launch Options. Paste exactly (with the quotes — the path has spaces):
   ```
   "/Users/<your-username>/Library/Application Support/Steam/steamapps/common/Cuphead/run_bepinex.sh" %command%
   ```
   Replace `<your-username>` with your macOS username. (The setup script prints the full correct line at the end — copy it from there.)

   Why this is needed: BepInEx on macOS uses `DYLD_INSERT_LIBRARIES` to inject itself, which has to be set in the launching shell. Steam launches the .app directly otherwise and BepInEx never loads.

4. Close the Steam properties window.

## Connect

1. **Wait for the host** to launch Cuphead and press F9 (or tell you they're hosting).

2. Launch Cuphead from Steam. You'll see the CupheadCoop status overlay top-left. (No console window pops up — that's normal on macOS; logs go to `BepInEx/LogOutput.log`.)

3. Press **`J`** to connect (skip if you set `AutoStart = Connect`). The overlay flips to `mode=Client` and the log shows:
   ```
   CoopClient: dialing Steam id 7656119…  (direct, relay fallback)
   CoopClient: Steam P2P session established with 7656119…
   CoopClient: handshake ok (v11)
   ```

4. Your Player 1 controls now drive the host's Player 2 cup, and your screen mirrors the host's world.

5. Press **`K`** to disconnect.

## Hotkeys

| Action | Key |
| --- | --- |
| Connect | `J` |
| Disconnect | `K` |
| Show/hide overlay | `O` |

(Mac's F-keys are intercepted by macOS even with `Fn` held, so the defaults are letter keys. Change them in `BepInEx/config/leif.cupheadcoop.cfg` if `J`/`K`/`O` collide with anything.)

## Troubleshooting

- **Overlay doesn't appear in-game:** the plugin didn't load. Check `~/Library/Application Support/Steam/steamapps/common/Cuphead/BepInEx/LogOutput.log` — should end with `Chainloader startup complete`.
- **No log file at all:** Steam isn't running BepInEx. Verify the launch options string — most common bug is missing the quotes.
- **`HostSteamId is not a valid SteamID64` in the log:** the config value is missing/typo'd. It's a bare 17-digit number, no quotes.
- **Dial times out repeatedly:** the host isn't hosting yet (they need F9), or one of you isn't logged into Steam. Being Steam friends helps Steam's relay establish the session — add each other if the direct connection won't stick.
- **Both PCs must run the same plugin version** (the handshake checks and refuses otherwise).
- **Old-school fallback:** if Steam's network misbehaves, both sides can set `Transport = Udp` in the cfg and use the pre-v1.0 direct-IP path (LAN or ZeroTier, host's UDP 47777 reachable).

## What's actually happening under the hood

You're not really "joining" the host's game session in the usual sense — both PCs are running their own copy of Cuphead. Your inputs travel over Steam's P2P network (direct when possible, Valve relay when NAT blocks it) and replace the host's local Player 2 input. The host's world state (players, enemies, projectiles, animations) streams back at 30 Hz so your screen mirrors theirs. ~50ms perceived input lag is the cost; the upside is no Steam Remote Play video compression.
