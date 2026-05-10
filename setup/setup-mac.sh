#!/usr/bin/env bash
# CupheadCoop client installer for native macOS Cuphead.
#
# Usage:
#   ./setup-mac.sh <host-ip-or-zerotier-address> [path-to-Cuphead-folder]
#
# Example:
#   ./setup-mac.sh 192.168.0.4
#   ./setup-mac.sh 10.242.74.251 "/Users/me/Library/Application Support/Steam/steamapps/common/Cuphead"
#
# Pre-reqs:
#   - Cuphead installed via Steam (native Mac build).
#   - This script lives in the same folder as CupheadCoop-v0.1.0.zip.
#   - bash, curl, ditto (all preinstalled on macOS).

set -euo pipefail

HOST_IP="${1:-}"
CUPHEAD_DIR="${2:-}"
PORT="47777"
CONNECT_KEY="cuphead-coop-v0"

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# BepInEx 5.4.23.5 macOS_universal ships a Preloader.dll that crashes on launch with
# `DllNotFoundException: libc.so.6` — its platform detection unconditionally tries the
# Linux uname before catching, and on macOS the call hard-fails before the OSX fallback
# fires. 5.4.22's unix preloader catches the missing-libc and falls through cleanly.
# Don't bump this URL without confirming the upstream macOS preloader is fixed.
BEPINEX_URL="https://github.com/BepInEx/BepInEx/releases/download/v5.4.22/BepInEx_unix_5.4.22.0.zip"

TMP="$(mktemp -d)"
trap 'rm -rf "$TMP"' EXIT

if [[ -z "$HOST_IP" ]]; then
  echo "usage: $0 <host-ip-or-zerotier-address> [path-to-cuphead-folder]" >&2
  exit 1
fi

# Locate the plugin payload. Safari on macOS auto-extracts .zip files, so we accept either:
#   (a) CupheadCoop-v0.1.0.zip sitting next to the script,
#   (b) an already-extracted CupheadCoop/ folder containing CupheadCoop.dll, OR
#   (c) the wrapper folder CupheadCoop-v0.1.0/ that Archive Utility sometimes creates.
PLUGIN_ZIP=""
PLUGIN_DIR=""
for try in \
  "$SCRIPT_DIR/CupheadCoop-v0.1.0.zip" \
  "$SCRIPT_DIR"/CupheadCoop-v*.zip; do
  [[ -f "$try" ]] && PLUGIN_ZIP="$try" && break
done
if [[ -z "$PLUGIN_ZIP" ]]; then
  for try in \
    "$SCRIPT_DIR/CupheadCoop" \
    "$SCRIPT_DIR/CupheadCoop-v0.1.0/CupheadCoop" \
    "$SCRIPT_DIR"/CupheadCoop-v*/CupheadCoop; do
    if [[ -d "$try" && -f "$try/CupheadCoop.dll" ]]; then
      PLUGIN_DIR="$try"
      break
    fi
  done
fi
if [[ -z "$PLUGIN_ZIP" && -z "$PLUGIN_DIR" ]]; then
  echo "error: couldn't find the plugin payload next to this script." >&2
  echo "       expected one of:" >&2
  echo "         $SCRIPT_DIR/CupheadCoop-v0.1.0.zip" >&2
  echo "         $SCRIPT_DIR/CupheadCoop/ (with CupheadCoop.dll inside)" >&2
  echo "         $SCRIPT_DIR/CupheadCoop-v0.1.0/CupheadCoop/" >&2
  exit 1
fi

# 1. Locate Cuphead.
if [[ -z "$CUPHEAD_DIR" ]]; then
  for candidate in \
    "$HOME/Library/Application Support/Steam/steamapps/common/Cuphead" \
    "/Applications/Steam/steamapps/common/Cuphead"; do
    if [[ -d "$candidate" ]]; then
      CUPHEAD_DIR="$candidate"
      break
    fi
  done

  # Fallback: scan all Steam libraries listed in libraryfolders.vdf.
  if [[ -z "$CUPHEAD_DIR" ]]; then
    LIB_VDF="$HOME/Library/Application Support/Steam/steamapps/libraryfolders.vdf"
    if [[ -f "$LIB_VDF" ]]; then
      while IFS= read -r path; do
        local_path="${path}/steamapps/common/Cuphead"
        if [[ -d "$local_path" ]]; then
          CUPHEAD_DIR="$local_path"
          break
        fi
      done < <(grep -oE '"path"[[:space:]]*"[^"]*"' "$LIB_VDF" | sed -E 's/.*"path"[[:space:]]*"([^"]*)".*/\1/')
    fi
  fi
fi

if [[ -z "$CUPHEAD_DIR" || ! -d "$CUPHEAD_DIR" ]]; then
  echo "error: couldn't auto-find Cuphead. Pass the install folder as the second arg." >&2
  echo "       e.g. $0 $HOST_IP \"$HOME/Library/Application Support/Steam/steamapps/common/Cuphead\"" >&2
  exit 2
fi

# Sanity-check we're in the right place.
APP=$(find "$CUPHEAD_DIR" -maxdepth 2 -name "Cuphead.app" -type d -print -quit 2>/dev/null || true)
if [[ -z "$APP" ]]; then
  echo "error: Cuphead.app not found inside $CUPHEAD_DIR" >&2
  echo "       (this script targets the native macOS build; Wine/Whisky needs the Windows installer.)" >&2
  exit 3
fi

echo "✓ Cuphead at: $CUPHEAD_DIR"
echo "  app:        $APP"

# 2. Install BepInEx if missing.
if [[ ! -f "$CUPHEAD_DIR/run_bepinex.sh" || ! -d "$CUPHEAD_DIR/BepInEx/core" ]]; then
  echo "→ Installing BepInEx 5.4.22 (unix)…"
  curl -L --fail --progress-bar -o "$TMP/bep.zip" "$BEPINEX_URL"
  ditto -x -k "$TMP/bep.zip" "$TMP/bep"
  # Copy contents (not the parent folder).
  /bin/cp -R "$TMP/bep/" "$CUPHEAD_DIR/"
  chmod +x "$CUPHEAD_DIR/run_bepinex.sh" 2>/dev/null || true
  # 5.4.22's run_bepinex.sh ships with a blank executable_name; on macOS this must
  # point at the .app bundle so doorstop knows what to inject into.
  /usr/bin/sed -i '' 's/^executable_name=""$/executable_name="Cuphead.app"/' "$CUPHEAD_DIR/run_bepinex.sh" 2>/dev/null || true
  # Remove macOS quarantine flag so Steam launching the script doesn't get blocked.
  xattr -dr com.apple.quarantine "$CUPHEAD_DIR/run_bepinex.sh" "$CUPHEAD_DIR/BepInEx" 2>/dev/null || true
  echo "✓ BepInEx installed"
else
  echo "✓ BepInEx already present"
fi

# 2a. Patch Cuphead's bundled Mono dllmap so BepInEx's preloader can find libc.
# BepInEx 5.x's PlatformUtils hard-codes [DllImport("libc.so.6")] for Linux uname()
# detection. On macOS Mono's OSVersion.Platform reports "Unix", so the preloader takes
# the Linux branch and crashes with DllNotFoundException before any plugin loads.
# Fix: add a libc.so.6 → libSystem.dylib remap to the dllmap config Mono reads at startup.
# This is idempotent and safe — adds one line, only takes effect on macOS.
for cfg in \
  "$CUPHEAD_DIR/Cuphead.app/Contents/Mono/etc/mono/config" \
  "$CUPHEAD_DIR/Cuphead.app/Contents/MonoBleedingEdge/etc/mono/config"; do
  if [[ -f "$cfg" ]] && ! grep -q 'libc.so.6' "$cfg"; then
    /usr/bin/sed -i '' \
      -e '/<dllmap dll="libc" target="libc.dylib"/a\
\	<dllmap dll="libc.so.6" target="/usr/lib/libSystem.dylib" os="osx"/>' "$cfg"
    echo "✓ Mono dllmap patched: $cfg"
  fi
done

# 3. Drop the plugin (zip and folder forms both supported).
mkdir -p "$CUPHEAD_DIR/BepInEx/plugins"
if [[ -n "$PLUGIN_ZIP" ]]; then
  ditto -x -k "$PLUGIN_ZIP" "$CUPHEAD_DIR/BepInEx/plugins/"
  echo "✓ Plugin (from zip): $CUPHEAD_DIR/BepInEx/plugins/CupheadCoop/"
else
  rm -rf "$CUPHEAD_DIR/BepInEx/plugins/CupheadCoop"
  /bin/cp -R "$PLUGIN_DIR" "$CUPHEAD_DIR/BepInEx/plugins/CupheadCoop"
  echo "✓ Plugin (from folder): $CUPHEAD_DIR/BepInEx/plugins/CupheadCoop/"
fi
xattr -dr com.apple.quarantine "$CUPHEAD_DIR/BepInEx/plugins/CupheadCoop" 2>/dev/null || true

# 4. Pre-write the plugin config with the host IP and sensible defaults.
mkdir -p "$CUPHEAD_DIR/BepInEx/config"
cat > "$CUPHEAD_DIR/BepInEx/config/leif.cupheadcoop.cfg" <<EOF
## CupheadCoop config — pre-seeded by setup-mac.sh.

[Debug]
ForceP2WalkRight = false
Verbose = false

[Hotkeys]
# Mac note: F10/F11 are commonly intercepted by macOS (Mission Control etc.) and
# don't reach Cuphead even with Fn held. Letter keys are reliable. Pick something
# you don't use in gameplay; J/K aren't bound by Cuphead's defaults.
Host = F9
Connect = J
Disconnect = K

[Input]
SendRateHz = 60
BufferFrames = 2

[Network]
RemoteHost = $HOST_IP
Port = $PORT
ConnectKey = $CONNECT_KEY
EOF
echo "✓ Plugin config written (RemoteHost = $HOST_IP, Port = $PORT)"

# 5. Enable BepInEx console on macOS — uses the launching Terminal as console.
BEP_CFG="$CUPHEAD_DIR/BepInEx/config/BepInEx.cfg"
if [[ -f "$BEP_CFG" ]]; then
  /usr/bin/sed -i '' '/^\[Logging\.Console\]/,/^\[/ { s/^Enabled = false$/Enabled = true/; }' "$BEP_CFG" || true
  echo "✓ BepInEx console enabled"
fi

cat <<EOF

────────────────────────────────────────
Last manual step (Steam can't be edited from a script):

  1. Open Steam.
  2. Right-click Cuphead → Properties → General → Launch Options.
  3. Paste, including the quotes:

     "$CUPHEAD_DIR/run_bepinex.sh" %command%

  4. Close the dialog and launch Cuphead from Steam normally.
────────────────────────────────────────

In-game:
  • Start a single-player game (or local 2-player for M4 spectator-view).
  • Press J to connect to $HOST_IP:$PORT.
  • Top-left overlay should switch to mode=Client; the BepInEx log will show
    "CoopClient: dialing …" then "CoopClient: handshake ok".
  • Press K to disconnect when you're done.

(F-keys are intentionally avoided on Mac — macOS frequently intercepts F10/F11
even with Fn held, so the keypress never reaches Cuphead. Change the hotkeys
in $CUPHEAD_DIR/BepInEx/config/leif.cupheadcoop.cfg if J/K conflict for you.)

If macOS blocks run_bepinex.sh on first launch (Gatekeeper), open Terminal and run:
  xattr -dr com.apple.quarantine "$CUPHEAD_DIR"
EOF
