# AutoPilot.App

A .NET MAUI Blazor hybrid desktop app that manages multiple GitHub Copilot CLI sessions. AutoPilot provides a native GUI for creating, orchestrating, and interacting with parallel Copilot agent sessions — acting as a multi-agent control plane.

## What Problem It Solves

Working with GitHub Copilot CLI is powerful, but limited to a single terminal session at a time. AutoPilot lets you:

- Run **multiple Copilot sessions in parallel**, each with its own model, working directory, and conversation history
- **Orchestrate agents** from a dashboard — broadcast the same prompt to all sessions at once
- **Resume sessions** across app restarts — sessions persist to disk and can be picked up later
- **Choose connection modes** — from simple embedded stdio to a persistent server that outlives the app

## Features

### Multi-Session Management
Create named sessions with different models and working directories. Sessions appear in a sidebar and can be switched between instantly. Each session maintains its own conversation history and processing state.

### Chat Interface
Full chat UI with streaming responses, real-time activity logging, Markdown rendering (code blocks, inline code, bold), and auto-scrolling. Shows typing indicators and tool execution status as Copilot works.

### Session Orchestrator Dashboard
A grid view of all active sessions showing their last messages, streaming output, and processing state. Includes per-card message input and a **Broadcast to All** feature to send the same prompt to every idle session simultaneously.

### Real-Time Activity Log
During processing, the UI displays a live activity feed showing Copilot's intent (`💭 Thinking...`), tool calls (`🔧 Running bash...`), and completion status (`✅ Tool completed`). This gives full visibility into multi-step agent workflows.

### Session Persistence & Resume
- **Active sessions** are saved to `~/.copilot/autopilot-active-sessions.json` and automatically restored on app relaunch
- **All Copilot sessions** persisted in `~/.copilot/session-state/` can be browsed and resumed from the sidebar's "Saved Sessions" panel
- Conversation history is reconstructed from the SDK's `events.jsonl` files on resume
- Sessions display their first user message as a title for easy identification

### UI State Persistence
The app remembers which page you were on (Chat, Dashboard, or Settings) and which session was active, restoring both on relaunch via `~/.copilot/autopilot-ui-state.json`.

### Auto-Reconnect
If a session disconnects during a prompt, the service automatically attempts to resume the session by its GUID and retry the message.

### Per-Session Working Directory
Each session can target a different directory on disk. A native macOS folder picker (via `UIDocumentPickerViewController`) is available for browsing.

### Model Selection
Sessions can be created with any of the supported models:
`claude-opus-4.6`, `claude-sonnet-4.5`, `claude-sonnet-4`, `claude-haiku-4.5`, `gpt-5.2`, `gpt-5.1`, `gpt-5`, `gpt-5-mini`, `gemini-3-pro-preview`

### System Instructions
Automatically loads project-level instructions from `.github/copilot-instructions.md` and appends them to every session's system message. When a session targets the AutoPilot project directory, it also injects build/relaunch instructions.

### Crash Logging
Unhandled exceptions and unobserved task failures are caught globally and written to `~/.copilot/autopilot-crash.log`.

## Connection Modes

AutoPilot supports three transport modes, configurable from the Settings page:

| Mode | Transport | Lifecycle | Best For |
|------|-----------|-----------|----------|
| **Embedded** (default) | stdio | Dies with app | Simple single-machine use |
| **TCP Server** | SDK-managed TCP | Dies with app | More stable long sessions |
| **Persistent Server** | Detached TCP server | Survives app restarts | Session continuity across relaunches |

### Embedded (stdio)
The SDK spawns a Copilot CLI process and communicates via stdin/stdout. Simplest setup — no port configuration needed. The process terminates when the app closes.

### TCP Server
The SDK spawns and manages a Copilot CLI process using TCP transport internally. More stable for long-running sessions, but the server still dies when the app exits.

### Persistent Server
The app spawns a **detached** Copilot CLI server process (`copilot --headless --port 4321`) that runs independently. The server's PID and port are tracked in `~/.copilot/autopilot-server.pid`. On relaunch, the app detects the existing server and reconnects. You can start/stop the server from the Settings page.

## Architecture

```
┌─────────────────────────────────────────────────────────┐
│                    AutoPilot.App                        │
│              (.NET MAUI Blazor Hybrid)                  │
│                                                         │
│  ┌──────────────┐  ┌────────────┐  ┌────────────────┐  │
│  │ SessionSidebar│  │  Home.razor│  │ Dashboard.razor│  │
│  │  (create/     │  │  (chat UI) │  │ (orchestrator) │  │
│  │   resume)     │  │            │  │                │  │
│  └──────┬───────┘  └─────┬──────┘  └───────┬────────┘  │
│         │                │                  │           │
│         └────────────────┼──────────────────┘           │
│                          │                              │
│                ┌─────────▼──────────┐                   │
│                │   CopilotService   │ (singleton)       │
│                │ ┌───────────────┐  │                   │
│                │ │ SessionState  │  │ ConcurrentDict    │
│                │ │  ├─ Session   │  │ of named sessions │
│                │ │  ├─ Info      │  │                   │
│                │ │  └─ Response  │  │                   │
│                │ └───────────────┘  │                   │
│                └─────────┬──────────┘                   │
│                          │                              │
│                ┌─────────▼──────────┐                   │
│                │   CopilotClient   │ (GitHub.Copilot.SDK)│
│                └─────────┬──────────┘                   │
│                          │                              │
│         ┌────────────────┼────────────────┐             │
│         │ stdio          │ TCP            │ TCP (remote)│
│         ▼                ▼                ▼             │
│   ┌──────────┐    ┌──────────┐    ┌──────────────┐     │
│   │ copilot  │    │ copilot  │    │  Persistent  │     │
│   │ (child)  │    │ (child)  │    │  Server      │     │
│   └──────────┘    └──────────┘    │  (detached)  │     │
│                                   └──────────────┘     │
│                                         ▲              │
│                          ┌──────────────┘              │
│                   ┌──────┴────────┐                    │
│                   │ ServerManager │ (PID file tracking) │
│                   └───────────────┘                    │
└─────────────────────────────────────────────────────────┘
```

### Key Components

- **`CopilotService`** — Singleton service wrapping the Copilot SDK. Manages a `ConcurrentDictionary` of named sessions, handles all SDK events (deltas, tool calls, intents, errors), marshals events to the UI thread via `SynchronizationContext`, and persists session/UI state to disk.
- **`ServerManager`** — Manages the persistent Copilot server lifecycle: start, stop, detect existing instances, PID file tracking, TCP health checks.
- **`CopilotClient`** / **`CopilotSession`** — From `GitHub.Copilot.SDK`. The client creates/resumes sessions; sessions send prompts and emit events via the ACP (Agent Control Protocol).

### SDK Event Flow

When a prompt is sent, the SDK emits events processed by `HandleSessionEvent`:

1. `AssistantTurnStartEvent` → "Thinking..." activity
2. `AssistantMessageDeltaEvent` → streaming content chunks to the UI
3. `AssistantMessageEvent` → full message with optional tool requests
4. `ToolExecutionStartEvent` / `ToolExecutionCompleteEvent` → tool activity indicators
5. `AssistantIntentEvent` → intent/plan updates
6. `SessionIdleEvent` → turn complete, response finalized, notifications fired

## Project Structure

```
AutoPilot.App/
├── AutoPilot.App.csproj        # Project config, SDK reference, trimmer settings
├── MauiProgram.cs              # App bootstrap, DI registration, crash logging
├── relaunch.sh                 # Build + seamless relaunch script
├── .github/
│   └── copilot-instructions.md # System instructions loaded into every session
├── Models/
│   ├── AgentSessionInfo.cs     # Session metadata (name, model, history, state)
│   ├── ChatMessage.cs          # Chat message record (role, content, timestamp)
│   └── ConnectionSettings.cs   # Connection mode enum + serializable settings
├── Services/
│   ├── CopilotService.cs       # Core service: session CRUD, events, persistence
│   └── ServerManager.cs        # Persistent server lifecycle + PID tracking
├── Components/
│   ├── Layout/
│   │   ├── MainLayout.razor    # App shell with sidebar + content area
│   │   ├── SessionSidebar.razor# Session list, create/resume, model picker
│   │   └── NavMenu.razor       # Top navigation bar
│   └── Pages/
│       ├── Home.razor          # Chat UI with streaming + activity log
│       ├── Dashboard.razor     # Multi-session orchestrator grid
│       └── Settings.razor      # Connection mode selector, server controls
├── Platforms/
│   └── MacCatalyst/
│       ├── Entitlements.plist  # Sandbox disabled, network access enabled
│       ├── FolderPickerService.cs # Native macOS folder picker
│       └── Program.cs          # Mac Catalyst entry point
└── wwwroot/
    └── app.css                 # Global styles
```

## Prerequisites

- **.NET 10 SDK** (Preview) — the project targets `net10.0-maccatalyst`
- **.NET MAUI workload** — install with `dotnet workload install maui`
- **GitHub Copilot CLI** — installed globally via npm (`npm install -g @github/copilot`)
- **macOS** — the app runs as a Mac Catalyst application (macOS 15.0+)
- **GitHub Copilot subscription** — required for the CLI to authenticate

## Building & Running

### First-time setup

```bash
# Install .NET MAUI workload
dotnet workload install maui

# Restore NuGet packages
cd /path/to/AutoPilot.App
dotnet restore
```

### Build and run

```bash
# Build for Mac Catalyst
dotnet build -f net10.0-maccatalyst

# Run the app
open bin/Debug/net10.0-maccatalyst/maccatalyst-arm64/AutoPilot.App.app
```

### Relaunch after code changes

The project includes a `relaunch.sh` script for seamless hot-relaunch. It builds, copies to a staging directory, launches a new instance, waits for it to start, then kills the old one:

```bash
./relaunch.sh
```

This is safe to run from a Copilot session inside the app — the new instance is fully running before the old one is terminated.

## Configuration

### Settings files (all in `~/.copilot/`)

| File | Purpose |
|------|---------|
| `autopilot-settings.json` | Connection mode, host, port, auto-start preference |
| `autopilot-active-sessions.json` | List of active sessions (session ID, display name, model) for restore on relaunch |
| `autopilot-ui-state.json` | Last active page and session name |
| `autopilot-server.pid` | PID and port of the persistent Copilot server |
| `autopilot-crash.log` | Unhandled exception log |
| `session-state/<guid>/events.jsonl` | Per-session event history (managed by Copilot SDK) |

### Example `autopilot-settings.json`

```json
{
  "Mode": 0,
  "Host": "localhost",
  "Port": 4321,
  "AutoStartServer": false
}
```

Mode values: `0` = Embedded, `1` = Server, `2` = Persistent.

## How It Works

### Session Lifecycle

1. **Create**: User enters a name, picks a model and optional working directory in the sidebar. `CopilotService.CreateSessionAsync` calls `CopilotClient.CreateSessionAsync` with a `SessionConfig` (model, working directory, system message). The SDK spawns/connects to Copilot and returns a `CopilotSession`.

2. **Chat**: User types a message → `SendPromptAsync` adds it to history, calls `session.SendAsync`, and awaits a `TaskCompletionSource` that completes when `SessionIdleEvent` fires. Streaming deltas are emitted to the UI in real time.

3. **Persist**: After every session create/close, the active session list is written to disk. The Copilot SDK independently persists session state in `~/.copilot/session-state/<guid>/`.

4. **Resume**: On relaunch, `RestorePreviousSessionsAsync` reads the active sessions file and calls `ResumeSessionAsync` for each. Conversation history is reconstructed from the SDK's `events.jsonl`. Users can also manually resume any saved session from the sidebar.

5. **Close**: `CloseSessionAsync` disposes the `CopilotSession`, removes it from the dictionary, and updates the active sessions file.

### Event Handling

All SDK events are received on background threads. `CopilotService` captures the UI `SynchronizationContext` during initialization and uses `_syncContext.Post` to marshal event callbacks to the Blazor UI thread, where components call `StateHasChanged()` to re-render.

### Reconnect on Failure

If `SendAsync` throws (e.g., the underlying process died), the service attempts to resume the session by its persisted GUID and retry the prompt once. This is transparent to the user — they see a "🔄 Reconnecting session..." activity indicator.

### Persistent Server Detection

On startup in Persistent mode, `ServerManager.DetectExistingServer()` reads `autopilot-server.pid`, checks if the process is alive via TCP connect, and reuses it if available. Stale PID files are cleaned up automatically.

## NuGet Dependencies

| Package | Version | Purpose |
|---------|---------|---------|
| `GitHub.Copilot.SDK` | 0.1.22 | Copilot CLI client (ACP protocol) |
| `Microsoft.Maui.Controls` | (MAUI SDK) | .NET MAUI framework |
| `Microsoft.AspNetCore.Components.WebView.Maui` | (MAUI SDK) | Blazor WebView for MAUI |
| `Microsoft.Extensions.Logging.Debug` | 10.0.0 | Debug logging |

> **Note**: The csproj includes `<TrimmerRootAssembly Include="GitHub.Copilot.SDK" />` to prevent the linker from stripping SDK event types needed for runtime pattern matching. Do not remove this.
