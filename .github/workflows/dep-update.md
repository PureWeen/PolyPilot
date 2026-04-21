---
on:
  workflow_dispatch:
  schedule: daily

permissions:
  contents: read
  pull-requests: read

engine: copilot

network:
  allowed:
    - defaults
    - dotnet

tools:
  github:
    toolsets: [repos]

safe-outputs:
  create-pull-request:
    auto-merge: true

timeout-minutes: 45

---

# Update NuGet Dependencies

Update all NuGet package references, dotnet tools, and the MauiDevFlow skill to their latest available versions, then open a PR with the changes.

## Package Groups

### Group 1: GitHub Copilot SDK (nuget.org)

Package: `GitHub.Copilot.SDK`

```bash
SDK_LATEST=$(curl -s 'https://api.nuget.org/v3-flatcontainer/github.copilot.sdk/index.json' | jq -r '.versions[-1]')
echo "Latest SDK: $SDK_LATEST"
```

Update ALL of these to the **same** version:
- `PolyPilot/PolyPilot.csproj`
- `PolyPilot.Console/PolyPilot.csproj`
- `PolyPilot.Gtk/PolyPilot.Gtk.csproj`
- `PolyPilot.Tests/PolyPilot.Tests.csproj`
- `PolyPilot.Provider.Abstractions/PolyPilot.Provider.Abstractions.csproj`

### Group 2: MauiDevFlow Packages + CLI Tool (Azure DevOps dotnet10 feed)

Query the latest version — the ADO feed does NOT sort versions correctly, so use `sort -t. -k4,4n -k5,5n -k6,6n` to sort by preview number then build:
```bash
DEVFLOW_LATEST=$(curl -s 'https://pkgs.dev.azure.com/dnceng/public/_packaging/dotnet10/nuget/v3/flat2/microsoft.maui.devflow.agent/index.json' | jq -r '.versions[]' | sort -t. -k4,4n -k5,5n -k6,6n | tail -1)
echo "Latest DevFlow: $DEVFLOW_LATEST"
```

Compare it to the current version in the csproj files. These are prerelease versions like `0.1.0-preview.5.26217.12`. If the version from the feed differs from the version in the csproj files, update ALL of these to the new version:
- `Microsoft.Maui.DevFlow.Agent` — in `PolyPilot/PolyPilot.csproj` (inside `<When Condition="'$(Configuration)' == 'Debug'">`)
- `Microsoft.Maui.DevFlow.Blazor` — in `PolyPilot/PolyPilot.csproj` (inside `<When Condition="'$(Configuration)' == 'Debug'">`)
- `Microsoft.Maui.DevFlow.Agent.Gtk` — in `PolyPilot.Gtk/PolyPilot.Gtk.csproj` (inside `<When Condition="'$(Configuration)' == 'Debug'">`)
- `Microsoft.Maui.DevFlow.Blazor.Gtk` — in `PolyPilot.Gtk/PolyPilot.Gtk.csproj` (inside `<When Condition="'$(Configuration)' == 'Debug'">`)
- `microsoft.maui.cli` — in `dotnet-tools.json` (update the `"version"` field)

After updating the versions, install the maui CLI and run the skill update:
```bash
dotnet tool install -g Microsoft.Maui.Cli --version "$DEVFLOW_LATEST" --add-source https://pkgs.dev.azure.com/dnceng/public/_packaging/dotnet10/nuget/v3/index.json 2>/dev/null || true
export PATH="$HOME/.dotnet/tools:$PATH"
maui devflow update-skill
```
This updates the MauiDevFlow agent skill files at `.claude/skills/maui-ai-debugging/`. Include any changed skill files in the commit.

### Group 3: Other NuGet Packages (nuget.org)

Query the latest stable version for each package. Use this pattern:
```bash
curl -s 'https://api.nuget.org/v3-flatcontainer/PACKAGE_NAME_LOWERCASE/index.json' | jq -r '[.versions[] | select(test("^[0-9]+\\.[0-9]+\\.[0-9]+$")))] | last'
```

Note: For packages with a `$(MauiVersion)` variable (like `Microsoft.Maui.Controls` and `Microsoft.AspNetCore.Components.WebView.Maui`), do NOT update them — they are pinned to the MAUI workload version.

Update each package in ALL csproj files where it appears to the **same** latest stable version:

| Package | Locations |
|---------|-----------|
| `CommunityToolkit.Maui` | `PolyPilot/PolyPilot.csproj`, `PolyPilot.Gtk/PolyPilot.Gtk.csproj` |
| `Markdig` | `PolyPilot/PolyPilot.csproj`, `PolyPilot.Gtk/PolyPilot.Gtk.csproj`, `PolyPilot.Tests/PolyPilot.Tests.csproj` |
| `sqlite-net-pcl` | `PolyPilot/PolyPilot.csproj`, `PolyPilot.Gtk/PolyPilot.Gtk.csproj`, `PolyPilot.Tests/PolyPilot.Tests.csproj` |
| `SQLitePCLRaw.bundle_green` | `PolyPilot/PolyPilot.csproj`, `PolyPilot.Gtk/PolyPilot.Gtk.csproj`, `PolyPilot.Tests/PolyPilot.Tests.csproj` |
| `QRCoder` | `PolyPilot/PolyPilot.csproj` |
| `ZXing.Net.Maui.Controls` | `PolyPilot/PolyPilot.csproj` |
| `Spectre.Console` | `PolyPilot.Console/PolyPilot.csproj` |
| `coverlet.collector` | `PolyPilot.Tests/PolyPilot.Tests.csproj` |
| `Microsoft.NET.Test.Sdk` | `PolyPilot.Tests/PolyPilot.Tests.csproj` |
| `xunit` | `PolyPilot.Tests/PolyPilot.Tests.csproj` |
| `xunit.runner.visualstudio` | `PolyPilot.Tests/PolyPilot.Tests.csproj` |

Skip these — they track the .NET SDK version or use variables:
- `Microsoft.Maui.Controls` (uses `$(MauiVersion)`)
- `Microsoft.AspNetCore.Components.WebView.Maui` (uses `$(MauiVersion)`)
- `Microsoft.AspNetCore.Components.WebView` (tied to .NET SDK)
- `Microsoft.Extensions.Logging.Debug` (tied to .NET SDK)
- `Microsoft.Extensions.DependencyInjection` (tied to .NET SDK)
- `Microsoft.Extensions.DependencyInjection.Abstractions` (tied to .NET SDK)
- `Microsoft.Maui.Essentials.AI` (experimental CI build)
- `Platform.Maui.Linux.Gtk4*` (third-party GTK platform, update carefully)

## Important Constraints

- This workflow runs on **ubuntu** — the MAUI workload is NOT installed. Do NOT try to build the full solution (`PolyPilot.slnx`) or any MAUI project. Only the test project can be built.
- Do NOT attempt to fix breaking API changes. If a version bump causes build errors, **revert that specific package** to its current version and proceed with the other updates.
- Each package group is independent — update whichever ones build cleanly.
- Always ensure the same package uses the **same version** across all csproj files where it appears.
- The `dotnet test` command takes 5-10 minutes. Always pipe through `| tail -30` to avoid filling output buffers. Run it once and wait for completion.

## Steps

1. **Query latest versions** for all package groups.

2. **Compare with current versions** across all csproj files and `dotnet-tools.json`. If everything is already up to date, stop and do not create a PR.

3. **Update MauiDevFlow packages + CLI tool** — query the latest DevFlow version from the ADO feed. If it differs from the current version in the csproj files, update all four `Microsoft.Maui.DevFlow.*` PackageReference versions AND the `microsoft.maui.cli` version in `dotnet-tools.json`. Then install the maui CLI and run `maui devflow update-skill`. Include any changed files under `.claude/skills/maui-ai-debugging/` in the commit.

4. **Update other NuGet packages** (Group 3) — update all packages to their latest stable versions, ensuring consistency across projects.

5. **Update GitHub.Copilot.SDK** — update ALL csproj files to the same latest version.

6. **Build and test in one step** — run tests to verify everything works:
   ```bash
   dotnet test PolyPilot.Tests/PolyPilot.Tests.csproj --configuration Debug --nologo 2>&1 | tail -30
   ```
   This takes 5-10 minutes. If the build fails with SDK API/type errors (breaking changes), **revert only the SDK** back to what it was before in ALL csproj files, then re-run. If tests fail due to a different package, revert only that package and re-run.

7. **Create a PR** — if ANY packages were updated, commit all changes and create a PR via the MCP gateway.

   First, commit and generate the patch:
   ```bash
   git checkout -b chore/update-nuget-deps
   git add -A
   git commit -m "chore: update NuGet dependencies"
   git format-patch origin/main --stdout > /tmp/gh-aw/aw-chore-update-nuget-dependencies.patch
   ```

   Then call the safe-outputs MCP gateway via curl. The copilot CLI MCP tools are blocked by policy — calling the gateway directly via HTTP is the only reliable method. The gateway API key is in the `$MCP_GATEWAY_API_KEY` environment variable:
   ```bash
   # Initialize MCP session
   INIT_RESP=$(curl -s -X POST "http://host.docker.internal:80/mcp/safeoutputs" \
     -H "Content-Type: application/json" \
     -H "Authorization: $MCP_GATEWAY_API_KEY" \
     -d '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2024-11-05","capabilities":{},"clientInfo":{"name":"dep-update","version":"1.0"}}}')
   SESSION_ID=$(echo "$INIT_RESP" | jq -r '.result.sessionId // empty')

   # Call create_pull_request (set PR_BODY to a JSON-escaped summary of changes)
   curl -s -X POST "http://host.docker.internal:80/mcp/safeoutputs" \
     -H "Content-Type: application/json" \
     -H "Authorization: $MCP_GATEWAY_API_KEY" \
     -H "Mcp-Session-Id: $SESSION_ID" \
     -d "{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/call\",\"params\":{\"name\":\"create_pull_request\",\"arguments\":{\"title\":\"chore: update NuGet dependencies\",\"body\":\"$PR_BODY\"}}}"
   ```
   
   Set `$PR_BODY` to a JSON-escaped string listing each package with old → new versions. If any packages were reverted, note them in the body.
