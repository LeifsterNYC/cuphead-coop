#!/usr/bin/env bash
# v1.2.0 autonomous verification run (headless, two instances, Goldberg, UDP).
# Usage: tools/run-verification.sh [kill-p2-sec]
# Produces a PASS/FAIL report on stdout; leaves logs in place for deeper digging.
# Assumes: Debug build already deployed to both installs; run from repo root.
set -u
HOST="/f/SteamLibrary/steamapps/common/Cuphead"
CLI="/f/SteamLibrary/steamapps/common/Cuphead-client"
HLOG="$HOST/BepInEx/LogOutput.log"
CLOG="$CLI/BepInEx/LogOutput.log"
KILLSEC="${1:-20}"

say() { echo "== $*"; }

# --- save-safety baseline -----------------------------------------------------
SAVEDIR="/c/Users/Leif/AppData/Roaming/Cuphead"
SAVEHASH_BEFORE=$(md5sum "$SAVEDIR"/*.sav 2>/dev/null | md5sum | cut -d' ' -f1)

# --- config both installs -----------------------------------------------------
setopt() { # file section key value  (append-to-section if missing)
  python - "$1" "$2" "$3" "$4" <<'EOF'
import io, re, sys
path, sect, key, val = sys.argv[1:5]
s = io.open(path, encoding='utf-8-sig').read()
pat = re.compile(r'(?m)^' + re.escape(key) + r' = .*$')
m = re.search(r'(?ms)^\[' + re.escape(sect) + r'\]\n(.*?)(?=^\[|\Z)', s)
if m and pat.search(s, m.start(1), m.end(1)):
    s = s[:m.start(1)] + pat.sub(key + ' = ' + val, s[m.start(1):m.end(1)], count=1) + s[m.end(1):]
elif m:
    s = s[:m.end(1)] + key + ' = ' + val + '\n' + s[m.end(1):]
else:
    s += '\n[' + sect + ']\n' + key + ' = ' + val + '\n'
io.open(path, 'w', encoding='utf-8', newline='\n').write(s)
EOF
}

for D in "$HOST" "$CLI"; do
  CFG="$D/BepInEx/config/leif.cupheadcoop.cfg"
  setopt "$CFG" Debug BlockSaves true
  setopt "$CFG" Debug AutoPlay true
  setopt "$CFG" Debug DumpAnimState true
  setopt "$CFG" Debug Verbose false
  setopt "$CFG" Network Transport Udp
  # full disk logging (Debug level lines)
  BCFG="$D/BepInEx/config/BepInEx.cfg"
  python - "$BCFG" <<'EOF'
import io, re, sys
p = sys.argv[1]
s = io.open(p, encoding='utf-8-sig').read()
s = re.sub(r'(?ms)(\[Logging\.Disk\].*?)^LogLevels = .*?$', r'\1LogLevels = All', s, count=1)
io.open(p, 'w', encoding='utf-8', newline='\n').write(s)
EOF
done
HCFG="$HOST/BepInEx/config/leif.cupheadcoop.cfg"
CCFG="$CLI/BepInEx/config/leif.cupheadcoop.cfg"
setopt "$HCFG" Network AutoStart Host
setopt "$HCFG" Debug AutoLoadLevel Veggies
setopt "$HCFG" Debug KillP2AfterSec "$KILLSEC"
setopt "$CCFG" Network AutoStart Connect
setopt "$CCFG" Debug AutoLoadLevel ""
setopt "$CCFG" Debug KillP2AfterSec 0

# --- goldberg in ----------------------------------------------------------------
# Kill stale instances FIRST — a running Cuphead holds steam_api64.dll open.
taskkill //IM Cuphead.exe //F >/dev/null 2>&1
sleep 4
for try in 1 2 3 4 5; do
  cp "$HOST/steam_api64.dll.goldberg" "$HOST/steam_api64.dll" 2>/dev/null && break
  sleep 3
done
cp "$HOST/steam_api64.dll.goldberg" "$HOST/steam_api64.dll" || { echo "FATAL: host goldberg swap"; exit 1; }
cp "$CLI/steam_api64.dll.goldberg" "$CLI/steam_api64.dll"   || { echo "FATAL: client goldberg swap"; exit 1; }

# --- launch ---------------------------------------------------------------------
(cd "$HOST" && ./Cuphead.exe -batchmode -nographics &)
sleep 3
(cd "$CLI" && ./Cuphead.exe -batchmode -nographics &)

say "instances launched; waiting for client game-over mirror (max 300s)"
for i in $(seq 1 50); do
  grep -aq "LevelEventSync: both players dead" "$CLOG" 2>/dev/null && break
  sleep 6
done
sleep 15   # let the retry card flow settle

# --- collect --------------------------------------------------------------------
say "RESULTS"
pass=0; fail=0
check() { # name grep-file pattern
  local name="$1" file="$2" pat="$3"
  if grep -aqE "$pat" "$file" 2>/dev/null; then echo "PASS: $name"; pass=$((pass+1));
  else echo "FAIL: $name  (pattern: $pat)"; fail=$((fail+1)); fi
}

check "handshake v13"            "$CLOG" "handshake ok \(v13\)"
check "level loaded on host"     "$HLOG" "auto-loading level"
check "client followed to level" "$CLOG" "scene_level_veggies"
check "30Hz stream"              "$CLOG" "sync: rx=(29|30|31)"
check "anim dump host"           "$HLOG" "ANIMDUMP HOST"
check "anim dump client"         "$CLOG" "ANIMDUMP CLIENT"
check "shooting layer live (w1=1 on client)" "$CLOG" "ANIMDUMP CLIENT P1 .*w1=1"
check "a death occurred (scripted or natural)" "$CLOG" "PlayerDeathSync: hid"
check "ghost spawned"            "$CLOG" "PlayerDeathSync: spawned death ghost"
check "game-over mirrored on client" "$CLOG" "LevelEventSync: both players dead"
check "client save writes blocked"   "$CLOG" "ClientSaveGuard: blocking"
check "sfx tx on host"           "$HLOG" "LEVELSTATE .*sfxTx=[1-9]"
check "sfx rx on client"         "$CLOG" "LEVELSTATE .*sfxRx=[1-9]"
# "subscriber threw ... — skipped" is the HostLoseWatchdog isolating a half-torn-down stock
# subscriber by design (expected in the both-dead deadlock recovery) — not a plugin failure.
hex=$(grep -aE "Exception|NullReference" "$HLOG" | grep -avc "subscriber threw")
cex=$(grep -aE "Exception|NullReference" "$CLOG" | grep -avc "subscriber threw")
if [ "$hex" = "0" ]; then echo "PASS: no host exceptions"; pass=$((pass+1)); else echo "FAIL: host exceptions=$hex"; fail=$((fail+1)); fi
if [ "$cex" = "0" ]; then echo "PASS: no client exceptions"; pass=$((pass+1)); else echo "FAIL: client exceptions=$cex"; fail=$((fail+1)); fi

# anim parity sample: compare most recent P1 L0 hash host vs client
hl0=$(grep -a "ANIMDUMP HOST P1" "$HLOG" | tail -1 | grep -oE "L0=0x[0-9a-fA-F]+" | head -1)
cl0=$(grep -a "ANIMDUMP CLIENT P1" "$CLOG" | tail -1 | grep -oE "L0=0x[0-9a-fA-F]+" | head -1)
echo "anim parity sample: host P1 $hl0 vs client P1 $cl0 (informational — timing skew expected)"

# --- teardown -------------------------------------------------------------------
taskkill //IM Cuphead.exe //F >/dev/null 2>&1
sleep 3
until cp "$HOST/steam_api64.dll.steam_orig" "$HOST/steam_api64.dll" 2>/dev/null; do sleep 2; done
until cp "$CLI/steam_api64.dll.steam_orig" "$CLI/steam_api64.dll" 2>/dev/null; do sleep 2; done

SAVEHASH_AFTER=$(md5sum "$SAVEDIR"/*.sav 2>/dev/null | md5sum | cut -d' ' -f1)
if [ "$SAVEHASH_BEFORE" = "$SAVEHASH_AFTER" ]; then echo "PASS: save files untouched"; pass=$((pass+1));
else echo "FAIL: SAVE FILES CHANGED"; fail=$((fail+1)); fi

# restore production configs
for D in "$HOST" "$CLI"; do
  CFG="$D/BepInEx/config/leif.cupheadcoop.cfg"
  setopt "$CFG" Debug BlockSaves false
  setopt "$CFG" Debug AutoPlay false
  setopt "$CFG" Debug DumpAnimState false
  setopt "$CFG" Network AutoStart Off
done
setopt "$HCFG" Network Transport Steam
setopt "$HCFG" Debug AutoLoadLevel ""
setopt "$HCFG" Debug KillP2AfterSec 0

say "SUMMARY: $pass passed, $fail failed"
[ "$fail" = "0" ]
