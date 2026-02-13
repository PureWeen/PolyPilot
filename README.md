<p align="center">
  <img src="PolyPilot/wwwroot/PolyPilot_logo_lg.png" alt="PolyPilot Logo" width="200">
</p>

<h1 align="center">PolyPilot</h1>

<p align="center">
  <strong>Your AI Fleet Commander — Run an army of GitHub Copilot agents from a single app.</strong>
</p>

<p align="center">
  <em>Multi-agent orchestration • Real-time streaming • Cross-platform • Remote access from your phone</em>
</p>

---

## What is PolyPilot?

PolyPilot is a **multi-agent control plane for GitHub Copilot**. It's a cross-platform native app (macOS, Windows, Android, iOS) built with .NET MAUI and Blazor that lets you spin up, orchestrate, and monitor **dozens of parallel Copilot coding agents** — each with its own model, working directory, and conversation — all from one dashboard.

Think of it as **mission control for AI-powered development**: you launch agents, assign them tasks across different repos, watch them work in real time, and manage everything from a single pane of glass — or from your phone while you're away from your desk.

### Why PolyPilot?

The Copilot CLI is powerful, but it's one agent in one terminal. What if you could:

- 🚀 **Run 10+ Copilot agents simultaneously**, each working on a different task or repo
- 📡 **Broadcast a single prompt to all agents at once** and watch them fan out in parallel
- 🔄 **Resume any session** across app restarts — your agents never lose context
- 📱 **Monitor and control everything from your phone** via secure WebSocket bridge and DevTunnel
- 🧠 **Mix and match models** — Claude, GPT, Gemini — in the same workspace
- 🏗️ **Organize agents into groups**, pin favorites, and sort by activity

That's PolyPilot.

## 🗺️ Active Planning Doc

Current Copilot SDK event/chat-fidelity planning work is tracked in:

- [`COPILOT-SDK-CHAT-FIDELITY-PLAN.md`](COPILOT-SDK-CHAT-FIDELITY-PLAN.md)

## ✨ Key Features

### 🎛️ Multi-Session Orchestrator Dashboard
A real-time grid view of all active agents. Each card shows streaming output, tool execution status, token usage, and queue depth. Send targeted prompts to individual agents or **Broadcast to All** to fan out work across your entire fleet.

### 💬 Rich Chat Interface
Full-featured chat UI with streaming responses, Markdown rendering (code blocks, inline code, bold), real-time activity indicators, and auto-scrolling. See exactly what each agent is thinking and doing — including tool calls, reasoning blocks, and intent changes.

### 🔧 Live Agent Activity Feed
Watch your agents work in real time: `💭 Thinking...` → `🔧 Running bash...` → `✅ Tool completed`. Full visibility into multi-step agentic workflows with tool execution tracking and reasoning transparency.

### 💾 Session Persistence & Resume
Sessions survive app restarts. Active sessions are automatically saved and restored. Conversation history is reconstructed from event logs. Browse and resume any previously saved session from the sidebar — agents never lose their place.

### 📱 Remote Access from Your Phone
Run agents on your desktop, monitor from your phone. PolyPilot's WebSocket bridge server + Azure DevTunnel integration creates a secure tunnel so you can watch agents work, send prompts, and manage sessions from anywhere. Just scan a QR code to connect.

### 🧠 Multi-Model Support
Create sessions with different AI models and compare results side by side. Assign Claude to one task, GPT to another, and Gemini to a third — all running in parallel in the same workspace.

### 📂 Per-Session Working Directories
Point each agent at a different repo or directory. Native folder pickers on macOS and Windows. Manage worktrees for parallel git operations across agents.

### 🏗️ Session Organization
Groups, pinning, and multiple sort modes (Last Active, Created, A–Z, Manual) let you manage large fleets of agents without losing track. Collapsible groups keep things tidy.

### 🔌 Flexible Connection Modes
From embedded stdio for quick single-machine use, to a persistent server that survives app restarts, to remote mode for mobile access — pick the transport that fits your workflow.

### 🛡️ Auto-Reconnect
If an agent's underlying process dies mid-conversation, PolyPilot automatically resumes the session and retries — transparent to you.

## Connection Modes

PolyPilot supports three transport modes, configurable from the Settings page:

| Mode | Transport | Lifecycle | Best For |
|------|-----------|-----------|----------|
| **Embedded** (default) | stdio | Dies with app | Quick single-machine use |
| **TCP Server** | SDK-managed TCP | Dies with app | Stable long sessions |
| **Persistent Server** | Detached TCP server | Survives app restarts | Always-on agent fleet |

**Embedded** — Zero-config. The SDK spawns Copilot CLI via stdin/stdout. Process dies with the app.

**TCP Server** — More stable for long-running sessions. SDK manages the TCP lifecycle internally.

**Persistent Server** — The app spawns a detached Copilot CLI server (`copilot --headless`) that runs independently and survives app restarts. On relaunch, PolyPilot detects the existing server and reconnects automatically.

## Architecture

PolyPilot is a three-layer stack: **Blazor UI** → **Service Layer** → **Copilot SDK**, built to handle real-time streaming from multiple concurrent agent sessions.

```
┌─────────────────────────────────────────────────────────┐
│                      PolyPilot                          │
│              (.NET MAUI Blazor Hybrid)                   │
│                                                         │
│  ┌──────────────┐  ┌─────────────────┐  ┌───────────┐  │
│  │SessionSidebar│  │ Dashboard.razor  │  │ Settings  │  │
│  │  (create/    │  │ (orchestrator +  │  │  .razor   │  │
│  │   resume)    │  │   chat UI)       │  │           │  │
│  └──────┬───────┘  └───────┬─────────┘  └─────┬─────┘  │
│         │                  │                   │        │
│         └──────────────────┼───────────────────┘        │
│                            │                            │
│                  ┌─────────▼──────────┐                 │
│                  │   CopilotService   │ (singleton)     │
│                  │ ┌───────────────┐  │                 │
│                  │ │ SessionState  │  │ ConcurrentDict  │
│                  │ │  ├─ Session   │  │ of named        │
│                  │ │  ├─ Info      │  │ sessions        │
│                  │ │  └─ Response  │  │                 │
│                  │ └───────────────┘  │                 │
│                  └─────────┬──────────┘                 │
│                            │                            │
│                  ┌─────────▼──────────┐                 │
│                  │   CopilotClient   │ (Copilot SDK)    │
│                  └─────────┬──────────┘                 │
│                            │                            │
│         ┌──────────────────┼──────────────────┐         │
│         │ stdio            │ TCP              │ TCP     │
│         ▼                  ▼                  ▼         │
│   ┌──────────┐       ┌──────────┐      ┌───────────┐   │
│   │ copilot  │       │ copilot  │      │ Persistent│   │
│   │ (child)  │       │ (child)  │      │  Server   │   │
│   └──────────┘       └──────────┘      │ (detached)│   │
│                                        └─────┬─────┘   │
│                            ┌─────────────────┘         │
│                     ┌──────┴────────┐                   │
│                     │ ServerManager │ (PID tracking)    │
│                     └───────────────┘                   │
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
PolyPilot/
├── PolyPilot.csproj            # Project config, SDK reference, trimmer settings
├── MauiProgram.cs              # App bootstrap, DI registration, crash logging
├── relaunch.sh                 # Build + seamless hot-relaunch script (macOS)
├── Models/
│   ├── AgentSessionInfo.cs     # Session metadata (name, model, history, state)
│   ├── ChatMessage.cs          # Chat message record (role, content, timestamp)
│   ├── ConnectionSettings.cs   # Connection mode enum + serializable settings
│   ├── SessionOrganization.cs  # Groups, pins, sort mode for session management
│   ├── BridgeMessages.cs       # WebSocket bridge protocol (19 message types)
│   ├── RepositoryInfo.cs       # Managed repository metadata
│   ├── DiffParser.cs           # Git diff parsing for inline display
│   └── PlatformHelper.cs       # Platform detection (IsDesktop, IsMobile)
├── Services/
│   ├── CopilotService.cs       # Core service: session CRUD, events, persistence
│   ├── CopilotService.*.cs     # Partial classes: Events, Bridge, Persistence, Organization, Utilities
│   ├── ChatDatabase.cs         # SQLite chat history persistence
│   ├── ServerManager.cs        # Persistent server lifecycle + PID tracking
│   ├── DevTunnelService.cs     # Azure DevTunnel CLI wrapper for remote sharing
│   ├── WsBridgeServer.cs       # WebSocket bridge server (desktop → mobile)
│   ├── WsBridgeClient.cs       # WebSocket bridge client (mobile → desktop)
│   ├── RepoManager.cs          # Git repo cloning, worktree management
│   ├── DemoService.cs          # Offline demo mode for testing UI
│   ├── QrScannerService.cs     # QR code scanning for mobile connection setup
│   └── TailscaleService.cs     # Tailscale VPN integration for LAN sharing
├── Components/
│   ├── Layout/
│   │   ├── MainLayout.razor    # App shell with sidebar + content area
│   │   ├── SessionSidebar.razor# Session list, create/resume, groups, sorting
│   │   ├── SessionListItem.razor # Individual session row with status
│   │   ├── CreateSessionForm.razor # New session form (model, dir, name)
│   │   └── NavMenu.razor       # Top navigation bar
│   ├── Pages/
│   │   ├── Dashboard.razor     # Multi-session orchestrator grid + chat UI
│   │   └── Settings.razor      # Connection mode, server controls, tunnel setup
│   ├── SessionCard.razor       # Dashboard grid card with streaming output
│   ├── ExpandedSessionView.razor # Full-screen single-session chat view
│   ├── ChatMessageList.razor   # Message list with Markdown rendering
│   ├── DiffView.razor          # Inline git diff viewer
│   ├── ModelSelector.razor     # Model picker dropdown
│   └── RemoteDirectoryPicker.razor # Remote directory browser for mobile
├── Platforms/
│   ├── MacCatalyst/            # Mac Catalyst entitlements, folder picker
│   ├── Windows/                # WinUI entry point, folder picker
│   ├── Android/                # Android platform bootstrapping
│   └── iOS/                    # iOS platform bootstrapping
└── wwwroot/
    └── app.css                 # Global styles
```

## Supported Platforms

One codebase, four platforms:

| Platform | Target Framework | Status |
|----------|-----------------|--------|
| **macOS** (Mac Catalyst) | `net10.0-maccatalyst` | ✅ Primary development target |
| **Windows** | `net10.0-windows10.0.19041.0` | ✅ Supported |
| **Android** | `net10.0-android` | ✅ Supported (Remote mode) |
| **iOS** | `net10.0-ios` | ✅ Supported (Remote mode) |

Mobile devices connect to a desktop instance via WebSocket bridge — run your agent fleet on your workstation, control it from your pocket.

## Prerequisites

- **.NET 10 SDK** (Preview)
- **.NET MAUI workload** — install with `dotnet workload install maui`
- **GitHub Copilot CLI** — installed globally via npm (`npm install -g @github/copilot`)
- **GitHub Copilot subscription** — required for the CLI to authenticate

### Platform-specific requirements

- **macOS**: macOS 15.0+ for Mac Catalyst
- **Windows**: Windows 10 (build 17763+). The app runs as an unpackaged WinUI 3 application
- **Android/iOS**: Requires a desktop instance running with a DevTunnel for remote connection

## Building & Running

### First-time setup

```bash
# Install .NET MAUI workload
dotnet workload install maui

# Restore NuGet packages
cd PolyPilot
dotnet restore
```

### macOS (Mac Catalyst)

```bash
dotnet build PolyPilot.csproj -f net10.0-maccatalyst
open bin/Debug/net10.0-maccatalyst/maccatalyst-arm64/PolyPilot.app
```

The project includes a `relaunch.sh` script for seamless hot-relaunch during development:

```bash
./relaunch.sh
```

### Windows

```bash
dotnet build PolyPilot.csproj -f net10.0-windows10.0.19041.0
.\bin\Debug\net10.0-windows10.0.19041.0\win-x64\PolyPilot.exe
```

### Android

```bash
dotnet build PolyPilot.csproj -f net10.0-android -t:Install   # Build + deploy to connected device
adb shell am start -n com.companyname.PolyPilot/crc645dd8ecec3b5d9ba6.MainActivity
```

## 📱 Remote Access via DevTunnel

Mobile devices (Android/iOS) connect to a desktop instance over the network using [Azure DevTunnels](https://learn.microsoft.com/en-us/azure/developer/dev-tunnels/). This creates a secure, publicly-accessible tunnel so you can control your agent fleet from anywhere.

### Step 1: Install the DevTunnel CLI (on your desktop)

**Windows:**
```bash
winget install Microsoft.devtunnel
```

**macOS:**
```bash
brew install --cask devtunnel
```

### Step 2: Start the desktop app and enable sharing

1. Launch PolyPilot on your desktop (macOS or Windows)
2. Go to **Settings** and select **Persistent** or **Embedded** mode
3. If using Persistent mode, click **Start Server** to start the Copilot server
4. Under **Share via DevTunnel**:
   - Click **Login with GitHub** (first time only — opens browser for OAuth)
   - Click **Start Tunnel** — this starts a WebSocket bridge and creates a DevTunnel
5. Once running, the UI shows:
   - A **tunnel URL** (e.g., `https://xxx.devtunnels.ms`)
   - An **access token** for authentication
   - A **QR code** that encodes both the URL and token

### Step 3: Connect from your mobile device

**Option A — Scan QR Code (recommended):**
1. Open PolyPilot on your phone
2. Go to **Settings** → tap **Scan QR Code**
3. Point your camera at the QR code shown on the desktop
4. The URL and token are filled in automatically
5. Tap **Save & Reconnect**

**Option B — Manual entry:**
1. Open PolyPilot on your phone
2. Go to **Settings** → **Remote Server**
3. Enter the tunnel URL and access token from the desktop
4. Tap **Save & Reconnect**

### Under the hood

```
┌─────────────────┐         DevTunnel          ┌──────────────────┐
│  Mobile Device   │ ──── wss://xxx.ms ────▶   │  Desktop App     │
│  (PolyPilot)     │                            │  (PolyPilot)     │
│                  │                            │                  │
│  WsBridgeClient  │  ◄── JSON state sync ──▶  │  WsBridgeServer  │
│                  │                            │       │          │
└──────────────────┘                            │  CopilotService  │
                                                │       │          │
                                                │  Copilot CLI     │
                                                └──────────────────┘
```

The desktop app runs a `WsBridgeServer` on a local port, which the DevTunnel exposes publicly. The mobile app's `WsBridgeClient` connects via WebSocket and receives real-time session state, chat messages, and tool execution events. Commands sent from the mobile device (create session, send prompt, etc.) are forwarded to the desktop's `CopilotService`.

The tunnel URL and ID are persisted across restarts — stopping and restarting the tunnel reuses the same URL so mobile devices don't need to reconfigure.

## Configuration

### Settings files (stored in `~/.polypilot/`)

| File | Purpose |
|------|---------|
| `settings.json` | Connection mode, host, port, auto-start preference |
| `active-sessions.json` | Active sessions for restore on relaunch |
| `ui-state.json` | Last active page and session name |
| `organization.json` | Session groups, pins, sort preferences |
| `server.pid` | PID and port of the persistent Copilot server |
| `crash.log` | Unhandled exception log |
| `repos.json` | Managed repository list |
| `repos/` | Bare git clones for managed repos |
| `worktrees/` | Git worktrees for parallel agent work |
| `chat_history.db` | SQLite database of chat history |

### Example `settings.json`

```json
{
  "Mode": 0,
  "Host": "localhost",
  "Port": 4321,
  "AutoStartServer": false
}
```

Mode values: `0` = Embedded, `1` = Server, `2` = Persistent.

## How It All Comes Together

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

On startup in Persistent mode, `ServerManager.DetectExistingServer()` reads `PolyPilot-server.pid`, checks if the process is alive via TCP connect, and reuses it if available. Stale PID files are cleaned up automatically.

## NuGet Dependencies

| Package | Purpose |
|---------|---------|
| `GitHub.Copilot.SDK` | Copilot CLI client (ACP protocol) |
| `Microsoft.Maui.Controls` | .NET MAUI framework |
| `Microsoft.AspNetCore.Components.WebView.Maui` | Blazor WebView for MAUI |
| `Markdig` | Markdown parsing & rendering |
| `sqlite-net-pcl` | Chat history persistence |
| `QRCoder` | QR code generation for remote setup |
| `ZXing.Net.Maui.Controls` | QR code scanning on mobile |

> **Note**: The csproj includes `<TrimmerRootAssembly Include="GitHub.Copilot.SDK" />` to prevent the linker from stripping SDK event types needed for runtime pattern matching. Do not remove this.

---

<p align="center">
  <strong>Built with 🤖 by AI agents, for AI agents.</strong><br>
  <em>Most of PolyPilot's features were built by GitHub Copilot coding agents — orchestrated from within PolyPilot itself.</em>
</p>
