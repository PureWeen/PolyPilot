---
name: processing-state-safety
description: >
  Checklist and invariants for modifying IsProcessing state, event handlers, watchdog,
  abort/error paths, or CompleteResponse in CopilotService. Use when: (1) Adding or
  modifying code paths that set IsProcessing=false, (2) Touching HandleSessionEvent,
  CompleteResponse, AbortSessionAsync, or the processing watchdog, (3) Adding new
  SDK event handlers, (4) Debugging stuck sessions showing "Thinking..." forever,
  (5) Modifying IsResumed, HasUsedToolsThisTurn, or ActiveToolCallCount,
  (6) Adding diagnostic log tags. Covers: 8 invariants from 7 PRs of fix cycles,
  the 8 code paths that clear IsProcessing, and common regression patterns.
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
7. Call `FlushCurrentResponse(state)` BEFORE clearing IsProcessing
8. Add a diagnostic log entry (`[COMPLETE]`, `[ERROR]`, `[ABORT]`, etc.)
9. Run on UI thread (via `InvokeOnUI()` or already on UI thread)

## The 8 Paths That Clear IsProcessing

| # | Path | File | Thread | Notes |
|---|------|------|--------|-------|
| 1 | CompleteResponse | Events.cs | UI (via Invoke) | Normal completion |
| 2 | SessionErrorEvent | Events.cs | Background → InvokeOnUI | SDK error |
| 3 | Watchdog timeout | Events.cs | Timer → InvokeOnUI | No events for 120s/600s |
| 4 | AbortSessionAsync (local) | CopilotService.cs | UI | User clicks Stop |
| 5 | AbortSessionAsync (remote) | CopilotService.cs | UI | Mobile stop |
| 6 | SendAsync reconnect failure | CopilotService.cs | UI | Prompt send failed after reconnect |
| 7 | SendAsync initial failure | CopilotService.cs | UI | Prompt send failed |
| 8 | Bridge OnTurnEnd | Bridge.cs | Background → InvokeOnUI | Remote mode turn complete |

## Content Persistence Safety

### Turn-End Flush
`FlushCurrentResponse` is called on `AssistantTurnEndEvent` to persist accumulated response text at each sub-turn boundary. Without this, response content between `assistant.turn_end` and `session.idle` is lost if the app restarts (the ReviewPRs bug — response content was lost on app restart).

### Dedup Guard on Resume
`FlushCurrentResponse` includes a dedup check: if the last non-tool assistant message in History has identical content, it skips the add and just clears `CurrentResponse`. This prevents duplicates when SDK replays events after session resume.

## 8 Invariants

### INV-1: Complete state cleanup
Every IsProcessing=false path clears ALL fields. See checklist above.

### INV-2: UI thread for mutations
ALL IsProcessing mutations go through UI thread via `InvokeOnUI()`.

### INV-3: ProcessingGeneration guard
Use generation guard before clearing IsProcessing. `SyncContext.Post` is
async — new `SendPromptAsync` can race between `Post()` and callback.

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
`HasUsedToolsThisTurn`, `HasReceivedEventsSinceResume` should use
`Volatile.Write`/`Volatile.Read`. ARM weak memory model issue.
(Currently partial — resets use plain assignment.)

### INV-8: No InvokeAsync in HandleComplete
`HandleComplete` is already on UI thread. `InvokeAsync` defers execution
causing stale renders.

## Top 4 Recurring Mistakes

1. **Incomplete cleanup** — modifying one IsProcessing path without
   updating ALL fields that must be cleared simultaneously.
2. **ActiveToolCallCount as sole tool signal** — gets reset/skipped
   in several paths; always check `HasUsedToolsThisTurn` too.
3. **Background thread mutations** — mutating IsProcessing or related
   state on SDK event threads instead of marshaling to UI thread.
4. **Missing content flush on turn boundaries** — `FlushCurrentResponse`
   must be called at every point where accumulated text could be lost
   (turn_end, tool_start, abort, error, watchdog). The turn_end call
   was missing until PR #224, causing response loss on app restart.

## Regression History

7 PRs of fix/regression cycles: #141 → #147 → #148 → #153 → #158 → #163 → #164.
See `references/regression-history.md` for the full timeline with root causes.
