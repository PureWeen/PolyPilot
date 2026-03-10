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

## PR #332 — Rescue Stuck Chat Sessions (TurnEnd Fallback + Smart Watchdog)

### Root Cause 1: TurnEnd fallback permanently disabled for tool-using sessions
- **Symptom**: Messages stop streaming mid-response (appear stuck, need "prod" to continue).
  More precisely: `SessionIdleEvent` is lost (SDK bug #299) and nothing else fires `CompleteResponse`.
- **Root cause**: `HasUsedToolsThisTurn = true` caused the 4s `AssistantTurnEndEvent` fallback
  to be **completely skipped** (not just delayed). For any session that ever used tools, the guard
  permanently disabled recovery. The session then waited the full 600s watchdog timeout.
- **Fix**: Use `ActiveToolCallCount > 0` to skip the 4s fallback (tools genuinely running).
  When `ActiveToolCallCount == 0` but `HasUsedToolsThisTurn == true`, wait an additional 30s
  (`TurnEndIdleToolFallbackAdditionalMs = 30_000`) then fire `CompleteResponse`. Total wait: 34s
  instead of 600s. The `AssistantTurnStartEvent` CTS cancels the fallback if the LLM starts a new round.

### Root Cause 2: Watchdog couldn't distinguish active tools from lost SDK events
- **Symptom**: Sessions with long-running tools (builds, tests) killed after 600s even when
  the tool finished successfully and the SDK dropped the terminal event.
- **Root cause**: Watchdog timeout path only had one behavior: kill with error. It couldn't tell
  "tool still running" from "tool done, lost SessionIdleEvent."
- **Fix**: 3-way branch in `RunProcessingWatchdogAsync` at the timeout threshold:
  - **Case A** (`hasActiveTool && !exceededMaxTime && !demo && !remote`):
    Probe `_serverManager.IsServerRunning()` (TCP check). If alive → reset `LastEventAtTicks` + continue.
    If dead → fall through to Case C (kill).
  - **Case B** (`!hasActiveTool && HasUsedToolsThisTurn && !exceededMaxTime`):
    Call `CompleteResponse(state, watchdogGen)` then `break`. Clean completion, no error message.
    This is now **Path #4 that clears IsProcessing** (added to the table in SKILL.md).
  - **Case C** (default): Kill with "⚠️ Session appears stuck" error (original behavior).

### Additional fixes
- **Periodic watchdog flush** — Every 15s, if `CurrentResponse` has content, flush to History.
  Ensures user sees streaming content even when all SDK events stop (midway through long tool).
  Uses `ProcessingGeneration` guard (captured before `InvokeOnUI`, validated inside lambda) to
  prevent stale ticks from flushing new-turn content into the old turn.
- **ExternalToolRequestedEvent** — Added to `SdkEventMatrix` as `TimelineOnly`. Was arriving as
  "Unhandled" causing log spam without any functional impact.
- **InvokeOnUI() in TurnEnd fallback Task.Run** — Switched from local `Invoke()` function to
  class-level `InvokeOnUI()` for unambiguous intent in cross-thread closures (INV-13).
- **44 behavioral safety tests** + 7 watchdog tests + 4 TurnEnd fallback tests = 55 new tests.
  All use source-code assertions and reflection-based state inspection to verify invariants.

### Key insight
These two bugs affected EVERY agent session because: (a) agents always use tools, so
`HasUsedToolsThisTurn` is always `true`, and (b) agent tasks frequently take >10 min (build,
test, research). The bugs compounded: fallback disabled → 600s watchdog is only hope → watchdog
kills after 600s → user loses the entire turn. PR #332 reduced worst-case stuck-session
recovery from 600s to 34s for lost-event scenarios.
