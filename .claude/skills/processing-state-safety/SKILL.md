---
name: processing-state-safety
description: >
  Checklist and invariants for modifying IsProcessing state, event handlers, watchdog,
  abort/error paths, or CompleteResponse in CopilotService. Use when: (1) Adding or
  modifying code paths that set IsProcessing=false, (2) Touching HandleSessionEvent,
  CompleteResponse, AbortSessionAsync, or the processing watchdog, (3) Adding new
  SDK event handlers, (4) Debugging stuck sessions showing "Thinking..." forever
  or spinner stuck, (5) Modifying IsResumed, HasUsedToolsThisTurn, or ActiveToolCallCount,
  (6) Adding diagnostic log tags, (7) Modifying session restore paths
  (RestorePreviousSessionsAsync / EnsureSessionConnectedAsync) that must initialize
  watchdog-dependent state,
  (8) Modifying ReconcileOrganization or any code that reads Organization.Sessions
  during the IsRestoring window, (9) Session appears hung or unresponsive after tool use.
  Covers: 18 invariants from 13 PRs of fix cycles,
  the 21+ code paths that set/clear IsProcessing, and common regression patterns.
---

# Processing State Safety

## When Clearing IsProcessing — The Checklist

Every code path that sets `IsProcessing = false` MUST also:
1. Clear `IsResumed = false`
2. Clear `HasUsedToolsThisTurn = false`
3. Clear `ActiveToolCallCount = 0`
4. Clear `ProcessingStartedAt = null`
5. Clear `ToolCallCount = 0`
6. Clear `ProcessingPhase = 0`
7. Clear `SendingFlag = 0` (prevents session deadlock on next send)
8. Clear `IsReconnectedSend = false` (prevents stale 35s timeout on next watchdog start)
9. Call `ClearPermissionDenials()`
10. Call `FlushCurrentResponse(state)` BEFORE clearing IsProcessing
11. Fire `OnSessionComplete` (unblocks orchestrator loops waiting for completion)
12. Add a diagnostic log entry (`[COMPLETE]`, `[ERROR]`, `[ABORT]`, etc.)
13. Run on UI thread (via `InvokeOnUI()` or already on UI thread)
14. After changes, run `ProcessingWatchdogTests.cs` to catch regressions

## The 16 Paths That Set/Clear IsProcessing

### Paths that CLEAR IsProcessing (→ false)

| # | Path | File | Thread | Tag | Notes |
|---|------|------|--------|-----|-------|
| 1 | CompleteResponse | Events.cs | UI (via Invoke) | `[COMPLETE]` | Normal completion |
| 2 | SessionErrorEvent | Events.cs | Background → InvokeOnUI | `[ERROR]` | SDK error |
| 3 | Watchdog timeout (kill) | Events.cs | Timer → InvokeOnUI | `[WATCHDOG]` | No events for 120s/600s, server dead, or max time exceeded (Case C) |
| 4 | Watchdog clean complete | Events.cs | Timer → InvokeOnUI | `[WATCHDOG]` | Tools done, lost terminal event → calls CompleteResponse (Case B, PR #332) |
| 5 | AbortSessionAsync (local) | CopilotService.cs | UI | `[ABORT]` | User clicks Stop |
| 6 | AbortSessionAsync (remote) | CopilotService.cs | UI | — | Mobile stop |
| 7 | SendAsync reconnect failure | CopilotService.cs | UI | `[ERROR]` | Prompt send failed after reconnect |
| 8 | SendAsync initial failure | CopilotService.cs | UI | `[ERROR]` | Prompt send failed |
| 9 | Bridge OnTurnEnd | Bridge.cs | Background → InvokeOnUI | `[BRIDGE-COMPLETE]` | Remote mode turn complete |
| 10 | Tool health recovery | Events.cs | Timer → InvokeOnUI | `[TOOL-HEALTH-COMPLETE]` | Dead connection detected by health check timer |
| 11 | Watchdog crash handler | Events.cs | Timer → InvokeOnUI | `[WATCHDOG-CRASH]` | Safety net when watchdog loop itself throws |
| 12 | Permission recovery failure | Events.cs | UI | `[PERMISSION-RECOVER]` | ClearProcessingStateForRecoveryFailure — recovery can't proceed |
| 13 | Permission recovery cleanup | Events.cs | UI | `[PERMISSION-RECOVER]` | After successful session resume, clears old state before resend |
| 14 | Steer error | CopilotService.cs | UI | `[STEER-ERROR]` | Soft steer SendAsync failure |
| 15 | ForceCompleteProcessing | Organization.cs | UI (via InvokeOnUI) | `[DISPATCH]` | Orchestration forces unstarted workers complete |

Additional clearing paths exist in `CopilotService.Providers.cs` (4 paths for external
provider OnTurnEnd/OnError/OnMemberTurnEnd/OnMemberError) and `DemoService.cs` (1 path).
These follow simpler patterns and don't participate in the SDK event flow.

### Path that RE-ARMS IsProcessing (→ true)

| # | Path | File | Thread | Tag | Notes |
|---|------|------|--------|-----|-------|
| 16 | TurnStart re-arm | Events.cs | Background → InvokeOnUI | `[EVT-REARM]` | Premature session.idle recovery (PR #375) |

Path #16 fires when `AssistantTurnStartEvent` arrives with `IsProcessing=false` on the
current non-orphaned state. This detects premature `session.idle` (SDK sends idle mid-turn
then continues). Re-arm sets `IsProcessing=true`, restarts the watchdog, and logs `[EVT-REARM]`.
Does NOT create a new TCS — the old one was already completed with partial content.

> **Note:** EVT-REARM is now a **secondary defense**. The primary fix is IDLE-DEFER (PR #399),
> which prevents premature completion in the first place by checking `BackgroundTasks`.
> EVT-REARM remains as defense-in-depth for edge cases where `BackgroundTasks` is null.

### Path that DEFERS completion (IsProcessing stays true)

| Path | File | Tag | Notes |
|------|------|-----|-------|
| IDLE-DEFER | Events.cs | `[IDLE-DEFER]` | SessionIdleEvent with active background tasks (PR #399) |

When `HasActiveBackgroundTasks(idle)` returns true (sub-agents or shells running),
`SessionIdleEvent` flushes text via `FlushCurrentResponse` but does NOT call
`CompleteResponse`. Processing stays active. The watchdog and future idle events
(without background tasks) handle eventual completion.

## Content Persistence Safety

### Turn-End Flush
`FlushCurrentResponse` is called on `AssistantTurnEndEvent` to persist accumulated response text at each sub-turn boundary. Without this, response content between `assistant.turn_end` and `session.idle` is lost if the app restarts (the ReviewPRs bug — response content was lost on app restart).

### Dedup Guard on Resume
`FlushCurrentResponse` includes a dedup check: if the last non-tool assistant message in History has identical content, it skips the add and just clears `CurrentResponse`. This prevents duplicates when SDK replays events after session resume.

### ChatDatabase Resilience (PR #276)
`ChatDatabase` methods catch ALL exceptions (`catch (Exception ex)`) — not just specific types.
All 15 `_ = _chatDb.AddMessageAsync(...)` callers in CopilotService are fire-and-forget.
If the catch filter is too narrow, uncaught exceptions become **unobserved task exceptions**
that crash the app. The DB is a write-through cache; `events.jsonl` is the source of truth
and replays on session resume via `BulkInsertAsync`. DB write failures are self-healing.
**NEVER narrow the ChatDatabase catch filters** — use `catch (Exception ex)` always.

## 18 Invariants

### INV-1: Complete state cleanup
Every IsProcessing=false path clears ALL fields. See checklist above.

### INV-2: UI thread for mutations
ALL IsProcessing mutations go through UI thread via `InvokeOnUI()`.

### INV-3: ProcessingGeneration guard
Use generation guard before clearing IsProcessing. `SyncContext.Post` is
async — new `SendPromptAsync` can race between `Post()` and callback.

```csharp
// Capture BEFORE posting to UI thread
var gen = Interlocked.Read(ref state.ProcessingGeneration);
InvokeOnUI(() =>
{
    // Validate INSIDE the callback — abort if a new turn started
    if (Interlocked.Read(ref state.ProcessingGeneration) != gen) return;
    // Safe to clear IsProcessing here
});
```

### INV-4: No hardcoded short timeouts
NEVER add hardcoded short timeouts for session resume. The watchdog
(120s/600s) with tiered approach is the correct mechanism.

### INV-5: HasUsedToolsThisTurn > ActiveToolCallCount
`ActiveToolCallCount` alone is insufficient. `AssistantTurnStartEvent`
resets it between tool rounds. `HasUsedToolsThisTurn` persists.

### INV-6: IsResumed scoping
`IsResumed` scoped to mid-turn resumes (`isStillProcessing=true`).
Cleared on ALL termination paths. Extends watchdog to 600s.
Clearing guarded on `!hasActiveTool && !HasUsedToolsThisTurn`.

### INV-7: Volatile for cross-thread fields
When adding NEW cross-thread boolean/int flags, use `Volatile.Write`/`Volatile.Read`
for ARM weak memory model correctness. Existing fields `HasUsedToolsThisTurn` and
`HasReceivedEventsSinceResume` use plain assignment (pre-existing inconsistency —
tracked separately, do not fix inline). Do NOT introduce additional plain-assignment
cross-thread fields without a tracking comment explaining the gap.

### INV-8: No InvokeAsync in HandleComplete
`HandleComplete` is already on UI thread. `InvokeAsync` defers execution
causing stale renders.

### INV-9: Session restore must initialize all watchdog-dependent state
The restore path (`RestorePreviousSessionsAsync` + `EnsureSessionConnectedAsync`) is
separate from `SendPromptAsync`. Any field that affects watchdog timeout selection or
dispatch routing must be initialized in BOTH paths:
- `IsMultiAgentSession` — set via `IsSessionInMultiAgentGroup()` before `StartProcessingWatchdog`
- `HasReceivedEventsSinceResume` / `HasUsedToolsThisTurn` — set via `GetEventsFileRestoreHints()`
- `IsResumed` — set on the `AgentSessionInfo` when `isStillProcessing` is true

When `ReconcileOrganization` hasn't run yet (during `IsRestoring` window),
`Organization.Sessions` metadata may be stale. Any code that reads metadata
during this window must call `ReconcileOrganization(allowPruning: false)` first.
This additive mode safely adds missing entries without pruning loading sessions.

### INV-10: TurnEnd fallback must not be permanently suppressed (PR #332)
The `AssistantTurnEndEvent` 4s fallback → `CompleteResponse` guards against
premature firing during multi-tool sessions. **Do NOT** use `HasUsedToolsThisTurn`
to skip this fallback entirely — that permanently disables recovery for all
agent sessions and leaves them 100% dependent on `SessionIdleEvent`. If that
event is dropped (SDK bug #299), the session sticks for 600s.

**Correct approach**: Use `ActiveToolCallCount > 0` to skip the 4s fallback
(tools are still running). If tools are done (`ActiveToolCallCount == 0`) but
`HasUsedToolsThisTurn` is true, use an extended 30s delay
(`TurnEndIdleToolFallbackAdditionalMs = 30_000`). The cancellation token from
`AssistantTurnStartEvent` is the correct mechanism to prevent premature firing
when the LLM does multi-round tool use.

### INV-11: Watchdog must distinguish active tools from lost events (PR #332)
Blindly waiting the full 600s tool timeout when `ActiveToolCallCount == 0`
(tools finished) is wrong — the SDK may have silently dropped the terminal event
(`SessionIdleEvent`). The watchdog timeout path must use a 3-way branch:

- **Case A** (`hasActiveTool && server alive`): Probe `_serverManager.IsServerRunning()`
  (TCP port check). If alive → reset `LastEventAtTicks` and continue. If dead → fall through to kill.
- **Case B** (`!hasActiveTool && HasUsedToolsThisTurn && !exceededMaxTime`): Call
  `CompleteResponse` cleanly (no error message) then `break`. Lost terminal event scenario.
- **Case C** (default): Kill with "⚠️ Session appears stuck" error message. Max time
  exceeded, server dead, or something genuinely wrong.

This prevents the "10-minute kill" where tools ran successfully but the session
was murdered because the SDK dropped the follow-up `SessionIdleEvent`.

### INV-11b: Case B file-size-growth dead-connection detection
When Case B defers because `events.jsonl` modification time is within the freshness
window (1800s for multi-agent sessions), it ALSO checks whether the file has actually
grown since the last deferral. A stale modification time (e.g., after
`ConnectionLostException` kills the JSON-RPC connection) can keep sessions stuck for
up to 30 minutes without this guard.

**Fields and constant:**

| Name | Type | Purpose |
|------|------|---------|
| `WatchdogCaseBLastFileSize` | `long` | File size (bytes) at previous deferral check |
| `WatchdogCaseBStaleCount` | `int` | Consecutive watchdog checks with no file growth |
| `WatchdogCaseBMaxStaleChecks` | `const int = 2` | Max stale checks before deferral stops |

**Logic:** On each Case B deferral, compare the current `events.jsonl` file size to
`WatchdogCaseBLastFileSize`. If the file has grown → reset `WatchdogCaseBStaleCount`
to 0 and update `WatchdogCaseBLastFileSize`. If the file has NOT grown → increment
`WatchdogCaseBStaleCount`. When `WatchdogCaseBStaleCount >= WatchdogCaseBMaxStaleChecks`,
stop deferring — the connection is dead even though the file's modification time
looks fresh.

**Detection time:** ~6 minutes (1 baseline cycle + 2 stale checks × ~120s each) instead of 30 minutes.

**Resets:** Both `WatchdogCaseBLastFileSize` and `WatchdogCaseBStaleCount` are reset
to 0 in three places:
1. When real SDK events arrive (line 233-235 in Events.cs) — connection is alive
2. In `StartProcessingWatchdog` — new watchdog cycle begins
3. In `ForceCompleteProcessingAsync` — orchestration forces completion

**Why not just shorten the freshness window?** Multi-agent sessions legitimately have
long periods between events (workers running tools for minutes). The freshness window
must stay large for correctness. File-size growth is the right signal: a live
connection always appends to `events.jsonl`, a dead one never does.

### INV-12: All background→UI dispatches must capture ProcessingGeneration (PR #332)
Any code that posts work to the UI thread from a background thread (watchdog loop,
`Task.Run`, timer callbacks) must:
1. Capture `var gen = Interlocked.Read(ref state.ProcessingGeneration)` **before** the `InvokeOnUI` call
2. Validate `if (Interlocked.Read(ref state.ProcessingGeneration) != gen) return;` **inside** the lambda

Without this guard, a stale watchdog tick (racing with abort+resend) can flush
content from a new turn into the old turn's history. Every Case B and Case C
watchdog callback has this guard; the periodic flush callback must too.

### INV-13: Use InvokeOnUI() (class method) in Task.Run closures (PR #332)
The local `Invoke(Action)` function inside `HandleSessionEvent` (declared at
line ~249) can have scoping ambiguity when captured by `Task.Run` closures.
Use the class-level `InvokeOnUI()` method in all `Task.Run` and timer callbacks
for explicit, unambiguous UI thread dispatch. The local `Invoke` works but the
intent is less clear when reading cross-threaded code.

### INV-14: IsOrphaned guards on all event/timer entry points (PR #373)
When a `SessionState` is orphaned (after reconnect creates a replacement):
1. Set `state.IsOrphaned = true` (volatile)
2. Set `ProcessingGeneration = long.MaxValue` (prevents any generation check from passing)
3. Call `state.ResponseCompletion?.TrySetCanceled()` (unblocks orchestrator waits)

ALL event/timer entry points must check `state.IsOrphaned` and return immediately:
- `HandleSessionEvent` (line ~214)
- `CompleteResponse` (line ~913) — TrySetCanceled + return
- Watchdog loop (line ~1820) — exit loop
- Watchdog InvokeOnUI callbacks (line ~2095) — skip
- Tool health/recovery handlers — skip

Without this, stale SDK events from the disposed old `CopilotSession` pass through
to the shared `Info` object and corrupt the replacement session's state.

### INV-15: TryUpdate for atomic state swaps (PR #373)
When replacing a `SessionState` in `_sessions` after reconnect, use
`_sessions.TryUpdate(key, newState, expectedOldState)` instead of
`_sessions[key] = newState`. This prevents a stale `Task.Run` (from an earlier
reconnect) from overwriting a newer reconnect's state. If TryUpdate fails,
discard the result — someone else already updated.

### INV-16: Register handler BEFORE publishing to dictionary (PR #373)
When creating a new `SessionState` (reconnect or sibling re-resume):
```csharp
resumed.On(evt => HandleSessionEvent(newState, evt));  // 1. Handler first
_sessions.TryUpdate(key, newState, oldState);           // 2. Publish second
```
If reversed, a race window exists where events arrive before the handler is
registered, and those events are lost permanently.

### INV-17: Sibling re-resume must reload MCP servers (PR #373)
Both the primary reconnect path and the sibling loop must call:
- `cfg.LoadMcpServers()` — MCP server handles are tied to the disposed client
- `cfg.LoadSkillDirectories()` — same issue

The primary path was missing these until PR #373 Round 5. Asymmetry between
the sibling and primary reconnect configs is a recurring bug pattern.

### INV-18: SessionIdleEvent is not always terminal (PR #399)
`SessionIdleEvent` with active background tasks (`HasActiveBackgroundTasks()` returns true)
means "foreground quiesced, background still running" — NOT true completion.
This path:
1. Cancels the TurnEnd→Idle fallback timer
2. Flushes accumulated text via `FlushCurrentResponse` (preserves content)
3. Does **NOT** call `CompleteResponse` — `IsProcessing` stays `true`
4. Logs `[IDLE-DEFER]` with task counts

The watchdog continues running and will eventually time out if background tasks never
finish. A subsequent `SessionIdleEvent` without background tasks completes normally.
**Do NOT** add `IsProcessing = false` to the IDLE-DEFER path — it would prematurely
complete the response while sub-agents are still working.

## Top 5 Recurring Mistakes

1. **Incomplete cleanup** — modifying one IsProcessing path without
   updating ALL fields that must be cleared simultaneously.
2. **Suppressing the TurnEnd fallback for tool sessions** — using `HasUsedToolsThisTurn`
   to skip the fallback entirely leaves agent sessions with zero recovery when
   `SessionIdleEvent` is dropped. Use `ActiveToolCallCount` to guard and an
   extended delay for the tool-used case. (PR #332)
3. **Background thread mutations** — mutating IsProcessing or related
   state on SDK event threads instead of marshaling to UI thread.
4. **Missing content flush on turn boundaries** — `FlushCurrentResponse`
   must be called at every point where accumulated text could be lost
   (turn_end, tool_start, abort, error, watchdog). The turn_end call
   was missing until PR #224, causing response loss on app restart.
5. **Missing state initialization on session restore** — `IsMultiAgentSession`,
   `IsResumed`, and other flags must be set on restored sessions BEFORE
   `StartProcessingWatchdog` is called. The restore path in
   `RestorePreviousSessionsAsync` / `EnsureSessionConnectedAsync` is separate
   from `SendPromptAsync` and must independently initialize all state the watchdog
   depends on. PR #284 fixed `IsMultiAgentSession` not being set during restore,
   causing the watchdog to use 120s instead of 600s for multi-agent workers.

**Retired mistake (was #2):** *ActiveToolCallCount as sole tool signal* — still relevant per
INV-5, but the more impactful version is #2 above (suppressing the fallback entirely).

## Diagnosing a Stuck Session

When a session shows "Thinking..." indefinitely:

1. **Check the diagnostic log** — `~/.polypilot/event-diagnostics.log`
   ```bash
   grep 'SESSION_NAME' ~/.polypilot/event-diagnostics.log | tail -20
   ```
   Look for the last `[SEND]` (turn started) and whether `[IDLE]` or `[COMPLETE]` followed.

2. **Check if the watchdog is running** — look for `[WATCHDOG]` entries after the `[SEND]`.
   If none appear, the watchdog wasn't started (see INV-9 for restore path issues).

3. **Check `IsProcessing` state** — via MauiDevFlow CDP:
   ```bash
   maui-devflow cdp Runtime evaluate "document.querySelector('.processing-indicator')?.textContent"
   ```

4. **Common stuck patterns:**
   | Symptom | Likely Cause | Fix |
   |---------|-------------|-----|
   | `[SEND]` then silence | SDK never responded, watchdog will catch at 120s | Wait or abort |
   | `[EVT] TurnEnd` but no `[IDLE]` | `session.idle` is ephemeral (never on disk). If live events stopped: IDLE-DEFER deferred the idle and `IsProcessing` was cleared before the follow-up arrived (#403) | Watchdog catches; #403 fix re-arms IsProcessing |
   | `[IDLE-DEFER]` then long silence | Background tasks (sub-agents/shells) active | Check `[IDLE-DIAG]` for backgroundTasks count; watchdog will eventually catch (INV-18) |
   | `[IDLE-DEFER]` with `IsProcessing=False` | IDLE-DEFER fired but IsProcessing was already cleared by watchdog/reconnect | #403 IDLE-DEFER-REARM fix re-arms IsProcessing |
   | `[COMPLETE]` fired but spinner persists | UI thread not notified | Check INV-2, INV-8 |
   | `[WATCHDOG]` clears but re-sticks | New turn started before watchdog callback ran | Check INV-3 generation guard |
   | After relaunch: session shows "Working" for 600s | Session was active on CLI during relaunch; poll-then-resume waiting for `session.shutdown` (only disk-persisted terminal event) | Normal — watchdog clears at 600s. `session.idle` is ephemeral, never on disk |

5. **Nuclear option** — user clicks Stop (AbortSessionAsync, path #5/#6).

## Key Facts About session.idle

- `session.idle` is **`ephemeral: true`** in the SDK schema — intentionally NOT written to events.jsonl
- Events written to disk: `session.start`, `session.resume`, `session.shutdown` — NOT `session.idle`
- PolyPilot receives `SessionIdleEvent` over the live SDK event stream
- CLI correctly populates `backgroundTasks` field (proven with `[IDLE-DIAG]` instrumentation)
- When `backgroundTasks` is active, IDLE-DEFER defers completion until a subsequent idle with empty backgroundTasks
- After app relaunch, the poll-then-resume pattern watches events.jsonl for `session.shutdown` only (the only terminal event on disk)

## Regression History

10 PRs of fix/regression cycles: #141 → #147 → #148 → #153 → #158 → #163 → #164 → #276 → #284 → #332.
Additional safety PRs: #373 (orphaned state guards), #375 (premature idle re-arm), #399 (IDLE-DEFER for background tasks), #472 (poll-then-resume + IDLE-DEFER-REARM + model selection).
See `references/regression-history.md` for the full timeline with root causes.
