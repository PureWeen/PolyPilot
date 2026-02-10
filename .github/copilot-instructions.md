# AutoPilot.App — Copilot Instructions

## Build & Deploy Commands

### Mac Catalyst (primary dev target)
```bash
./relaunch.sh              # Build + seamless hot-relaunch (ALWAYS use this after code changes)
dotnet build -f net10.0-maccatalyst   # Build only
```
`relaunch.sh` builds, copies to staging, launches the new instance, waits for it to be ready, then kills the old one. Safe to run from a Copilot session inside the app itself.

### Tests
```bash
cd ../AutoPilot.App.Tests && dotnet test              # Run all tests
cd ../AutoPilot.App.Tests && dotnet test --filter "FullyQualifiedName~ChatMessageTests"  # Run one test class
cd ../AutoPilot.App.Tests && dotnet test --filter "FullyQualifiedName~ChatMessageTests.UserMessage_SetsRoleAndType"  # Single test
```
The test project lives at `../AutoPilot.App.Tests/` (sibling directory). It includes source files from the main project via `<Compile Include>` links because the MAUI project can't be directly referenced from a plain `net10.0` test project. When adding new model or utility classes, add a corresponding `<Compile Include>` entry to the test csproj if the file has no MAUI dependencies.

**Always run tests after modifying models, bridge messages, or serialization logic.** When adding new features or changing existing behavior, update or add tests to match. The tests serve as a living specification of the app's data contracts and parsing logic.

### Android
```bash
dotnet build -f net10.0-android                  # Build only
dotnet build -f net10.0-android -t:Install       # Build + deploy to connected device (use this, not bare `adb install`)
adb shell am start -n com.companyname.autopilot.app/crc645dd8ecec3b5d9ba6.MainActivity   # Launch
```
Fast Deployment requires `dotnet build -t:Install` — it pushes assemblies to `.__override__` on device.

### iOS (physical device)
```bash
dotnet build -f net10.0-ios -r ios-arm64         # Build only
xcrun devicectl device install app --device <UDID> bin/Debug/net10.0-ios/ios-arm64/AutoPilot.App.app/
xcrun devicectl device process launch --device <UDID> com.companyname.autopilot.app
```
Do NOT use `dotnet build -t:Run` for physical iOS — it hangs waiting for the app to exit.

### iOS Simulator
```bash
dotnet build -f net10.0-ios -t:Run -p:_DeviceName=:v2:udid=<UDID>
```

### MauiDevFlow (UI inspection & debugging)
The app integrates `Redth.MauiDevFlow.Agent` + `Redth.MauiDevFlow.Blazor` for remote UI inspection. See `.claude/skills/maui-ai-debugging/SKILL.md` for the full command reference.
```bash
maui-devflow MAUI status           # Agent connection
maui-devflow cdp status            # CDP/Blazor WebView
maui-devflow MAUI tree             # Visual tree
maui-devflow cdp snapshot          # DOM snapshot (best for AI)
maui-devflow MAUI logs             # Application ILogger output
```
For Android, always run `adb reverse tcp:9223 tcp:9223 && adb reverse tcp:9222 tcp:9222` after deploy.

## Architecture

This is a .NET MAUI Blazor Hybrid app targeting Mac Catalyst, Android, and iOS. It manages multiple GitHub Copilot CLI sessions through a native GUI.

### Three-Layer Stack
1. **Blazor UI** (`Components/`) — Razor components rendered in a BlazorWebView. All styling is CSS in `wwwroot/app.css` and scoped `.razor.css` files.
2. **Service Layer** (`Services/`) — `CopilotService` (singleton) manages sessions via `ConcurrentDictionary<string, SessionState>`. Events from the SDK arrive on background threads and are marshaled to the UI thread via `SynchronizationContext.Post`.
3. **SDK** — `GitHub.Copilot.SDK` (`CopilotClient`/`CopilotSession`) communicates with the Copilot CLI process via ACP (Agent Control Protocol) over stdio or TCP.

### Connection Modes
- **Embedded** (default on desktop): SDK spawns copilot via stdio, dies with app.
- **Persistent**: App spawns a detached `copilot --headless` server tracked via PID file; survives restarts.
- **Remote**: Connects to a remote server URL (e.g., DevTunnel). Only mode available on mobile.

### WebSocket Bridge (Remote Viewer Protocol)
`WsBridgeServer` runs on the desktop app and exposes session state over WebSocket. `WsBridgeClient` runs on mobile apps to receive live updates and send commands. The protocol is defined in `Models/BridgeMessages.cs` with typed payloads and message type constants in `BridgeMessageTypes`.

`DevTunnelService` manages a `devtunnel host` process to expose the bridge over the internet, with QR code scanning for easy mobile setup (`QrScannerPage.xaml`).

### Platform Differences
`Models/PlatformHelper.cs` exposes `IsDesktop`/`IsMobile` and controls which `ConnectionMode`s are available. Mobile can only use Remote mode. Desktop defaults to Embedded.

## Critical Conventions

### Git Workflow
- **NEVER force push** (`git push --force` / `git push -f`). Always add new commits on top of existing ones.
- When contributing to an existing PR, add commits — do not rebase or squash interactively.
- Use `git add -f` when adding files matched by `.gitignore` patterns (e.g., `*.app/` catches `AutoPilot.App/`).

### No `static readonly` fields that call platform APIs
`static readonly` fields are evaluated during type initialization — before MAUI's platform layer is ready on Android/iOS. This causes `TypeInitializationException` crashes.

**Always use lazy properties instead:**
```csharp
// ❌ WRONG — crashes on Android/iOS
private static readonly string MyPath = Path.Combine(FileSystem.AppDataDirectory, "file.json");

// ✅ CORRECT — deferred until first access
private static string? _myPath;
private static string MyPath => _myPath ??= Path.Combine(FileSystem.AppDataDirectory, "file.json");
```

This applies to `FileSystem.AppDataDirectory`, `Environment.GetFolderPath()`, and any Android/iOS-specific API. See `CopilotService.cs` (lines 19-59) and `ConnectionSettings.cs` (lines 29-53) for examples.

### File paths on iOS/Android vs desktop
- **Desktop**: Use `Environment.SpecialFolder.UserProfile` → `~/.copilot/`
- **iOS/Android**: Use `FileSystem.AppDataDirectory` (persistent across restarts). `Environment.SpecialFolder.LocalApplicationData` on iOS resolves to a cache directory that can be purged.
- Always wrap in try/catch with `Path.GetTempPath()` fallback.

### Linker / Trimmer
`<TrimmerRootAssembly Include="GitHub.Copilot.SDK" />` in the csproj prevents the linker from stripping SDK event types needed for runtime pattern matching. Do NOT remove this.

### Mac Catalyst Sandbox
Disabled in `Platforms/MacCatalyst/Entitlements.plist` — required for spawning copilot CLI processes and binding network ports.

### Edge-to-edge on Android (.NET 10)
.NET 10 MAUI defaults `ContentPage.SafeAreaEdges` to `None` (edge-to-edge). For this Blazor app, safe area insets are handled entirely in CSS/JS — do NOT set `SafeAreaEdges="Container"` on MainPage.xaml or add `padding-bottom` on body, as this causes double-padding.

### SDK Event Flow
When a prompt is sent, the SDK emits events processed by `HandleSessionEvent` in order:
1. `AssistantTurnStartEvent` → "Thinking..." indicator
2. `AssistantMessageDeltaEvent` → streaming content chunks
3. `AssistantMessageEvent` → full message (may include tool requests)
4. `ToolExecutionStartEvent` / `ToolExecutionCompleteEvent` → tool activity
5. `AssistantIntentEvent` → intent/plan updates
6. `SessionIdleEvent` → turn complete, response finalized

### Blazor Input Performance
Avoid `@bind:event="oninput"` — causes round-trip lag per keystroke. Use plain HTML inputs with JS event listeners and read values via `JS.InvokeAsync<string>("eval", "document.getElementById('id')?.value")` on submit.

### Session Persistence
- Active sessions: `~/.copilot/autopilot-active-sessions.json`
- Session state: `~/.copilot/session-state/<guid>/events.jsonl` (SDK-managed)
- UI state: `~/.copilot/autopilot-ui-state.json`
- Settings: `~/.copilot/autopilot-settings.json`
- Crash log: `~/.copilot/autopilot-crash.log`
