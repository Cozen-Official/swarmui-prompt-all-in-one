#!/usr/bin/env bash
# =============================================================================
# dev-setup.sh — one-shot test environment bootstrap for swarmui-prompt-all-in-one
#
# What this script does
# ─────────────────────
# 1. Installs system and .NET toolchain prerequisites (apt / dotnet-install.sh).
# 2. Clones SwarmUI next to this repo (defaults to ../SwarmUI).
# 3. Copies this extension into SwarmUI's Extensions folder.
# 4. Writes a minimal SwarmUI settings file so the installer page is skipped.
# 5. Builds SwarmUI (dotnet build).
# 6. Installs Python test dependencies (playwright + chromium browser).
# 7. Starts SwarmUI in headless mode on http://localhost:7801.
# 8. Waits for the extension to appear in the startup log and prints the result.
#
# Usage
# ─────
#   bash dev-setup.sh            # full setup + start
#   bash dev-setup.sh --no-start # setup only, do not start SwarmUI at the end
#
# Environment variables (all optional)
# ─────────────────────────────────────
#   SWARM_DIR   absolute path where SwarmUI should be cloned/found
#               default: <repo-root>/../SwarmUI
#   SWARM_PORT  port SwarmUI listens on
#               default: 7801
#   SWARM_LOG   path to the SwarmUI console log written by this script
#               default: /tmp/swarmui-dev.log
# =============================================================================

set -euo pipefail

# ── helpers ───────────────────────────────────────────────────────────────────
info()    { echo "  [INFO] $*"; }
success() { echo "  [✓] $*"; }
warn()    { echo "  [!] $*"; }
die()     { echo "  [✗] $*" >&2; exit 1; }

# ── resolve paths ─────────────────────────────────────────────────────────────
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_DIR="$SCRIPT_DIR"
SWARM_DIR="${SWARM_DIR:-"$(cd "$REPO_DIR/.." && pwd)/SwarmUI"}"
SWARM_PORT="${SWARM_PORT:-7801}"
SWARM_LOG="${SWARM_LOG:-/tmp/swarmui-dev.log}"
EXTENSION_NAME="swarmui-prompt-all-in-one"
EXTENSION_CLASS="PromptAllInOne.PromptAllInOneExtension"
EXTENSION_DEST="$SWARM_DIR/src/Extensions/$EXTENSION_NAME"
SETTINGS_FILE="$SWARM_DIR/Data/Settings.fds"

START_SWARM=true
for arg in "$@"; do
  [[ "$arg" == "--no-start" ]] && START_SWARM=false
done

echo ""
echo "╔══════════════════════════════════════════════════════════════════╗"
echo "║  swarmui-prompt-all-in-one  dev-setup.sh                        ║"
echo "╚══════════════════════════════════════════════════════════════════╝"
echo ""
info "Repo dir  : $REPO_DIR"
info "SwarmUI   : $SWARM_DIR"
info "Port      : $SWARM_PORT"
info "Log       : $SWARM_LOG"
echo ""

# ── 1. system prerequisites ───────────────────────────────────────────────────
echo "── Step 1: system prerequisites ──────────────────────────────────────"

if [[ "$(uname -s)" == "Linux" ]]; then
  if command -v apt-get &>/dev/null; then
    # Packages needed by Playwright's Chromium headless shell on Ubuntu/Debian
    PLAYWRIGHT_DEPS=(
      libasound2t64 libatk-bridge2.0-0 libatk1.0-0 libatspi2.0-0
      libcairo2 libcups2 libdbus-1-3 libdrm2 libgbm1 libglib2.0-0
      libnspr4 libnss3 libpango-1.0-0 libpangocairo-1.0-0
      libx11-6 libxcb1 libxcomposite1 libxdamage1 libxext6
      libxfixes3 libxi6 libxkbcommon0 libxrandr2 libxrender1
    )
    MISSING=()
    for pkg in "${PLAYWRIGHT_DEPS[@]}"; do
      dpkg -s "$pkg" &>/dev/null || MISSING+=("$pkg")
    done
    if [[ ${#MISSING[@]} -gt 0 ]]; then
      info "Installing ${#MISSING[@]} missing apt package(s)..."
      # Capture full output; show it only on failure so normal runs stay clean.
      # --no-upgrade prevents apt from trying to upgrade already-installed packages
      # (avoids transient mirror failures for unrelated package updates).
      APT_OUT=$(sudo apt-get install -y --no-upgrade "${MISSING[@]}" 2>&1) || {
        echo "$APT_OUT"; die "apt-get install failed (see above).";
      }
      success "apt packages installed"
    else
      success "All apt packages already present"
    fi
  fi
fi

# ── 2. .NET 8 ─────────────────────────────────────────────────────────────────
echo "── Step 2: .NET 8 SDK ────────────────────────────────────────────────"

if ! command -v dotnet &>/dev/null; then
  info "dotnet not found. Installing .NET 8 SDK via official script..."
  curl -fsSL https://dot.net/v1/dotnet-install.sh | bash -s -- --channel 8.0
  export PATH="$HOME/.dotnet:$PATH"
  success ".NET 8 SDK installed"
else
  DOTNET_MAJOR=$(dotnet --version 2>/dev/null | cut -d. -f1)
  if [[ "$DOTNET_MAJOR" -ge 8 ]]; then
    success "dotnet $DOTNET_MAJOR already present: $(dotnet --version)"
  else
    info "dotnet $(dotnet --version) found but SwarmUI requires .NET 8+. Installing .NET 8 SDK..."
    curl -fsSL https://dot.net/v1/dotnet-install.sh | bash -s -- --channel 8.0
    export PATH="$HOME/.dotnet:$PATH"
    success ".NET 8 SDK installed"
  fi
fi

# ── 3. Python + pip ───────────────────────────────────────────────────────────
echo "── Step 3: Python dependencies ───────────────────────────────────────"

PYTHON="${PYTHON:-python3}"
if ! command -v "$PYTHON" &>/dev/null; then
  die "python3 not found. Please install Python 3.8+ and re-run."
fi
info "Using $($PYTHON --version)"

info "Installing Python test dependencies from tests/requirements.txt..."
"$PYTHON" -m pip install --quiet -r "$REPO_DIR/tests/requirements.txt"
success "Python dependencies installed"

# Install Playwright's Chromium browser (idempotent — skips download if already present)
info "Ensuring Playwright Chromium browser is installed..."
"$PYTHON" -m playwright install chromium
success "Playwright Chromium ready"

# ── 4. Clone SwarmUI ──────────────────────────────────────────────────────────
echo "── Step 4: SwarmUI repository ────────────────────────────────────────"

if [[ -d "$SWARM_DIR/.git" ]]; then
  success "SwarmUI already cloned at $SWARM_DIR"
else
  info "Cloning SwarmUI (depth=1) into $SWARM_DIR ..."
  git clone --depth=1 https://github.com/mcmonkeyprojects/SwarmUI.git "$SWARM_DIR"
  success "SwarmUI cloned"
fi

# ── 5. Install extension ──────────────────────────────────────────────────────
echo "── Step 5: Install extension into SwarmUI ────────────────────────────"

mkdir -p "$SWARM_DIR/src/Extensions"

# Always refresh so local edits are picked up on every run.
rm -rf "$EXTENSION_DEST"

# On Linux, MSBuild resolves symlinks to physical paths which breaks the
# relative paths in SwarmUI.extension.props.  Use cp -r (equivalent to the
# Windows mklink /J directory junction used in the problem statement).
cp -r "$REPO_DIR" "$EXTENSION_DEST"
success "Extension copied to $EXTENSION_DEST"

# ── 6. SwarmUI settings (skip installer) ──────────────────────────────────────
echo "── Step 6: SwarmUI settings ──────────────────────────────────────────"

mkdir -p "$(dirname "$SETTINGS_FILE")"

if [[ -f "$SETTINGS_FILE" ]] && grep -q "IsInstalled: true" "$SETTINGS_FILE"; then
  success "Settings file already has IsInstalled: true"
else
  info "Writing minimal settings file to skip the installer..."
  # SwarmUI reads its own FDS (Fred Data Store) format.  The only key that
  # matters for skipping the installer is IsInstalled.  Everything else will
  # be written by SwarmUI itself on first run.
  cat > "$SETTINGS_FILE" << 'SETTINGS_EOF'
IsInstalled: true
InstallDate: 2024-12-01
SETTINGS_EOF
  success "Settings file written to $SETTINGS_FILE"
fi

# ── 7. Copy Autocompletions tag files ─────────────────────────────────────────
echo "── Step 7: Copy Autocompletions tag files ────────────────────────────"

AUTOCOMPLETIONS_SRC="$REPO_DIR/tests/Autocompletions"
AUTOCOMPLETIONS_DEST="$SWARM_DIR/Data/Autocompletions"

if [[ -d "$AUTOCOMPLETIONS_SRC" ]]; then
  mkdir -p "$AUTOCOMPLETIONS_DEST"
  cp -r "$AUTOCOMPLETIONS_SRC/." "$AUTOCOMPLETIONS_DEST/"
  success "Autocompletions copied to $AUTOCOMPLETIONS_DEST"
else
  warn "No Autocompletions folder found at $AUTOCOMPLETIONS_SRC — skipping."
fi

# ── 8. Build SwarmUI ──────────────────────────────────────────────────────────
echo "── Step 8: Build SwarmUI ─────────────────────────────────────────────"

BINARY="$SWARM_DIR/src/bin/live_release/SwarmUI.dll"

if [[ -f "$BINARY" ]]; then
  success "SwarmUI already built at $BINARY"
else
  info "Building SwarmUI (this may take a few minutes)..."
  BUILD_LOG="$SWARM_DIR/build.log"
  dotnet build "$SWARM_DIR/src/SwarmUI.csproj" \
    --configuration Release \
    -o "$SWARM_DIR/src/bin/live_release" \
    --nologo \
    -v quiet \
    > "$BUILD_LOG" 2>&1 || { cat "$BUILD_LOG"; die "dotnet build failed (see above)."; }

  [[ -f "$BINARY" ]] || die "Build appeared to succeed but $BINARY not found."
  success "SwarmUI built (full log: $BUILD_LOG)"
fi

# ── 9. Optional: start SwarmUI ────────────────────────────────────────────────
if [[ "$START_SWARM" == "false" ]]; then
  echo ""
  echo "Setup complete (--no-start)."
  echo "  To start SwarmUI manually:"
  echo "    cd $SWARM_DIR && dotnet $BINARY --launch_mode none --port $SWARM_PORT"
  exit 0
fi

echo "── Step 9: Start SwarmUI ─────────────────────────────────────────────"

# Kill any leftover SwarmUI process on this port
EXISTING_PID=$(lsof -ti tcp:"$SWARM_PORT" 2>/dev/null || true)
if [[ -n "$EXISTING_PID" ]]; then
  info "Stopping existing process on port $SWARM_PORT (PID $EXISTING_PID)..."
  kill "$EXISTING_PID" 2>/dev/null || true
  sleep 2
fi

info "Starting SwarmUI on port $SWARM_PORT (log: $SWARM_LOG)..."
# SwarmUI resolves BuiltinExtensions/ relative to CWD, so it must be started
# from inside its own directory.  The --port flag sets the listen port.
(cd "$SWARM_DIR" && dotnet "$BINARY" \
  --launch_mode none \
  --port "$SWARM_PORT" \
  > "$SWARM_LOG" 2>&1) &
SWARM_PID=$!
echo "$SWARM_PID" > /tmp/swarmui-dev.pid
info "SwarmUI PID: $SWARM_PID"

# Wait for startup — poll for the extension-loaded log line (up to 120 s)
EXT_LOADED=false
SWARM_UP=false
for i in $(seq 1 120); do
  sleep 1
  if grep -qF "$EXTENSION_CLASS" "$SWARM_LOG" 2>/dev/null; then
    EXT_LOADED=true
  fi
  if grep -qF "is now running" "$SWARM_LOG" 2>/dev/null; then
    SWARM_UP=true
    break
  fi
  # Abort early if the process died
  if ! kill -0 "$SWARM_PID" 2>/dev/null; then
    warn "SwarmUI process exited unexpectedly."
    break
  fi
  [[ $((i % 10)) -eq 0 ]] && info "Still waiting... (${i}s)"
done

echo ""
if [[ "$SWARM_UP" == "true" ]]; then
  success "SwarmUI is running at http://localhost:$SWARM_PORT"
else
  warn "SwarmUI did not reach 'is now running' within 120 s."
  warn "Check $SWARM_LOG for details."
fi

if [[ "$EXT_LOADED" == "true" ]]; then
  success "Extension $EXTENSION_CLASS loaded successfully"
else
  warn "Extension $EXTENSION_CLASS not seen in log yet."
  warn "It may still be compiling — check $SWARM_LOG."
fi

echo ""
echo "Setup complete."
echo "  SwarmUI URL : http://localhost:$SWARM_PORT"
echo "  PID file    : /tmp/swarmui-dev.pid"
echo "  Log file    : $SWARM_LOG"
echo ""
echo "  Run UI tests:   python3 tests/<test_file>.py"
echo "  Stop SwarmUI:   kill \$(cat /tmp/swarmui-dev.pid)"
