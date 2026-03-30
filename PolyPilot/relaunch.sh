#!/bin/bash
# Builds PolyPilot, then kills the old instance and launches a new one.
#
# ARCHITECTURE: PolyPilot (the UI) connects to a PERSISTENT headless Copilot CLI
# server over TCP. Sessions and tool execution live in the CLI, NOT the UI.
# Killing the UI drops the TCP connection but the CLI keeps running.
#
# IMPORTANT: The kill+relaunch happens in a DETACHED background process so that
# this script returns immediately after a successful build. This is critical when
# a Copilot agent calls relaunch.sh via a bash tool — the tool result must be
# delivered back to the CLI BEFORE the UI is killed, otherwise the TCP drop
# interrupts the agent's turn and it goes idle.
#
# Sequence:
#   Phase 1 (synchronous — agent sees output):
#     1. Build the MAUI app
#     2. Copy to staging directory
#     3. Spawn Phase 2 in background
#     4. Exit 0 (bash tool completes, result delivered to CLI)
#
#   Phase 2 (background — runs after tool returns):
#     5. Wait RELAUNCH_DELAY seconds (let tool result propagate)
#     6. Kill old PolyPilot UI instance(s)
#     7. Launch new instance
#     8. Verify stability
#     9. Log result to ~/.polypilot/relaunch.log
#
# IMPORTANT: ONLY launches if build succeeds. If build fails:
#   - Shows clear error messages with line numbers and error codes
#   - Does NOT launch old/stale binary
#   - Exits with code 1
#   - Old app instance remains running
#
# Usage:
#   ./relaunch.sh                # Normal relaunch (background kill+launch)
#   ./relaunch.sh --sync         # Synchronous relaunch (old behavior, blocks until stable)

PROJECT_DIR="$(cd "$(dirname "$0")" && pwd)"
BUILD_DIR="$PROJECT_DIR/bin/Debug/net10.0-maccatalyst/maccatalyst-arm64"
APP_NAME="PolyPilot.app"
STAGING_DIR="$PROJECT_DIR/bin/staging"

MAX_LAUNCH_ATTEMPTS=2
STABILITY_SECONDS=8
# Seconds to wait before killing the old UI. Gives the CLI time to receive the
# bash tool result and continue the agent's turn before the TCP connection drops.
RELAUNCH_DELAY=10
RELAUNCH_LOG="$HOME/.polypilot/relaunch.log"

# Parse flags
SYNC_MODE=false
while [[ $# -gt 0 ]]; do
    case "$1" in
        --sync)
            SYNC_MODE=true
            shift
            ;;
        --continue-session)
            # Deprecated: no longer needed since the tool result is delivered before kill.
            echo "⚠️  --continue-session is deprecated (tool result now delivered before kill)"
            shift
            ;;
        *)
            shift
            ;;
    esac
done

# Prefer ~/.dotnet/dotnet (.NET 10) over system dotnet (.NET 7)
if [ -x "$HOME/.dotnet/dotnet" ]; then
    export PATH="$HOME/.dotnet:$PATH"
fi

# Capture PIDs of currently running PolyPilot app instances BEFORE build.
# Use end-of-line anchor so we only match the app binary (path ends with "PolyPilot"),
# NOT the copilot headless server bundled inside PolyPilot.app/Contents/MonoBundle/copilot.
OLD_PIDS=$(ps -eo pid,comm | grep "PolyPilot$" | grep -v grep | awk '{print $1}' | tr '\n' ' ')

echo "🔨 Building..."
cd "$PROJECT_DIR"

# -p:ValidateXcodeVersion=false bypasses the .NET SDK's Xcode version-string gate.
# Safe for minor version skew (Apple ships Xcode faster than .NET certifies it).
# A major Xcode incompatibility will still surface as a compile/link error.
BUILD_OUTPUT=$(dotnet build PolyPilot.csproj -f net10.0-maccatalyst -p:ValidateXcodeVersion=false 2>&1)
BUILD_EXIT_CODE=$?

if [ $BUILD_EXIT_CODE -ne 0 ]; then
    echo "❌ BUILD FAILED!"
    echo ""
    echo "Error details:"
    echo "$BUILD_OUTPUT" | grep -A 5 "error CS" || echo "$BUILD_OUTPUT" | tail -30
    echo ""
    echo "To fix: Check the error messages above and correct the code issues."
    echo "Old app instance remains running."
    exit 1
fi

# Build succeeded, show brief success message
echo "$BUILD_OUTPUT" | tail -3

echo "📦 Copying to staging..."
rm -rf "$STAGING_DIR/$APP_NAME"
mkdir -p "$STAGING_DIR"
if ! ditto "$BUILD_DIR/$APP_NAME" "$STAGING_DIR/$APP_NAME"; then
    echo "❌ Failed to copy app bundle to staging"
    echo "Old app instance remains running."
    exit 1
fi

# --- Phase 2: Kill old UI + launch new UI ---
# This function does the actual kill+launch work. In default (async) mode it runs
# in a detached background process; in --sync mode it runs inline.
do_relaunch() {
    local LOG="$1"
    local DELAY="$2"
    local OLD_PIDS="$3"
    local STAGING_DIR="$4"
    local APP_NAME="$5"
    local MAX_ATTEMPTS="$6"
    local STAB_SECONDS="$7"

    mkdir -p "$(dirname "$LOG")"
    echo "--- Relaunch started at $(date) ---" >> "$LOG"

    if [ "$DELAY" -gt 0 ]; then
        echo "Waiting ${DELAY}s for tool result to propagate..." >> "$LOG"
        sleep "$DELAY"
    fi

    # Kill old instance(s) so ports (e.g. MauiDevFlow 9223) are freed
    if [ -n "$OLD_PIDS" ]; then
        for OLD_PID in $OLD_PIDS; do
            echo "Killing PID $OLD_PID" >> "$LOG"
            kill "$OLD_PID" 2>/dev/null || true
        done
        sleep 2
        # SIGKILL any survivors — MAUI apps may not exit cleanly from SIGTERM
        for OLD_PID in $OLD_PIDS; do
            if kill -0 "$OLD_PID" 2>/dev/null; then
                echo "Force-killing PID $OLD_PID" >> "$LOG"
                kill -9 "$OLD_PID" 2>/dev/null || true
            fi
        done
        sleep 1
    fi

    for ATTEMPT in $(seq 1 "$MAX_ATTEMPTS"); do
        echo "Launching attempt $ATTEMPT/$MAX_ATTEMPTS..." >> "$LOG"
        mkdir -p ~/.polypilot
        nohup "$STAGING_DIR/$APP_NAME/Contents/MacOS/PolyPilot" > ~/.polypilot/console.log 2>&1 &
        NEW_PID=$!

        if [ -z "$NEW_PID" ]; then
            echo "Failed to launch" >> "$LOG"
            if [ "$ATTEMPT" -lt "$MAX_ATTEMPTS" ]; then
                continue
            fi
            echo "FAILED: Could not launch after $MAX_ATTEMPTS attempts" >> "$LOG"
            return 1
        fi

        STABLE=true
        for i in $(seq 1 "$STAB_SECONDS"); do
            sleep 1
            if ! kill -0 "$NEW_PID" 2>/dev/null; then
                STABLE=false
                break
            fi
        done

        if [ "$STABLE" = true ]; then
            echo "SUCCESS: New instance stable (PID $NEW_PID)" >> "$LOG"
            return 0
        fi

        echo "Instance crashed (PID $NEW_PID)" >> "$LOG"
        if [ "$ATTEMPT" -ge "$MAX_ATTEMPTS" ]; then
            echo "FAILED: Unstable after $MAX_ATTEMPTS attempts" >> "$LOG"
            return 1
        fi
    done
}

if [ "$SYNC_MODE" = true ]; then
    # --sync mode: old behavior, blocks until relaunch completes.
    # Use this from a terminal, NOT from a Copilot agent bash tool.
    echo "🔪 Closing old instance(s)..."
    do_relaunch "$RELAUNCH_LOG" 0 "$OLD_PIDS" "$STAGING_DIR" "$APP_NAME" "$MAX_LAUNCH_ATTEMPTS" "$STABILITY_SECONDS"
    if [ $? -eq 0 ]; then
        echo "✅ Relaunch complete!"
        exit 0
    else
        echo "❌ Relaunch failed. Check $RELAUNCH_LOG"
        exit 1
    fi
else
    # Default: async mode. Return immediately so the bash tool result is delivered
    # to the CLI before the TCP connection drops.
    (do_relaunch "$RELAUNCH_LOG" "$RELAUNCH_DELAY" "$OLD_PIDS" "$STAGING_DIR" "$APP_NAME" "$MAX_LAUNCH_ATTEMPTS" "$STABILITY_SECONDS") &
    disown

    echo ""
    echo "✅ Build succeeded! Relaunch scheduled."
    echo "   The old app will be killed in ~${RELAUNCH_DELAY}s and new one launched."
    echo "   Check $RELAUNCH_LOG for relaunch status."
    exit 0
fi
