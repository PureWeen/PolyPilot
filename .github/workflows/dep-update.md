---
on:
  workflow_dispatch:
  schedule: weekly on monday

permissions:
  contents: read
  pull-requests: read

engine: copilot

network: defaults

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

## Steps

1. **Query latest versions** for all three package groups using the curl commands above.

2. **Compare with current versions** in the csproj files and `dotnet-tools.json`. If everything is already up to date, stop — do not create a PR.

3. **Update csproj files** — for each `PackageReference` that needs updating, change the `Version` attribute to the latest version. Be careful to only change the version number, not the surrounding XML structure. Ensure ALL projects referencing the same package get the same version.

4. **Update dotnet-tools.json** — update the `microsoft.maui.cli` version.

5. **Build to verify** — run `dotnet build PolyPilot.slnx -c Debug --nologo` to confirm everything compiles. If it fails, investigate and fix or revert.

6. **Run tests** — run `dotnet test PolyPilot.Tests --configuration Debug --nologo` to confirm tests pass.

7. **Create a PR** with title `chore: update NuGet dependencies` and a body that lists each package updated with old → new versions.
