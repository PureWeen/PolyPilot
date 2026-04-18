---
on:
  workflow_dispatch:
  schedule: weekly on monday

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

---

# Update NuGet Dependencies

Update all NuGet package references and the MauiDevFlow CLI tool to their latest available versions, then open a PR with the changes.

## Packages to Update

### GitHub Copilot SDK (nuget.org)

Package: `GitHub.Copilot.SDK`

Query the latest version from nuget.org:
```bash
curl -s 'https://api.nuget.org/v3-flatcontainer/github.copilot.sdk/index.json' | jq -r '.versions[-1]'
```

This package appears in multiple csproj files — update ALL of them to the same version:
- `PolyPilot/PolyPilot.csproj`
- `PolyPilot.Console/PolyPilot.csproj`
- `PolyPilot.Gtk/PolyPilot.csproj`
- `PolyPilot.Tests/PolyPilot.Tests.csproj`
- `PolyPilot.Provider.Abstractions/PolyPilot.Provider.Abstractions.csproj`

### MauiDevFlow Packages (Azure DevOps dotnet10 feed)

These are prerelease packages. Query the latest version from the ADO feed:
```bash
curl -s 'https://pkgs.dev.azure.com/dnceng/public/_packaging/dotnet10/nuget/v3/flat2/microsoft.maui.devflow.agent/index.json' | jq -r '.versions[-1]'
```

All four MauiDevFlow packages must use the same version:
- `Microsoft.Maui.DevFlow.Agent` — in `PolyPilot/PolyPilot.csproj`
- `Microsoft.Maui.DevFlow.Blazor` — in `PolyPilot/PolyPilot.csproj`
- `Microsoft.Maui.DevFlow.Agent.Gtk` — in `PolyPilot.Gtk/PolyPilot.csproj`
- `Microsoft.Maui.DevFlow.Blazor.Gtk` — in `PolyPilot.Gtk/PolyPilot.csproj`

### MauiDevFlow CLI Tool

The `microsoft.maui.cli` tool version in `dotnet-tools.json` should match the MauiDevFlow packages:
```bash
curl -s 'https://pkgs.dev.azure.com/dnceng/public/_packaging/dotnet10/nuget/v3/flat2/microsoft.maui.cli/index.json' | jq -r '.versions[-1]'
```

Update the version in `dotnet-tools.json`.

## Important Constraints

- This workflow runs on **ubuntu** — the MAUI workload is NOT installed. Do NOT try to build the full solution (`PolyPilot.slnx`) or any MAUI project. Only the test project can be built.
- Do NOT attempt to fix breaking API changes. If a version bump causes build errors, **revert that specific package** to its current version and proceed with the other updates.
- Each package group (SDK, DevFlow, CLI tool) is independent — update whichever ones build cleanly.

## Steps

1. **Query latest versions** for all three package groups using the curl commands above.

2. **Compare with current versions** in the csproj files and `dotnet-tools.json`. If everything is already up to date, stop and do not create a PR.

3. **Update MauiDevFlow packages first** — update all four `Microsoft.Maui.DevFlow.*` PackageReference versions and the `microsoft.maui.cli` version in `dotnet-tools.json`. These are Debug-only conditional references and do not affect the test build, so no build verification is needed for them.

4. **Update GitHub.Copilot.SDK** — update ALL csproj files to the same latest version. Then build the test project to verify:
   ```bash
   dotnet build PolyPilot.Tests/PolyPilot.Tests.csproj -c Debug --nologo
   ```
   If the build fails with API/type errors (breaking changes), **revert the SDK version** back to what it was before in ALL csproj files. Do NOT try to fix breaking API changes — that requires a separate, dedicated PR. Log which version had breaking changes in the PR body.

5. **Run tests** (only if SDK was updated successfully):
   ```bash
   dotnet test PolyPilot.Tests/PolyPilot.Tests.csproj --configuration Debug --nologo
   ```
   If tests fail, revert the SDK update.

6. **Create a PR** — if ANY packages were updated, create a PR with title `chore: update NuGet dependencies` and a body that lists each package updated with old → new versions. If the SDK was reverted, note it in the body with the error summary.
