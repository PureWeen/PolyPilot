#!/bin/bash
# Builds AutoPilot.App, launches a new instance, waits for it to be ready,
# then kills the old instance(s) for a seamless handoff.
set -e

PROJECT_DIR="$(cd "$(dirname "$0")" && pwd)"
BUILD_DIR="$PROJECT_DIR/bin/Debug/net10.0-maccatalyst/maccatalyst-arm64"
APP_NAME="AutoPilot.app"
STAGING_DIR="$PROJECT_DIR/bin/staging"

# Capture PIDs of currently running instances BEFORE launch
OLD_PIDS=$(ps -eo pid,comm | grep "AutoPilot" | grep -v grep | grep -v "AutoPilot.App.csproj" | awk '{print $1}' | tr '\n' ' ')

echo "🔨 Building..."
cd "$PROJECT_DIR"
dotnet build AutoPilot.App.csproj -f net10.0-maccatalyst 2>&1 | tail -8

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
    NEW_PIDS=$(ps -eo pid,comm | grep "AutoPilot" | grep -v grep | grep -v "AutoPilot.App.csproj" | awk '{print $1}')
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
