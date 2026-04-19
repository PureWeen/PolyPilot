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
curl -s 'https://api.nuget.org/v3-flatcontainer/github.copilot.sdk/index.json' | jq -r '.versions[-1]'
```

Update ALL of these to the **same** version:
- `PolyPilot/PolyPilot.csproj`
- `PolyPilot.Console/PolyPilot.csproj`
- `PolyPilot.Gtk/PolyPilot.Gtk.csproj`
- `PolyPilot.Tests/PolyPilot.Tests.csproj`
- `PolyPilot.Provider.Abstractions/PolyPilot.Provider.Abstractions.csproj`

### Group 2: MauiDevFlow Packages + CLI Tool (Azure DevOps dotnet10 feed)

Query the latest versions:
```bash
curl -s 'https://pkgs.dev.azure.com/dnceng/public/_packaging/dotnet10/nuget/v3/flat2/microsoft.maui.devflow.agent/index.json' | jq -r '.versions[-1]'
curl -s 'https://pkgs.dev.azure.com/dnceng/public/_packaging/dotnet10/nuget/v3/flat2/microsoft.maui.cli/index.json' | jq -r '.versions[-1]'
```

All MauiDevFlow packages and the CLI tool must use the **same** version:
- `Microsoft.Maui.DevFlow.Agent` — in `PolyPilot/PolyPilot.csproj`
- `Microsoft.Maui.DevFlow.Blazor` — in `PolyPilot/PolyPilot.csproj`
- `Microsoft.Maui.DevFlow.Agent.Gtk` — in `PolyPilot.Gtk/PolyPilot.Gtk.csproj`
- `Microsoft.Maui.DevFlow.Blazor.Gtk` — in `PolyPilot.Gtk/PolyPilot.Gtk.csproj`
- `microsoft.maui.cli` — in `dotnet-tools.json`

After updating, also run:
```bash
maui devflow update-skill
```
This updates the MauiDevFlow skill at `.claude/skills/maui-ai-debugging/`. If `maui` CLI is not installed globally, install it first:
```bash
dotnet tool install -g Microsoft.Maui.Cli --prerelease
```

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

3. **Update MauiDevFlow packages + CLI tool** — update all four `Microsoft.Maui.DevFlow.*` PackageReference versions and the `microsoft.maui.cli` version in `dotnet-tools.json`. These are Debug-only conditional references, so no build verification is needed. Then run `maui devflow update-skill` to update the AI skill files.

4. **Update other NuGet packages** (Group 3) — update all packages to their latest stable versions, ensuring consistency across projects.

5. **Update GitHub.Copilot.SDK** — update ALL csproj files to the same latest version.

6. **Build and test in one step** — run tests to verify everything works:
   ```bash
   dotnet test PolyPilot.Tests/PolyPilot.Tests.csproj --configuration Debug --nologo 2>&1 | tail -30
   ```
   This takes 5-10 minutes. If the build fails with SDK API/type errors (breaking changes), **revert only the SDK** back to what it was before in ALL csproj files, then re-run. If tests fail due to a different package, revert only that package and re-run.

7. **Create a PR** — if ANY packages were updated, commit all changes and create the PR output files. The `create_pull_request` MCP tool is NOT available as a copilot CLI tool — you must write the output files directly for the safe-outputs job:

   ```bash
   # Commit changes
   git checkout -b chore/update-nuget-deps
   git add -A
   git commit -m "chore: update NuGet dependencies"
   
   # Write the patch file (safe-outputs looks for /tmp/gh-aw/aw-*.patch)
   git format-patch origin/main --stdout > /tmp/gh-aw/aw-chore-update-nuget-dependencies.patch
   
   # Write the safe-outputs JSONL entry
   echo '{"type":"create_pull_request","title":"chore: update NuGet dependencies","body":"<PR_BODY_HERE>"}' >> "$GH_AW_SAFE_OUTPUTS"
   ```
   
   Replace `<PR_BODY_HERE>` with a single-line JSON-escaped body listing each package with old → new versions. If any packages were reverted, note them with the error summary.
