# PolyPilot — Copilot Instructions

## Build & Deploy Commands

### Mac Catalyst (primary dev target)
```bash
./relaunch.sh              # Build + seamless hot-relaunch (ALWAYS use this after code changes)
dotnet build -f net10.0-maccatalyst   # Build only
```
`relaunch.sh` builds, copies to staging, kills the old instance (freeing ports like MauiDevFlow 9223), then launches the new one. Safe to run from a Copilot session inside the app itself.

### Tests
```bash
cd ../PolyPilot.Tests && dotnet test              # Run all tests
cd ../PolyPilot.Tests && dotnet test --filter "FullyQualifiedName~ChatMessageTests"  # Run one test class
cd ../PolyPilot.Tests && dotnet test --filter "FullyQualifiedName~ChatMessageTests.UserMessage_SetsRoleAndType"  # Single test
```
The test project lives at `../PolyPilot.Tests/` (sibling directory). It includes source files from the main project via `<Compile Include>` links because the MAUI project can't be directly referenced from a plain `net10.0` test project. When adding new model or utility classes, add a corresponding `<Compile Include>` entry to the test csproj if the file has no MAUI dependencies.

**Always run tests after modifying models, bridge messages, or serialization logic.** When adding new features or changing existing behavior, update or add tests to match. The tests serve as a living specification of the app's data contracts and parsing logic.

### Android
```bash
dotnet build -f net10.0-android                  # Build only
dotnet build -f net10.0-android -t:Install       # Build + deploy to connected device (use this, not bare `adb install`)
adb shell am start -n com.microsoft.PolyPilot/crc64ef8e1bf56c865459.MainActivity   # Launch
```
Fast Deployment requires `dotnet build -t:Install` — it pushes assemblies to `.__override__` on device.

**Package name**: `com.microsoft.PolyPilot` (not `com.companyname.PolyPilot`)  
**Launch activity**: `crc64ef8e1bf56c865459.MainActivity`

### iOS (physical device)
```bash
dotnet build -f net10.0-ios -r ios-arm64         # Build only
xcrun devicectl device install app --device <UDID> bin/Debug/net10.0-ios/ios-arm64/PolyPilot.app/
xcrun devicectl device process launch --device <UDID> com.companyname.PolyPilot
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
For Android, always run `adb reverse tcp:9223 tcp:9223` after deploy.

## Architecture

This is a .NET MAUI Blazor Hybrid app targeting Mac Catalyst, Android, and iOS. It manages multiple GitHub Copilot CLI sessions through a native GUI.

### Three-Layer Stack
1. **Blazor UI** (`Components/`) — Razor components rendered in a BlazorWebView. All styling is CSS in `wwwroot/app.css` and scoped `.razor.css` files.
2. **Service Layer** (`Services/`) — `CopilotService` (singleton) manages sessions via `ConcurrentDictionary<string, SessionState>`. Events from the SDK arrive on background threads and are marshaled to the UI thread via `SynchronizationContext.Post`.
3. **SDK** — `GitHub.Copilot.SDK` (`CopilotClient`/`CopilotSession`) communicates with the Copilot CLI process via ACP (Agent Control Protocol) over stdio or TCP.

### Connection Modes
- **Embedded** (fallback): SDK spawns copilot via stdio, dies with app.
- **Persistent** (default on desktop): App spawns a detached `copilot --headless` server tracked via PID file; survives restarts.
- **Remote**: Connects to a remote server URL (e.g., DevTunnel). Only mode available on mobile.
- **Demo**: Local mock responses for testing without a network connection.

Mode and CLI source selections persist immediately to `~/.polypilot/settings.json` when the user clicks the corresponding card — no "Save & Reconnect" needed for the choice itself. "Save & Reconnect" is only needed to actually reconnect with the new settings.

### Mode Switching & Session Persistence
When switching between Embedded and Persistent modes (via Settings → Save & Reconnect), `ReconnectAsync` tears down the existing client and restores sessions from disk. Key safety mechanisms:

1. **Merge-based `SaveActiveSessionsToDisk()`** — reads the existing `active-sessions.json` and preserves entries whose session directory still exists on disk, even if not currently in memory. This prevents partial restores from clobbering the full list. The merge logic is in `CopilotService.Persistence.cs` → `MergeSessionEntries()` (static, testable).

2. **`_closedSessionIds`** — tracks sessions explicitly closed by the user so the merge doesn't re-add them. Cleared on `ReconnectAsync`.

3. **`IsRestoring` flag** — set during `RestorePreviousSessionsAsync`. Guards per-session `SaveActiveSessionsToDisk()` and `ReconcileOrganization()` calls to avoid unnecessary disk I/O and race conditions during bulk restore.

4. **Persistent fallback notice** — if `InitializeAsync` can't start the persistent server, it falls back to Embedded and sets `FallbackNotice` with a visible warning banner on the Dashboard.

### WebSocket Bridge (Remote Viewer Protocol)
`WsBridgeServer` runs on the desktop app and exposes session state over WebSocket. `WsBridgeClient` runs on mobile apps to receive live updates and send commands. The protocol is defined in `Models/BridgeMessages.cs` with typed payloads and message type constants in `BridgeMessageTypes`.

`DevTunnelService` manages a `devtunnel host` process to expose the bridge over the internet, with QR code scanning for easy mobile setup (`QrScannerPage.xaml`).

### Platform Differences
`Models/PlatformHelper.cs` exposes `IsDesktop`/`IsMobile` and controls which `ConnectionMode`s are available. Mobile can only use Remote mode. Desktop defaults to Persistent.

**Mobile-only behavior:**
- Desktop menu items (Fix with Copilot, Copilot Console, Terminal, VS Code) are hidden via `PlatformHelper.IsDesktop` guards in `SessionListItem.razor`.
- Report Bug opens the browser with a pre-filled GitHub issue URL via `Launcher.Default.OpenAsync` instead of the inline sidebar form.
- Processing status indicator shows elapsed time and tool round count, synced via bridge.

## Critical Conventions

### Git Workflow
- **NEVER use `git push --force`** — always use `git push --force-with-lease` instead when a force push is needed (e.g., after a rebase). This prevents overwriting remote changes made by others.
- **NEVER commit screenshots, images, or binary files** — use `git diff --stat` or `git status` before committing to verify no `.png`, `.jpg`, `.bmp`, or other image files are staged. Screenshots from PolyPilot (e.g., `screenshot_*.png`) are generated locally and must NEVER be committed. The `.gitignore` blocks common patterns, but always double-check.
- **NEVER use `git add -A` or `git add .` blindly** — always review what's being staged first with `git status`. Prefer `git add <specific-files>` when possible to avoid accidentally committing generated files.
- **When creating a new branch for a PR**, always base it on `upstream/main` (or `origin/main`). Do NOT branch from whatever HEAD happens to be — the repo may be on a feature branch. Use `git checkout -b <branch> upstream/main`. After creating the branch, verify with `git log --oneline upstream/main..HEAD` that only your commits appear.
- When contributing to an existing PR, prefer adding commits on top. Rebase only when explicitly asked.
- Use `git add -f` when adding files matched by `.gitignore` patterns (e.g., `*.app/` catches `PolyPilot/`).

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
1. `SessionUsageInfoEvent` → server acknowledged, sets `ProcessingPhase=1`
2. `AssistantTurnStartEvent` → model generating, sets `ProcessingPhase=2`
3. `AssistantMessageDeltaEvent` → streaming content chunks
4. `AssistantMessageEvent` → full message (may include tool requests)
5. `ToolExecutionStartEvent` → tool activity starts, sets `ProcessingPhase=3`, increments `ToolCallCount` on complete
6. `ToolExecutionCompleteEvent` → tool done, increments `ToolCallCount`
7. `AssistantIntentEvent` → intent/plan updates
8. `AssistantTurnEndEvent` → end of a sub-turn, tool loop continues
9. `SessionIdleEvent` → turn complete, response finalized

### Processing Status Indicator
`AgentSessionInfo` tracks three fields for the processing status UI:
- `ProcessingStartedAt` (DateTime?) — set to `DateTime.UtcNow` in `SendPromptAsync`
- `ToolCallCount` (int) — incremented on each `ToolExecutionCompleteEvent`
- `ProcessingPhase` (int) — 0=Sending, 1=ServerConnected, 2=Thinking, 3=Working

All three are reset in `SendPromptAsync` (new turn) and cleared in `CompleteResponse` (turn done) and `AbortSessionAsync` (user stop). They're synced to mobile via `SessionSummary` in the bridge protocol.

The UI shows: "Sending…" → "Server connected…" → "Thinking…" → "Working · Xm Xs · N tool calls…".

### Abort Behavior
`AbortSessionAsync` must clear ALL processing state:
- `IsProcessing = false`, `IsResumed = false`
- `ProcessingStartedAt = null`, `ToolCallCount = 0`, `ProcessingPhase = 0`
- `MessageQueue.Clear()` — prevents queued messages from auto-sending after abort
- `_queuedImagePaths.TryRemove()` — clears associated image attachments
- `CancelProcessingWatchdog()` and `ResponseCompletion.TrySetCanceled()`

In remote mode, the mobile client optimistically clears all fields and delegates to the bridge server.

### Processing Watchdog
The processing watchdog (`RunProcessingWatchdogAsync` in `CopilotService.Events.cs`) detects stuck sessions by checking how long since the last SDK event. It checks every 15 seconds and has two timeout tiers:
- **120 seconds** (inactivity timeout) — for sessions with no tool activity
- **600 seconds** (tool execution timeout) — used when ANY of these are true:
  - A tool call is actively running (`ActiveToolCallCount > 0`)
  - The session was resumed mid-turn after app restart (`IsResumed`)
  - Tools have been used this turn (`HasUsedToolsThisTurn`) — even between tool rounds when the model is thinking

The 10-second resume timeout was removed — the watchdog handles all stuck-session detection.

When the watchdog fires, it marshals state mutations to the UI thread via `InvokeOnUI()` and adds a system warning message. All code paths that set `IsProcessing = false` must go through the UI thread.

### Diagnostic Log Tags
The event diagnostics log (`~/.polypilot/event-diagnostics.log`) uses these tags:
- `[SEND]` — prompt sent, IsProcessing set to true
- `[EVT]` — SDK event received (only SessionIdleEvent, AssistantTurnEndEvent, SessionErrorEvent)
- `[IDLE]` — SessionIdleEvent dispatched to CompleteResponse
- `[COMPLETE]` — CompleteResponse executed or skipped
- `[RECONNECT]` — session replaced after disconnect
- `[ERROR]` — SessionErrorEvent or SendAsync/reconnect failure cleared IsProcessing
- `[ABORT]` — user-initiated abort cleared IsProcessing
- `[BRIDGE-COMPLETE]` — bridge OnTurnEnd cleared IsProcessing
- `[INTERRUPTED]` — app restart detected interrupted turn (watchdog timeout after resume)

Every code path that sets `IsProcessing = false` MUST have a diagnostic log entry. This is critical for debugging stuck-session issues.

### Thread Safety: IsProcessing Mutations
All mutations to `state.Info.IsProcessing` must be marshaled to the UI thread. SDK events arrive on background threads. Use `InvokeOnUI()` (not bare `Invoke()`) to combine state mutation + notification in a single callback. Key patterns:
- **CompleteResponse**: Already runs on UI thread (dispatched via `Invoke()`)
- **Watchdog callback**: Uses `InvokeOnUI()` with generation guard
- **SessionErrorEvent**: Uses `InvokeOnUI()` to combine OnError + IsProcessing + OnStateChanged
- **Resume fallback**: Removed (watchdog handles it)
- **SendAsync error paths**: Run on UI thread inline (in SendPromptAsync's catch blocks)

### Model Selection
The model is set at **session creation time** via `SessionConfig.Model`. The SDK does **not** support changing models per-message or mid-session — `MessageOptions` has no `Model` property. 

When a user changes the model via the UI dropdown:
- `session.Model` is updated locally (affects UI display only)
- The SDK continues using the original model from session creation
- To truly switch models, the session must be destroyed and recreated

### SDK Data Types
- `AssistantUsageData` properties (`InputTokens`, `OutputTokens`, etc.) are `Double?` not `int?`
- Use `Convert.ToInt32(value)` for conversion, not `value as int?`
- `QuotaSnapshots` is `Dictionary<string, object>` with `JsonElement` values
- Premium quota fields: `isUnlimitedEntitlement`, `entitlementRequests`, `usedRequests`, `remainingPercentage`, `resetDate`

### Blazor Input Performance
Avoid `@bind:event="oninput"` — causes round-trip lag per keystroke. Use plain HTML inputs with JS event listeners and read values via `JS.InvokeAsync<string>("eval", "document.getElementById('id')?.value")` on submit.

### Session Persistence
- Active sessions: `~/.polypilot/active-sessions.json` (includes `LastPrompt` — last user message if session was processing during save)
- Session state: `~/.copilot/session-state/<guid>/events.jsonl` (SDK-managed, stays in ~/.copilot)
- UI state: `~/.polypilot/ui-state.json`
- Settings: `~/.polypilot/settings.json`
- Crash log: `~/.polypilot/crash.log`
- Organization: `~/.polypilot/organization.json`
- Server PID: `~/.polypilot/server.pid`
- Repos/worktrees: `~/.polypilot/repos.json`, `~/.polypilot/repos/`, `~/.polypilot/worktrees/`

## Remote Mode (WsBridge Protocol)

### Architecture
Mobile apps connect to the desktop server via WebSocket. `WsBridgeServer` runs on desktop (port 4322), `WsBridgeClient` runs on mobile. The protocol is JSON-based state-sync defined in `Models/BridgeMessages.cs`.

### Common Pitfalls for Future Agents

1. **Remote mode operations must be handled separately.** Any `CopilotService` method that touches `state.Session` (the SDK `CopilotSession`) will crash in remote mode because `state.Session` is `null!`. Always check `IsRemoteMode` first and delegate to `_bridgeClient`.

2. **Optimistic adds need full state.** When adding a session optimistically in remote mode (before server confirms), you must:
   - Add to `_sessions` dictionary
   - Add to `_pendingRemoteSessions` (prevents `SyncRemoteSessions` from removing it)
   - Add `SessionMeta` to `Organization.Sessions` (or `GetOrganizedSessions()` won't render it)
   - Do all this BEFORE awaiting the bridge send (race condition with server response)

3. **Thread safety.** `SyncRemoteSessions` runs on the bridge client's background thread. `_sessions` is `ConcurrentDictionary` (safe). `_pendingRemoteSessions` is `ConcurrentDictionary` (safe). But `Organization.Sessions` is a plain `List<SessionMeta>` — access from the UI thread only.

4. **Adding new bridge commands.** When adding client→server commands:
   - Add a constant to `BridgeMessageTypes` in `BridgeMessages.cs`
   - Add a payload class if needed (or reuse `SessionNamePayload`)
   - Add the send method to `WsBridgeClient`
   - Add a `case` handler in `WsBridgeServer.HandleClientMessage()`
   - Add the remote-mode delegation in `CopilotService`
   - Add tests in `RemoteModeTests.cs`

5. **DevTunnel strips auth headers.** The `X-Tunnel-Authorization` header is consumed by DevTunnel infrastructure. `WsBridgeServer.ValidateClientToken` trusts loopback connections since DevTunnel proxies to localhost after its own auth.

### Test Coverage
Test files in `PolyPilot.Tests/`:
- `BridgeMessageTests.cs` — Bridge protocol serialization, type constants
- `RemoteModeTests.cs` — Remote mode payloads, organization state, chat serialization
- `ChatMessageTests.cs` — Chat message factory methods, state transitions
- `AgentSessionInfoTests.cs` — Session info properties, history, queue, processing status fields
- `SessionOrganizationTests.cs` — Groups, sorting, metadata
- `ConnectionSettingsTests.cs` — Settings persistence
- `CopilotServiceInitializationTests.cs` — Initialization error handling, mode switching, fallback notices, CLI source persistence
- `SessionPersistenceTests.cs` — Merge-based `SaveActiveSessionsToDisk()`, closed session exclusion, directory checks
- `ScenarioReferenceTests.cs` — Validates UI scenario JSON + cross-references with unit tests
- `EventsJsonlParsingTests.cs` — SDK event log parsing
- `PlatformHelperTests.cs` — Platform detection
- `ToolResultFormattingTests.cs` — Tool output formatting
- `UiStatePersistenceTests.cs` — UI state save/load
- `ProcessingWatchdogTests.cs` — Watchdog constants, timeout selection, HasUsedToolsThisTurn, IsResumed, abort clears queue and processing status
- `CliPathResolutionTests.cs` — CLI path resolution
- `InitializationModeTests.cs` — Mode initialization
- `PersistentModeTests.cs` — Persistent mode behavior
- `ReflectionCycleTests.cs` — Reflection cycle logic
- `SessionDisposalResilienceTests.cs` — Session disposal
- `RenderThrottleTests.cs` — Render throttling
- `DevTunnelServiceTests.cs` — DevTunnel service
- `WsBridgeServerAuthTests.cs` — Bridge auth
- `ModelSelectionTests.cs` — Model selection

UI scenario definitions live in `PolyPilot.Tests/Scenarios/mode-switch-scenarios.json` — executable via MauiDevFlow CDP commands against a running app.

Tests include source files via `<Compile Include>` links in the csproj. When adding new model classes, add a corresponding link entry.

### Test Safety
- Tests must **NEVER** call `ConnectionSettings.Save()` or `ConnectionSettings.Load()` — these read/write `~/.polypilot/settings.json` which is shared with the running app.
- All tests use `ReconnectAsync(settings)` with an in-memory settings object.
- Never use `ConnectionMode.Embedded` in tests — it spawns real copilot processes. Use `ConnectionMode.Persistent` with port 19999 for deterministic failures, or `ConnectionMode.Demo` for success paths.
- CopilotService dependencies are injected via interfaces: `IChatDatabase`, `IServerManager`, `IWsBridgeClient`, `IDemoService`. Test stubs live in `TestStubs.cs`.
