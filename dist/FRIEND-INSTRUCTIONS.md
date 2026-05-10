# CupheadCoop — your setup (macOS client)

Hi! You're the **client**. The other PC runs the game's simulation; your PC streams its inputs over and renders what the host sends back. One-time setup, ~5 min.

## What you need
- Cuphead (Steam, native macOS — not Whisky/Wine/Crossover)
- These files (download from the GitHub release):
  - `CupheadCoop-v0.2.0.zip` — the plugin
  - `setup-mac.sh` — the installer (also in the release attachments)
- The host PC's LAN IPv4 (e.g. `192.168.0.4`) — ask the host. If you're not on the same physical network, install [ZeroTier](https://www.zerotier.com/download/) on both PCs, join the same network, and use the host's `10.x.x.x` ZeroTier IP.

## Install

1. Open Terminal and `cd` to wherever you saved the files. Then:
   ```sh
   chmod +x setup-mac.sh
   ./setup-mac.sh <host-ip>
   ```
   Example: `./setup-mac.sh 192.168.0.4`

   The script:
   - Auto-finds Cuphead in `~/Library/Application Support/Steam/steamapps/common/Cuphead/`
   - Installs BepInEx 5.4.22
   - Patches Cuphead's bundled Mono dllmap (BepInEx's macOS bootstrap is broken without this)
   - Drops the plugin into `BepInEx/plugins/`
   - Pre-seeds the config with the host's IP

2. **Set Cuphead's Steam launch options.** Open Steam → right-click Cuphead → Properties → Launch Options. Paste exactly (with the quotes — the path has spaces):
   ```
   "/Users/<your-username>/Library/Application Support/Steam/steamapps/common/Cuphead/run_bepinex.sh" %command%
   ```
   Replace `<your-username>` with your macOS username. (The setup script prints the full correct line at the end — copy it from there.)

   Why this is needed: BepInEx on macOS uses `DYLD_INSERT_LIBRARIES` to inject itself, which has to be set in the launching shell. Steam launches the .app directly otherwise and BepInEx never loads.

3. Close the Steam properties window.

## Connect

1. **Wait for the host** to launch Cuphead, start local 2-player co-op, and press F9. They'll tell you when they're ready.

2. Launch Cuphead from Steam. You'll see the CupheadCoop status overlay top-left. (No console window pops up — that's normal on macOS; logs go to `BepInEx/LogOutput.log`.)

3. Start a single-player game (or local 2-player if you want spectator-view of both players).

4. Once you're in a level, press **`J`** to connect. The overlay should flip to `mode=Client` and the in-game log shows:
   ```
   CoopClient: NetManager started, dialing 192.168.0.4:47777
   CoopClient: handshake ok (v2)
   ```

5. Your Player 1 controls now drive the host's Player 2 cup. The host will see you on their screen.

6. Press **`K`** to disconnect.

## Hotkeys

| Action | Key |
| --- | --- |
| Connect | `J` |
| Disconnect | `K` |
| Show/hide overlay | `O` |

(Mac's F-keys are intercepted by macOS even with `Fn` held, so the defaults are letter keys. Change them in `BepInEx/config/leif.cupheadcoop.cfg` if `J`/`K`/`O` collide with anything.)

## Troubleshooting

- **Overlay doesn't appear in-game:** the plugin didn't load. Check `~/Library/Application Support/Steam/steamapps/common/Cuphead/BepInEx/LogOutput.log` — should end with `Chainloader startup complete` and `Press F9 to host, J to connect…`.
- **No log file at all:** Steam isn't running BepInEx. Verify the launch options string in step 2 — most common bug is missing the quotes.
- **`CoopClient: Start failed with exception`** in the overlay log: confirm the host has F9 pressed and you have the right IP. If both are set, check that UDP 47777 isn't being firewalled on the host's end.
- **Overlay shows `mode=Client` but `seq=0` and stays there:** packets aren't reaching the host. Same firewall/network checks as above.
- **Pressing J does nothing:** another macOS app is grabbing it. Open the cfg and change `Connect = J` to a different letter, then restart Cuphead.

## What's actually happening under the hood

You're not really "joining" the host's game session in the usual sense — both PCs are running their own copy of Cuphead. Your inputs travel over the network and replace the host's local Player 2 input. The host's world state (player positions) streams back at 30 Hz so your screen mirrors theirs. Render-only on the client side, no rollback. ~50ms perceived input lag is the cost; the upside is no Steam Remote Play video compression.
