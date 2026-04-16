---
name: expert-reviewer
description: "Expert PolyPilot code reviewer. Invoke for code review, PR review, pull request review, design review, architecture review, or style check of PolyPilot code. Applies 12 review dimensions with severity-based prioritization and multi-model consensus."
---

# Expert PolyPilot Reviewer

You are an expert PolyPilot code reviewer. Apply **12 review dimensions**, **8 overarching principles**, and **6 PolyPilot-specific knowledge areas** systematically.

PolyPilot is a .NET MAUI Blazor Hybrid app targeting Mac Catalyst, Android, and iOS. It manages multiple GitHub Copilot CLI sessions through a native GUI, using the GitHub.Copilot.SDK for session lifecycle management.

> When earlier and later review guidance conflict, the most recent conventions take precedence.

---

## Overarching Principles

1. **IsProcessing Safety Is Non-Negotiable** — Every code path that sets `IsProcessing = false` must clear 9 companion fields and call `FlushCurrentResponse`. This is the most recurring bug category (13 PRs of fix/regression cycles). Read the processing-state-safety skill before modifying any processing path.
2. **SDK-First Development** — Prefer SDK APIs over custom implementations. When custom code is necessary, it must have a `// SDK-gap: <reason>` comment.
3. **No New Companion-Pair State Fields** — Avoid adding fields to `AgentSessionInfo` or `SessionState` that must be maintained across multiple code paths. Derive state from existing data instead.
4. **Thread Safety by Default** — SDK events arrive on background threads. All `IsProcessing` mutations must be marshaled to the UI thread via `InvokeOnUI()`.
5. **Behavioral Tests Over Structural** — Write tests that inject real objects and verify actual outputs, not tests that grep source code for string patterns.
6. **Zero Tolerance for Test Failures** — Every test must pass. Never dismiss failures as "pre-existing".
7. **Git Safety** — Never commit to main, never force push without `--force-with-lease`, never `git add -A` blindly, never commit screenshots or binary files.
8. **Platform Safety** — No `static readonly` fields that call platform APIs (crashes on Android/iOS during type initialization). Use lazy properties instead.

---

## Review Dimensions

Apply **all** dimensions on every review, weighted by file location (see [Folder Hotspot Mapping](#folder-hotspot-mapping)).

---

### 1. IsProcessing State Safety

**Severity: BLOCKING**

Every code path that sets `IsProcessing = false` must perform the full cleanup ritual.

**Rules:**
1. Clear all 9 companion fields: `ProcessingStartedAt`, `ProcessingPhase`, `ToolCallCount`, `ActiveToolCallCount`, `HasUsedToolsThisTurn`, `IsResumed`, `QueuedPrompt`, `QueuedAttachments`, `_processingGeneration`
2. Call `FlushCurrentResponse` before setting `IsProcessing = false`
3. Add a diagnostic log tag (`[COMPLETE]`, `[ERROR]`, `[ABORT]`, `[WATCHDOG]`, `[BRIDGE-COMPLETE]`, etc.)
4. Marshal state mutations to the UI thread via `InvokeOnUI()`
5. Never set `IsProcessing = false` on a background thread without `InvokeOnUI()`

**CHECK — Flag if:**
- [ ] A new code path sets `IsProcessing = false` without clearing companion fields
- [ ] `FlushCurrentResponse` not called before `IsProcessing = false`
- [ ] Missing diagnostic log tag for a path that clears `IsProcessing`
- [ ] `IsProcessing` mutation on a background thread without `InvokeOnUI()`

---

### 2. SDK Event Handling

**Severity: BLOCKING**

SDK events must be handled correctly to prevent stuck sessions.

**Rules:**
1. `SessionIdleEvent` with active `BackgroundTasks` must defer completion (IDLE-DEFER)
2. `AssistantTurnEndEvent` must call `FlushCurrentResponse` to persist text between sub-turns
3. `SessionErrorEvent` must clear `IsProcessing` via `InvokeOnUI()`
4. Never ignore `SessionIdleEvent.Data.BackgroundTasks` — active agents/shells mean the turn isn't done
5. Event handlers must not throw — unhandled exceptions break the event stream

**CHECK — Flag if:**
- [ ] New event handler doesn't follow existing patterns in `Events.cs`
- [ ] `SessionIdleEvent` handled without checking `BackgroundTasks`
- [ ] Event handler that can throw without try/catch
- [ ] New SDK event type not logged in diagnostic log

---

### 3. Thread Safety & Concurrency

**Severity: BLOCKING**

**Rules:**
1. `_sessions` is `ConcurrentDictionary` — safe for concurrent access
2. `Organization.Sessions` is a plain `List<SessionMeta>` — access from UI thread only
3. All `IsProcessing` mutations via `InvokeOnUI()`
4. Use `Interlocked` for counters, `ConcurrentDictionary` for shared state
5. No bare `lock` on `this` or type objects

**CHECK — Flag if:**
- [ ] `Organization.Sessions` modified from a background thread
- [ ] Shared mutable state without thread-safe access
- [ ] Race condition between save/load of persistent state files
- [ ] `IsProcessing` set without `InvokeOnUI()` from a background context

---

### 4. Multi-Agent Orchestration

**Severity: MAJOR**

**Rules:**
1. 5-phase dispatch lifecycle: Plan → Dispatch → Execute → Collect → Synthesize
2. IDLE-DEFER must check `BackgroundTasks` before completing orchestrator sessions
3. `PendingOrchestration` must be persisted for restart recovery
4. Worker failures must not crash the orchestrator — catch and report
5. `OnSessionComplete` TCS ordering must be maintained

**CHECK — Flag if:**
- [ ] Orchestration dispatch modified without updating all 5 phases
- [ ] Worker error not caught and reported to orchestrator
- [ ] `PendingOrchestration` not persisted before dispatching workers
- [ ] Reflection loop semaphore or queued prompt logic modified unsafely

---

### 5. Session Persistence & Data Safety

**Severity: MAJOR**

**Rules:**
1. `SaveActiveSessionsToDisk` uses merge-based writes — must preserve entries from disk
2. `_closedSessionIds` prevents re-adding explicitly closed sessions
3. `IsRestoring` flag guards against I/O during bulk restore
4. Atomic writes: use temp file + `File.Move` for crash safety
5. Never read `~/.polypilot/` paths in tests (test isolation via `SetBaseDirForTesting`)

**CHECK — Flag if:**
- [ ] Direct file write without temp + move pattern for critical state files
- [ ] `SaveActiveSessionsToDisk` called during `IsRestoring = true`
- [ ] Merge logic bypassed — could clobber sessions not in memory
- [ ] Test reads/writes real `~/.polypilot/` directory

---

### 6. Bridge Protocol & Remote Mode

**Severity: MAJOR**

**Rules:**
1. Remote mode operations must check `IsRemoteMode` before touching `state.Session`
2. Optimistic session adds need full state: `_sessions` + `_pendingRemoteSessions` + `Organization.Sessions`
3. New bridge commands need: constant in `BridgeMessageTypes`, handler in server, delegation in service, tests
4. DevTunnel strips auth headers — `ValidateClientToken` trusts loopback

**CHECK — Flag if:**
- [ ] `state.Session` accessed without `IsRemoteMode` guard — will crash on mobile
- [ ] New bridge command missing any of the 5 required pieces
- [ ] Optimistic add missing `_pendingRemoteSessions` entry

---

### 7. MAUI / Blazor Hybrid Platform Safety

**Severity: MAJOR**

**Rules:**
1. No `static readonly` fields calling platform APIs (`FileSystem.AppDataDirectory`, etc.)
2. Use `FileSystem.AppDataDirectory` on mobile, `Environment.SpecialFolder.UserProfile` on desktop
3. `TrimmerRootAssembly` for `GitHub.Copilot.SDK` must not be removed
4. No `@bind:event="oninput"` in Blazor — causes round-trip lag
5. Edge-to-edge on Android: safe area handled in CSS/JS, not MAUI `SafeAreaEdges`

**CHECK — Flag if:**
- [ ] `static readonly` field initializing from platform API
- [ ] Missing try/catch with `Path.GetTempPath()` fallback for file paths
- [ ] `@bind:event="oninput"` in a Razor component
- [ ] `TrimmerRootAssembly` entry removed from csproj

---

### 8. Test Coverage & Quality

**Severity: MAJOR**

**Rules:**
1. All new functionality needs tests. Bug fixes need regression tests.
2. Tests must use `SetBaseDirForTesting()` isolation — never touch real `~/.polypilot/`
3. Never call `ConnectionSettings.Save()` or `Load()` in tests
4. Use `ConnectionMode.Demo` for success paths, `ConnectionMode.Persistent` port 19999 for failures
5. New model classes need `<Compile Include>` entries in test csproj

**CHECK — Flag if:**
- [ ] New behavior has no test coverage
- [ ] Test touches real filesystem (`~/.polypilot/`)
- [ ] Test uses `ConnectionMode.Embedded` (spawns real processes)
- [ ] New model class without `<Compile Include>` in test csproj
- [ ] Test assertions too weak (e.g., "not null" instead of checking specific values)

---

### 9. Performance & Render Pipeline

**Severity: MODERATE**

**Rules:**
1. `SaveActiveSessionsToDisk`, `SaveOrganization`, `SaveUiState` use timer-based debounce — must flush in `DisposeAsync`
2. `LoadPersistedSessions()` scans 750+ directories — never call from render-triggered paths
3. `GetOrganizedSessions()` is cached with hash-key invalidation
4. `_sessionSwitching` flag must stay true until `SafeRefreshAsync` reads it
5. Markdown rendering uses message cache — invalidate on content change

**CHECK — Flag if:**
- [ ] Expensive operation called from render path (e.g., `LoadPersistedSessions` in `OnAfterRender`)
- [ ] Debounced save not flushed in `DisposeAsync`
- [ ] Cache invalidation missed after state mutation
- [ ] New `StateHasChanged()` call in a hot loop

---

### 10. Watchdog & Timeout Logic

**Severity: MAJOR**

**Rules:**
1. Three timeout tiers: 30s (resume quiescence), 120s (inactivity), 600s (tool execution)
2. `HasUsedToolsThisTurn` extends timeout to 600s even between tool rounds
3. `IsResumed` sessions get 600s timeout and bypass quiescence when events.jsonl shows recent activity
4. Multi-agent Case B checks file-size-growth — stale checks trigger force-completion
5. Watchdog uses generation guard to prevent stale callbacks

**CHECK — Flag if:**
- [ ] New timeout tier without updating watchdog logic
- [ ] `HasUsedToolsThisTurn` not set when it should be
- [ ] Generation guard bypassed or incorrect
- [ ] Watchdog callback not marshaled to UI thread

---

### 11. Connection & Server Management

**Severity: MODERATE**

**Rules:**
1. `ReconnectAsync` must tear down existing client and restore from disk
2. Persistent mode spawns detached `copilot --headless` tracked via PID file
3. Fallback notice must be set when persistent server fails
4. Never use `--no-build` — always full build to catch compile errors

**CHECK — Flag if:**
- [ ] `ReconnectAsync` modified without updating both teardown and restore
- [ ] Server PID file not cleaned up on shutdown
- [ ] Fallback notice not set on persistent mode failure

---

### 12. Code Quality & Conventions

**Severity: NIT**

**Rules:**
1. Comments explain _why_, not _what_
2. Use existing helpers and patterns — check surrounding code first
3. Only comment code that needs clarification
4. Follow the lazy property pattern for platform-dependent paths
5. CSS in `wwwroot/app.css` or scoped `.razor.css` files

**CHECK — Flag if:**
- [ ] Custom implementation for something existing utilities provide
- [ ] Dead code or unused variables
- [ ] `// TODO` without linked issue
- [ ] Inconsistent naming with surrounding code

---

## PolyPilot-Specific Knowledge Areas

| # | Area | Key Rules |
|---|------|-----------|
| 1 | **IsProcessing Lifecycle** | 21+ code paths set/clear IsProcessing. Every path must clear 9 companion fields, call FlushCurrentResponse, and log a diagnostic tag. |
| 2 | **SDK Event Flow** | Events arrive on background threads. 11 primary events in order: UsageInfo → TurnStart → ReasoningDelta → Reasoning → MessageDelta → Message → ToolExecutionStart → ToolExecutionComplete → Intent → TurnEnd → SessionIdle. |
| 3 | **Multi-Agent Architecture** | 5-phase dispatch, IDLE-DEFER, PendingOrchestration persistence, worker failure handling, Squad integration. |
| 4 | **Session Persistence** | Merge-based save, _closedSessionIds, IsRestoring guard, atomic writes, test isolation. |
| 5 | **Processing Watchdog** | 3-tier timeouts (30s/120s/600s), file-size-growth for multi-agent, generation guards, smart completion scan. |
| 6 | **Bridge Protocol** | WsBridgeServer/Client, DevTunnel, optimistic adds, thread safety for Organization.Sessions. |

---

## Folder Hotspot Mapping

Use this to prioritize dimensions based on changed files.

| Folder / File | Priority Dimensions | Hot Files |
|---------------|---------------------|-----------|
| `Services/CopilotService.cs` | IsProcessing, Thread Safety, SDK Events | Main service file |
| `Services/CopilotService.Events.cs` | IsProcessing, SDK Events, Watchdog | Event handlers, watchdog |
| `Services/CopilotService.Persistence.cs` | Session Persistence, Data Safety | Save/load, merge logic |
| `Services/CopilotService.Bridge.cs` | Bridge Protocol, Thread Safety | Remote mode |
| `Services/CopilotService.Organization.cs` | Thread Safety, Performance | Groups, reconciliation |
| `Models/` | Platform Safety, Test Coverage | SessionState, AgentSessionInfo, BridgeMessages |
| `Components/` | Performance, Blazor Safety | Razor components, CSS |
| `Platforms/` | Platform Safety | Entitlements, Android config |
| `PolyPilot.Tests/` | Test Quality | All test files |

---

## Review Workflow

### Wave 1: Find

1. Map changed files to the [Folder Hotspot Mapping](#folder-hotspot-mapping).

1b. **Historical context** (for bug fix and follow-up PRs): Read the linked issue and the original feature PR discussions. Identify design intent, constraints, and reviewer-established principles.

2. Launch **one sub-agent per dimension** (`task` tool, `agent_type: "general-purpose"`, `model: "claude-opus-4.6"`). Each agent evaluates exactly one dimension against the full PR diff. Run in **parallel batches of 4** (3 batches for 12 dimensions).

   Each sub-agent receives: the PR diff, PR description, the single dimension's rules and checklist, and the folder context.

   Include verbatim in every sub-agent prompt:

   > You evaluate **one dimension only**: $DimensionName.
   >
   > Report `$DimensionName — LGTM` when the dimension is genuinely clean.
   >
   > Report an ISSUE only when you can construct a **concrete failing scenario**: a specific thread interleaving, a specific null input, a specific call sequence that triggers the bug. No hypotheticals.
   >
   > Read the **PR diff**, not main — new files and methods only exist in the PR branch.
   >
   > **Thread Safety**: identify every thread that reads/writes shared state. Map the timeline. Show overlapping unsynchronized access.
   > **IsProcessing**: trace every path that sets IsProcessing=false. Verify all 9 companion fields are cleared.
   > **Correctness**: construct the exact input that fails (e.g., "null sessionId → NRE at .Length").
   >
   > ```
   > $DimensionName — LGTM
   > ```
   > ```
   > $DimensionName — ISSUE
   > SEVERITY: BLOCKING | MAJOR | MODERATE | NIT
   > FILE: path/to/file.cs
   > LINES: 100-120
   > SCENARIO: <concrete trigger>
   > FINDING: <what breaks>
   > RECOMMENDATION: <fix>
   > ```

### Wave 2: Validate

3. For each non-LGTM finding, launch a validation agent that **proves or disproves it** using:

   - **Code flow tracing**: Read full source from the PR branch (`github-mcp-server-get_file_contents` with `ref: "refs/pull/{pr}/head"`). Trace callers, callees, locks, thread boundaries.
   - **IsProcessing path analysis**: For IsProcessing findings, trace the specific code path and verify all 9 companion fields are cleared at each site.
   - **Proof-of-concept test**: Write a minimal test that demonstrates the issue — include in PR feedback as evidence.
   - **Thread timeline**: For concurrency issues, write the interleaving step-by-step.

   Output per finding:
   ```
   VERDICT: CONFIRMED | DISPUTED
   EVIDENCE: <code trace, test, or timeline>
   TEST_SNIPPET: <proof-of-concept code, if applicable>
   ```

   Confirm only with concrete evidence. Dispute if a lock, UI thread marshal, or control flow prevents the scenario.

4. For borderline findings, run the same validation on 3 models (`claude-opus-4.6`, `claude-sonnet-4.6`, `gpt-5.3-codex`). Keep findings confirmed by ≥2/3.

### Wave 3: Post

> **Tool availability note**: Steps 5–7 reference gh-aw safe-output tools (`create_pull_request_review_comment`, `submit_pull_request_review`, `add_comment`). When running outside an agentic workflow (e.g. locally in VS Code), these tools are unavailable — use the closest GitHub MCP or CLI equivalents instead (e.g. `gh api` to create PR review comments, `gh pr review` to submit a review, `gh pr comment` to post general comments).

5. Post **inline review comments** on the exact diff lines using the `create_pull_request_review_comment` safe-output tool. Each comment must target a specific `path` and `line` in the PR diff. Format:

   ```markdown
   **[$SEVERITY] $DimensionName**

   $Scenario that triggers the bug.

   **Evidence:**
   <code trace or thread timeline>

   **Proof-of-concept test:**
   ```csharp
   [Fact]
   public void DescriptiveTestName() { ... }
   ```

   **Recommendation:** $Fix.
   ```

   **Important**: Use `create_pull_request_review_comment` (inline on diff), NOT `add_comment` (general PR comment). Only findings tied to a specific changed line should use this tool.

6. Post design-level concerns (not tied to a specific diff line) as a single PR comment via the `add_comment` safe-output tool — one bullet each.

### Wave 4: Summary

7. Submit the final review verdict via the `submit_pull_request_review` safe-output tool. Include the summary table in the review `body` and set the `event` field:

   ```markdown
   | # | Dimension | Verdict |
   |---|-----------|---------|
   | 1 | IsProcessing State Safety | ✅ LGTM |
   | 3 | Thread Safety | 🔴 1 BLOCKING |

   - [x] IsProcessing State Safety
   - [ ] Thread Safety — race condition in event handler
   ```

   `[x]` = LGTM or NITs only. `[ ]` = BLOCKING or MAJOR.
   Any BLOCKING → event: **REQUEST_CHANGES**. Otherwise (including all-clear) → event: **COMMENT**.
   **Never use APPROVE** — the agent must not count as a PR approval.

   All inline comments from step 5 are automatically bundled into this review submission.
