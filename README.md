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
- 🔄 **Resume any session** across app restarts — your agents never lose context
- 📱 **Monitor and control everything from your phone** via secure WebSocket bridge and DevTunnel
- 🧠 **Mix and match models** — Claude, GPT, Gemini — in the same workspace
- 🏗️ **Organize agents into groups**, pin favorites, and sort by activity
- 🌿 **Manage repos and worktrees** — clone repos, create branches, spin up isolated worktrees per agent

That's PolyPilot.

## ✨ Key Features

### 🎛️ Multi-Session Orchestrator Dashboard
A real-time grid view of all active agents. Each card shows streaming output, tool execution status, token usage, and queue depth. Send targeted prompts to individual agents from a single dashboard.

### 💬 Rich Chat Interface
Full-featured chat UI with streaming responses, Markdown rendering (code blocks, inline code, bold), real-time activity indicators, and auto-scrolling. See exactly what each agent is thinking and doing — including tool calls, reasoning blocks, and intent changes.

### 🔧 Live Agent Activity Feed
Watch your agents work in real time: `💭 Thinking...` → `🔧 Running bash...` → `✅ Tool completed`. Full visibility into multi-step agentic workflows with tool execution tracking and reasoning transparency.

### 💾 Session Persistence & Resume
Sessions survive app restarts. Active sessions are automatically saved and restored. Conversation history is reconstructed from event logs. Browse and resume any previously saved session from the sidebar — agents never lose their place.

### 📱 Remote Access from Your Phone
Run agents on your desktop, monitor from your phone. PolyPilot's WebSocket bridge + Azure DevTunnel integration creates a secure tunnel so you can watch agents work, send prompts, and manage sessions from anywhere. Just scan a QR code to connect.

### 🧠 Multi-Model Support
Create sessions with different AI models and compare results side by side. Assign Claude to one task, GPT to another, and Gemini to a third — all running in parallel in the same workspace.

### 📂 Per-Session Working Directories
Point each agent at a different repo or directory. Native folder pickers on macOS and Windows. Manage worktrees for parallel git operations across agents.

### 🌿 Repository & Worktree Management
Clone repositories, create branches and worktrees, and launch sessions — all from the sidebar. **Quick Branch + Session** creates a new branch and session in one click. **Named Branch + Session** lets you specify a branch name or PR number (`#123`). Worktrees are tracked, linked to sessions, and cleaned up automatically when sessions close.

### 🤖 Multi-Agent Presets & Squad Teams
Launch pre-configured teams with one click. Built-in presets like **PR Review Squad** spin up an orchestrator plus multiple workers, each with their own model and role. Auto-discovers `.squad` and `.ai-team` directories in your repos for custom team definitions with agent charters and routing configs.

### 🔀 Worktree Strategies
Control how multi-agent teams share code:
- **Shared** — all agents work in the same directory
- **Orchestrator Isolated** — orchestrator gets its own branch, workers share one
- **Fully Isolated** — every agent gets a unique worktree and branch for zero-conflict parallel work

### 🏗️ Session Organization
Groups, pinning, and multiple sort modes (Last Active, Created, A–Z, Manual) let you manage large fleets of agents without losing track. Collapsible groups keep things tidy.

### 🎉 Fiesta Mode — Multi-Machine Orchestration
Discover and link other PolyPilot instances on your LAN. Start a "Fiesta" to fan out work to linked worker machines via `@mention` routing. Workers are discovered automatically and linked in Settings. Use `@worker-name` in your prompts to dispatch tasks to specific machines.

### ⌨️ Slash Commands
Built-in slash commands give you quick control without leaving the chat: `/help`, `/clear`, `/version`, `/compact`, `/new`, `/sessions`, `/rename`, `/diff`, `/status`, `/mcp`, `/plugin`.

### 🔔 Smart Notifications
Get notified when agents finish tasks, encounter errors, or need your attention — even when the app is in the background.

### 🛡️ Processing Watchdog
Automatically detects agents stuck in a "Thinking..." state and recovers them. A 3-tier timeout system handles quiescent sessions (30s), active tool execution (10min), and general inactivity (2min) — no more zombie sessions.

### 🔁 Reflection Cycles
Goal-based iterative refinement for agents. Set a goal, and the agent will loop — executing, evaluating, and refining — until it meets the completion criteria or hits a max iteration count. Great for test-driven workflows and multi-step tasks.

### 📊 Usage Stats
Track session metrics: messages sent, tokens used, session duration, and lines suggested. Visible in the bottom bar for quick reference.

### 🎮 Demo Mode
Test the UI without a Copilot connection. The built-in demo service simulates streaming responses, tool calls, and activity indicators with realistic timing.

### 🔌 Flexible Connection Modes
From embedded stdio for quick single-machine use, to a persistent server that survives app restarts, to remote mode for mobile access — pick the transport that fits your workflow.

### 🛡️ Auto-Reconnect
If an agent's underlying process dies mid-conversation, PolyPilot automatically resumes the session and retries — transparent to you.

### 🔄 Git Auto-Update
When running from a git checkout, PolyPilot can automatically detect and pull updates from the main branch — keeping your instance up to date without manual intervention.

### 🌐 Tailscale Integration
Detects your Tailscale VPN status and IP automatically, making it easy to share your agent fleet across your Tailscale network.

## 🔁 Iterating on PolyPilot — Self-Building Workflow

PolyPilot is designed to be developed **from within itself**. You can open a Copilot session pointed at the PolyPilot repo, make changes, and use the included `relaunch.sh` script to seamlessly rebuild and relaunch — all without leaving the app.

### How it works

```bash
# macOS
./relaunch.sh

# Windows
powershell -ExecutionPolicy Bypass -File relaunch.ps1
```

On macOS, the script:
1. **Builds** the project (`dotnet build -f net10.0-maccatalyst`)
2. **Copies** the built app to a staging directory
3. **Launches** the new instance
4. **Verifies** the new instance is stable (waits a few seconds)
5. **Kills** the old instance — seamless handoff, no downtime

On Windows, `relaunch.ps1` follows the same pattern for the WinUI target.

If the build fails, the old instance keeps running and you see clear error output. No stale binaries are ever launched.

This means an agent working inside PolyPilot can edit code, run `./relaunch.sh`, and immediately test its own changes in the freshly-built app — a tight feedback loop for AI-driven development.

> **Most of PolyPilot's features were built by GitHub Copilot coding agents — orchestrated from within PolyPilot itself.**

## Supported Platforms

| Platform | Status |
|----------|--------|
| **macOS** (Mac Catalyst) | ✅ Primary development target |
| **Windows** | ✅ Supported |
| **Android** | ✅ Supported (Remote mode) |
| **iOS** | ✅ Supported (Remote mode) |
| **Linux** (GTK head) | 🧪 Experimental (`PolyPilot.Gtk`) |

Mobile devices connect to a desktop instance via WebSocket bridge — run your agent fleet on your workstation, control it from your pocket.

## Getting Started

### Install with Homebrew (macOS)

```bash
brew tap PureWeen/tap
brew install --cask polypilot
```

This installs the notarized PolyPilot.app and the GitHub Copilot CLI automatically.

### Prerequisites (build from source)

- **.NET 10 SDK** (Preview)
- **.NET MAUI workload** — `dotnet workload install maui`
- **GitHub Copilot CLI** — `brew install copilot-cli` or `npm install -g @github/copilot`
- **GitHub Copilot subscription**

### Build & Run

```bash
cd PolyPilot

# macOS
dotnet build -f net10.0-maccatalyst
open bin/Debug/net10.0-maccatalyst/maccatalyst-arm64/PolyPilot.app

# Or use the hot-relaunch script for iterative development
./relaunch.sh

# Windows
dotnet build -f net10.0-windows10.0.19041.0

# Android (deploy to connected device)
dotnet build -f net10.0-android -t:Install

# Linux (GTK head)
dotnet build PolyPilot.Gtk/PolyPilot.Gtk.csproj
dotnet run --project PolyPilot.Gtk/PolyPilot.Gtk.csproj
```

## 🧪 Testing

PolyPilot has two layers of testing:

- **1,200+ unit tests** (xUnit, .NET 10) — deterministic tests covering models, services, multi-agent orchestration, persistence, and parsing. Run with `cd PolyPilot.Tests && dotnet test`.
- **Executable UI scenarios** (JSON + MauiDevFlow CDP) — end-to-end flows validated against a running app instance. 25+ multi-agent scenarios (reflection loops, Squad discovery, group lifecycle) and 10+ mode-switch scenarios.

The test project shares source files with the main MAUI project via `<Compile Include>` links, so tests run on any machine without platform SDKs. `ScenarioReferenceTests.cs` cross-references every CDP scenario with its deterministic unit-test equivalent.

See [`docs/testing.md`](docs/testing.md) for the full testing guide, scenario format, and how to add new tests.

## 📱 Remote Access via DevTunnel

Mobile devices connect to your desktop over [Azure DevTunnels](https://learn.microsoft.com/en-us/azure/developer/dev-tunnels/) — a secure tunnel to control your agent fleet from anywhere.

1. Install the DevTunnel CLI (`brew install --cask devtunnel` on macOS, `winget install Microsoft.devtunnel` on Windows)
2. In PolyPilot Settings, click **Login with GitHub** then **Start Tunnel**
3. Scan the **QR code** from your phone to connect instantly

---

<p align="center">
  <strong>Built with 🤖 by AI agents, for AI agents.</strong>
</p>
