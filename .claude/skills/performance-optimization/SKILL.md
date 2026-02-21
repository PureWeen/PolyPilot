---
name: performance-optimization
description: >
  Performance invariants and optimization knowledge for PolyPilot's render pipeline,
  session switching, persistence, and caching layers. Use when: (1) Modifying
  RefreshSessions, GetOrganizedSessions, or SafeRefreshAsync, (2) Touching
  SaveActiveSessionsToDisk, SaveOrganization, or SaveUiState, (3) Working with
  LoadPersistedSessions or session directory scanning, (4) Modifying markdown
  rendering or the message cache, (5) Optimizing render cycle performance or
  adding Blazor component rendering, (6) Working with debounce timers or
  DisposeAsync cleanup. Covers: session-switch bottleneck fix, debounce flush
  requirements, expensive operation guards, cache invalidation strategy, and
  render cycle analysis.
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
