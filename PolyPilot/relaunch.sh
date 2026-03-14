#!/bin/bash
# Builds PolyPilot, kills the old instance (freeing ports like MauiDevFlow 9223),
# then launches a new instance.
# 
# IMPORTANT: ONLY launches if build succeeds. If build fails:
#   - Shows clear error messages with line numbers and error codes
#   - Does NOT launch old/stale binary
#   - Exits with code 1
#   - Old app instance remains running
#
# Kill-first ensures the new instance binds the same MauiDevFlow agent port,
# so `maui-devflow` auto-discovery continues to work across relaunches.

PROJECT_DIR="$(cd "$(dirname "$0")" && pwd)"
BUILD_DIR="$PROJECT_DIR/bin/Debug/net10.0-maccatalyst/maccatalyst-arm64"
APP_NAME="PolyPilot.app"
STAGING_DIR="$PROJECT_DIR/bin/staging"

MAX_LAUNCH_ATTEMPTS=2
STABILITY_SECONDS=8

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
ditto "$BUILD_DIR/$APP_NAME" "$STAGING_DIR/$APP_NAME"

# Kill old instance(s) first so ports (e.g. MauiDevFlow 9223) are freed
if [ -n "$OLD_PIDS" ]; then
    echo "🔪 Closing old instance(s)..."
    for OLD_PID in $OLD_PIDS; do
        echo "   Killing PID $OLD_PID"
        kill "$OLD_PID" 2>/dev/null || true
    done
    # Brief pause to let ports release
    sleep 1
fi

for ATTEMPT in $(seq 1 "$MAX_LAUNCH_ATTEMPTS"); do
    echo "🚀 Launching new instance (attempt $ATTEMPT/$MAX_LAUNCH_ATTEMPTS)..."
    mkdir -p ~/.polypilot
    nohup "$STAGING_DIR/$APP_NAME/Contents/MacOS/PolyPilot" > ~/.polypilot/console.log 2>&1 &
    NEW_PID=$!

    if [ -z "$NEW_PID" ]; then
        echo "⚠️  Failed to launch new instance."
        if [ "$ATTEMPT" -lt "$MAX_LAUNCH_ATTEMPTS" ]; then
            echo "🔁 Retrying launch..."
            continue
        fi
        exit 1
    fi

    echo "✅ New instance running (PID $NEW_PID)"
    echo "🔎 Verifying stability for ${STABILITY_SECONDS}s..."
    STABLE=true
    for i in $(seq 1 "$STABILITY_SECONDS"); do
        sleep 1
        if ! kill -0 "$NEW_PID" 2>/dev/null; then
            STABLE=false
            break
        fi
    done

    if [ "$STABLE" = true ]; then
        echo "✅ Relaunch complete!"
        exit 0
    fi

    echo "❌ New instance crashed quickly (PID $NEW_PID)."
    if [ "$ATTEMPT" -lt "$MAX_LAUNCH_ATTEMPTS" ]; then
        echo "🔁 Retrying launch..."
        continue
    fi

    echo "⚠️  New instance is unstable."
    exit 1
done
