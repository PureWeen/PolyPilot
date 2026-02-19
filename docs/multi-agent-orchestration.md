# Multi-Agent Orchestration — Architecture Spec

> **Read this before modifying orchestration, sentinel protocol, session reconciliation, or reflection loops.**

## Overview

PolyPilot's multi-agent system lets you create a **team of AI sessions** that work together. Each session can use a different AI model. An orchestrator coordinates work dispatch, response collection, and quality evaluation.

### Key Files

| File | Purpose |
|------|---------|
| `PolyPilot/Services/CopilotService.Organization.cs` | Orchestration engine (dispatch, reflection loop, reconciliation) |
| `PolyPilot/Models/SessionOrganization.cs` | `SessionGroup`, `SessionMeta`, `MultiAgentMode`, `MultiAgentRole` |
| `PolyPilot/Models/ReflectionCycle.cs` | Reflection state, stall detection, sentinel parsing, evaluator prompts |
| `PolyPilot/Services/CopilotService.Events.cs` | TCS completion (IsProcessing → TrySetResult ordering) |
| `PolyPilot.Tests/MultiAgentRegressionTests.cs` | 30 regression tests covering all known bugs |
| `PolyPilot.Tests/SessionOrganizationTests.cs` | 14 grouping stability tests |
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

## OrchestratorReflect — Detailed Loop

### Participants
- **1 Orchestrator** — Plans, delegates, synthesizes. Set via `SessionMeta.Role = Orchestrator`
- **N Workers** — Execute assigned tasks in parallel. Each can use a different model (`SessionMeta.PreferredModel`)
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
    If no assignments parsed → orchestrator decided goal is met → break

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
| ⏱️ Max iterations | `CurrentIteration >= MaxIterations` | `IsActive = false` |
| ⚠️ Stalled | 2 consecutive responses with >90% Jaccard similarity | `IsStalled = true` |
| ⚠️ Error budget | 3 consecutive errors within a single iteration | `IsStalled = true` |
| 🛑 Cancelled | CancellationToken triggered | `OperationCanceledException` |
| ⏸️ Paused | User set `IsPaused = true` | Loop condition fails |

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

---

## Stall Detection

Two mechanisms, both in `ReflectionCycle.CheckStall()`:

1. **Exact hash match** — Sliding window of last 5 response hashes. If current hash matches any → stall.
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
│   ├── WorktreeId, RepoId (links to repo/worktree)
│   └── SortOrder
│
└── Sessions: List<SessionMeta>
    ├── SessionName
    ├── GroupId (→ SessionGroup.Id)
    ├── Role (Worker/Orchestrator)
    ├── PreferredModel (e.g., "claude-opus-4.6")
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
3. Creates N worker sessions with `PreferredModel` set per worker
4. All sessions get `WorktreeId` if provided

**Critical:** Both `Role` and `PreferredModel` must be set on all sessions. These are the markers that `ReconcileOrganization` uses to identify multi-agent sessions. Without them, sessions get scattered on restart.

---

## Error Handling in Reflection Loops

```
try {
    // ... full iteration (plan → dispatch → collect → evaluate)
}
catch (OperationCanceledException) { throw; }  // User cancellation propagates
catch (Exception ex) {
    CurrentIteration--;        // Retry same iteration, don't skip ahead
    ConsecutiveStalls++;       // Borrow stall counter as error counter
    if (ConsecutiveStalls >= 3) {
        IsStalled = true;      // Give up after 3 retries
        break;
    }
    await Task.Delay(2000);    // Back off before retry
}
```

This prevents a single transient error (network hiccup, model timeout) from killing the entire reflection cycle.

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
- **`MultiAgentRegressionTests.cs`** (30 tests) — JSON corruption, reconciliation scattering, preset markers, mode enums, reflection loop logic, TCS ordering, lifecycle scenarios
- **`SessionOrganizationTests.cs`** → `GroupingStabilityTests` (14 tests) — JSON round-trips, delete+reconcile, orphan handling

### Executable Scenarios
- **`PolyPilot.Tests/Scenarios/multi-agent-scenarios.json`** — CDP-based scenarios for MauiDevFlow testing against a running app

### What to Test After Changes
1. **Changed orchestration logic?** → Run `MultiAgentRegressionTests`
2. **Changed reconciliation?** → Run `GroupingStabilityTests`
3. **Changed TCS/event handling?** → Run `ProcessingWatchdogTests` + verify reflection loop completes
4. **Changed sentinel parsing?** → Run `ReflectionCycleTests`
5. **Changed session persistence?** → Run full suite, verify `organization.json` survives restart
