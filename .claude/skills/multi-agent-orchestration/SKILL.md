---
name: multi-agent-orchestration
description: >
  Invariants, lifecycle documentation, and error recovery strategies for PolyPilot's
  multi-agent orchestration system. Use when: (1) Modifying dispatch logic in
  SendViaOrchestratorAsync or SendViaOrchestratorReflectAsync, (2) Touching worker
  execution, result collection, or synthesis phases, (3) Modifying PendingOrchestration
  persistence or resume logic, (4) Debugging orchestrator-worker communication failures,
  (5) Adding error handling around worker dispatch or completion, (6) Modifying
  OnSessionComplete coordination or TCS ordering, (7) Working with reflection loop
  concurrency (semaphores, queued prompts), (8) Modifying SessionIdleEvent handling
  or IDLE-DEFER logic (BackgroundTasks check). Covers: 5-phase dispatch lifecycle,
  IDLE-DEFER background task guard, restart recovery via PendingOrchestration, worker
  failure patterns, and connection error handling.
---

# Multi-Agent Orchestration — Invariants & Error Recovery

> **Read this before modifying orchestration dispatch, worker execution, synthesis,
> reflection loops, or PendingOrchestration persistence.**

> ⚠️ **LONG-RUNNING SESSION SAFETY**: Multi-agent workers routinely run for 5-30+
> minutes. ANY watchdog change, timeout tweak, or session lifecycle fix MUST be
> validated against long-running workers or it WILL kill legitimate sessions.
> See the **Long-Running Session Safety** section and run `LongRunningSessionSafetyTests`
> before merging ANY change to watchdog, Case B, or session lifecycle paths.

## Overview

PolyPilot's orchestration system coordinates work between an orchestrator session
and N worker sessions. This skill documents the invariants that prevent data loss,
stuck sessions, and coordination failures.

### Key Files

| File | Purpose |
|------|---------|
| `CopilotService.Organization.cs` | Orchestration engine — dispatch, synthesis, reflection |
| `CopilotService.Codespace.cs` | Multi-server routing via `GetClientForGroup` |
| `CopilotService.Events.cs` | TCS completion, OnSessionComplete firing |
| `Models/SessionOrganization.cs` | Group/session metadata, modes, roles |
| `Models/ReflectionCycle.cs` | Reflection state, stall detection |

### Multi-Server Routing via `GetClientForGroup`

When PolyPilot manages sessions across multiple servers (local + codespace), `GetClientForGroup`
(`CopilotService.Codespace.cs:38`) selects the correct `CopilotClient` for a given group:

```
GetClientForGroup(groupId)
  ├── groupId provided AND group.IsCodespace?
  │     ├── Yes → return _codespaceClients[groupId]  (throws if disconnected)
  │     └── No  → fall through
  └── return _client  (main local SDK connection, throws if uninitialized)
```

**Thread safety**: Uses `SnapshotGroups()` (not live `Organization.Groups` list) because the
method may be called from an await continuation on a background thread.

**Callers** (primary session creation/resume paths):
- `Organization.cs` → worker fresh session revival (dead event stream recovery)
- `Persistence.cs` → session creation and resume during restore
- `CopilotService.cs` → main session creation and resume paths

Note: Codespace health-check resumes (`ResumeCodespaceSessionsAsync`) and permission-recovery
resumes (`TryRecoverPermissionAsync`) use their client references directly, bypassing this method.

**Fail-fast design**: If a codespace group is expected but not connected, throws immediately
with a diagnostic message rather than silently falling back to the local client.

---

## The 5-Phase Orchestration Lifecycle

Every orchestrator dispatch (single-pass and reflect) follows these phases:

```
┌─────────────────────────────────────────────────────────────────┐
│  Phase 1: PLAN                                                   │
│  ├── Orchestrator receives user prompt + worker list             │
│  ├── Builds planning prompt with worker models/descriptions      │
│  ├── EarlyDispatchOnWorkerBlocks = true (resolve TCS mid-turn)   │
│  ├── Orchestrator responds with @worker:name task blocks         │
│  └── ParseTaskAssignments extracts → List<TaskAssignment>        │
│       └── If no assignments: send nudge → retry parse (up to 3x)│
│           └── If still none: orchestrator handled directly       │
└─────────────────────────────────────────────────────────────────┘
                              │
                              ▼
┌─────────────────────────────────────────────────────────────────┐
│  Phase 2: DISPATCH                                               │
│  ├── SavePendingOrchestration() — BEFORE dispatching             │
│  ├── Fire OnOrchestratorPhaseChanged(Dispatching)                │
│  ├── Launch worker tasks in parallel: Task.WhenAll(workers)      │
│  │   └── Workers staggered with 1s delay between dispatches      │
│  └── Each worker gets: system prompt + original prompt + task    │
└─────────────────────────────────────────────────────────────────┘
                              │
                              ▼
┌─────────────────────────────────────────────────────────────────┐
│  Phase 3: COLLECT (WaitingForWorkers)                            │
│  ├── Await all worker completions (10-min timeout each)          │
│  ├── WaitForSessionIdleAsync on orchestrator (early dispatch)    │
│  ├── Collect WorkerResult[] with response, success, duration     │
│  └── Failed workers: response = error message, success = false   │
└─────────────────────────────────────────────────────────────────┘
                              │
                              ▼
┌─────────────────────────────────────────────────────────────────┐
│  Phase 4: SYNTHESIZE                                             │
│  ├── Build synthesis prompt with all worker results              │
│  ├── Send to orchestrator for final response                     │
│  ├── ClearPendingOrchestration() — in finally block              │
│  └── Fire OnOrchestratorPhaseChanged(Complete)                   │
└─────────────────────────────────────────────────────────────────┘
                              │
                              ▼
┌─────────────────────────────────────────────────────────────────┐
│  Phase 5: IDLE-DEFER (Background Task Safety) — PR #399          │
│  ├── SessionIdleEvent handler checks HasActiveBackgroundTasks()  │
│  ├── If agents/shells active: flush text, log [IDLE-DEFER],     │
│  │   and break WITHOUT calling CompleteResponse                  │
│  └── Only truly idle (no background tasks) → CompleteResponse    │
│                                                                   │
│  This prevents premature TCS completion when workers are still   │
│  running as background agents/shells dispatched by the CLI.      │
└─────────────────────────────────────────────────────────────────┘
```

> **Phase 5 (IDLE-DEFER) is not a sequential phase** — it's a guard in the
> `SessionIdleEvent` handler (Events.cs:622-642) that applies to ALL sessions.
> It's listed here because it fundamentally changed the premature idle story
> for orchestrator workers. See **IDLE-DEFER & BackgroundTasks** section below.

### OrchestratorReflect: Extended Loop

OrchestratorReflect wraps phases 1–4 in a loop with evaluation:

```
while (IsActive && !IsPaused && CurrentIteration < MaxIterations):
    Drain _reflectQueuedPrompts (user messages during loop)
    Phase 1–4 (as above)
    Phase 5: EVALUATE
    ├── With evaluator: score + rationale → RecordEvaluation()
    ├── Self-eval: check for [[GROUP_REFLECT_COMPLETE]] sentinel
    └── AutoAdjustFromFeedback() detects quality degradation
    Phase 6: STALL DETECTION
    ├── CheckStall() compares synthesis to previous (Jaccard > 0.9)
    └── 2 consecutive stalls → IsStalled = true → break
```

> **Sentinel note**: `ReflectionCycle.cs` defines `CompletionSentinel = "[[REFLECTION_COMPLETE]]"`
> (used by `IsGoalMet()` regex). The orchestrator prompts in `Organization.cs` use
> `[[GROUP_REFLECT_COMPLETE]]` for model-facing instructions. Both are checked, but
> via different mechanisms — `IsGoalMet()` for the ReflectionCycle, string.Contains
> for orchestrator-level evaluation.

---

## PendingOrchestration — Restart Recovery

### Purpose

If the app restarts while workers are processing, we'd lose their work. `PendingOrchestration`
is persisted to disk BEFORE dispatching workers, enabling recovery.

### File Location

`{PolyPilotBaseDir}/pending-orchestration.json`

### Schema

```csharp
internal class PendingOrchestration
{
    string GroupId           // Multi-agent group ID
    string OrchestratorName  // Name of orchestrator session
    List<string> WorkerNames // Names of dispatched workers
    string OriginalPrompt    // The user's original request
    DateTime StartedAt       // UTC timestamp of dispatch
    bool IsReflect           // True for OrchestratorReflect mode
    int ReflectIteration     // Current iteration (reflect only)
}
```

### Lifecycle

| Event | Action |
|-------|--------|
| Before dispatch | `SavePendingOrchestration()` |
| After synthesis completes | `ClearPendingOrchestration()` (in finally) |
| App restart | `ResumeOrchestrationIfPendingAsync()` |
| Group deleted | `ClearPendingOrchestration()` |
| Orchestrator missing | `ClearPendingOrchestration()` |
| All workers idle | Collect results → synthesize → clear |

### Timestamp Convention: Local Time for Dispatch Filtering

Worker result collection filters messages with `m.Timestamp >= dispatchTime` to avoid picking up
stale responses from previous dispatches. **Local time (`DateTime.Now`) is used throughout**, not UTC:

- `dispatchTime = DateTime.Now` — set before sending prompts to workers
- `ChatMessage.Timestamp` uses the parsed event timestamp if present in `LoadHistoryFromDiskAsync`,
  falling back to `DateTime.Now` when the event has no `timestamp` field
- `PendingOrchestration.StartedAt` is persisted in UTC, but **converted to local time** on resume:
  ```csharp
  var dispatchTimeLocal = pending.StartedAt.Kind == DateTimeKind.Utc
      ? pending.StartedAt.ToLocalTime()
      : pending.StartedAt;
  ```

**Why local time?** All message timestamps in the in-memory `History` and in `events.jsonl` parsing
use local time. Using UTC for dispatch filtering would cause mismatches and miss valid responses.
UTC is only used for disk persistence (JSON serialization) — all runtime filtering is local.

### Resume Flow (`ResumeOrchestrationIfPendingAsync`)

```
1. Load pending-orchestration.json
2. Validate: group exists? orchestrator session exists?
   └── If not: clear file and return
3. Poll workers every 5s until all are idle (15-min timeout)
4. Collect last assistant response from each worker (post-dispatch)
5. Build synthesis prompt → send to orchestrator
6. Clear pending orchestration file
```

---

## Worker Failure Handling

### Per-Worker Execution (`ExecuteWorkerAsync`)

```csharp
private async Task<WorkerResult> ExecuteWorkerAsync(
    string workerName, string task, string originalPrompt, CancellationToken ct)
{
    try
    {
        var response = await SendPromptAndWaitAsync(workerName, prompt, ct);
        return new WorkerResult(workerName, response, success: true, duration);
    }
    catch (OperationCanceledException)
    {
        return new WorkerResult(workerName, "Cancelled", success: false, duration);
    }
    catch (Exception ex)
    {
        return new WorkerResult(workerName, $"Error: {ex.Message}", success: false, duration);
    }
}
```

### Failure Patterns

| Failure | Behavior | Recovery |
|---------|----------|----------|
| Worker timeout (10 min) | TCS times out → exception | WorkerResult.Success = false, included in synthesis |
| Worker cancellation | OperationCanceledException | WorkerResult.Success = false, marked "Cancelled" |
| Worker SDK error | SessionErrorEvent fires | Error clears IsProcessing → TCS completed with error |
| Connection lost | Depends on timing | See Connection Error Handling below |

### INV-O1: Workers NEVER block orchestrator completion

Even if all workers fail, the orchestrator still receives a synthesis prompt with
failure messages. The orchestrator can then explain what went wrong.

---

## Connection/Cancellation Error Recovery

### SendPromptAndWaitAsync Error Paths

```csharp
private async Task<string> SendPromptAndWaitAsync(
    string sessionName, string prompt, CancellationToken ct)
{
    // 1. Send prompt (may fail)
    await SendPromptAsync(sessionName, prompt, ct);
    
    // 2. Wait for TCS completion (may timeout or cancel)
    var tcs = GetOrCreateTCS(sessionName);
    using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
    cts.CancelAfter(TimeSpan.FromMinutes(10));
    
    return await tcs.Task.WaitAsync(cts.Token);
}
```

### Error Scenarios

| Scenario | Detection | Handling |
|----------|-----------|----------|
| Connection error during worker dispatch | SendPromptAsync throws | Worker task catches → WorkerResult.Success = false |
| Connection error during synthesis | SendPromptAsync throws | Orchestrator loop catches → retry or mark failed |
| Worker completes after orchestrator error | TCS may have been cancelled | Worker result lost (acceptable — orchestrator already failed) |
| SDK disconnection mid-response | SessionErrorEvent | TCS.TrySetException() → propagates to caller |

### INV-O2: Connection errors during dispatch MUST NOT leave PendingOrchestration stale

The finally block in `SendViaOrchestratorAsync` always calls `ClearPendingOrchestration()`,
even if dispatch throws. This prevents orphaned pending files.

---

## OnSessionComplete Coordination

### Purpose

`OnSessionComplete` is fired when a session finishes processing (IsProcessing → false).
Orchestrator loops use this to detect when workers finish.

**Signature**: `event Action<string, string>? OnSessionComplete` — `(sessionName, summary)`

### Ordering Invariant (from processing-state-safety)

**INV-O3: IsProcessing = false BEFORE TrySetResult BEFORE OnSessionComplete**

```csharp
// CORRECT ORDER in CompleteResponse (Events.cs ~line 1062-1080):
state.Info.IsProcessing = false;                    // 1. Clear processing state
state.ResponseCompletion?.TrySetResult(fullResponse);// 2. Complete TCS (may run sync continuation)
OnSessionComplete?.Invoke(name, summary);            // 3. Notify listeners
OnStateChanged?.Invoke();                            // 4. UI update
```

If TrySetResult runs before IsProcessing=false, the reflection loop's synchronous
continuation may see `IsProcessing = true` and fail to send the next prompt.

### INV-O4: OnSessionComplete fired on ALL termination paths

All paths that clear IsProcessing (currently 19+ across Events.cs, CopilotService.cs,
Organization.cs, Bridge.cs, and Providers.cs) should fire OnSessionComplete. Otherwise,
orchestrator loops waiting on workers hang forever. Key invocation sites include:
CompleteResponse, SessionErrorEvent, watchdog timeout, watchdog crash recovery,
abort, steer error, and bridge completion.

---

## Reflection Loop Concurrency

### Semaphores

| Semaphore | Purpose |
|-----------|---------|
| `_reflectLoopLocks[groupId]` | Prevents concurrent reflect loops per group |
| `_modelSwitchLocks[sessionName]` | Prevents concurrent model switches during dispatch |

### INV-O5: Only ONE reflect loop per group at a time

Without `_reflectLoopLocks`, a second user message while the loop is awaiting workers
starts a competing loop. Both race over `ReflectionCycle` state, causing:
- Duplicate worker dispatches
- Lost worker results (collected by wrong loop)
- Corrupted iteration counts

### Queued Prompts (`_reflectQueuedPrompts`)

When the semaphore is held, incoming prompts are queued. At the start of each
iteration, queued prompts are drained and sent to the orchestrator:

```csharp
// Drain and send queued prompts
if (_reflectQueuedPrompts.TryGetValue(groupId, out var queue))
{
    while (queue.TryDequeue(out var queuedPrompt))
    {
        await SendPromptAsync(orchestratorName, queuedPrompt, ct);
    }
}
```

---

## Invariant Checklist for Orchestration Code

When modifying orchestration, verify:

- [ ] **INV-O1**: Worker failures are captured in WorkerResult, not thrown
- [ ] **INV-O2**: PendingOrchestration cleared in finally block
- [ ] **INV-O3**: IsProcessing cleared before TCS completion (see processing-state-safety)
- [ ] **INV-O4**: OnSessionComplete fired on all termination paths
- [ ] **INV-O5**: Reflect loop protected by semaphore
- [ ] **INV-O6**: Phase changes fire OnOrchestratorPhaseChanged for UI updates
- [ ] **INV-O7**: Worker timeouts use 10-minute default (600s for resumed sessions)
- [ ] **INV-O8**: Cancellation tokens propagated to all async operations
- [ ] **INV-O15**: IDLE-DEFER flushes CurrentResponse before breaking (content preservation)
- [ ] **INV-O17**: IDLE-DEFER-REARM re-arms IsProcessing when idle arrives with backgroundTasks but IsProcessing is already false (PR #472)

---

## Session Stability & Reconnect Safety (PR #373)

> **Critical context for multi-agent:** Reconnects affect ALL sibling sessions
> in a group, not just the session that triggered the reconnect. Every invariant
> below applies during orchestrator dispatch.

### INV-O9: Sibling Re-Resume Must Orphan Old State

When `CopilotClient` is recreated (connection drop + reconnect), ALL sessions
sharing that client need their `CopilotSession` re-resumed. The sibling loop
in `SendPromptAsync` (line ~2630) creates a **fresh `SessionState`** for each
sibling to prevent the shared-Info write-write race:

```csharp
// 1. Mark old state as orphaned FIRST
otherState.IsOrphaned = true;
Interlocked.Exchange(ref otherState.ProcessingGeneration, long.MaxValue);
otherState.ResponseCompletion?.TrySetCanceled();  // Unblock orchestrator!

// 2. Create fresh state with same Info
var siblingState = new SessionState { Session = resumed, Info = otherState.Info };

// 3. Register handler BEFORE publishing to dictionary
resumed.On(evt => HandleSessionEvent(siblingState, evt));

// 4. Atomic swap prevents stale Task.Run from overwriting
if (!_sessions.TryUpdate(key, siblingState, otherState)) { /* discard */ }
```

**Why TrySetCanceled matters for orchestration:** Workers await
`ResponseCompletion` TCS. If a reconnect orphans the state without canceling
the TCS, `ExecuteWorkerAsync` hangs forever → orchestration deadlocks.

### INV-O10: Collection Snapshots Before Task.Run

`Organization.Sessions` and `Organization.Groups` are `List<T>` — NOT
thread-safe. The sibling loop runs in `Task.Run` (background thread).
Snapshot BEFORE entering the closure:

```csharp
var sessionSnapshots = Organization.Sessions.ToList();
var groupSnapshots = Organization.Groups.ToList();
_ = Task.Run(async () => {
    // Use snapshots, not live collections
});
```

### INV-O11: MCP Servers Must Reload on Reconnect

Both the primary reconnect path AND the sibling loop must call
`LoadMcpServers()` and `LoadSkillDirectories()`. Old server handles
are tied to the disposed client. Without reload, MCP tools silently
fail after reconnect.

### INV-O12: IsProcessing Guard on Sibling Re-Resume

Never orphan a sibling that's actively processing (mid-turn). The
sibling loop checks `if (otherState.Info.IsProcessing) continue;`
and re-checks after the async `ResumeSessionAsync` call (TOCTOU guard).

### INV-O13: IsOrphaned Guards — 5-Layer Defense

Every event/timer entry point checks `state.IsOrphaned` first:

| Layer | Location | Purpose |
|-------|----------|---------|
| 1 | HandleSessionEvent:214 | Block ALL SDK events |
| 2 | CompleteResponse:913 | TrySetCanceled + return |
| 3 | Watchdog loop:1820 | Exit running watchdog |
| 4 | Watchdog callback:2095 | Guard InvokeOnUI |
| 5 | Tool/health handlers | Stale tool events |

---

## Long-Running Session Safety

> ⚠️ **This section is mandatory reading before touching ANY watchdog, timeout,
> or session lifecycle code.** Multi-agent workers are the longest-running sessions
> in PolyPilot. Changes that seem safe for interactive 30-second sessions can kill
> 20-minute review workers, losing all their work.

### Real-World Session Durations

| Scenario | Typical Duration | Max Observed |
|----------|-----------------|--------------|
| Interactive user chat | 5-30s | 2 min |
| Single-tool agent | 30s-2 min | 5 min |
| Multi-agent worker (review) | 3-11 min | 20 min |
| Multi-agent worker (fix+review) | 5-15 min | 30 min |
| OrchestratorReflect full loop | 15-30 min | 60 min |

### What Looks Like a Stuck Session But Isn't

These are **legitimate** long pauses that must NOT trigger watchdog kills:

1. **Model thinking between tool rounds** (30s-5 min): After tool output, the LLM
   decides its next action. Mtime may appear frozen on some filesystems.
   ❌ Detecting "mtime unchanged for N checks" WILL false-positive here.
   ✅ File-size-growth check (INV-O16) is safe: the CLI writes
   `AssistantMessageDeltaEvent` entries while the model streams tokens, so the file
   keeps growing. Only a dead connection stops all writes.

2. **Large context ingestion** (1-3 min): Worker receives 60K+ char diff, model
   processes before first response. Zero events between prompt send and first delta.

3. **Multi-round tool execution bursts**: Worker runs 5 tools in 30s, then thinks
   for 3 min, then runs 5 more. Bursty, not continuous.

4. **Sub-agent dispatch** (5-15 min): Worker dispatches 5 sub-agents via task tool.
   From our perspective, ONE tool call is running for 15 minutes. events.jsonl gets
   one `tool_execution_start` and nothing until `tool_execution_complete`.

### The Cardinal Rule

> **NEVER add a timeout or staleness check that can kill a session where the CLI
> server process is alive and the events.jsonl file was written AFTER the current
> turn started.** The 1800s multi-agent freshness window exists because workers
> genuinely need 30 minutes. If detection needs to be faster, fix the ROOT CAUSE
> (e.g., missing event handler) rather than tightening timeouts.

### Safe vs Unsafe Watchdog Changes

| Change | Safe? | Why |
|--------|-------|-----|
| Fix missing `.On(evt => ...)` handler registration | ✅ | Root cause fix, no timeout change |
| Reduce `WatchdogMultiAgentCaseBFreshnessSeconds` | ❌ | Workers genuinely run 20+ min |
| Add mtime staleness counter (kill after N unchanged checks) | ❌ | mtime may not reflect rapid writes during model thinking; use file-size growth (INV-O16) instead |
| Add file-size-growth check (`WatchdogCaseBMaxStaleChecks`) | ✅ | Detects dead connections without tightening the 1800s window; see below |
| Reduce `WatchdogMaxCaseBResets` from 40 | ❌ | 40 × 120s = 80 min, matches worker execution timeout |
| Add `IsOrphaned` flag to skip stale callbacks | ✅ | Guards stale state, no timeout change |
| Add event handler on revival path | ✅ | Root cause fix, no timeout change |
| Abort IsProcessing siblings during client recreation | ⚠️ | Safe IF orchestrator retries, but loses in-flight work |

### INV-O14: Watchdog Changes Must Pass Long-Running Tests

Every PR that modifies watchdog logic, Case B freshness, timeout constants, or
session lifecycle (revival, reconnect, dispose) **MUST** run
`LongRunningSessionSafetyTests` and verify all pass. These tests simulate:
- 20-minute workers with bursty event patterns
- 5-minute model thinking pauses (zero events)
- Revival mid-execution
- events.jsonl written once then frozen

If a test fails, the change would kill a legitimate long-running session in production.

### INV-O16: Case B File-Size-Growth Check (Dead Connection Detection)

Case B now checks **both** events.jsonl modification time freshness **and** file size
growth to detect dead connections. This addresses the scenario where
`ConnectionLostException` kills the JSON-RPC connection but the CLI process has
already written events to the file — so mtime stays fresh but no new data arrives.

**Constant**: `WatchdogCaseBMaxStaleChecks = 2`

**Mechanism**: On each Case B deferral, the watchdog records the current file size
of events.jsonl. If the file has not grown for `WatchdogCaseBMaxStaleChecks` (2)
consecutive deferrals, the session is force-completed — the connection is dead.

**Why this is safe (unlike mtime staleness)**:
- **mtime** can appear frozen during model thinking pauses (5+ min) due to
  filesystem timestamp granularity, causing false positives if used as a staleness
  signal.
- **File size** only stops growing when the CLI truly stops appending events. During
  model thinking between tool rounds, the CLI writes `AssistantMessageDeltaEvent`
  entries as the model streams tokens — the file keeps growing. Only a genuinely
  dead connection (e.g., `ConnectionLostException`) stops all writes.
- The 1800s freshness window (`WatchdogMultiAgentCaseBFreshnessSeconds`) is
  preserved — no regression for long-running workers (issue #365). The growth
  check is an **additional** safety layer that fires only when the file is fresh
  (mtime-wise) but not growing (dead connection).

**Timeline for dead connection detection**: ~360s (3 cycles: 1 baseline + 2 stale checks × ~120s each)
instead of 30+ minutes under the old mtime-only approach.

---

## Common Bugs & Mitigations

### Bug: Worker result lost on app restart

**Symptom**: Worker finished while app was restarting, result not in synthesis.

**Root cause**: Worker completed after `pending-orchestration.json` was read but
before `MonitorAndSynthesizeAsync` started polling.

**Mitigation**: Collect results from worker chat history post-dispatch timestamp,
not from live TCS tracking.

### Bug: Orchestrator stuck in "Waiting for workers"

**Symptom**: Phase shows "WaitingForWorkers" forever despite workers being idle.

**Root cause**: Worker's OnSessionComplete wasn't fired (incomplete cleanup path).

**Mitigation**: Ensure all IsProcessing=false paths fire OnSessionComplete
(currently 19+ paths across Events.cs, CopilotService.cs, Organization.cs,
Bridge.cs, and Providers.cs).

### Bug: Reflection loop processes stale user message

**Symptom**: User sent "stop" but loop continued with old task.

**Root cause**: Queued prompt not drained before iteration.

**Mitigation**: Drain `_reflectQueuedPrompts` at TOP of each iteration, before planning.

### Bug: Duplicate dispatches to same worker

**Symptom**: Worker receives task twice, confusing its context.

**Root cause**: ParseTaskAssignments returned duplicates (orchestrator repeated
@worker block).

**Mitigation**: Deduplicate assignments by worker name before dispatch.

### Bug: Session death after reconnect (PR #373)

**Symptom**: Sessions die instantly after connection recovery. "Thinking…" shows
briefly then clears. Multiple sessions in a group all die at once.

**Root cause**: Old and new `SessionState` share the SAME `Info` object. Stale
`SessionIdleEvent` from the orphaned old `CopilotSession` passes the generation
check and clears `IsProcessing` on the shared Info, killing the new session.

**Mitigation**: `IsOrphaned` volatile flag on SessionState, checked at all 5
entry points. Old state's `ProcessingGeneration` set to `long.MaxValue`.
Fresh `SessionState` created for siblings (not reused).

### Bug: Orchestration deadlock on reconnect

**Symptom**: Worker dispatch hangs forever during synthesis. Orchestrator never
completes. No errors in log.

**Root cause**: Reconnect orphaned a worker's state without calling
`TrySetCanceled()` on its `ResponseCompletion` TCS. The orchestrator's
`Task.WhenAll(workerTasks)` awaits forever.

**Mitigation**: Always `TrySetCanceled()` on old state's TCS during orphan.
`ExecuteWorkerAsync` catches `OperationCanceledException` and returns
`WorkerResult.Success = false`.

### Bug: Dead event stream after worker revival (discovered 2026-03-15)

**Symptom**: Worker shows "Thinking…" or "Working…" forever after being
re-dispatched. Diagnostic log shows `[SEND]` but zero `[EVT]` entries.
events.jsonl has 100+ events (CLI is working) but `HandleSessionEvent` never
fires. Watchdog Case B defers for 30+ minutes.

**Root cause**: `ExecuteWorkerAsync` revival path (Organization.cs ~line 1516)
creates a fresh `SessionState` and `CopilotSession` but did NOT call
`freshSession.On(evt => HandleSessionEvent(freshState, evt))`. Without the
event handler, the SDK transport delivers events to nobody. The session
completes server-side but the app never knows.

**Why it took 30+ min to detect**: Case B freshness check uses events.jsonl
mtime. The CLI wrote events to the file (mtime updates), so the freshness
check kept deferring. With `WatchdogMultiAgentCaseBFreshnessSeconds = 1800`,
it deferred for 30 min before the file aged out.

**Fix**: Register event handler on fresh session BEFORE sending, copy
`IsMultiAgentSession`, mark old state as `IsOrphaned`.

**Why NOT mtime staleness detection**: Initially considered tracking mtime
changes across consecutive checks to detect "wrote once then stopped." But
mtime-based detection risks false positives during long model thinking pauses
where filesystem timestamp granularity may not reflect ongoing writes.
The root cause fix (register handler) is the correct approach.

> **Note**: This concern is specific to **mtime-based** staleness. The **file-size-
> growth** check (INV-O16 / `WatchdogCaseBMaxStaleChecks`) does NOT share this
> false-positive risk: during model thinking, the CLI writes
> `AssistantMessageDeltaEvent` entries as tokens stream, so the file keeps growing.
> Only a dead connection stops all writes. See **Long-Running Session Safety** section.

**Additional mitigation (file-size-growth check)**: Even with the handler fix,
dead connections from `ConnectionLostException` can still cause 30+ min delays
under the mtime-only approach. The Case B file-size-growth check
(`WatchdogCaseBMaxStaleChecks = 2`) now detects this in ~360s (3 cycles: 1 baseline + 2 stale checks) by verifying
events.jsonl is actually growing, not just recently modified. See **INV-O16**.

### Bug: Steering cancels in-flight orchestration (PR #375)

**Symptom**: User sends a follow-up message to a busy orchestrator (e.g., "also
check PR #400" while workers are running). Instead of queuing, Dashboard routes
through `SteerSessionAsync` which bumps `ProcessingGeneration`, canceling the
in-flight orchestration `ResponseCompletion` TCS. `SendToMultiAgentGroupAsync`
gets `TaskCanceledException`. Workers complete but their results are never
collected. Orchestrator appears stuck.

**Root cause**: Dashboard.razor dispatch routing checked `IsProcessing` and
routed to `SteerSessionAsync` BEFORE checking if the session is an orchestrator.
Steering is designed for regular sessions where you want to redirect the agent.
For orchestrators, steering is destructive — it cancels the dispatch/synthesis
lifecycle.

**Fix**: Check `GetOrchestratorGroupId(sessionName)` WITHIN the `IsProcessing`
block, BEFORE the steer path. If session is an orchestrator, route to
`EnqueueMessage` instead. The queued message will be sent after the current
orchestration completes. Logged as `QUEUED_ORCH_BUSY` in event diagnostics.

**Key invariant**: Orchestrator sessions must NEVER be steered while processing.
Always queue. Workers CAN still be steered (useful for "stop" or "focus on X").

**Tests**: `MultiAgentRegressionTests.cs` — 8 tests in "Orchestrator-Steer
Conflict Tests (PR #375)" region:
- Structural test verifying orchestrator check appears before steer in Dashboard
- Verify EnqueueMessage (not steer) is used for orchestrators
- Verify non-orchestrator sessions still get steered
- Long-running orchestrator (15min) follow-up must queue

### Bug: Premature session.idle truncates orchestrator results (PR #375, FIXED in PR #399)

**Symptom**: CLI sends `session.idle` prematurely mid-turn (after only a few
tool rounds), then continues processing for 15+ more tool rounds. The
`CompleteResponse` fires on the premature idle, completing the
`ResponseCompletion` TCS with partial content. If this is a worker, the
orchestrator receives truncated results in synthesis.

**Root cause**: SDK/CLI bug — variant of bug #299 (missing idle). Instead of
missing the idle entirely, it sends it too early. The idle arrives, passes all
generation guards, and CompleteResponse runs with whatever content has been
flushed so far.

**Primary fix (PR #399 — IDLE-DEFER)**: `SessionIdleEvent` handler now checks
`HasActiveBackgroundTasks(idle)` (Events.cs:629). If `BackgroundTasks.Agents`
or `BackgroundTasks.Shells` has any entries, the handler flushes accumulated
text but does NOT call `CompleteResponse` — it breaks early and logs
`[IDLE-DEFER]`. The next `SessionIdleEvent` without background tasks triggers
the real completion. This prevents TCS completion with partial content.

**Defense-in-depth (still active)**:
- `[EVT-REARM]` (Events.cs:529): `AssistantTurnStartEvent` after premature idle
  re-arms IsProcessing and sets `PrematureIdleSignal`. Keeps UI showing "Working…".
- `RecoverFromPrematureIdleIfNeededAsync` (Organization.cs:1759): Polls signal +
  events.jsonl freshness to collect full response after TCS got partial content.
- `IsEventsFileActive` (Organization.cs:1946): Checks events.jsonl mtime < 15s.

These defense layers handle edge cases where `BackgroundTasks` data isn't
present (older CLI versions, or non-agent premature idles).

**Filed**: See GitHub issue for tracking.

---

## IDLE-DEFER & BackgroundTasks (PR #399)

> **This is the primary fix for premature idle in multi-agent workers.**
> Before PR #399, premature `session.idle` events caused truncated worker
> responses. The defense-in-depth layers (`EVT-REARM`, `RecoverFromPrematureIdleIfNeededAsync`)
> mitigated but didn't fully fix the problem for orchestrator TCS completion.

### How It Works

The CLI's `SessionIdleEvent` now includes a `Data.BackgroundTasks` object with:
- `Agents` — array of active background agent processes
- `Shells` — array of active shell/terminal processes

When a worker uses `task` tool to dispatch sub-agents, or runs shell commands,
these appear as background tasks. A `session.idle` with active background tasks
means "foreground processing quiesced, but background work is ongoing."

### Code Path (Events.cs:622-642)

```csharp
case SessionIdleEvent idle:
    CancelTurnEndFallback(state);
    
    if (HasActiveBackgroundTasks(idle))
    {
        Debug($"[IDLE-DEFER] ...");
        Invoke(() => {
            if (state.IsOrphaned) return;
            FlushCurrentResponse(state);  // Preserve accumulated text
            NotifyStateChangedCoalesced();
        });
        break; // Do NOT call CompleteResponse
    }
    
    // Only reach here when truly idle (no background tasks)
    CompleteResponse(state, idleGeneration);
```

### HasActiveBackgroundTasks (Events.cs:1856-1861)

```csharp
internal static bool HasActiveBackgroundTasks(SessionIdleEvent idle)
{
    var bt = idle.Data?.BackgroundTasks;
    if (bt == null) return false;
    return (bt.Agents is { Length: > 0 }) || (bt.Shells is { Length: > 0 });
}
```

### Impact on Multi-Agent Orchestration

1. **Workers dispatching sub-agents**: Previously, these would trigger premature
   completion when the foreground quiesced. Now correctly deferred.
2. **Workers running shell commands**: Same — deferred until shells complete.
3. **TCS ordering**: The `ResponseCompletion` TCS is no longer completed with
   partial content for the common case. Only edge cases (old CLI, missing
   BackgroundTasks data) fall through to the defense-in-depth layers.
4. **Content preservation**: `FlushCurrentResponse` is called on each deferred
   idle, ensuring accumulated text is preserved even if the app restarts.

### INV-O15: IDLE-DEFER Must Flush Before Breaking

The `FlushCurrentResponse(state)` call inside the IDLE-DEFER block is critical.
Without it, accumulated response text in `state.CurrentResponse` would be lost
if another idle event arrives or the app restarts. The flush preserves content
into `state.FlushedResponse` and chat history.

### Defense-in-Depth Layers (Still Active)

These layers remain as fallbacks for edge cases where IDLE-DEFER doesn't fire:

| Layer | Mechanism | When It Helps |
|-------|-----------|---------------|
| `[EVT-REARM]` | Re-arm IsProcessing on TurnStart after idle | Old CLI without BackgroundTasks |
| `PrematureIdleSignal` | ManualResetEventSlim set on re-arm | Signals ExecuteWorkerAsync to recover |
| `RecoverFromPrematureIdleIfNeededAsync` | Poll signal + events.jsonl freshness | Collect full response after partial TCS |
| `IsEventsFileActive` | events.jsonl mtime < 15s | Detect ongoing CLI activity |

### IDLE-DEFER-REARM (PR #472)

When multiple `session.idle` events arrive for a session with active `backgroundTasks`,
the first IDLE-DEFER correctly defers completion. But if `CompleteResponse` runs between
the first and second idle (e.g., triggered by a watchdog or another code path), then
`IsProcessing` is already `false` when the second idle arrives. Without re-arming,
background agents would finish their work but no completion event would fire — the
classic "zero-idle" symptom where the session appears stuck.

**IDLE-DEFER-REARM** detects this scenario and re-arms processing:

```csharp
case SessionIdleEvent idle:
    CancelTurnEndFallback(state);
    
    if (HasActiveBackgroundTasks(idle))
    {
        // ... existing IDLE-DEFER logic (flush, break) ...
        
        // REARM: If IsProcessing is already false, re-arm it so the
        // next idle (without background tasks) triggers CompleteResponse.
        if (!state.Info.IsProcessing)
        {
            state.Info.IsProcessing = true;
            state.Info.HasUsedToolsThisTurn = true; // 600s tool timeout
            RestartProcessingWatchdog(state);
            Debug($"[IDLE-DEFER-REARM] ...");
        }
        break;
    }
    
    CompleteResponse(state, idleGeneration);
```

**What the re-arm sets:**
- `IsProcessing = true` — so the next non-deferred idle fires `CompleteResponse`
- `HasUsedToolsThisTurn = true` — enables the 600s tool timeout instead of 120s,
  since background agents may run for several minutes
- Restarts the processing watchdog — provides a safety net if no further idle arrives
- Logs `[IDLE-DEFER-REARM]` to `event-diagnostics.log`

### `session.idle` Is Ephemeral — Never Persisted

> **⚠️ `session.idle` is NEVER written to `events.jsonl` by design.** It is a
> transient signal from the CLI, not a persisted event. Any restore or recovery
> logic that depends on reading `session.idle` from disk is fundamentally flawed.
> The poller in `PollEventsAndResumeWhenIdleAsync` watches for `session.shutdown`
> instead, which IS persisted.

This matters for IDLE-DEFER because after an app restart, there will be no
`session.idle` replay — the watchdog and resume logic handle completion detection
for interrupted sessions, not idle event replay.

---

## "Fix with Copilot" — Multi-Agent Awareness

### Current State

`BuildCopilotPrompt` in `SessionSidebar.razor` (line 2578) generates a fix
prompt for the external `copilot` CLI. **It is NOT multi-agent aware.**

When a user clicks "Fix with Copilot" on a session that's part of a
multi-agent group, the prompt should include:

### Required Context for Multi-Agent Fix

1. **Group membership**: session role (orchestrator/worker), group name, mode
2. **Worker list**: all workers in the group and their descriptions/models
3. **Event diagnostics**: last 30 lines of `event-diagnostics.log` for the group
4. **Multi-agent testing instructions**: the agent must verify orchestration
   still works after the fix (dispatch → worker execution → synthesis → cleanup)

### GetBugReportDebugInfo Enhancement

When `selectedBugSession` is part of a multi-agent group, include:

```
--- Multi-Agent Context ---
Group: PR Review Squad
Mode: Orchestrator
Role: orchestrator (or worker-2)
Workers: worker-1 (claude-sonnet-4.6), worker-2 (gpt-5.3-codex), ...
OrchestratorMode: OrchestratorReflect
PendingOrchestration: (contents or "none")
--- Recent Event Diagnostics (group) ---
[SEND] 'PR Review Squad-orchestrator' ...
[DISPATCH] ...
```

### BuildCopilotPrompt Enhancement

When the selected session is in a multi-agent group, append:

```
## Multi-Agent Testing Requirements
This session is part of a multi-agent group. After fixing:
1. Verify the fix doesn't break orchestration dispatch (DISPATCH-ROUTE → DISPATCH → SEND)
2. Test that workers still complete and report back to orchestrator
3. Check that PendingOrchestration is cleared after synthesis
4. Run `grep "$GROUP_NAME" ~/.polypilot/event-diagnostics.log | tail -30` to verify event flow
5. If modifying IsProcessing paths, verify all 9 companion fields are cleared (see INV-1)
6. If modifying reconnect paths, verify IsOrphaned guards (see INV-O9-O13)
```

---

## Live Testing Multi-Agent Orchestration

When testing multi-agent orchestration on a running PolyPilot instance, use
the event diagnostics log and these checklists to verify correct behavior.

### Quick Health Check (run this first)

```bash
GROUP="$GROUP_NAME"  # e.g., "PR Review Squad"

# 1. Is orchestrator processing?
grep "$GROUP-orchestrator" ~/.polypilot/event-diagnostics.log | tail -3

# 2. Are workers alive?
for w in 1 2 3 4 5; do
  last=$(grep "$GROUP-worker-$w'" ~/.polypilot/event-diagnostics.log 2>/dev/null | tail -1)
  [[ -n "$last" ]] && echo "W$w: $last"
done

# 3. Any completions or deferred idles?
grep "$GROUP" ~/.polypilot/event-diagnostics.log | grep -E "IDLE|IDLE-DEFER|COMPLETE|DISPATCH.*completed" | tail -10

# 4. Any errors?
grep "$GROUP" ~/.polypilot/event-diagnostics.log | grep -E "ERROR|WATCHDOG" | tail -5

# 5. PendingOrchestration state?
cat ~/.polypilot/pending-orchestration.json 2>/dev/null | head -3 || echo "(empty)"
```

### Monitoring Orchestrator Dispatch

```bash
grep "DISPATCH\|IDLE-DEFER" ~/.polypilot/event-diagnostics.log | grep "$GROUP" | tail -20
```

**Expected sequence for N-worker dispatch:**
```
[DISPATCH-ROUTE] session='<orch>' → mode=Orchestrator
[DISPATCH] SendToMultiAgentGroupAsync: group='<name>', members=N+1
[DISPATCH] Early dispatch: @worker blocks detected in flushed text
[IDLE] '<orch>' CompleteResponse dispatched
[COMPLETE] '<orch>' CompleteResponse executing
[DISPATCH] '<orch>' iteration 0: K raw assignments
[DISPATCH] Dispatching K tasks: worker-1, worker-2, ...
[DISPATCH] Saved pending orchestration
[SEND] 'worker-1' IsProcessing=true gen=1
[SEND] 'worker-2' IsProcessing=true gen=1  (1s later)
[SEND] 'worker-3' IsProcessing=true gen=1  (2s later)
```

### Monitoring Worker Execution

**Signs of healthy worker:**
- TurnStart/TurnEnd pairs cycling every 2-30 seconds (tool rounds)
- `[IDLE-DEFER]` entries when worker has active sub-agents (expected, not stuck)
- Eventually a SessionIdleEvent (no background tasks) → CompleteResponse → COMPLETE
- No [ERROR] or [WATCHDOG] entries

**Signs of stuck worker:**
- No TurnEnd/TurnStart for >120s (watchdog will catch at 120s or 600s)
- [WATCHDOG] entries appearing
- Worker stays in TurnStart without TurnEnd for >5 min (long tool call OK, but >10 min suspicious)

### Monitoring OrchestratorReflect Mode

Reflect mode runs multiple iterations. Expect this pattern:

```
[SEND] orchestrator gen=1       → Plan (dispatches W1)
[DISPATCH] Worker W1 completed  → Reflect synthesis
[SEND] orchestrator gen=2       → Evaluate, dispatch W2
[DISPATCH] Worker W2 completed  → Reflect synthesis
[SEND] orchestrator gen=3       → Evaluate, maybe dispatch both
...
[SEND] orchestrator gen=N       → Final synthesis (309 chars) → DONE
```

**Key observations from live testing (PR Review Squad + Evaluate Ortinau Skills):**
- Orchestrator mode: 3 workers, 1 round, 12 min total, 52s synthesis
- OrchestratorReflect mode: 2 workers, 7 iterations, 26 min total, progressively
  shorter responses indicating convergence (6701→4736→507→102 chars)
- Zero-assignment iteration (gen=11 had 0 assignments) handled correctly —
  orchestrator re-reflected and dispatched new assignments
- Duplicate IDLE events (SDK bug) handled gracefully — CompleteResponse skipped

### Full End-to-End Checklist

1. **Dispatch Phase**:
   - [ ] Orchestrator receives user prompt
   - [ ] DISPATCH-ROUTE logged with correct mode
   - [ ] Early dispatch detects @worker blocks
   - [ ] Correct number of assignments parsed
   - [ ] PendingOrchestration saved to disk before dispatch
   - [ ] Workers staggered with 1s delay
   - [ ] Each worker gets [SEND] with gen=1

2. **Worker Execution Phase**:
   - [ ] Each worker actively processes (TurnStart/TurnEnd cycling)
   - [ ] Workers with sub-agents show [IDLE-DEFER] entries (expected)
   - [ ] Watchdog Case B correctly defers when events.jsonl is fresh
   - [ ] File-size-growth check does NOT fire during active workers (file is growing)
   - [ ] No [ERROR] entries
   - [ ] Each worker eventually gets SessionIdleEvent (no BackgroundTasks) → CompleteResponse

3. **Collection Phase**:
   - [ ] After ALL workers complete, orchestrator synthesis triggered
   - [ ] Orchestrator gets [SEND] with new generation
   - [ ] No workers stuck in IsProcessing after completion
   - [ ] Duplicate IDLE events skipped ("IsProcessing already false")

4. **Synthesis Phase**:
   - [ ] Orchestrator processes synthesis
   - [ ] Orchestrator completes (SessionIdleEvent → CompleteResponse)
   - [ ] PendingOrchestration file empty/deleted

5. **Reflection Phase** (OrchestratorReflect only):
   - [ ] Orchestrator evaluates worker results after each iteration
   - [ ] New iterations dispatch fresh worker assignments
   - [ ] Zero-assignment iterations handled (re-reflect or terminate)
   - [ ] Response sizes decrease over iterations (convergence signal)
   - [ ] Orchestrator terminates after max iterations or goal met

6. **Error Recovery**:
   - [ ] Worker failure → WorkerResult.Success=false in synthesis
   - [ ] App restart mid-dispatch → PendingOrchestration resumes
   - [ ] Watchdog catches stuck sessions (120s idle / 600s tool)
   - [ ] Reconnect during orchestration → TCS canceled, workers get error result
   - [ ] IsOrphaned prevents stale callbacks from corrupting active sessions

### Common Live Test Failures

| Symptom | Diagnostic Command | Likely Cause |
|---------|-------------------|--------------|
| Workers never start | `grep "SEND.*worker" diagnostics.log` | Dispatch parse failed; check @worker format |
| One worker stuck | `grep "worker-N" diagnostics.log \| tail -5` | SDK bug, watchdog catches at 120-600s |
| Synthesis never sent | `grep "orchestrator.*SEND" diagnostics.log` | Task.WhenAll waiting; check for stuck worker |
| Orchestrator stuck post-synthesis | Check [WATCHDOG] entries | Zero-idle SDK bug; watchdog catches at 30s |
| PendingOrchestration stale | `cat ~/.polypilot/pending-orchestration.json` | Finally block didn't run; check for crash |
| All sessions die after reconnect | Check [RECONNECT] entries | IsOrphaned not set; see INV-O9 |
| Orchestration hangs on reconnect | Check for missing TrySetCanceled | TCS not canceled; see INV-O9 |
| Many IDLE-DEFER entries | `grep "IDLE-DEFER" diagnostics.log` | Normal — worker has active sub-agents; wait for completion |
| IDLE-DEFER but worker never completes | Check if background tasks are leaking | Sub-agent/shell not terminating; check CLI logs |
| IDLE-DEFER-REARM entries | `grep "IDLE-DEFER-REARM" diagnostics.log` | Normal — IsProcessing was false when deferred idle arrived; re-armed for next completion |
| Worker stuck 30+ min, events.jsonl fresh | Check file size across watchdog cycles | Dead connection — file-size-growth check should catch in ~360s (INV-O16) |

---

## Test Coverage & Gaps

### Existing Test Files

| File | Tests | Coverage |
|------|-------|----------|
| `MultiAgentRegressionTests.cs` | ~70 | Organization, reconciliation, presets, reflection bugs |
| `ReflectionCycleTests.cs` | ~95 | Sentinels, iteration, stall detection, evaluation |
| `ProcessingWatchdogTests.cs` | ~35 | Session state, abort, reconnect, watchdog constants |
| `MultiAgentGapTests.cs` | ~30 | @worker parsing, task assignments, delegation |

### ✅ Well-Covered

- PendingOrchestration save/load/clear (5 tests)
- @worker block parsing (20+ tests)
- Reflection iteration counting (8+ tests)
- Stall detection (5+ tests)
- IsProcessing flag ordering (3 tests)

### ❌ Critical Gaps (priority tests to add)

| Gap | Priority | What to test |
|-----|----------|-------------|
| ForceCompleteProcessingAsync | HIGH | All 9 INV-1 fields cleared, TCS resolved, timers canceled |
| Mixed worker success/failure synthesis | HIGH | 2 succeed + 1 fail → synthesis includes both |
| IsOrphaned guard coverage | HIGH | Event on orphaned state → no Info mutation |
| TryUpdate concurrency | HIGH | Stale Task.Run can't overwrite newer reconnect |
| Sibling TCS cancel on reconnect | HIGH | Orphaned worker's TCS → OperationCanceledException |
| Zero-assignment in reflect mode | MEDIUM | 0 assignments → re-reflect or terminate |
| Worker stagger delay | MEDIUM | 1s gap between worker [SEND] timestamps |
| Early dispatch edge cases | MEDIUM | Partial @worker blocks, orphaned blocks |

### Adding Tests — Quick Reference

Test stubs are in `PolyPilot.Tests/TestStubs.cs`. Key patterns:
```csharp
// Use Demo mode for success paths
var settings = new ConnectionSettings { Mode = ConnectionMode.Demo };
var service = new CopilotService(db, serverManager, bridgeClient, demoService);
await service.ReconnectAsync(settings);

// Never use Embedded mode (spawns real processes)
// Use Persistent with port 19999 for deterministic failures
```

When adding model classes, add `<Compile Include>` to `PolyPilot.Tests.csproj`.

---

## SDK-First Migration Guide

Before adding or modifying orchestration, dispatch, or worker management code, check if the Copilot SDK (v0.2.0+) already provides the capability.

### Migration Matrix

| Need | SDK API | Status | Notes |
|------|---------|--------|-------|
| Start parallel workers | `session.Rpc.Fleet.StartAsync()` | 🟢 **Adopted** | Used for fleet mode; custom orchestration uses `SendPromptAsync` for dispatch with persistence |
| Track subagent lifecycle | `SubagentStartedEvent` / `SubagentCompletedEvent` / `SubagentFailedEvent` | 🟡 **Events received** but not used for orchestration decisions |
| Select/deselect agents | `session.Rpc.Agent.SelectAsync()` / `DeselectAsync()` | 🟢 **Adopted** | Used in CopilotService.cs for agent selection |
| Manage skills per-session | `session.Rpc.Skills.ListAsync()` / `EnableAsync()` / `DisableAsync()` | 🔴 **Not adopted** | PolyPilot has custom `DiscoverAvailableSkills()` |
| Read/write session plan | `session.Rpc.Plan.ReadAsync()` / `UpdateAsync()` / `DeleteAsync()` | 🔴 **Not adopted** | Could surface plan in UI, enable user editing |
| Switch session mode | `session.Rpc.Mode.SetAsync(SessionModeGetResultMode.Plan)` | 🔴 **Not adopted** | Per-message mode set via `MessageOptions.Mode` (different API) |
| Switch model mid-session | `session.Rpc.Model.SwitchToAsync()` | 🔴 **Not adopted** | Available but not used; model changes go through session recreation |
| Set reasoning effort | `SessionConfig.ReasoningEffort` / `SessionModelSwitchToRequest.ReasoningEffort` | 🔴 **Not adopted** | Levels: "low", "medium", "high", "xhigh" |
| Restrict worker tools | `SessionConfig.AvailableTools` / `ExcludedTools` | 🔴 **Not adopted** | Could enforce tool restrictions per worker role |
| Register custom agents | `SessionConfig.CustomAgents` | 🔴 **Not adopted** | `CustomAgentConfig` with name, prompt, tools, MCP servers |
| Request structured user input | `session.Rpc.Ui.ElicitationAsync()` | 🔴 **Not adopted** | Schema-based structured input |
| Manage MCP servers | `session.Rpc.Mcp.ListAsync()` / `EnableAsync()` / `DisableAsync()` | 🔴 **Not adopted** | Per-session MCP management |
| Worker system prompt injection | `SessionConfig.SystemMessage` with `SectionOverride` | 🔴 **Not adopted** | Can append/prepend/replace system prompt sections |
| Handle slash commands | `session.Rpc.Commands.HandlePendingCommandAsync()` | 🔴 **Not adopted** | Programmatic slash command responses |
| Workspace file management | `session.Rpc.Workspace.ListFiles/ReadFile/CreateFile` | 🔴 **Not adopted** | Session workspace (plan.md, context files) |
| Hook into tool execution | `SessionConfig.Hooks` (PreToolUse/PostToolUse) | 🔴 **Not adopted** | Could enforce tool permissions per worker |
| Force agent to continue | `SubagentStop` hook with `decision: "block"` | 🔴 **JS SDK only** | Block completion and force another turn — could prevent workers stopping too early |

### What to Keep Custom (and Why)

| Custom Code | Why SDK Can't Replace It |
|-------------|-------------------------|
| **PendingOrchestration persistence** | Restart recovery via JSON — SDK Fleet has no equivalent persistence across app restarts |
| **@worker: block parsing** | PolyPilot-specific protocol for orchestrator→worker routing — not an SDK concern |
| **Reflection loop with quality scoring** | Custom evaluation + Jaccard similarity for stall detection — no SDK equivalent |
| **Worker result collection with timeouts** | Custom retry nudges, premature-idle recovery, partial result tolerance |
| **Multi-agent group management** | UI concerns: group creation, preset picker, squad integration, organization persistence |

### Rule

> **Before adding new orchestration/dispatch code:** Check this matrix. If the SDK has an API, use it. If not, add a `// SDK-gap: <reason>` comment explaining why custom code is needed. When the SDK ships new versions, re-check this matrix for newly available APIs.
