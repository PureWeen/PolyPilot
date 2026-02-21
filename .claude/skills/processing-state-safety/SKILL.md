---
name: processing-state-safety
description: >
  Safety guide for modifying IsProcessing, watchdog, session resume, or abort code paths in CopilotService.
  Use when: (1) Modifying any code that sets IsProcessing to true or false, (2) Changing watchdog timeout
  logic or adding new timeout paths, (3) Touching session resume/restore logic, (4) Modifying
  AbortSessionAsync or CompleteResponse, (5) Adding new processing-related fields to AgentSessionInfo
  or SessionState, (6) Debugging sessions stuck in "Thinking" state, (7) Reviewing PRs that touch
  CopilotService.Events.cs, CopilotService.cs, or CopilotService.Utilities.cs processing paths.
---

# Processing State Safety Guide

**This knowledge was hard-won across 7 PRs and 16 fix/regression cycles.** The stuck-session
bug (sessions permanently showing "Thinking...") is the single most recurring issue in this
codebase. Read this BEFORE modifying any processing-related code.

## The Core Invariant

**Every code path that sets `IsProcessing = false` MUST perform ALL of these steps:**

```csharp
FlushCurrentResponse(state);                              // BEFORE clearing — saves accumulated response
CancelProcessingWatchdog(state);
Interlocked.Exchange(ref state.ActiveToolCallCount, 0);
state.HasUsedToolsThisTurn = false;
state.Info.IsResumed = false;
state.Info.IsProcessing = false;
state.Info.ProcessingStartedAt = null;
state.Info.ToolCallCount = 0;
state.Info.ProcessingPhase = 0;
```

If you skip any of these, a future turn will inherit stale state and break.

## The 7 Paths That Clear IsProcessing

| # | Path | Location | Notes |
|---|------|----------|-------|
| 1 | CompleteResponse | CopilotService.Events.cs ~L699 | Normal completion (SessionIdleEvent) |
| 2 | SessionErrorEvent | CopilotService.Events.cs ~L517 | SDK error — wrapped in InvokeOnUI |
| 3 | Watchdog timeout | CopilotService.Events.cs ~L1192 | No events for 120s/600s — InvokeOnUI + generation guard |
| 4 | AbortSessionAsync (local) | CopilotService.cs ~L1681 | User clicks Stop |
| 5 | AbortSessionAsync (remote) | CopilotService.cs ~L1638 | Remote mode optimistic clear |
| 6 | SendAsync reconnect failure | CopilotService.cs ~L1600 | Reconnect+retry failed |
| 7 | SendAsync initial failure | CopilotService.cs ~L1613 | First send attempt failed |

**If you add a new path, or modify an existing one, audit ALL 7.**

## 7 Mistakes That Keep Recurring

### 1. Forgetting companion fields on error paths
**What happens**: You clear `IsProcessing` and `ProcessingPhase` but forget `IsResumed` or
`HasUsedToolsThisTurn`. Next turn inherits stale 600s timeout or stale tool state.
**PRs where this happened**: #148, #158, #164

### 2. Missing FlushCurrentResponse before clearing
**What happens**: Accumulated `CurrentResponse` (StringBuilder) content is silently lost.
User sees "Thinking..." then nothing — the partial response vanishes.
**PRs where this happened**: #158

### 3. Using ActiveToolCallCount alone as tool signal
**What happens**: `ToolExecutionStartEvent` dedup path on resume **skips `ActiveToolCallCount++`**
(line ~295 in Events.cs). So `hasActiveTool` is 0 even with a tool genuinely running.
`HasUsedToolsThisTurn` persists across tool rounds and is the reliable signal.
**PRs where this happened**: #148, #163

### 4. Adding hardcoded short timeouts for resume
**What happens**: A 10s timeout kills sessions that are legitimately processing (tool calls
take 30-60s between events). The watchdog's tiered 120s/600s approach is the correct mechanism.
**PRs where this happened**: #148

### 5. Mutating state on background threads
**What happens**: SDK events arrive on worker threads. `IsProcessing` write on a background
thread races with Blazor rendering on the UI thread. Use `InvokeOnUI()` for all `state.Info.*`
mutations from background code.
**PRs where this happened**: #147, #148, #163

### 6. Clearing IsResumed without checking tool activity
**What happens**: After resume, the dedup path leaves `ActiveToolCallCount` at 0, so
`hasActiveTool` is false. If you clear `IsResumed` based only on `HasReceivedEventsSinceResume`,
the 600s timeout drops to 120s and kills resumed mid-tool sessions.
**Guard condition**: `!hasActiveTool && !HasUsedToolsThisTurn`
**PRs where this happened**: #163

### 7. InvokeAsync in HandleComplete (Dashboard.razor)
**What happens**: `HandleComplete` is called from `CompleteResponse` via
`Invoke(SyncContext.Post)` — already on UI thread. Wrapping in `InvokeAsync` defers
`StateHasChanged()` to next render cycle, causing stale "Thinking" indicators.
**PRs where this happened**: #153

## Processing Watchdog Architecture

`RunProcessingWatchdogAsync` in `CopilotService.Events.cs` checks every 15 seconds:

**Two timeout tiers:**
- **120 seconds** (inactivity) — no tool activity at all
- **600 seconds** (tool execution) — when ANY of these are true:
  - `ActiveToolCallCount > 0` (tool actively running)
  - `IsResumed` (session resumed mid-turn after app restart)
  - `HasUsedToolsThisTurn` (tools used earlier in this turn — between rounds)

**Staleness check on restore**: `IsSessionStillProcessing` checks `File.GetLastWriteTimeUtc`
of `events.jsonl`. If >600s old, the session is treated as idle regardless of last event type.
Prevents sessions from being stuck after long app restarts.

**IsResumed clearing**: After events flow on a resumed session, the watchdog clears `IsResumed`
to transition 600s → 120s. Guarded by `!hasActiveTool && !HasUsedToolsThisTurn` (dispatched
via `InvokeOnUI`).

**Generation guard**: Watchdog captures `ProcessingGeneration` before posting the timeout
callback via `InvokeOnUI`. Inside the callback, it verifies the generation still matches.
This prevents a stale watchdog from killing a new turn if the user aborts + resends during
the async dispatch window.

## Thread Safety Rules

1. **All `state.Info.*` mutations from background threads** → `InvokeOnUI()`
2. **`HasUsedToolsThisTurn`, `HasReceivedEventsSinceResume`** → `Volatile.Write` on set, `Volatile.Read` on check (ARM memory model)
3. **`ActiveToolCallCount`** → `Interlocked.Increment`/`Decrement`/`Exchange` (concurrent tool starts/completions)
4. **`LastEventAtTicks`** → `Interlocked.Exchange`/`Read` (long requires atomic ops)
5. **`ProcessingGeneration`** → `Interlocked.Increment` on send, `Interlocked.Read` on check

## Diagnostic Log Tags

Every `IsProcessing = false` path MUST have a diagnostic log entry:

| Tag | Meaning |
|-----|---------|
| `[SEND]` | Prompt sent, IsProcessing set to true |
| `[EVT]` | SDK lifecycle event received |
| `[IDLE]` | SessionIdleEvent dispatched to CompleteResponse |
| `[COMPLETE]` | CompleteResponse executed or skipped |
| `[ERROR]` | SessionErrorEvent or SendAsync failure cleared IsProcessing |
| `[ABORT]` | User-initiated abort cleared IsProcessing |
| `[BRIDGE-COMPLETE]` | Bridge OnTurnEnd cleared IsProcessing |
| `[INTERRUPTED]` | App restart detected interrupted turn |
| `[WATCHDOG]` | Watchdog clearing IsResumed or timing out |
| `[RECONNECT]` | Session replaced after disconnect |

## Regression History (for context)

| PR | What broke | Root cause |
|----|-----------|------------|
| #141 | 120s timeout killed tool executions | Single timeout tier too aggressive |
| #147 | Stale IDLE killed new turns | Missing ProcessingGeneration guard |
| #148 | 10s resume timeout killed active sessions | Hardcoded short timeout |
| #148 | 120s during tool loops | ActiveToolCallCount reset between rounds |
| #148 | IsResumed leaked → permanent 600s | Not cleared on abort/error/watchdog |
| #153 | Stale "Thinking" renders | InvokeAsync deferred StateHasChanged |
| #158 | Response content silently lost | No FlushCurrentResponse before clearing |
| #163 | Resumed mid-tool killed at 120s | IsResumed cleared without tool guard |
| #164 | Processing fields not reset on error | New fields added to only some paths |
