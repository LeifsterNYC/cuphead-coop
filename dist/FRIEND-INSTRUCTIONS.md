# CupheadCoop — host PC setup

Hi! You're the **host**. Your PC runs the game simulation; the other PC streams its input over and receives back what it should render. This is a one-time setup.

## What you need
- Cuphead (Steam, Windows)
- These files:
  - `CupheadCoop-v0.2.0.zip`
  - `BepInEx_win_x64_5.4.23.5.zip` from <https://github.com/BepInEx/BepInEx/releases/tag/v5.4.23.5>

## Install (5 minutes)

1. **Find your Cuphead folder.** Right-click Cuphead in Steam → Manage → Browse local files. Note the path — you'll drop files here. Typically:
   ```
   C:\Program Files (x86)\Steam\steamapps\common\Cuphead\
   ```
   or wherever your Steam library lives (e.g. `F:\SteamLibrary\steamapps\common\Cuphead\`).

2. **Install BepInEx.** Open `BepInEx_win_x64_5.4.23.5.zip` and copy its contents (a `BepInEx/` folder, `winhttp.dll`, `doorstop_config.ini`, `changelog.txt`) into your Cuphead folder.

3. **First-launch BepInEx.** Launch Cuphead from Steam normally. A black BepInEx console window pops up. Wait until Cuphead's main menu loads, then quit. This generates `BepInEx/config/` and `BepInEx/plugins/`.

4. **Install the plugin.** Open `CupheadCoop-v0.2.0.zip` — there's a `CupheadCoop/` folder inside. Copy that whole folder into `<Cuphead>/BepInEx/plugins/`. Final layout:
   ```
   <Cuphead>\BepInEx\plugins\CupheadCoop\CupheadCoop.dll
   <Cuphead>\BepInEx\plugins\CupheadCoop\LiteNetLib.dll
   ```

5. **Open the firewall.** When you first host, Windows will pop up a firewall prompt for Cuphead. Allow it on **private networks** (UDP port 47777). If you miss the prompt, add an inbound rule manually for `Cuphead.exe`.

## Test it

1. Launch Cuphead. The BepInEx console window should also appear and show:
   ```
   [Info: Cuphead Co-op] CupheadCoop 0.2.0 loading...
   [Info: Cuphead Co-op] Harmony patches applied: 5
   [Info: Cuphead Co-op] Press F9 to host, ...
   ```
   If you don't see those lines, the plugin didn't load — check the path in step 4.

2. Start **local 2-player co-op** (the second cup is required — that's the one your friend will control). Pick any level.

3. Once in the level, press **F9**. The console logs `CoopHost: listening on UDP 47777`. The top-left in-game overlay flips to `mode=Host`.

4. Tell your friend to connect from their end. The console will log `CoopHost: client connected from <ip>` once they handshake.

5. The second cup is now controlled by your friend's keyboard/controller. Play normally. Press **F11** to disconnect.

## Telling your friend your IP

They need your **LAN IPv4** to connect. Open `cmd` and run:
```
ipconfig
```
Look for the `IPv4 Address` under your active network adapter (Wi-Fi or Ethernet) — typically `192.168.x.x`. Send that number to your friend; they'll put it in their config.

If you're not on the same physical network, both of you can use **ZeroTier** — install it on both PCs, join the same ZeroTier network, and use the `10.x.x.x` ZeroTier IP instead.

## Troubleshooting

- **No BepInEx console:** the Steam launch isn't loading BepInEx. Make sure `winhttp.dll` is sitting next to `Cuphead.exe`.
- **`Plugin targets a wrong version of BepInEx (5.4.x)` warning:** safe to ignore on Windows; it's a soft compatibility check.
- **Friend can't connect:** confirm the firewall is open and that you've actually pressed F9 *after* loading into a level (not in the main menu).
- **Cup 2 doesn't move when friend presses keys:** check the overlay — `net=rx` means input is flowing. If `net=--`, the handshake didn't land.

## Files in this folder

- `CupheadCoop-v0.2.0.zip` — the plugin (this is what you install).
- `FRIEND-INSTRUCTIONS.md` — this file.
