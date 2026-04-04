---
name: copilot-sdk-reference
description: >
  Complete API reference for GitHub.Copilot.SDK v0.2.1. Consult before implementing
  session lifecycle, orchestration, event handling, hooks, plan management, context
  compaction, tool monitoring, or any feature that interacts with the Copilot CLI server.
  Use when: (1) Adding new session management code, (2) Modifying event handlers,
  (3) Building orchestration/dispatch features, (4) Adding tool monitoring or permissions,
  (5) Working with plans, modes, or models, (6) Any feature touching CopilotService,
  (7) Updating the SDK NuGet package version.
---

# GitHub Copilot SDK v0.2.1 — Complete API Reference

**Package:** `GitHub.Copilot.SDK` v0.2.1
**NuGet:** `<PackageReference Include="GitHub.Copilot.SDK" Version="0.2.1" />`
**XML Docs:** `~/.nuget/packages/github.copilot.sdk/0.2.1/lib/net8.0/GitHub.Copilot.SDK.xml`
**Types:** 453 total

> **Rule:** Before implementing custom session/event/orchestration code, check this reference.
> If the SDK has an API, use it. If custom code is necessary, add `// SDK-gap: <reason>`.

---

## 1. Session Creation

### SessionConfig (creation-time properties)

| Property | Type | Purpose |
|----------|------|---------|
| `SessionId` | string? | Pre-assign session GUID |
| `ClientName` | string? | Client identifier |
| `Model` | string? | Initial model (e.g., "claude-opus-4.6-1m") |
| `ReasoningEffort` | string? | "low", "medium", "high", "xhigh" |
| `WorkingDirectory` | string? | Session working directory |
| `ConfigDir` | string? | Config directory (default: ~/.copilot) |
| `SystemMessage` | SystemMessageConfig? | Custom system prompt with section overrides |
| `Tools` | ToolHandler[]? | Custom tool definitions |
| `AvailableTools` | string[]? | Whitelist of allowed tools |
| `ExcludedTools` | string[]? | Blacklist of denied tools |
| `Hooks` | SessionHooks? | All 6 hook handlers |
| `McpServers` | McpServerConfig[]? | MCP server configurations |
| `CustomAgents` | CustomAgentConfig[]? | Custom agent definitions |
| `Agent` | string? | Select a custom agent by name |
| `SkillDirectories` | string[]? | Additional skill search paths |
| `DisabledSkills` | string[]? | Skills to disable |
| `InfiniteSessions` | InfiniteSessionConfig? | Auto-compaction settings |
| `Provider` | ProviderConfig? | Custom model provider (BYOK) |
| `Streaming` | bool? | Enable/disable streaming |
| `OnPermissionRequest` | PermissionRequestHandler | **Required** — permission callback |
| `OnUserInputRequest` | UserInputHandler? | User input callback |
| `OnElicitationRequest` | ElicitationHandler? | Structured input callback (new in 0.2.1) |
| `Commands` | CommandDefinition[]? | Custom slash commands (new in 0.2.1) |
| `OnEvent` | SessionEventHandler? | Raw event callback |

### ResumeSessionConfig

Same properties as SessionConfig plus:
- `DisableResume` — bool, skip event replay on resume

### Key Types

- **SystemMessageConfig** — `Mode` (append/prepend/replace), `Content`, `Sections` (per-section overrides)
- **SectionOverride** — `Action` (append/prepend/replace), `Content`, `Transform` (dynamic)
- **CustomAgentConfig** — `Name`, `DisplayName`, `Description`, `Tools`, `Prompt`, `McpServers`, `Infer`
- **InfiniteSessionConfig** — `Enabled`, `BackgroundCompactionThreshold`, `BufferExhaustionThreshold`
- **CommandDefinition** — Custom slash commands with handler (new in 0.2.1)
- **ElicitationHandler** — Structured user input handler (new in 0.2.1)

---

## 2. Session RPC APIs (`session.Rpc.*`)

Access via `CopilotSession.Rpc` property.

### Plan API (`session.Rpc.Plan`)

| Method | Purpose |
|--------|---------|
| `ReadAsync()` | Read plan — returns `Exists`, `Content`, `Path` |
| `UpdateAsync(content)` | Write/update plan content |
| `DeleteAsync()` | Delete the plan |

### Mode API (`session.Rpc.Mode`)

| Method | Purpose |
|--------|---------|
| `GetAsync()` | Get current mode |
| `SetAsync(mode)` | Set mode: `SessionModeGetResultMode.Plan`, `.Autopilot`, `.Interactive` |

### Model API (`session.Rpc.Model`)

| Method | Purpose |
|--------|---------|
| `GetCurrentAsync()` | Get current model |
| `SwitchToAsync(model, reasoningEffort?)` | Switch model mid-session with optional reasoning effort |

### Fleet API (`session.Rpc.Fleet`)

| Method | Purpose |
|--------|---------|
| `StartAsync(prompt)` | Start fleet/parallel execution mode |

### Agent API (`session.Rpc.Agent`)

| Method | Purpose |
|--------|---------|
| `ListAsync()` | List available agents |
| `GetCurrentAsync()` | Get currently selected agent |
| `SelectAsync(name)` | Select an agent |
| `DeselectAsync()` | Deselect current agent |
| `ReloadAsync()` | Reload agent definitions |

### Skills API (`session.Rpc.Skills`)

| Method | Purpose |
|--------|---------|
| `ListAsync()` | List available skills |
| `EnableAsync(name)` | Enable a skill |
| `DisableAsync(name)` | Disable a skill |
| `ReloadAsync()` | Reload skill definitions |

### MCP API (`session.Rpc.Mcp`)

| Method | Purpose |
|--------|---------|
| `ListAsync()` | List MCP servers |
| `EnableAsync(name)` | Enable an MCP server |
| `DisableAsync(name)` | Disable an MCP server |
| `ReloadAsync()` | Reload MCP configurations |

### Extensions API (`session.Rpc.Extensions`)

| Method | Purpose |
|--------|---------|
| `ListAsync()` | List extensions |
| `EnableAsync(name)` | Enable an extension |
| `DisableAsync(name)` | Disable an extension |
| `ReloadAsync()` | Reload extensions |

### Plugins API (`session.Rpc.Plugins`)

| Method | Purpose |
|--------|---------|
| `ListAsync()` | List installed plugins |

### Shell API (`session.Rpc.Shell`)

| Method | Purpose |
|--------|---------|
| `ExecAsync(command, cwd?, timeout?)` | Execute shell command |
| `KillAsync(id, signal?)` | Kill a running shell process |

### Tools API (`session.Rpc.Tools`)

| Method | Purpose |
|--------|---------|
| `HandlePendingToolCallAsync(id, result, error?)` | Respond to a pending external tool call |

### Commands API (`session.Rpc.Commands`)

| Method | Purpose |
|--------|---------|
| `HandlePendingCommandAsync(id, response)` | Respond to a queued slash command |

### Workspace API (`session.Rpc.Workspace`)

| Method | Purpose |
|--------|---------|
| `ListFilesAsync()` | List files in session workspace |
| `ReadFileAsync(path)` | Read a workspace file |
| `CreateFileAsync(path, content)` | Create a workspace file |

### Compaction API (`session.Rpc.Compaction`)

| Method | Purpose |
|--------|---------|
| `CompactAsync()` | Trigger context compaction |

### UI API (`session.Rpc.Ui`)

| Method | Purpose |
|--------|---------|
| `ElicitationAsync(message, schema)` | Request structured input from user |

### Permissions API (`session.Rpc.Permissions`)

| Method | Purpose |
|--------|---------|
| `HandlePendingPermissionRequestAsync(id, result)` | Respond to permission request |

---

## 3. Session Hooks (`SessionConfig.Hooks`)

Register via `SessionConfig.Hooks` or `ResumeSessionConfig.Hooks`.

| Hook | Input | Output | Purpose |
|------|-------|--------|---------|
| **OnPreToolUse** | `ToolName`, `ToolArgs`, `Cwd`, `Timestamp` | `PermissionDecision`, `ModifiedArgs`, `AdditionalContext`, `SuppressOutput` | Intercept/modify/block tool calls before execution |
| **OnPostToolUse** | `ToolName`, `ToolArgs`, `ToolResult`, `Cwd` | `ModifiedResult`, `AdditionalContext`, `SuppressOutput` | Modify tool results after execution |
| **OnUserPromptSubmitted** | `Prompt`, `Cwd`, `Timestamp` | `ModifiedPrompt`, `AdditionalContext`, `SuppressOutput` | Intercept/rewrite user prompts |
| **OnSessionStart** | `Cwd`, `Source`, `InitialPrompt`, `Timestamp` | `AdditionalContext`, `ModifiedConfig` | Custom session initialization |
| **OnSessionEnd** | `Reason`, `FinalMessage`, `Error`, `Cwd` | `CleanupActions`, `SessionSummary`, `SuppressOutput` | Custom cleanup (reason: complete/error/abort/timeout/user_exit) |
| **OnErrorOccurred** | `Error`, `ErrorContext`, `Recoverable`, `Cwd` | `ErrorHandling`, `RetryCount`, `UserNotification`, `SuppressOutput` | Custom error recovery |

### JS-Only Hooks (not in .NET SDK yet)

| Hook | Purpose |
|------|---------|
| **AgentStop** | Fires when agent naturally stops; can return `decision: "block"` to force continuation |
| **SubagentStop** | Fires when subagent completes; can block to force another turn |

---

## 4. Events (76 types)

Register via `session.On(evt => ...)` or `SessionConfig.OnEvent`.

### Assistant Events
| Event | Purpose |
|-------|---------|
| `AssistantTurnStartEvent` | Turn begins |
| `AssistantTurnEndEvent` | Turn ends (sub-turn boundary) |
| `AssistantMessageEvent` | Complete message with tool requests |
| `AssistantMessageDeltaEvent` | Streaming content chunk |
| `AssistantStreamingDeltaEvent` | Low-level streaming delta |
| `AssistantIntentEvent` | Intent/plan update |
| `AssistantUsageEvent` | Token usage (InputTokens, OutputTokens as Double?, ReasoningEffort) |
| `AssistantReasoningEvent` | Complete reasoning content |
| `AssistantReasoningDeltaEvent` | Streaming reasoning chunk |

### Session Lifecycle Events
| Event | Purpose |
|-------|---------|
| `SessionStartEvent` | Session created (includes `ReasoningEffort`, `Model`, `Context`) |
| `SessionResumeEvent` | Session resumed (includes `ReasoningEffort`, `Context`) |
| `SessionIdleEvent` | Turn complete (may include `BackgroundTasks` for deferred completion) |
| `SessionErrorEvent` | Session error |
| `SessionShutdownEvent` | Session shutting down (includes `CodeChanges`) |
| `SessionWarningEvent` | Non-fatal warning |
| `SessionInfoEvent` | Informational |
| `SessionModelChangeEvent` | Model changed (includes previous/new `ReasoningEffort`) |
| `SessionModeChangedEvent` | Mode changed (interactive/plan/autopilot) |
| `SessionPlanChangedEvent` | Plan file updated (operation: create/update/delete) |
| `SessionTitleChangedEvent` | Auto-rename |
| `SessionTruncationEvent` | Context truncated |
| `SessionContextChangedEvent` | Context changed |
| `SessionHandoffEvent` | Delegated to GitHub (CCA) |
| `SessionSnapshotRewindEvent` | Undo/rewind |
| `SessionBackgroundTasksChangedEvent` | Background task status updated |
| `SessionWorkspaceFileChangedEvent` | Workspace file changed |
| `SessionRemoteSteerableChangedEvent` | Remote steering capability changed (new in 0.2.1) |
| `CapabilitiesChangedEvent` | Session capabilities changed (new in 0.2.1) |
| `SessionCustomAgentsUpdatedEvent` | Custom agents updated (new in 0.2.1) |

### Tool Events
| Event | Purpose |
|-------|---------|
| `ToolExecutionStartEvent` | Tool execution begins |
| `ToolExecutionProgressEvent` | Tool execution progress |
| `ToolExecutionPartialResultEvent` | Partial tool result |
| `ToolExecutionCompleteEvent` | Tool execution finished (includes structured Result with text, images, audio, resources) |
| `ToolUserRequestedEvent` | User requested a tool action |
| `SessionToolsUpdatedEvent` | Available tools changed |

### Subagent Events
| Event | Purpose |
|-------|---------|
| `SubagentStartedEvent` | Subagent began (includes `AgentName`, `AgentDisplayName`, `ToolCallId`) |
| `SubagentCompletedEvent` | Subagent finished |
| `SubagentFailedEvent` | Subagent failed (includes `Error`) |
| `SubagentSelectedEvent` | Subagent selected (includes `Tools`) |
| `SubagentDeselectedEvent` | Subagent deselected |

### Skill & Extension Events
| Event | Purpose |
|-------|---------|
| `SkillInvokedEvent` | Skill was invoked |
| `SessionSkillsLoadedEvent` | Skills loaded |
| `SessionExtensionsLoadedEvent` | Extensions loaded |
| `SessionMcpServersLoadedEvent` | MCP servers loaded |
| `SessionMcpServerStatusChangedEvent` | MCP server status changed |

### Permission & Input Events
| Event | Purpose |
|-------|---------|
| `PermissionRequestedEvent` | Permission needed (variants: Shell, Read, Write, Hook, Mcp, Memory, Url, CustomTool) |
| `PermissionCompletedEvent` | Permission resolved |
| `ElicitationRequestedEvent` | Structured input requested (includes `RequestedSchema`) |
| `ElicitationCompletedEvent` | Structured input completed |
| `UserInputRequestedEvent` | Free-text input requested |
| `UserInputCompletedEvent` | Free-text input completed |
| `UserMessageEvent` | User sent a message (includes `AgentMode`, `Attachments`) |

### Command Events
| Event | Purpose |
|-------|---------|
| `CommandQueuedEvent` | Slash command queued |
| `CommandExecuteEvent` | Slash command executing |
| `CommandCompletedEvent` | Slash command completed |
| `CommandsChangedEvent` | Available commands changed |

### Hook Events
| Event | Purpose |
|-------|---------|
| `HookStartEvent` | Hook invocation began (includes `HookType`, `Input`) |
| `HookEndEvent` | Hook invocation completed (includes `Output`, `Success`, `Error`) |

### Other Events
| Event | Purpose |
|-------|---------|
| `SystemMessageEvent` | System prompt injection |
| `SystemNotificationEvent` | System notification (AgentCompleted, AgentIdle, ShellCompleted, ShellDetachedCompleted) |
| `ExitPlanModeRequestedEvent` | Plan mode exit requested (includes `PlanContent`) |
| `ExitPlanModeCompletedEvent` | Plan mode exit completed |
| `ExternalToolRequestedEvent` | External tool execution requested |
| `ExternalToolCompletedEvent` | External tool execution completed |
| `McpOauthRequiredEvent` | MCP OAuth required |
| `McpOauthCompletedEvent` | MCP OAuth completed |
| `SessionCompactionStartEvent` | Context compaction started |
| `SessionCompactionCompleteEvent` | Context compaction completed (includes `TokensUsed`) |
| `PendingMessagesModifiedEvent` | Pending messages changed |
| `SamplingRequestedEvent` | MCP sampling requested (new in 0.2.1) |
| `SamplingCompletedEvent` | MCP sampling completed (new in 0.2.1) |
| `AbortEvent` | Turn aborted |

---

## 5. Model Information

### ModelInfo (from `ListModelsAsync`)

| Property | Purpose |
|----------|---------|
| `Id` | Model slug (e.g., "claude-opus-4.6-1m") — use this, NOT `Name` |
| `Name` | Display name (e.g., "Claude Opus 4.6 (1M Context)") — for UI only |
| `SupportedReasoningEfforts` | List of supported levels |
| `DefaultReasoningEffort` | Default level for this model |
| `Supports.ReasoningEffort` | Bool: does this model support reasoning effort? |
| `Billing` | Token pricing info |
| `Limits` | Token limits, vision limits |
| `Policy` | Usage policy |

---

## 6. Server-Level APIs (`CopilotClient.ServerRpc`)

| API | Methods |
|-----|---------|
| `ServerRpc.Models` | `ListAsync()` — list all available models |
| `ServerRpc.Account` | `GetQuotaAsync()` — quota/usage info |
| `ServerRpc.Tools` | `HandlePendingToolCallAsync()` — server-scoped tool handling |
| `ServerRpc.Mcp` | Server-scoped MCP management (new in 0.2.1) |
| `ServerRpc.SessionFs` | `SetProviderAsync()` — filesystem provider (new in 0.2.1) |

---

## 7. CLI Slash Commands (42 total)

Available in interactive mode via the Copilot CLI:

| Category | Commands |
|----------|----------|
| **Agent/Environment** | `/init`, `/agent`, `/skills`, `/mcp`, `/plugin` |
| **Models/Subagents** | `/model`, `/delegate`, `/fleet`, `/tasks` |
| **Code** | `/ide`, `/diff`, `/pr`, `/review`, `/lsp`, `/terminal-setup` |
| **Permissions** | `/allow-all`, `/add-dir`, `/list-dirs`, `/cwd`, `/reset-allowed-tools` |
| **Session** | `/resume`, `/rename`, `/context`, `/usage`, `/session`, `/compact`, `/share`, `/copy`, `/rewind` |
| **Help** | `/help`, `/changelog`, `/feedback`, `/theme`, `/update`, `/version`, `/experimental`, `/clear`, `/instructions`, `/streamer-mode` |
| **Other** | `/exit`, `/login`, `/logout`, `/new`, `/plan`, `/research`, `/restart`, `/undo`, `/user` |

---

## 8. CLI Built-in Agent Definitions

Three built-in agents (in `definitions/*.agent.yaml`):

| Agent | Model | Tools | Purpose |
|-------|-------|-------|---------|
| **explore** | claude-haiku-4.5 | grep, glob, view, lsp | Fast codebase exploration, <300 words, parallel tool calls |
| **task** | claude-haiku-4.5 | all | Execute commands; brief on success, verbose on failure |
| **code-review** | claude-sonnet-4.5 | all (read-only) | High-signal review; NEVER comments on style/formatting |

---

## 9. SDK Version History

| Version | Key Additions |
|---------|---------------|
| **0.2.1** | `CommandDefinition`/`CommandHandler` (custom slash commands), `ElicitationHandler`/`ElicitationContext` (structured input callbacks), `ServerMcpApi`, `ServerSessionFsApi`, `CapabilitiesChangedEvent`, `SamplingRequestedEvent`, `SessionRemoteSteerableChangedEvent`, `SessionCustomAgentsUpdatedEvent`, `ISessionUiApi`, `InputOptions` |
| **0.2.0** | Hooks (PreToolUse, PostToolUse, UserPromptSubmitted, SessionStart, SessionEnd, ErrorOccurred), Plan API, Fleet API, Agent API, Skills API, MCP API, Compaction API, Workspace API, Elicitation API, ReasoningEffort, InfiniteSessionConfig, CustomAgentConfig, SystemMessageConfig with SectionOverride |

> **On SDK update:** Re-evaluate this document. Check for new RPC APIs, events, hooks, and SessionConfig properties. Update the migration matrices in `processing-state-safety` and `multi-agent-orchestration` skills.
