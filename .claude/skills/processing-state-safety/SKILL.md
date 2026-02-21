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

Modifying processing state code involves these steps:

1. Identify which of the 7 cleanup paths you're touching
2. Apply the cleanup checklist to your change
3. Verify all 7 paths still satisfy the checklist
4. Ensure thread safety rules are followed

If debugging a stuck session, see [references/regression-history.md](references/regression-history.md)
for the 7 common mistakes and full regression timeline across 7 PRs.

## The Cleanup Checklist

Every code path that sets `IsProcessing = false` MUST perform ALL of these:

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

Skip any field not applicable to the path (e.g., remote mode has no `ActiveToolCallCount`).

## The 7 Cleanup Paths

| # | Path | Location | Notes |
|---|------|----------|-------|
| 1 | CompleteResponse | Events.cs ~L699 | Normal completion via SessionIdleEvent (saves response inline, not via FlushCurrentResponse) |
| 2 | SessionErrorEvent | Events.cs ~L517 | SDK error — wrapped in InvokeOnUI |
| 3 | Watchdog timeout | Events.cs ~L1192 | InvokeOnUI + generation guard |
| 4 | AbortSessionAsync (local) | CopilotService.cs ~L1681 | User clicks Stop |
| 5 | AbortSessionAsync (remote) | CopilotService.cs ~L1638 | Remote mode optimistic clear |
| 6 | SendAsync reconnect failure | CopilotService.cs ~L1600 | Reconnect+retry failed |
| 7 | SendAsync initial failure | CopilotService.cs ~L1613 | First send attempt failed |
| 8 | Bridge OnTurnEnd | Bridge.cs ~L127 | Remote mode normal turn completion — InvokeOnUI |

**When adding a new field to AgentSessionInfo or SessionState**, add its reset to ALL 8 paths.
**When adding a new cleanup path**, copy the full checklist from an existing path (path 3 is the most complete).

## Key Watchdog Rules

- **Two timeout tiers**: 120s inactivity, 600s tool execution
- **600s triggers when**: `ActiveToolCallCount > 0` OR `IsResumed` OR `HasUsedToolsThisTurn`
- **Never add timeouts shorter than 120s** for resume — tool calls gap 30-60s between events
- **`ActiveToolCallCount` returns to 0 between tool rounds** — `AssistantTurnStartEvent` resets it to 0 (line ~365). Between rounds the model reasons about the next tool call, so `hasActiveTool` is 0 even though the session is actively working. Always check `HasUsedToolsThisTurn` too
- **IsResumed clearing** must guard on `!hasActiveTool && !HasUsedToolsThisTurn`
- **Staleness check**: `IsSessionStillProcessing` uses `File.GetLastWriteTimeUtc` >600s = idle

## Thread Safety Rules

- All `state.Info.*` mutations from background threads → `InvokeOnUI()`
- `HasUsedToolsThisTurn`, `HasReceivedEventsSinceResume` → `Volatile.Write`/`Read`
- `ActiveToolCallCount` → `Interlocked` operations only
- Capture `ProcessingGeneration` before `SyncContext.Post`, verify inside callback

For detailed thread safety patterns and the full regression history, see
[references/regression-history.md](references/regression-history.md).
