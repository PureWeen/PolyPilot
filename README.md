<p align="center">
  <img src="PolyPilot/wwwroot/PolyPilot_logo_lg.png" alt="PolyPilot Logo" width="200">
</p>

<h1 align="center">PolyPilot</h1>

<p align="center">
  <strong>Your AI Fleet Commander — Run an army of GitHub Copilot agents from a single app.</strong>
</p>

<p align="center">
  <a href="https://github.com/PureWeen/PolyPilot/releases/latest"><img src="https://img.shields.io/github/v/release/PureWeen/PolyPilot?style=flat-square&color=blue" alt="Latest Release"></a>
  <a href="https://github.com/PureWeen/PolyPilot/releases/latest"><img src="https://img.shields.io/github/downloads/PureWeen/PolyPilot/total?style=flat-square&color=green" alt="Downloads"></a>
  <img src="https://img.shields.io/badge/platforms-macOS%20%7C%20Windows%20%7C%20Android%20%7C%20iOS%20%7C%20Linux-lightgrey?style=flat-square" alt="Platforms">
</p>

---

<p align="center">
  <img src="docs/desktop-sessions.png" alt="PolyPilot Dashboard" width="700">
</p>

## What is PolyPilot?

**Mission control for AI-powered development.** Launch dozens of GitHub Copilot agents, assign them to different repos, watch them work in real time, and manage everything from one dashboard — or from your phone.

| | |
|---|---|
| 🚀 **Run 10+ agents simultaneously** | Each with its own model, repo, and conversation |
| 🧠 **Mix models freely** | Claude Opus + GPT-5 + Sonnet in the same workspace |
| 🤖 **One-click team presets** | PR Review Squad, multi-agent orchestration, custom squads |
| 📱 **Control from your phone** | Scan a QR code, manage your fleet from anywhere |
| 🎉 **Fiesta Mode** | Spread work across multiple machines on your LAN |
| 🔄 **Sessions never die** | Persistent across restarts, auto-reconnect, auto-resume |

---

## ✨ Key Features

### 🎛️ Fleet Dashboard
A real-time grid of all active agents. Streaming output, tool execution, token usage — everything at a glance. Send prompts to any agent without switching windows.

<p align="center">
  <img src="docs/desktop-chat.png" alt="PolyPilot Chat" width="600">
</p>

### 🤖 Multi-Agent Teams
Launch pre-built or custom agent teams with one click:

- **PR Review Squad** — 5 Opus workers each dispatch 3 sub-agents (Opus, Sonnet, Codex) and synthesize consensus reviews
- **Custom Squads** — Drop a `.squad/` directory in your repo with agent charters, routing rules, and shared context
- **Worktree Strategies** — Shared, orchestrator-isolated, or fully-isolated branches per agent

### 🎉 Fiesta Mode
Turn your LAN into a compute cluster. Discover other PolyPilot instances, pair with a string or push-to-pair, then dispatch tasks to any machine with `@worker-name`. One PolyPilot to rule them all.

### 📱 Remote Access
Your agents run on your desktop. You control them from your pocket.

1. Start a tunnel in Settings (powered by [Azure DevTunnels](https://learn.microsoft.com/en-us/azure/developer/dev-tunnels/))
2. Scan the QR code on your phone
3. Full control: send prompts, watch streaming output, manage sessions

### 💬 Rich Chat
Streaming Markdown, code blocks, reasoning traces, tool call visualization, and real-time activity indicators (`💭 Thinking...` → `🔧 Running bash...` → `✅ Done`). You see everything the agent sees.

### 🧠 Any Model, Any Task
Create sessions with different models and compare results side by side. Claude for architecture, GPT for code generation, Gemini for review — all in parallel. Models are assigned per-session, and the `/agent` command lets you select specialized CLI agents (code-review, security-review) on the fly.

### 🌿 Git-Native Workflow
- **Quick Branch + Session** — one click to create a branch and start coding
- **Named Branch + Session** — specify a name or PR number (`#123`)
- **Worktree management** — clone, branch, isolate — all from the sidebar
- **Auto-cleanup** — worktrees are tracked and removed when sessions close

### 🔁 Reflection Cycles
Set a goal and let the agent loop: execute → evaluate → refine → repeat. Configurable iteration limits and completion criteria. Perfect for TDD and multi-step tasks.

### 💾 Bulletproof Persistence
Sessions survive everything — app restarts, machine reboots, CLI crashes. History is reconstructed from event logs. A 3-tier watchdog recovers stuck agents automatically. No zombie sessions, no lost work.

### ⌨️ Slash Commands
Quick control without leaving the chat: `/help` `/clear` `/compact` `/new` `/sessions` `/rename` `/diff` `/status` `/mcp` `/plugin` `/agent`

### 🔔 Notifications & Tailscale
Get notified when agents finish or hit errors — even in the background. Tailscale VPN is detected automatically for easy fleet sharing across your network.

### 🎮 Demo Mode
Try the full UI without a Copilot connection — simulated streaming, tool calls, and activity indicators with realistic timing.

---

## 🖥️ Supported Platforms

| Platform | Status | Install |
|----------|--------|---------|
| **macOS** | ✅ Primary | `brew install --cask polypilot` |
| **Windows** | ✅ Supported | [Download .zip](https://github.com/PureWeen/PolyPilot/releases/latest) |
| **Android** | ✅ Supported | [Download .apk](https://github.com/PureWeen/PolyPilot/releases/latest) |
| **iOS** | ✅ Supported | [Build from source](https://github.com/PureWeen/PolyPilot#build-from-source) |
| **Linux** | 🧪 Experimental | [.deb / .AppImage / .flatpak](https://github.com/PureWeen/PolyPilot/releases/latest) |

Mobile connects to a desktop instance via WebSocket bridge — your fleet runs on the workstation, your phone is the remote control.

## 🚀 Getting Started

### Homebrew (macOS — recommended)

```bash
brew tap PureWeen/tap
brew install --cask polypilot
```

### Build from Source

**Prerequisites:** [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) + `dotnet workload install maui` + [GitHub Copilot CLI](https://docs.github.com/en/copilot) + Copilot subscription

```bash
cd PolyPilot

# macOS
./relaunch.sh                                       # Build + hot-relaunch

# Windows
dotnet build -f net10.0-windows10.0.19041.0

# Android
dotnet build -f net10.0-android -t:Install

# Linux
dotnet run --project PolyPilot.Gtk/PolyPilot.Gtk.csproj
```

## 🔁 Self-Building Workflow

PolyPilot builds itself. Open a Copilot session pointed at the PolyPilot repo, make changes, run `./relaunch.sh`, and the app seamlessly rebuilds and relaunches — no downtime, no stale binaries, no leaving the app.

> **Most of PolyPilot's features were built by GitHub Copilot coding agents — orchestrated from within PolyPilot itself.**

## 🧪 Testing

- **3,000+ unit tests** — models, services, orchestration, persistence, parsing
- **Executable UI scenarios** — end-to-end CDP flows validated against a running instance

```bash
cd PolyPilot.Tests && dotnet test
```

See [`docs/testing.md`](docs/testing.md) for the full guide.

---

<p align="center">
  <strong>Built with 🤖 by AI agents, for AI agents.</strong>
</p>
