# Night 1 result — handoff for testing

## What got built
Full BepInEx plugin scaffolded, compiled, and deployed. Code structure is in place for milestones M1–M3 (input intercept + LAN transport + client→host input streaming). M4 (visual state sync from host to client) is NOT started — that's the next session's work.

**Plugin DLL is already deployed** at `F:\SteamLibrary\steamapps\common\Cuphead\BepInEx\plugins\CupheadCoop\` on this PC. BepInEx itself is also installed.

## What needs testing tomorrow

### Step 1 — does BepInEx load at all?
Launch Cuphead. A black BepInEx console window should appear behind the game (or alongside it). If no console: BepInEx didn't initialize. Check `<game>/BepInEx/LogOutput.log`.

### Step 2 — does the plugin load?
In the BepInEx console (or in `BepInEx/LogOutput.log`), look for:
```
[Info   :   BepInEx] Loading [Cuphead Co-op 0.1.0]
[Info   :Cuphead Co-op] CupheadCoop 0.1.0 loading…
[Info   :Cuphead Co-op] Harmony patches applied: 4
[Info   :Cuphead Co-op] Press F9 to host, F10 to connect to 127.0.0.1:47777, F11 to disconnect.
```
If any of those are missing, the plugin failed to load. Capture the full log and we'll debug.

### Step 3 — does the patch hook fire?
Easiest visual test: edit `<game>/BepInEx/config/leif.cupheadcoop.cfg` and set:
```
[Debug]
ForceP2WalkRight = true
```
Relaunch Cuphead, enter local 2-player co-op, leave Player 2's controls untouched. Player 2's cup should walk right by itself. If yes → Harmony patches are live, the intercept point is correct.

If Player 2 does NOT walk right:
- The patch isn't binding to the right method (Rewired version mismatch?).
- Or `RewiredPlayer2Id` isn't being discovered in time. Check BepInEx log for "Resolved Rewired Player 2 id = N".

### Step 4 — single-machine network test
On THIS PC, launch two instances of Cuphead (Steam normally blocks this — alternative is to use a sandboxed second copy, or skip to step 5).

OR easier: skip directly to step 5 with the second PC.

### Step 5 — two-PC LAN test
On the second PC:
1. Install BepInEx 5.4.23.5 win_x64 into its Cuphead folder. (Drop zip contents into `<game>/`. Launch Cuphead once to init BepInEx, quit.)
2. Copy `dev/cuphead/dist/CupheadCoop-v0.1.0.zip` over. Extract its `CupheadCoop/` folder into `<game>/BepInEx/plugins/`.
3. Edit `<game>/BepInEx/config/leif.cupheadcoop.cfg`:
   - `[Network] RemoteHost = <this PC's LAN IPv4>` (find via `ipconfig`)
   - keep Port = 47777, ConnectKey = cuphead-coop-v0

Then:
- **Host PC** (this one): launch Cuphead, start local 2-player co-op, press F9. Console: `CoopHost: listening on UDP 47777`.
- **Client PC**: launch Cuphead, start single-player, press F10. Console: `CoopClient: dialing ...:47777` → `CoopClient: handshake ok`.

Expected: on the host's screen, Player 2's cup is now controlled by whatever the client is pressing. The client's own screen still shows their local game.

## Likely failure modes I'd bet on

1. **Rewired discovery never fires.** PlayerManager may need a level to be loaded before its dictionary is populated. If the host log never shows "Resolved Rewired Player 2 id = ...", we'll need to hook a different lifecycle event (probably `PlayerInput.Init` or `PlayerManager` static initialization). Easy fix.

2. **BepInPlugin GUID conflict** with another mod. Unlikely since this is a fresh BepInEx install.

3. **Firewall blocks UDP 47777** between PCs. Windows will prompt the first time Cuphead binds the socket — accept it on both PCs.

4. **`net35`-target plugin fails to load.** If BepInEx logs a `BadImageFormatException` or similar, retarget to `netstandard2.0` and ship `netstandard.dll` alongside.

## Next session goals (M4 visual sync)
- Decide approach: stream host transforms back to client (lightweight), OR run sim on both with deterministic seed sync (heavy).
- Render-only client: host serializes Player 1, Player 2, projectiles, boss positions/animations every Nth tick; client interpolates and overrides its local Cuphead's transforms.

## Files to read first when picking this up
- `tasks/todo.md` — milestone plan
- `README.md` — overall architecture
- `CupheadCoop/src/Plugin.cs` — entry point and frame loop
- `CupheadCoop/src/Coop/RewiredPatches.cs` — the Harmony intercept

