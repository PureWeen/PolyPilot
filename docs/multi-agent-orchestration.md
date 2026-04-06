# Multi-Agent Orchestration — Architecture Spec

> **Read this before modifying orchestration, sentinel protocol, session reconciliation, or reflection loops.**

## Overview

PolyPilot's multi-agent system lets you create a **team of AI sessions** that work together. Each session can use a different AI model. An orchestrator coordinates work dispatch, response collection, and quality evaluation.

### Key Files

| File | Purpose |
|------|---------|
| `PolyPilot.Core/Services/CopilotService.Organization.cs` | Orchestration engine (dispatch, reflection loop, reconciliation, group deletion) |
| `PolyPilot.Core/Models/SessionOrganization.cs` | `SessionGroup`, `SessionMeta`, `MultiAgentMode`, `MultiAgentRole` |
| `PolyPilot.Core/Models/ReflectionCycle.cs` | Reflection state, stall detection, sentinel parsing, evaluator prompts |
| `PolyPilot.Core/Models/ModelCapabilities.cs` | `GroupPreset`, `UserPresets` (three-tier merge), built-in presets |
| `PolyPilot.Core/Models/SquadDiscovery.cs` | Squad directory parser (`.squad/` → `GroupPreset`) |
| `PolyPilot.Core/Models/SquadWriter.cs` | Squad directory writer (`GroupPreset` → `.squad/`) |
| `PolyPilot.Core/Services/CopilotService.Events.cs` | TCS completion (IsProcessing → TrySetResult ordering) |
| `PolyPilot/Components/Layout/SessionSidebar.razor` | Preset picker UI (sectioned: From Repo / Built-in / My Presets) |
| `PolyPilot.Tests/MultiAgentRegressionTests.cs` | 37 regression tests covering all known bugs |
| `PolyPilot.Tests/SessionOrganizationTests.cs` | 15 grouping stability tests |
| `PolyPilot.Tests/SquadDiscoveryTests.cs` | 22 Squad discovery tests |
| `PolyPilot.Tests/SquadWriterTests.cs` | 15 Squad write-back tests |
| `PolyPilot.Tests/Scenarios/multi-agent-scenarios.json` | Executable CDP test scenarios |

---

## Orchestration Modes

### Broadcast
Same prompt sent to **all sessions simultaneously**. No orchestrator. Each session responds independently. Use for: comparing model outputs, getting diverse perspectives.

### Sequential
Prompt sent to sessions **one at a time**. Each session sees previous responses. Use for: chain-of-thought across models, iterative refinement.

### Orchestrator (Single-Pass)
One orchestrator session plans and delegates:
1. **Plan** — Orchestrator receives user prompt + list of available workers with their models
2. **Dispatch** — Orchestrator emits `@worker:name task` assignments, parsed by `ParseTaskAssignments`
3. **Collect** — Workers execute in parallel (`Task.WhenAll`), each with 10-min timeout
4. **Synthesize** — Worker results sent back to orchestrator for final synthesis

No iteration. One pass through the loop.

### OrchestratorReflect (Iterative — The Main Mode)
Same as Orchestrator but **loops** until the goal is met, quality stalls, or max iterations reached. This is the primary mode for serious multi-agent work.

---

## UI Behavior by Mode

### Input Visibility

There are two kinds of text inputs in multi-agent groups:

1. **Group-level inputs** — "Send All" broadcast bars that dispatch a prompt to all sessions at once (or to the orchestrator). These appear in the group header area.
2. **Per-session inputs** — Individual chat inputs on each session card/expanded view. These let users talk to any individual session directly.

**Rule: Per-session inputs are ALWAYS visible.** Users must be able to chat with individual sessions regardless of orchestration mode. Only group-level inputs change visibility based on the mode.

#### Group-level input visibility by mode

| Mode | Group Input | Notes |
|------|-------------|-------|
| **Broadcast** | ✅ Visible | Textarea + "📡 Send All" button. Sends same prompt to all sessions. |
| **Sequential** | ✅ Visible | Textarea + "📡 Send All" button. Sends prompt to sessions one at a time. |
| **Orchestrator** | ❌ Hidden | User types in the orchestrator session's own input instead. |
| **OrchestratorReflect** | ❌ Hidden | Iterations spinner shown instead. User types in orchestrator session's input. |

#### Input locations (5 total)

| # | Location | File | CSS class | Type | Visibility |
|---|----------|------|-----------|------|------------|
| 1 | Dashboard expanded toolbar | `Dashboard.razor` | `ma-expanded-toolbar-input` | Group | Hidden for Orchestrator/Reflect |
| 2 | Dashboard grid header | `Dashboard.razor` | `ma-broadcast-input` | Group | Hidden for Orchestrator (Reflect shows iterations only) |
| 3 | Sidebar group controls | `SessionSidebar.razor` | `sidebar-ma-input-bar` | Group | Hidden for Orchestrator/Reflect |
| 4 | Grid card | `SessionCard.razor` | `card-input` | Per-session | **Always visible** |
| 5 | Expanded chat view | `ExpandedSessionView.razor` | `input-area` | Per-session | **Always visible** |

### Other per-mode UI elements

| Element | Broadcast | Sequential | Orchestrator | Reflect |
|---------|-----------|------------|--------------|---------|
| Mode dropdown | ✅ | ✅ | ✅ | ✅ |
| Group "Send All" input | ✅ | ✅ | ❌ | ❌ |
| Max iterations spinner | ❌ | ❌ | ❌ | ✅ |
| Per-session inputs | ✅ | ✅ | ✅ | ✅ |
| Phase indicator | ✅ | ✅ | ✅ | ✅ |
| Reflection progress | ❌ | ❌ | ❌ | ✅ |

### Message routing in Orchestrator/Reflect modes

When the user types in the **orchestrator session's input** (per-session input #4 or #5 above), the message is routed through `SendViaOrchestratorAsync` / `SendViaOrchestratorReflectAsync` — it doesn't just go to the orchestrator as a plain chat message. This routing is handled in `Dashboard.razor`'s send handler by checking if the session belongs to a multi-agent group in Orchestrator/Reflect mode. See `SendPromptToSession` → group mode check → `SendToMultiAgentGroupAsync`.

---

## OrchestratorReflect — Detailed Loop

### Participants
- **1 Orchestrator** — Plans, delegates, synthesizes. Set via `SessionMeta.Role = Orchestrator`
- **N Workers** — Execute assigned tasks in parallel. Each can use a different model (`SessionMeta.PreferredModel`) and have a **system prompt** (`SessionMeta.SystemPrompt`) that defines their specialization
- **1 Evaluator** (optional) — Independent quality judge on a separate model (`ReflectionCycle.EvaluatorSessionName`)

### The Loop (runs in `SendViaOrchestratorReflectAsync`)

```
while (IsActive && !IsPaused && CurrentIteration < MaxIterations):
    CurrentIteration++

    Phase 1: PLAN
    ├── Iteration 1: BuildOrchestratorPlanningPrompt(userPrompt, workerNames)
    └── Iteration 2+: BuildReplanPrompt(lastEvaluation, workerNames, userPrompt)
    
    Orchestrator responds with task assignments:
        @worker:worker-1 Implement the auth module
        @worker:worker-2 Write tests for the auth module
    
    ParseTaskAssignments extracts these → List<TaskAssignment>
    If no assignments AND iteration == 1 → error (retry up to 3 times)
    If no assignments AND iteration > 1 → orchestrator decided goal is met → break

    Phase 2: DISPATCH
    └── Send each assignment to its worker in parallel (Task.WhenAll)
        Each worker gets: "You are a worker agent..." + original prompt + assigned task

    Phase 3: COLLECT
    └── Wait for all workers (SendPromptAndWaitAsync, 10-min timeout per worker)
        Returns List<WorkerResult> (response, success, duration)

    Phase 4: EVALUATE (two paths)
    ├── WITH dedicated evaluator:
    │   ├── Orchestrator synthesizes worker results
    │   ├── Evaluator scores quality (0.0–1.0) with rationale
    │   ├── Score ≥ 0.9 or [[GROUP_REFLECT_COMPLETE]] → goal met → break
    │   └── RecordEvaluation tracks trend (Improving/Stable/Degrading)
    │
    └── SELF-evaluation (no evaluator):
        ├── Orchestrator gets combined synthesis + eval prompt
        ├── [[GROUP_REFLECT_COMPLETE]] sentinel → goal met → break
        └── [[NEEDS_ITERATION]] sentinel → scored as 0.4, continue

    Phase 5: STALL DETECTION
    ├── CheckStall() compares synthesis response to previous
    ├── Jaccard token similarity > 0.9 → stall detected
    ├── 1st consecutive stall: warn but continue
    └── 2nd consecutive stall: IsStalled = true → break

    Phase 6: AUTO-ADJUST
    └── AutoAdjustFromFeedback analyzes worker results, may suggest model changes

    SaveOrganization() after each iteration
```

### Exit Conditions (whichever hits first)

| Condition | How Detected | State |
|-----------|-------------|-------|
| ✅ Goal met | Evaluator score ≥ 0.9 or `[[GROUP_REFLECT_COMPLETE]]` sentinel | `GoalMet = true` |
| ⏱️ Max iterations | `CurrentIteration >= MaxIterations` | `IsCancelled = true` |
| ⚠️ Stalled | 2 consecutive responses with >90% Jaccard similarity | `IsStalled = true, IsCancelled = true` |
| ⚠️ Error budget | 3 consecutive errors within a single iteration | `IsStalled = true, IsCancelled = true` |
| 🛑 Cancelled | CancellationToken triggered or user `StopGroupReflection` | `IsCancelled = true` |
| ⏸️ Paused | User set `IsPaused = true` | Loop condition fails |

**IsCancelled invariant:** Every non-success exit MUST set `IsCancelled = true`. This allows `BuildCompletionSummary()` to distinguish successful completion from abnormal termination. `GoalMet = true` paths must NOT set `IsCancelled`.

---

## Invariants — What Breaks If You Violate These

### 1. TCS Ordering: `IsProcessing = false` BEFORE `TrySetResult`

**Where:** `CopilotService.Events.cs` → `CompleteResponse()` and `SessionErrorEvent` handler

**The rule:** When completing a response via the TaskCompletionSource (TCS), you MUST set `IsProcessing = false` BEFORE calling `TrySetResult()` or `TrySetException()`.

**Why:** In reflection loops, the TCS continuation runs **synchronously**. The next `SendPromptAsync` in the loop checks `IsProcessing` — if it's still `true`, it throws "already processing". This killed reflection loops after 1 iteration.

```csharp
// ✅ CORRECT ORDER
state.IsProcessing = false;           // 1. Clear flag first
state.ResponseCompletion?.TrySetResult(response);  // 2. Then signal completion

// ❌ WRONG — breaks reflection loops
state.ResponseCompletion?.TrySetResult(response);  // Continuation runs NOW
state.IsProcessing = false;           // Too late — next SendPromptAsync already threw
```

**Same rule applies to error paths** (`TrySetException`).

### 2. Reconciliation Must Not Scatter Multi-Agent Sessions

**Where:** `CopilotService.Organization.cs` → `ReconcileOrganization()`

**The rule:** Sessions that belong to multi-agent groups must NOT be auto-moved to repo groups during reconciliation. Two protections:

1. **Active group members**: If a session's `GroupId` matches any `IsMultiAgent` group, skip it
2. **Orphaned multi-agent sessions** (group was deleted): If `Role == Orchestrator` or `PreferredModel != null`, don't auto-move to repo groups — these markers indicate the session was part of a multi-agent group

**Why:** Reconciliation runs twice on startup (once in `LoadOrganization`, once after `RestorePreviousSessionsAsync`). Without protection, it redistributes multi-agent sessions across repo-based groups, destroying the team.

### 3. Never Edit `organization.json` While the App Is Running

**Why:** The app calls `SaveOrganization()` from ~30 places, constantly overwriting the file with its in-memory state. Any external edits are lost within seconds. To fix organization state: kill app → edit file → relaunch.

### 4. Sentinel Protocol Is Case-Insensitive But Must Be on Its Own Line

**Sentinels:**
- `[[GROUP_REFLECT_COMPLETE]]` — Goal achieved, stop iterating
- `[[NEEDS_ITERATION]]` — More work needed, continue
- `[[REFLECTION_COMPLETE]]` — Single-agent reflection goal met

**Detection:** `StringComparison.OrdinalIgnoreCase` for multi-agent; strict regex `^\s*\[\[REFLECTION_COMPLETE\]\]\s*$` (multiline) for single-agent.

### 5. Worker Prompt Must Include Original User Request

**Where:** `ExecuteWorkerAsync` (line ~772)

**Why:** Workers receive only their assigned subtask from the orchestrator. Without the original user request as context, they can't understand the broader goal. The prompt format is:

```
You are a worker agent. Complete the following task thoroughly.

## Original User Request (context)
{originalPrompt}

## Your Assigned Task
{task}
```

### 6. Orphaned Event Handlers Must Not Mutate State

**Where:** `CopilotService.Events.cs` → `HandleSessionEvent`, `isCurrentState` gate

**The rule:** When a session is reconnected, the old session's event handler becomes orphaned. ALL events from orphaned handlers must be blocked (not just terminal events). The `isCurrentState` check compares the captured state object with `_sessions[sessionName]` — if they don't match, the handler is orphaned.

**Why:** Orphaned handlers can produce ghost text deltas, phantom tool executions, and stale history entries that corrupt the current session's state.

### 7. Session Reconnect: Swap `_sessions` Before Wiring Handler

**Where:** `CopilotService.cs` → reconnect logic

**The rule:** `_sessions[sessionName] = newState` MUST execute BEFORE `newSession.On(evt => HandleSessionEvent(newState, evt))`. If the handler is wired first, early events from the new session see `isCurrentState=false` (because `_sessions` still points to old state) and get incorrectly dropped.

### 8. Image Queue: ALL Mutations Under `_imageQueueLock`

**Where:** `CopilotService.cs` and `CopilotService.Events.cs` — all `_queuedImagePaths` access

**The rule:** Every mutation of `_queuedImagePaths` (enqueue, dequeue, remove, clear, rename, close) must be inside `lock (_imageQueueLock)`. The inner lists (`List<List<string>>`) are not thread-safe.

### 9. `IsResumed` Must Be Cleared on ALL Terminal Paths

**Where:** `CopilotService.Events.cs` → `CompleteResponse`, `SessionErrorEvent`, watchdog timeout

**The rule:** `state.Info.IsResumed = false` must be set in every code path that sets `IsProcessing = false`. Otherwise, subsequent turns inherit the resumed session's 600s tool timeout.

### 10. All TCS Must Use `RunContinuationsAsynchronously`

**Where:** All `new TaskCompletionSource()` in `CopilotService.Events.cs`

**The rule:** Always use `new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)`. Without this, TCS continuations can run inline on the completing thread, causing reentrancy and stack overflows in reflection loops.

---

## Stall Detection

Two mechanisms, both in `ReflectionCycle.CheckStall()`:

1. **Exact string match** — Sliding window of last 5 full response strings. If current response matches any (full string equality, no hash) → stall.
2. **Jaccard token similarity** — Tokenize current and previous response by whitespace. If intersection/union > 0.9 → stall.

**Tolerance:** 2 consecutive stalls required before stopping. First stall generates a warning. This prevents false positives from models that happen to produce similar phrasing once.

**Reset:** `ResetStallDetection()` clears history. Called when resuming from pause.

---

## Quality Trend Tracking

`ReflectionCycle.EvaluationHistory` records per-iteration:
- `Score` (0.0–1.0)
- `Rationale` (string)
- `EvaluatorModel` (which model evaluated)
- `Timestamp`

`RecordEvaluation()` returns a `QualityTrend`:
- **Improving** — Latest score > previous + 0.1
- **Stable** — Within ±0.1
- **Degrading** — Latest score < previous - 0.1

Degrading trend triggers a `PendingAdjustments` warning suggesting model changes.

---

## Session Organization & Persistence

### Data Model

```
OrganizationState
├── Groups: List<SessionGroup>
│   ├── Id (GUID string)
│   ├── Name
│   ├── IsMultiAgent (bool)
│   ├── OrchestratorMode (Broadcast/Sequential/Orchestrator/OrchestratorReflect)
│   ├── OrchestratorPrompt (optional system prompt for orchestrator)
│   ├── ReflectionState: ReflectionCycle? (active cycle state)
│   ├── SharedContext (from decisions.md — prepended to worker prompts)
│   ├── RoutingContext (from routing.md — injected into orchestrator planning)
│   ├── WorktreeId, RepoId (links to repo/worktree)
│   └── SortOrder
│
└── Sessions: List<SessionMeta>
    ├── SessionName
    ├── GroupId (→ SessionGroup.Id)
    ├── Role (Worker/Orchestrator)
    ├── PreferredModel (e.g., "claude-opus-4.6")
    ├── SystemPrompt (worker specialization, e.g., "You are a security auditor...")
    ├── WorktreeId
    └── IsPinned, ManualOrder
```

### Persistence Flow
- **File:** `~/.polypilot/organization.json`
- **Save:** `SaveOrganization()` called from ~30 places (group CRUD, session moves, reflection state updates)
- **Load:** `LoadOrganization()` on startup → deserialize → `ReconcileOrganization()`
- **Reconciliation:** Matches sessions to repo groups by `WorktreeId`/`RepoId`, prunes stale groups, protects multi-agent sessions

### Group Presets
`CreateGroupFromPresetAsync(GroupPreset)` creates a full team:
1. Creates `SessionGroup` with mode and metadata
2. Creates orchestrator session with `Role = Orchestrator`, `PreferredModel` set
3. Creates N worker sessions with `PreferredModel` and `SystemPrompt` set per worker
4. All sessions get `WorktreeId` if provided

**Worker System Prompts:** Each worker can have a `SystemPrompt` defining its specialization. This prompt is:
- Included in `BuildOrchestratorPlanningPrompt` so the orchestrator knows each worker's expertise and routes tasks accordingly
- Prepended to the worker's task in `ExecuteWorkerAsync` (replaces the generic "You are a worker agent" prompt)
- Set via `SetSessionSystemPrompt(sessionName, prompt)` or via `GroupPreset.WorkerSystemPrompts`

**Critical:** Both `Role` and `PreferredModel` must be set on all sessions. These are the markers that `ReconcileOrganization` uses to identify multi-agent sessions. Without them, sessions get scattered on restart.

### Group Deletion

Deleting a group via `DeleteGroup(groupId)` behaves differently based on group type:

- **Multi-agent groups (`IsMultiAgent == true`):** All sessions in the group are **removed from the organization and closed asynchronously**. Multi-agent sessions are meaningless without their group — they have orchestrator/worker roles, preferred models, and system prompts that only make sense within the team context. Leaving them orphaned in the default group (the old behavior) caused confusion in the sidebar.

- **Regular groups (repo groups, etc.):** Sessions are **moved to the default group**. These are standalone sessions that the user may still want to access.

**Invariant:** After `DeleteGroup` on a multi-agent group, `Organization.Sessions` must contain zero entries with the deleted group's ID. The async close fires `CloseSessionAsync` on each session (disposing the SDK session, cleaning up image queues, and tracking closed session IDs to prevent merge re-addition).

---

## Error Handling in Reflection Loops

```
try {
    // ... full iteration (plan → dispatch → collect → evaluate)
}
catch (OperationCanceledException) {
    IsCancelled = true;        // Mark as cancelled for BuildCompletionSummary
    throw;                     // User cancellation propagates
}
catch (Exception ex) {
    CurrentIteration--;        // Retry same iteration, don't skip ahead
    ConsecutiveErrors++;       // Separate error counter (ConsecutiveStalls tracks repetition)
    if (ConsecutiveErrors >= 3) {
        IsStalled = true;      // Give up after 3 retries
        IsCancelled = true;    // Non-success termination
        break;
    }
    await Task.Delay(2000);    // Back off before retry
}
```

This prevents a single transient error (network hiccup, model timeout) from killing the entire reflection cycle. `ConsecutiveErrors` resets to 0 on successful iterations (alongside `ConsecutiveStalls`), so errors must be truly consecutive.

---

## Task Assignment Protocol

The orchestrator's planning prompt tells it to emit assignments in this format:

```
@worker:worker-name-1 Description of the task for this worker
@worker:worker-name-2 Description of the task for this worker
```

`ParseTaskAssignments` uses regex `@worker:(\S+)\s*([\s\S]*?)(?:@end|(?=@worker:)|$)` to extract these. Workers are matched against the `availableWorkers` list (case-insensitive, fuzzy-matched).

If no `@worker:` assignments are found, the orchestrator handled the request directly and the loop exits.

---

## Testing

### Unit Tests
- **`MultiAgentRegressionTests.cs`** (37 tests) — JSON corruption, reconciliation scattering, preset markers, mode enums, reflection loop logic, TCS ordering, lifecycle scenarios, persona tests
- **`SessionOrganizationTests.cs`** → `GroupingStabilityTests` (15 tests) — JSON round-trips, delete+cleanup, orphan handling, multi-agent vs regular group deletion
- **`SquadDiscoveryTests.cs`** (22 tests) — Squad directory discovery, team.md parsing, charter→system-prompt, decisions/routing context, three-tier merge, legacy `.ai-team/` compat
- **`ScenarioReferenceTests.cs`** — Validates scenario JSON structure, unique IDs, Squad integration scenario presence

### Executable Scenarios
- **`PolyPilot.Tests/Scenarios/multi-agent-scenarios.json`** — CDP-based scenarios for MauiDevFlow testing against a running app

### What to Test After Changes
1. **Changed orchestration logic?** → Run `MultiAgentRegressionTests`
2. **Changed reconciliation?** → Run `GroupingStabilityTests`
3. **Changed TCS/event handling?** → Run `ProcessingWatchdogTests` + verify reflection loop completes
4. **Changed sentinel parsing?** → Run `ReflectionCycleTests`
5. **Changed session persistence?** → Run full suite, verify `organization.json` survives restart

---

## Squad Integration — Repo-Level Team Discovery

### Overview

PolyPilot can discover and load team definitions from [bradygaster/squad](https://github.com/bradygaster/squad) format directories (`.squad/` or the legacy `.ai-team/`). Any repository that has been "squadified" automatically gets its teams available as presets in PolyPilot's multi-agent group creation flow.

### How Squad Maps to PolyPilot

| Squad File | PolyPilot Concept | How It's Used |
|------------|-------------------|---------------|
| `.squad/team.md` | `SessionGroup` + workers | Roster parsed for agent names and roles |
| `.squad/agents/{name}/charter.md` | `SessionMeta.SystemPrompt` | Charter content becomes worker system prompt |
| `.squad/routing.md` | Orchestrator planning context | Injected into `BuildOrchestratorPlanningPrompt` |
| `.squad/decisions.md` | Shared worker context | Prepended to all worker prompts as shared team knowledge |
| Squad coordinator | `MultiAgentMode.OrchestratorReflect` | Squad's iterative coordinator maps to PolyPilot's reflect loop |

### Discovery Flow

1. User clicks **🤖 Multi** → selects a worktree
2. `SquadDiscovery.Discover(worktreePath)` scans for `.squad/` or `.ai-team/`
3. If found, parses `team.md` + agent charters → builds a `GroupPreset`
4. Preset appears in the picker under **"📂 From Repo (Squad)"** section, above built-in presets
5. User clicks the Squad preset → `CreateGroupFromPresetAsync` creates the group with all agents and their charters as system prompts

### Preset Priority (Three-Tier Cascade)

```
Built-in presets  <  User presets (~/.polypilot/presets.json)  <  Repo teams (.squad/)
```

Repo teams shadow built-in/user presets with the same name when working in that repo's worktree.

### Squad Write-Back

When a user saves a multi-agent group as a preset and the group is associated with a worktree, PolyPilot writes the team definition back to `.squad/` format in the worktree root:

1. **`SaveGroupAsPreset`** resolves the worktree path from the group's `WorktreeId`
2. **`SquadWriter.WriteFromGroup`** converts the live `SessionGroup` + `SessionMeta` into Squad files:
   - `.squad/team.md` — Team name + agent roster table (Member | Role)
   - `.squad/agents/{name}/charter.md` — Worker system prompt as charter
   - `.squad/decisions.md` — Shared context (from `GroupPreset.SharedContext`)
   - `.squad/routing.md` — Routing context (from `GroupPreset.RoutingContext`)
3. The preset is also saved to `presets.json` as a personal backup

Agent names are sanitized: team-name prefixes are stripped (e.g., "Code Review Team-worker-1" → "worker-1"), names are lowercased and non-alphanumeric characters replaced with hyphens. Roles are derived from the first sentence of the system prompt, stripping "You are a/an" prefix.

This enables round-tripping: discover a Squad team → modify it in PolyPilot → save back → others can use the updated team definition from the repo.

### What PolyPilot Does NOT Do with Squad

- **No `history.md` persistence** — Squad agents accumulate learnings; PolyPilot sessions are stateless across restarts
- **No Scribe agent** — Squad's silent decision-logger is not replicated
- **No GitHub Actions integration** — Squad's label triage workflows are out of scope
- **No casting system** — Squad's thematic name universes; PolyPilot uses agent names as-is

### Security

- Agent charters (system prompts) are capped at 4,000 characters
- Model slugs are validated against `ModelCapabilities.AllModels`; unknown slugs fall back to app default
- Repo presets show a **📂** source badge so users know the definition came from the repo
- No file-read directives or code execution from parsed files

### GroupPreset Extensions for Squad Support

```csharp
public record GroupPreset(...)
{
    public bool IsUserDefined { get; init; }
    public bool IsRepoLevel { get; init; }           // Loaded from .squad/
    public string? SourcePath { get; init; }          // Path to .squad/ dir
    public string?[]? WorkerSystemPrompts { get; init; }
    public string? SharedContext { get; init; }        // From decisions.md
    public string? RoutingContext { get; init; }       // From routing.md
}
```
