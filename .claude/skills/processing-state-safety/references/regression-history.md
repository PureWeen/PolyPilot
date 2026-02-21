# Regression History & Common Mistakes

## 7 Mistakes That Keep Recurring

### 1. Forgetting companion fields on error paths
**What happens**: Clear `IsProcessing` and `ProcessingPhase` but forget `IsResumed` or
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

## Full Regression Timeline

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

## Thread Safety Details

1. **All `state.Info.*` mutations from background threads** → `InvokeOnUI()`
2. **`HasUsedToolsThisTurn`, `HasReceivedEventsSinceResume`** → `Volatile.Write` on set, `Volatile.Read` on check (ARM memory model)
3. **`ActiveToolCallCount`** → `Interlocked.Increment`/`Decrement`/`Exchange` (concurrent tool starts/completions)
4. **`LastEventAtTicks`** → `Interlocked.Exchange`/`Read` (long requires atomic ops)
5. **`ProcessingGeneration`** → `Interlocked.Increment` on send, `Interlocked.Read` on check
6. **`ProcessingGeneration` guard**: `SyncContext.Post` is async — a new `SendPromptAsync` can execute between the Post() and the callback. Capture generation before posting, verify inside callback.
