#!/bin/bash
# Builds PolyPilot, launches a new instance, waits for it to be ready,
# then kills the old instance(s) for a seamless handoff.
# 
# IMPORTANT: ONLY launches if build succeeds. If build fails:
#   - Shows clear error messages with line numbers and error codes
#   - Does NOT launch old/stale binary
#   - Exits with code 1
#   - Old app instance remains running
#
# This prevents the common issue where build errors go unnoticed and agents
# keep testing against stale code.

PROJECT_DIR="$(cd "$(dirname "$0")" && pwd)"
BUILD_DIR="$PROJECT_DIR/bin/Debug/net10.0-maccatalyst/maccatalyst-arm64"
APP_NAME="PolyPilot.app"
STAGING_DIR="$PROJECT_DIR/bin/staging"

# Capture PIDs of currently running instances BEFORE launch
OLD_PIDS=$(ps -eo pid,comm | grep "PolyPilot" | grep -v grep | grep -v "PolyPilot.csproj" | awk '{print $1}' | tr '\n' ' ')

echo "🔨 Building..."
cd "$PROJECT_DIR"

# Capture full build output to check for errors
BUILD_OUTPUT=$(dotnet build PolyPilot.csproj -f net10.0-maccatalyst 2>&1)
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
cp -R "$BUILD_DIR/$APP_NAME" "$STAGING_DIR/$APP_NAME"

echo "🚀 Launching new instance..."
open -n "$STAGING_DIR/$APP_NAME"

# Wait for the new app process to appear
echo "⏳ Waiting for new instance to start..."
for i in $(seq 1 30); do
    sleep 1
    NEW_PIDS=$(ps -eo pid,comm | grep "PolyPilot" | grep -v grep | grep -v "PolyPilot.csproj" | awk '{print $1}')
    for PID in $NEW_PIDS; do
        # Check if this PID is NOT in the old set — it's the new instance
        if ! echo "$OLD_PIDS" | grep -qw "$PID"; then
            echo "✅ New instance running (PID $PID)"
            # Give it a moment to fully initialize UI
            sleep 3
            # Now kill old instances
            if [ -n "$OLD_PIDS" ]; then
                echo "🔪 Closing old instance(s)..."
                for OLD_PID in $OLD_PIDS; do
                    echo "   Killing PID $OLD_PID"
                    kill "$OLD_PID" 2>/dev/null || true
                done
            fi
            echo "✅ Handoff complete!"
            exit 0
        fi
    done
done

echo "⚠️  Timed out waiting for new instance. Old instance left running."
exit 1
