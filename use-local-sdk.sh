#!/bin/bash
# Builds PolyPilot against a local source build of the Copilot SDK.
#
# Usage:
#   ./use-local-sdk.sh [path-to-sdk]    # Switch to local SDK
#   ./use-local-sdk.sh --revert         # Switch back to NuGet package
#
# The SDK path defaults to ../copilot-sdk (sibling directory). Override with:
#   ./use-local-sdk.sh /path/to/copilot-sdk
#   COPILOT_SDK_PATH=/path/to/copilot-sdk ./use-local-sdk.sh
#
# First-time setup:
#   git clone https://github.com/github/copilot-sdk.git ../copilot-sdk
#   # Or use PureWeen's fork with PolyPilot-specific fixes:
#   git clone https://github.com/PureWeen/copilot-sdk.git ../copilot-sdk
#   cd ../copilot-sdk && git checkout upstream_validation
#
# What it does:
#   1. Packs the local SDK as a NuGet package (version 0.3.0-local)
#   2. Adds a local NuGet source to nuget.config
#   3. Updates all csproj references to 0.3.0-local
#   4. Clears NuGet cache to force re-resolve
#
# To iterate on SDK changes:
#   1. Edit code in the copilot-sdk/dotnet/src/ directory
#   2. Re-run ./use-local-sdk.sh (rebuilds + re-packs automatically)
#   3. Build PolyPilot normally (dotnet build or ./relaunch.sh)

set -e

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
LOCAL_VERSION="0.3.0-local"

# Resolve SDK path: argument > env var > default sibling directory
if [ "$1" != "--revert" ] && [ -n "$1" ]; then
    SDK_DIR="$1"
elif [ -n "$COPILOT_SDK_PATH" ]; then
    SDK_DIR="$COPILOT_SDK_PATH"
else
    SDK_DIR="$SCRIPT_DIR/../copilot-sdk"
fi
NUPKG_DIR="$SDK_DIR/dotnet/nupkg"

if [ "$1" = "--revert" ]; then
    echo "⏪ Reverting to NuGet package..."
    cd "$SCRIPT_DIR"
    git checkout -- nuget.config \
        PolyPilot/PolyPilot.csproj \
        PolyPilot.Tests/PolyPilot.Tests.csproj \
        PolyPilot.Provider.Abstractions/PolyPilot.Provider.Abstractions.csproj \
        PolyPilot.Gtk/PolyPilot.Gtk.csproj \
        PolyPilot.Console/PolyPilot.csproj 2>/dev/null || true
    rm -rf ~/.nuget/packages/github.copilot.sdk/$LOCAL_VERSION
    echo "✅ Reverted to NuGet SDK. Run 'dotnet restore' to re-resolve."
    exit 0
fi

# Validate SDK path
if [ ! -d "$SDK_DIR/dotnet/src" ]; then
    echo "❌ Copilot SDK not found at: $SDK_DIR"
    echo ""
    echo "Clone it first:"
    echo "  git clone https://github.com/PureWeen/copilot-sdk.git $SDK_DIR"
    echo "  cd $SDK_DIR && git checkout upstream_validation"
    echo ""
    echo "Or specify a custom path:"
    echo "  ./use-local-sdk.sh /path/to/copilot-sdk"
    echo "  COPILOT_SDK_PATH=/path/to/sdk ./use-local-sdk.sh"
    exit 1
fi

echo "📦 Building local Copilot SDK..."
echo "   Source: $SDK_DIR (branch: $(cd "$SDK_DIR" && git branch --show-current 2>/dev/null || echo 'detached'))"
cd "$SDK_DIR/dotnet"
rm -rf nupkg
dotnet pack src/GitHub.Copilot.SDK.csproj -c Debug -o ./nupkg -p:Version=$LOCAL_VERSION --nologo 2>&1 | tail -3

if [ ! -f "$NUPKG_DIR/GitHub.Copilot.SDK.$LOCAL_VERSION.nupkg" ]; then
    echo "❌ Pack failed — nupkg not found"
    exit 1
fi

echo "🔧 Updating PolyPilot references..."
cd "$SCRIPT_DIR"

# Update nuget.config — add local source if not present
if ! grep -q "local-sdk" nuget.config; then
    sed -i '' '/<clear \/>/a\
    <add key="local-sdk" value="'"$NUPKG_DIR"'" />' nuget.config
    sed -i '' '/<packageSourceMapping>/a\
    <packageSource key="local-sdk">\
      <package pattern="GitHub.Copilot.SDK" />\
    </packageSource>' nuget.config
else
    # Update the path in case SDK location changed
    sed -i '' 's|key="local-sdk" value="[^"]*"|key="local-sdk" value="'"$NUPKG_DIR"'"|' nuget.config
fi

# Update all csproj files
for f in PolyPilot/PolyPilot.csproj PolyPilot.Tests/PolyPilot.Tests.csproj \
         PolyPilot.Provider.Abstractions/PolyPilot.Provider.Abstractions.csproj \
         PolyPilot.Gtk/PolyPilot.Gtk.csproj PolyPilot.Console/PolyPilot.csproj; do
    if [ -f "$f" ]; then
        sed -i '' 's/GitHub.Copilot.SDK" Version="[^"]*"/GitHub.Copilot.SDK" Version="'"$LOCAL_VERSION"'"/g' "$f"
    fi
done

# Clear cached local package so dotnet picks up the fresh build
rm -rf ~/.nuget/packages/github.copilot.sdk/$LOCAL_VERSION

echo "🔄 Restoring..."
dotnet restore PolyPilot/PolyPilot.csproj --force --nologo 2>&1 | tail -3

echo ""
echo "✅ PolyPilot now uses local Copilot SDK ($LOCAL_VERSION)"
echo "   SDK source: $SDK_DIR"
echo "   To rebuild after SDK changes: ./use-local-sdk.sh $([ "$SDK_DIR" != "$SCRIPT_DIR/../copilot-sdk" ] && echo "$SDK_DIR")"
echo "   To revert to NuGet:           ./use-local-sdk.sh --revert"
