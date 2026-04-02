---
name: performance-optimization
description: >
  Performance invariants and optimization knowledge for PolyPilot's render pipeline,
  session switching, persistence, caching layers, and startup performance. Use when:
  (1) Modifying RefreshSessions, GetOrganizedSessions, or SafeRefreshAsync,
  (2) Touching SaveActiveSessionsToDisk, SaveOrganization, or SaveUiState,
  (3) Working with LoadPersistedSessions or session directory scanning,
  (4) Modifying markdown rendering or the message cache,
  (5) Optimizing render cycle performance or adding Blazor component rendering,
  (6) Working with debounce timers or DisposeAsync cleanup,
  (7) Debugging startup slowness or blue screen on launch,
  (8) Modifying InitializeAsync or RestoreSessionsInBackgroundAsync.
  Covers: session-switch bottleneck fix, debounce flush requirements, expensive
  operation guards, cache invalidation strategy, render cycle analysis, and
  startup performance debugging.
---

# Performance Optimization

## Critical Invariants

### PERF-1: _sessionSwitching flag lifecycle
`_sessionSwitching` MUST stay `true` until `SafeRefreshAsync` reads it.
`RefreshState()` must NOT clear it — only `SafeRefreshAsync` clears it after
using it to skip the expensive JS draft capture round-trip.

**Impact:** Session switch went from 729–4027ms → 16–28ms when fixed.

**What happens if violated:** Every session switch triggers a JS interop call
to query all card inputs, capture draft text, focus state, and cursor position.
With 3+ sessions this adds 500-2000ms of blocking JS round-trip per switch.

### PERF-2: Never call LoadPersistedSessions() from hot paths
`LoadPersistedSessions()` → `GetPersistedSessions()` scans ALL session
directories (753+ in production). Each directory requires reading `workspace.yaml`
and `events.jsonl` headers.

**Safe callers:** `OnInitialized()`, `TogglePersistedSessions()` (on open),
and error recovery in `HandleResumeSession()`.

**Forbidden callers:** `RefreshSessions()`, `OnStateChanged` handlers, any
code triggered by render cycles.

### PERF-3: Debounce timers must flush in DisposeAsync
Three debounced save operations coalesce rapid-fire calls:
| Operation | Timer | File |
|-----------|-------|------|
| `SaveActiveSessionsToDisk()` | 2s | `CopilotService.Persistence.cs` |
| `SaveOrganization()` | 2s | `CopilotService.Organization.cs` |
| `SaveUiState()` | 1s | `CopilotService.Persistence.cs` |

`DisposeAsync` calls `FlushSaveActiveSessionsToDisk()` and
`FlushSaveOrganization()`. If you add a new debounced save, add a flush call.

### PERF-4: GetOrganizedSessions() cache invalidation
Cached with a composite hash key that includes session count, group count,
sort mode, and per-session processing state. The cache auto-invalidates when
any of these change. Do NOT add high-frequency fields (e.g., streaming content)
to the hash key — it would defeat the cache.

### PERF-5: ReconcileOrganization() skip guard
Skips work when the active session set is unchanged (hash of session names).
If you add new session types or visibility rules, ensure the hash accounts
for them or reconciliation may be incorrectly skipped.

### PERF-6: ReconcileOrganization() during IsRestoring window
`ReconcileOrganization` is skipped during `IsRestoring=true` to prevent pruning
sessions not yet loaded. But code that needs metadata during restore (e.g.,
`CompleteResponse` queue drain, `GetOrchestratorGroupId`, `IsSessionInMultiAgentGroup`)
must call `ReconcileOrganization(allowPruning: false)` to trigger an additive-only
update. This mode adds missing `SessionMeta` entries but never deletes anything.

**Impact:** Without this, multi-agent dispatch is silently bypassed after relaunch,
and the watchdog uses the wrong timeout tier (120s instead of 600s), killing workers.
See PR #284 and processing-state-safety INV-9.

## Caching Architecture

### Markdown Cache (`ChatMessageList.razor`)
- Key: string content (NOT `GetHashCode()` — had collision risk)
- LRU eviction at 1000 entries via `LinkedList<string>` tracking
- Static/shared across all sessions — deduplicates identical content
- `FileToDataUri()` runs synchronously for image paths in markdown

### GetOrganizedSessions() Cache (`CopilotService.Organization.cs`)
- Returns `IReadOnlyList<(SessionGroup, List<AgentSessionInfo>)>`
- Callers should NOT call `.ToList()` on the result (it's already materialized)
- Hash key: `HashCode.Combine(sessionCount, groupCount, sortMode, perSessionState)`

## Known Optimization Opportunities (Not Yet Implemented)

### GetAllSessions().ToList() — called 8+ times per render
Each call snapshots `ConcurrentDictionary` into a new `List`. A per-render
cached snapshot would reduce allocations but current perf is acceptable.

### No Virtualize component for chat messages
Manual windowing via `GetWindowedMessages()` at 25 messages (expanded) or
10 messages (card). Blazor `<Virtualize>` would help but requires fixed
item heights — chat messages have variable height from markdown rendering.

### ChatMessageItem has no ShouldRender()
Always re-renders when parent calls `StateHasChanged()`. Adding a content
hash comparison would skip re-renders for unchanged messages.

## Startup Performance Debugging

### Architecture
Startup has two phases:
1. **UI thread (blocking):** MAUI bootstrap → BlazorWebView creation → Blazor framework init → Dashboard `OnInitialized` → `InitializeAsync` → fires `RestoreSessionsInBackgroundAsync` on ThreadPool
2. **Background thread (non-blocking):** Read `active-sessions.json` → create lazy placeholders → eager-resume candidates → `ReconcileOrganization`

The UI renders as soon as `InitializeAsync` returns. Session restore NEVER blocks the UI thread (runs via `Task.Run`). If you see a blue screen, the problem is in phase 1 (UI thread), not phase 2.

### Instrumentation
`[STARTUP-TIMING]` log tags in `~/.polypilot/console.log`:
```
[STARTUP-TIMING] LoadOrganization: 63ms         ← reads organization.json
[STARTUP-TIMING] Pre-restore: 64ms              ← time before Task.Run
[STARTUP-TIMING] Session loop complete: 35056ms  ← background thread, NOT blocking UI
[STARTUP-TIMING] RestoreSessionsInBackground: 35095ms  ← total background time
```

### How to debug startup slowness

**Step 1: Measure the UI-visible delay**
```
BlazorDevFlow ConfigureHandler → Dashboard Restoring UI state
```
Find both timestamps in console.log for the current PID. The gap is the user-visible startup time. Normal: 5-8 seconds.

**Step 2: Identify which phase is slow**
- If `[STARTUP-TIMING] Pre-restore` is high (>500ms): `LoadOrganization` or `InitializeAsync` is slow on the UI thread
- If `[STARTUP-TIMING] Session loop` is high but UI rendered fast: background restore is slow but not blocking the user — acceptable
- If neither timing tag appears: the slowdown is in Blazor framework init (before our code runs) — check system load, WebView issues

**Step 3: A/B test against main**
```bash
# Save current branch
git stash && git checkout main
cd PolyPilot && ./relaunch.sh
# Measure: BlazorDevFlow Config → Dashboard Restore gap

# Switch back
git checkout <branch> && git stash pop
cd PolyPilot && ./relaunch.sh
# Compare gaps
```

⚠️ **Use `git stash` + `git checkout` — do NOT `git checkout origin/main -- .`** (checks out files but keeps wrong branch name, confuses the app's branch display).

**Step 4: Common false alarms**
- CPU load from concurrent test runs or builds causes 2-3x slowdown in Blazor init
- DLL file locks from running app cause build failures (retry after a few seconds)
- The first launch after a `dotnet clean` is always slower (JIT compilation)
- Run the comparison 2-3 times each to account for variance

### Critical rules
- **NEVER block the UI thread during restore.** All session loading uses `Task.Run` + `ConfigureAwait(false)`. Violations cause blue screen.
- **`LoadPersistedSessions()` is O(N) on ALL session directories** (750+). Never call from `InitializeAsync` or any UI-triggered path. See PERF-2.
- **InvokeOnUI callbacks during restore compete with Blazor rendering.** Minimize them. The restore path should batch state changes and call `NotifyStateChanged` once at the end, not per-session.
