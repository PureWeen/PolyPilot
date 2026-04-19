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

## Steps

1. **Query latest versions** for all package groups.

2. **Compare with current versions** across all csproj files and `dotnet-tools.json`. If everything is already up to date, stop and do not create a PR.

3. **Update MauiDevFlow packages + CLI tool** — update all four `Microsoft.Maui.DevFlow.*` PackageReference versions and the `microsoft.maui.cli` version in `dotnet-tools.json`. These are Debug-only conditional references, so no build verification is needed. Then run `maui devflow update-skill` to update the AI skill files.

4. **Update other NuGet packages** (Group 3) — update all packages to their latest stable versions, ensuring consistency across projects.

5. **Update GitHub.Copilot.SDK** — update ALL csproj files to the same latest version. Then build to verify:
   ```bash
   dotnet build PolyPilot.Tests/PolyPilot.Tests.csproj -c Debug --nologo
   ```
   If the build fails with API/type errors (breaking changes), **revert the SDK version** back to what it was before in ALL csproj files. Do NOT try to fix breaking API changes. Log which version had breaking changes in the PR body.

6. **Run tests** — build and run all tests to verify nothing is broken:
   ```bash
   dotnet test PolyPilot.Tests/PolyPilot.Tests.csproj --configuration Debug --nologo
   ```
   If tests fail due to a specific package update, revert only that package and re-run tests.

7. **Create a PR** — if ANY packages were updated, create a PR with title `chore: update NuGet dependencies` and a body that lists each package updated with old → new versions. If any packages were reverted, note them in the body with the error summary.
