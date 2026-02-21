# Regression History — Stuck Session Fixes

## PR #141 — Original Watchdog
- **Fix**: 120s inactivity watchdog checks every 15s, clears IsProcessing
- **Regression**: 120s too aggressive for tool executions (builds run 5+ min)

## PR #147 — Tool Timeout + SEND/COMPLETE Race
- Two-tier timeout: 120s inactivity, 600s tool execution
- ProcessingGeneration counter prevents stale IDLE from clearing new turn
- Start watchdog on restored sessions (was missing → stuck forever)
- Watchdog callback needs generation guard + InvokeOnUI
- **Regression**: Watchdog callback originally lacked generation guard

## PR #148 — Resume Timeouts + IsResumed
- Remove 10s resume timeout (killed active sessions)
- HasUsedToolsThisTurn flag (ActiveToolCallCount resets between tool rounds)
- Scope IsResumed to mid-turn only
- **Regression 1**: 10s resume timeout killed actively working sessions
- **Regression 2**: 120s timeout during tool loops (ActiveToolCallCount reset)
- **Regression 3**: IsResumed not cleared on abort/error/watchdog

## PR #153 — Thread Safety
- Session freezing from InvokeAsync in HandleComplete
- **Regression**: InvokeAsync defers StateHasChanged → stale renders

## PR #158 — Response Lost
- FlushCurrentResponse BEFORE clearing IsProcessing on all paths
- **Root cause**: Accumulated CurrentResponse silently lost

## PR #164 — Processing Status Fields
- ProcessingPhase, ToolCallCount, ProcessingStartedAt
- **Regression caught in review**: ToolCallCount needs Interlocked

## PR #163 — Staleness Check + IsResumed Clearing
- Staleness check on events.jsonl (>600s = idle)
- Clear IsResumed after events flow
- **Regression caught in review**: IsResumed clearing didn't guard on tool activity
- Fixed: Guard on `!hasActiveTool && !HasUsedToolsThisTurn`, InvokeOnUI
- Bridge OnTurnEnd (8th path) identified as missing full cleanup

## Known Remaining Issues
- `HasUsedToolsThisTurn` resets use plain assignment (not Volatile.Write)
- InvokeOnUI dispatch for IsResumed adds 15s delay (one watchdog cycle)
