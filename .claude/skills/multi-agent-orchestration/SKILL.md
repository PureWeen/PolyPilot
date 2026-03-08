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
  concurrency (semaphores, queued prompts). Covers: 4-phase dispatch lifecycle, restart
  recovery via PendingOrchestration, worker failure patterns, and connection error handling.
---

# Multi-Agent Orchestration — Invariants & Error Recovery

> **Read this before modifying orchestration dispatch, worker execution, synthesis,
> reflection loops, or PendingOrchestration persistence.**

## Overview

PolyPilot's orchestration system coordinates work between an orchestrator session
and N worker sessions. This skill documents the invariants that prevent data loss,
stuck sessions, and coordination failures.

### Key Files

| File | Purpose |
|------|---------|
| `CopilotService.Organization.cs` | Orchestration engine — dispatch, synthesis, reflection |
| `CopilotService.Events.cs` | TCS completion, OnSessionComplete firing |
| `Models/SessionOrganization.cs` | Group/session metadata, modes, roles |
| `Models/ReflectionCycle.cs` | Reflection state, stall detection |

---

## The 4-Phase Orchestration Lifecycle

Every orchestrator dispatch (single-pass and reflect) follows these phases:

```
┌─────────────────────────────────────────────────────────────────┐
│  Phase 1: PLAN                                                   │
│  ├── Orchestrator receives user prompt + worker list             │
│  ├── Builds planning prompt with worker models/descriptions      │
│  ├── Orchestrator responds with @worker:name task blocks         │
│  └── ParseTaskAssignments extracts → List<TaskAssignment>        │
│       └── If no assignments: send nudge → retry parse            │
│           └── If still none: orchestrator handled directly       │
└─────────────────────────────────────────────────────────────────┘
                              │
                              ▼
┌─────────────────────────────────────────────────────────────────┐
│  Phase 2: DISPATCH                                               │
│  ├── SavePendingOrchestration() — BEFORE dispatching             │
│  ├── Fire OnOrchestratorPhaseChanged(Dispatching)                │
│  ├── Launch worker tasks in parallel: Task.WhenAll(workers)      │
│  └── Each worker gets: system prompt + original prompt + task    │
└─────────────────────────────────────────────────────────────────┘
                              │
                              ▼
┌─────────────────────────────────────────────────────────────────┐
│  Phase 3: COLLECT (WaitingForWorkers)                            │
│  ├── Await all worker completions (10-min timeout each)          │
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
```

### OrchestratorReflect: Extended Loop

OrchestratorReflect wraps phases 1–4 in a loop with evaluation:

```
while (IsActive && !IsPaused && CurrentIteration < MaxIterations):
    Phase 1–4 (as above)
    Phase 5: EVALUATE
    ├── With evaluator: score + rationale → RecordEvaluation()
    └── Self-eval: check for [[GROUP_REFLECT_COMPLETE]] sentinel
    Phase 6: STALL DETECTION
    ├── CheckStall() compares synthesis to previous
    └── 2 consecutive stalls → IsStalled = true → break
```

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

### Ordering Invariant (from processing-state-safety)

**INV-O3: IsProcessing = false BEFORE TrySetResult**

```csharp
// CORRECT ORDER in CompleteResponse:
state.Info.IsProcessing = false;  // 1. Clear processing state
OnSessionComplete?.Invoke(name);   // 2. Notify listeners
tcs.TrySetResult(response);        // 3. Complete TCS (may run sync continuation)
```

If TrySetResult runs first, the reflection loop's synchronous continuation may see
`IsProcessing = true` and fail to send the next prompt.

### INV-O4: OnSessionComplete fired on ALL termination paths

All 8 paths that clear IsProcessing (see processing-state-safety) must also fire
OnSessionComplete. Otherwise, orchestrator loops waiting on workers hang forever.

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

**Mitigation**: Ensure all 8 IsProcessing=false paths fire OnSessionComplete.

### Bug: Reflection loop processes stale user message

**Symptom**: User sent "stop" but loop continued with old task.

**Root cause**: Queued prompt not drained before iteration.

**Mitigation**: Drain `_reflectQueuedPrompts` at TOP of each iteration, before planning.

### Bug: Duplicate dispatches to same worker

**Symptom**: Worker receives task twice, confusing its context.

**Root cause**: ParseTaskAssignments returned duplicates (orchestrator repeated
@worker block).

**Mitigation**: Deduplicate assignments by worker name before dispatch:
```csharp
var assignments = rawAssignments
    .GroupBy(a => a.WorkerName, StringComparer.OrdinalIgnoreCase)
    .Select(g => new TaskAssignment(g.Key, string.Join("\n\n---\n\n", g.Select(a => a.Task))))
    .ToList();
```
