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

## PR #276 — ChatDatabase Exception Filter
- All 9 ChatDatabase methods used narrow catch: `when (ex is SQLiteException or IOException or UnauthorizedAccessException)`
- SQLite async internals can throw `AggregateException`, `InvalidOperationException`, `ObjectDisposedException`
- All callers use fire-and-forget (`_ = _chatDb.AddMessageAsync(...)`) — uncaught exceptions became unobserved task exceptions
- Crash seen in 'FailedDelegation' session (3918 messages): `SQLiteAsyncConnection.WriteAsync` → `AggregateException` escaped filter
- **Fix**: Broadened all catch filters to `catch (Exception ex)`. Methods already return safe defaults and evict bad connections.
- **Key insight**: ChatDatabase is NOT on any IsProcessing cleanup path. It's a write-through cache; events.jsonl is the source of truth. DB failures self-heal on restart.
- **26 resilience tests** now cover: invalid paths, corrupt files, runtime corruption, file deletion, directory replacement, fire-and-forget unobserved exception pattern.

## Known Remaining Issues
- `HasUsedToolsThisTurn` resets use plain assignment (not Volatile.Write)
- InvokeOnUI dispatch for IsResumed adds 15s delay (one watchdog cycle)

## PR #284 — Multi-Agent Dispatch Bypass + Premature Watchdog on Restore
- **Root cause**: `ReconcileOrganization` was fully skipped during `IsRestoring=true`
  to prevent pruning sessions not yet loaded. But `CompleteResponse` runs during
  restore (when a restored turn completes), and it needs metadata to:
  1. Route through multi-agent dispatch (`GetOrchestratorGroupId`)
  2. Apply correct watchdog timeout (`IsSessionInMultiAgentGroup`)
- **Symptom 1**: Orchestrator generated `@worker` dispatch commands but they were
  silently ignored — dispatch pipeline bypassed because `GetOrchestratorGroupId`
  returned `null` (no metadata entry for the restored session)
- **Symptom 2**: Sessions killed as "stuck" after ~2 minutes — `IsMultiAgentSession`
  was never set on restored sessions, so watchdog used 120s instead of 600s
- **Fix 1**: `ReconcileOrganization(allowPruning: false)` — new additive mode that
  adds missing metadata without deleting anything. Called in `CompleteResponse`
  during the `IsRestoring` window.
- **Fix 2**: Set `state.IsMultiAgentSession` in `RestoreSingleSessionAsync` before
  `StartProcessingWatchdog` (reads from organization.json loaded at startup).
- **Pattern**: This is a variant of mistake #5 ("Missing state initialization on
  session restore") — the restore path must independently initialize ALL state that
  `SendPromptAsync` initializes, because events/watchdog/dispatch all depend on it.
