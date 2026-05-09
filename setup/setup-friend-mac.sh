#!/usr/bin/env bash
# CupheadCoop friend-PC installer for native macOS Cuphead.
#
# Usage:
#   ./setup-friend-mac.sh <host-ip-or-zerotier-address> [path-to-Cuphead-folder]
#
# Example:
#   ./setup-friend-mac.sh 192.168.0.4
#   ./setup-friend-mac.sh 10.242.74.251 "/Users/me/Library/Application Support/Steam/steamapps/common/Cuphead"
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
DIST_ZIP="${SCRIPT_DIR}/CupheadCoop-v0.1.0.zip"
BEPINEX_URL="https://github.com/BepInEx/BepInEx/releases/download/v5.4.23.5/BepInEx_macos_universal_5.4.23.5.zip"

TMP="$(mktemp -d)"
trap 'rm -rf "$TMP"' EXIT

if [[ -z "$HOST_IP" ]]; then
  echo "usage: $0 <host-ip-or-zerotier-address> [path-to-cuphead-folder]" >&2
  exit 1
fi

if [[ ! -f "$DIST_ZIP" ]]; then
  echo "error: CupheadCoop-v0.1.0.zip not found next to this script:" >&2
  echo "       expected at: $DIST_ZIP" >&2
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
  echo "→ Installing BepInEx 5.4.23.5 (macOS universal)…"
  curl -L --fail --progress-bar -o "$TMP/bep.zip" "$BEPINEX_URL"
  ditto -x -k "$TMP/bep.zip" "$TMP/bep"
  # Copy contents (not the parent folder).
  /bin/cp -R "$TMP/bep/" "$CUPHEAD_DIR/"
  chmod +x "$CUPHEAD_DIR/run_bepinex.sh" 2>/dev/null || true
  # Remove macOS quarantine flag so Steam launching the script doesn't get blocked.
  xattr -dr com.apple.quarantine "$CUPHEAD_DIR/run_bepinex.sh" "$CUPHEAD_DIR/BepInEx" 2>/dev/null || true
  echo "✓ BepInEx installed"
else
  echo "✓ BepInEx already present"
fi

# 3. Drop the plugin.
mkdir -p "$CUPHEAD_DIR/BepInEx/plugins"
ditto -x -k "$DIST_ZIP" "$CUPHEAD_DIR/BepInEx/plugins/"
xattr -dr com.apple.quarantine "$CUPHEAD_DIR/BepInEx/plugins/CupheadCoop" 2>/dev/null || true
echo "✓ Plugin: $CUPHEAD_DIR/BepInEx/plugins/CupheadCoop/"

# 4. Pre-write the plugin config with the host IP and friendly defaults.
mkdir -p "$CUPHEAD_DIR/BepInEx/config"
cat > "$CUPHEAD_DIR/BepInEx/config/leif.cupheadcoop.cfg" <<EOF
## CupheadCoop config — pre-seeded by setup-friend-mac.sh.

[Debug]
ForceP2WalkRight = false
Verbose = false

[Hotkeys]
Host = F9
Connect = F10
Disconnect = F11

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
  • Start a single-player game.
  • Press F10 to connect to $HOST_IP:$PORT.
  • Top-left overlay should show "CoopClient: handshake ok".
  • Press F11 to disconnect when you're done.

If F10 doesn't fire (some Mac keyboards bind F-keys to media controls),
either hold Fn+F10 or change the [Hotkeys] Connect entry in:
  $CUPHEAD_DIR/BepInEx/config/leif.cupheadcoop.cfg

If macOS blocks run_bepinex.sh on first launch (Gatekeeper), open Terminal and run:
  xattr -dr com.apple.quarantine "$CUPHEAD_DIR"
EOF
