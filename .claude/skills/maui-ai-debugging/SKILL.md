---
name: maui-ai-debugging
description: >
  End-to-end workflow for building, deploying, inspecting, and debugging .NET MAUI and MAUI Blazor Hybrid apps
  as an AI agent. Use when: (1) Building or running a MAUI app on iOS simulator, Android emulator, or Mac Catalyst,
  (2) Deploying a MAUI app to a device/emulator/simulator, (3) Inspecting or interacting with a running MAUI app's
  UI (visual tree, element tapping, filling text, screenshots, property queries), (4) Debugging Blazor WebView
  content inside a MAUI app via CDP, (5) Managing iOS simulators or Android emulators (create, boot, list, install),
  (6) Setting up the MauiDevFlow agent and CLI in a MAUI project, (7) Completing a build-deploy-inspect-fix feedback
  loop for MAUI app development. Covers: maui-devflow CLI, androidsdk.tool (android), appledev.tools (apple),
  adb, xcrun simctl, and dotnet build/run for all MAUI target platforms.
---

# MAUI AI Debugging

Build, deploy, inspect, and debug .NET MAUI apps from the terminal. This skill enables a complete
feedback loop: **build → deploy → inspect → fix → rebuild**.

## Prerequisites

Install the CLI tool: `dotnet tool install --global Redth.MauiDevFlow.CLI`

For platform-specific tools: `dotnet tool install --global androidsdk.tool` (Android)
and `dotnet tool install --global appledev.tools` (iOS/Mac).

## Integrating MauiDevFlow into a MAUI App

For complete setup instructions including NuGet packages, MauiProgram.cs registration,
Blazor script tag, Mac Catalyst entitlements, and Android port forwarding, see
[references/setup.md](references/setup.md).

**Quick summary:**
1. Add NuGet packages (`Redth.MauiDevFlow.Agent`, and `Redth.MauiDevFlow.Blazor` for Blazor Hybrid)
2. Register in `MauiProgram.cs` inside `#if DEBUG`
3. For Blazor Hybrid: add `<script src="chobitsu.js"></script>` to `wwwroot/index.html`
4. For Mac Catalyst: ensure `network.server` entitlement
5. For Android: run `adb reverse` for port forwarding

## Core Workflow

### 1. Ensure a Device/Simulator/Emulator is Running

**iOS Simulator:**
```bash
xcrun simctl list devices booted                              # check booted sims
xcrun simctl boot <UDID>                                      # boot if needed
apple simulator list --booted                                 # alternative
apple simulator boot <UDID>                                   # alternative
```

**Android Emulator:**
```bash
android avd list                                              # list AVDs
android avd start --name <avd-name>                           # start emulator
adb devices                                                   # verify connected
```

**Mac Catalyst:** No device setup needed — runs as desktop app.

### 2. Build and Deploy

```bash
# iOS Simulator
dotnet build -f net10.0-ios -t:Run -p:_DeviceName=:v2:udid=<UDID>

# Android Emulator
dotnet build -f net10.0-android -t:Run

# Mac Catalyst
dotnet build -f net10.0-maccatalyst -t:Run
```

Adjust TFM version (net9.0, net10.0) to match project. Check the `.csproj` `<TargetFrameworks>`.
Build + Run can take 30-120+ seconds. Use `initial_wait: 120` or higher for async monitoring.
The `-t:Run` flag keeps the process alive (--wait-for-exit). Run in background or a separate shell.

For Android emulators, set up port forwarding after deploy:
```bash
adb reverse tcp:9223 tcp:9223    # Agent
adb reverse tcp:9222 tcp:9222    # CDP (Blazor)
```

### 3. Verify Connectivity

```bash
maui-devflow MAUI status          # Agent connection (native)
maui-devflow cdp status           # CDP connection (Blazor WebView)
```

### 4. Inspect and Interact

See **Command Reference** below for the full command set.

**Typical inspection flow:**
1. `maui-devflow MAUI tree` — see the full visual tree with element IDs, types, text, bounds
2. `maui-devflow MAUI query --automationId "MyButton"` — find specific elements
3. `maui-devflow MAUI element <id>` — get full details (type, bounds, visibility, children)
4. `maui-devflow MAUI property <id> Text` — read any property by name
5. `maui-devflow MAUI screenshot --output screen.png` — visual verification

**Debugging styling/layout with property inspection:**
Use `property` to verify runtime values without relying solely on screenshots:
```bash
maui-devflow MAUI property <id> BackgroundColor    # verify dark mode colors
maui-devflow MAUI property <id> TextColor          # check text visibility
maui-devflow MAUI property <id> IsVisible          # check element visibility
maui-devflow MAUI property <id> Width              # verify layout sizing
maui-devflow MAUI property <id> Opacity            # check transparency
```
Combine tree + property for systematic debugging: get element IDs from `tree`, then inspect
specific properties. This is more reliable than screenshots for verifying exact color values,
font sizes, and layout metrics.

**Typical interaction flow:**
1. `maui-devflow MAUI fill <entryId> "text"` — type into Entry/Editor fields
2. `maui-devflow MAUI tap <buttonId>` — tap buttons, checkboxes, list items
3. `maui-devflow MAUI clear <entryId>` — clear text fields
4. Take screenshot to verify result

**Blazor WebView (if applicable):**
1. `maui-devflow cdp snapshot` — DOM tree as accessible text (best for AI)
2. `maui-devflow cdp Input fill "css-selector" "text"` — fill inputs
3. `maui-devflow cdp Input dispatchClickEvent "css-selector"` — click elements
4. `maui-devflow cdp Runtime evaluate "js-expression"` — run JS

**Debugging Blazor styling via CDP:**
Use `Runtime evaluate` to inspect computed styles and verify CSS:
```bash
maui-devflow cdp Runtime evaluate "getComputedStyle(document.querySelector('.my-class')).backgroundColor"
maui-devflow cdp Runtime evaluate "window.matchMedia('(prefers-color-scheme: dark)').matches"
maui-devflow cdp Runtime evaluate "document.styleSheets.length"
```
This enables verifying Blazor dark mode, layout, and styling without relying solely on screenshots.

### 5. Reading Application Logs

MauiDevFlow automatically captures all `Microsoft.Extensions.Logging` (`ILogger`) output
to rotating log files on the device. This means any `ILogger<T>` calls in the app's code
(or in libraries) are available for remote retrieval — invaluable for debugging.

```bash
maui-devflow MAUI logs                   # fetch 100 most recent log entries
maui-devflow MAUI logs --limit 50        # fetch 50 entries
maui-devflow MAUI logs --skip 100        # skip newest 100, get next batch
```

Output is color-coded by level (red=Critical/Error, yellow=Warning, green=Info, gray=Debug/Trace).
Each entry includes timestamp, log level, category (logger name), and message.

**Debugging workflow with logs:**
1. Reproduce the issue (tap a button, navigate, etc.)
2. `maui-devflow MAUI logs --limit 20` — check recent log entries for errors or warnings
3. If needed, add temporary `ILogger` calls to the app code for more detail:
   ```csharp
   _logger.LogInformation("Button tapped, item count: {Count}", items.Count);
   _logger.LogWarning("Unexpected state: {State}", currentState);
   ```
4. Rebuild, redeploy, reproduce, and fetch logs again

**Log configuration** (in `AddMauiDevFlowAgent` options):
- `EnableFileLogging` (default: `true`) — toggle file logging
- `MaxLogFileSize` (default: 1 MB) — max size per log file before rotation
- `MaxLogFiles` (default: 5) — number of rotated files to keep

The agent also exposes logs via REST: `GET /api/logs?limit=N&skip=N` returns a JSON array.

### 6. Fix and Rebuild

After identifying issues, edit source, rebuild (`dotnet build`), and redeploy.
The full cycle: edit code → `dotnet build -t:Run ...` → `maui-devflow MAUI status` → inspect.

## Command Reference

### maui-devflow MAUI (Native Agent)

Global options: `--agent-host` (default localhost), `--agent-port` (default 9223), `--platform`.

| Command | Description |
|---------|-------------|
| `MAUI status` | Agent connection status, platform, app name |
| `MAUI tree [--depth N]` | Visual tree (IDs, types, text, bounds). Depth 0=unlimited |
| `MAUI query --type T --automationId A --text T` | Find elements (any/all filters) |
| `MAUI tap <elementId>` | Tap an element |
| `MAUI fill <elementId> <text>` | Fill text into Entry/Editor |
| `MAUI clear <elementId>` | Clear text from element |
| `MAUI screenshot [--output path.png]` | PNG screenshot |
| `MAUI property <elementId> <prop>` | Read property (Text, IsVisible, FontSize, etc.) |
| `MAUI element <elementId>` | Full element JSON (type, bounds, children, etc.) |
| `MAUI navigate <route>` | Shell navigation (e.g. `//native`, `//blazor`) |
| `MAUI logs [--limit N] [--skip N]` | Fetch application logs (newest first) |

Element IDs come from `MAUI tree` or `MAUI query`. AutomationId-based elements use their
AutomationId directly. Others use generated hex IDs. When multiple elements share the same
AutomationId, suffixes are appended: `TodoCheckBox`, `TodoCheckBox_1`, `TodoCheckBox_2`, etc.

### maui-devflow cdp (Blazor WebView CDP)

Global option: `--endpoint` (default `ws://localhost:9222/devtools/browser`).

| Command | Description |
|---------|-------------|
| `cdp status` | CDP connection status |
| `cdp snapshot` | Accessible DOM text (best for AI agents) |
| `cdp Browser getVersion` | Browser/WebView version info |
| `cdp Runtime evaluate <expr>` | Evaluate JavaScript |
| `cdp DOM getDocument` | Full DOM document |
| `cdp DOM querySelector <sel>` | Find first matching element |
| `cdp DOM querySelectorAll <sel>` | Find all matching elements |
| `cdp DOM getOuterHTML <sel>` | Get outer HTML of element |
| `cdp Page navigate <url>` | Navigate to URL |
| `cdp Page reload` | Reload page |
| `cdp Page captureScreenshot` | Screenshot as base64 |
| `cdp Input dispatchClickEvent <sel>` | Click element by CSS selector |
| `cdp Input insertText <text>` | Insert text at focused element |
| `cdp Input fill <selector> <text>` | Focus + fill text into element |

### Agent REST API (Direct HTTP)

The agent exposes JSON endpoints on port 9223 (configurable):

| Endpoint | Method | Body |
|----------|--------|------|
| `/api/status` | GET | — |
| `/api/tree?depth=N` | GET | — |
| `/api/element/{id}` | GET | — |
| `/api/query?type=&text=&automationId=` | GET | — |
| `/api/action/tap` | POST | `{"elementId":"..."}` |
| `/api/action/fill` | POST | `{"elementId":"...","text":"..."}` |
| `/api/action/clear` | POST | `{"elementId":"..."}` |
| `/api/action/focus` | POST | `{"elementId":"..."}` |
| `/api/screenshot` | GET | — (returns PNG) |
| `/api/property/{id}/{name}` | GET | — |
| `/api/logs?limit=N&skip=N` | GET | — (returns JSON array of log entries) |

## Platform Details

For detailed platform-specific setup, simulator/emulator management, and troubleshooting:

- **Setup & Installation**: See [references/setup.md](references/setup.md)
- **iOS / Mac Catalyst**: See [references/ios-and-mac.md](references/ios-and-mac.md)
- **Android**: See [references/android.md](references/android.md)

## Tips

- Use `AutomationId` on important MAUI controls for stable element references.
- The visual tree only reflects what's currently rendered. Off-screen items in CollectionView
  may not appear until scrolled into view.
- **Shell navigation**: Use `maui-devflow MAUI navigate "//route"` for Shell-based apps.
  Routes are defined in AppShell.xaml via `Route` property on ShellContent elements.
- For Blazor Hybrid, `cdp snapshot` is the most AI-friendly way to read page state.
- Build times: Mac Catalyst ~5-10s, iOS ~30-60s, Android ~30-90s. Set appropriate timeouts.
- After Android deploy, always run `adb reverse` for port forwarding.
- **Property inspection** is more reliable than screenshots for verifying exact runtime values
  (colors, sizes, visibility). Use `tree` → `property` workflow for systematic debugging.
- **Application logs** are captured automatically from `ILogger`. Use `MAUI logs` to fetch
  them remotely. Add temporary `ILogger` calls for extra debug output, then fetch logs after
  reproducing issues. This is often faster than attaching a debugger.
