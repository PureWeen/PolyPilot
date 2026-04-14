#!/bin/bash
# launch.sh — Build and launch PolyPilot on Mac Catalyst.
#
# Single-command automation: builds the app and either launches a fresh instance
# or hot-relaunches an existing one. Run from anywhere in the repo.
#
# Usage:
#   ./launch.sh              # Build + launch (async relaunch if already running)
#   ./launch.sh --sync       # Build + launch, wait until stable
#   ./launch.sh --build-only # Build without launching
#
# This is the recommended entry point for "build and run PolyPilot on Mac".
# It delegates to PolyPilot/relaunch.sh for the actual kill+launch lifecycle.

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
RELAUNCH_SCRIPT="$SCRIPT_DIR/PolyPilot/relaunch.sh"

if [ ! -f "$RELAUNCH_SCRIPT" ]; then
    echo "❌ Cannot find PolyPilot/relaunch.sh (expected at $RELAUNCH_SCRIPT)"
    exit 1
fi

# Pass all arguments through to relaunch.sh
exec "$RELAUNCH_SCRIPT" "$@"
