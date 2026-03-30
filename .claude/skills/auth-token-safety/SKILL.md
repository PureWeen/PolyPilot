---
name: auth-token-safety
description: >
  Invariants, design rationale, and regression traps for PolyPilot's authentication
  and token forwarding system. Use when: (1) Modifying ResolveGitHubTokenForServer,
  TryReadCopilotKeychainToken, or RunProcessWithTimeout, (2) Touching CheckAuthStatusAsync,
  StartAuthPolling, StopAuthPolling, or ReauthenticateAsync, (3) Adding or modifying
  code paths that start the headless copilot server (StartServerAsync calls),
  (4) Modifying TryRecoverPersistentServerAsync or watchdog server recovery,
  (5) Touching InitializeAsync's Persistent mode startup path, (6) Debugging auth
  errors like "Session was not created with authentication info or custom provider",
  (7) Working with AuthNotice, the auth banner UI, or ClearAuthNotice,
  (8) Modifying _resolvedGitHubToken caching or invalidation,
  (9) Any change involving macOS Keychain access or the `security` CLI,
  (10) Adding new env var token sources or changing token resolution order.
  Covers: 9 invariants from PR #446 (8 review rounds), the macOS Keychain ACL problem,
  token expiration trap, and the three-tier token resolution design.
---

# Auth Token Safety

This document captures hard-won lessons from PR #446 (8 review rounds, 3-model consensus
reviews, and a live user regression). Read this before touching ANY auth-related code.

## Background: The Root Problem

The copilot CLI stores its OAuth token in the macOS login Keychain under service name
`"copilot-cli"` via keytar.node. When PolyPilot spawns `copilot --headless` as a detached
server process, that server binary has a **different path** than the terminal `copilot` binary.
macOS Keychain ACLs are per-binary-path — the headless server silently fails to read the
Keychain entry, causing: `"Session was not created with authentication info or custom provider"`.

### Why env var forwarding exists

PolyPilot reads the token from the Keychain (via the `security` CLI, which CAN access it
if the user grants permission) and forwards it as `COPILOT_GITHUB_TOKEN` env var to the
headless server process. The CLI gives `COPILOT_GITHUB_TOKEN` highest precedence among
env var token sources.

### Why this is dangerous

The forwarded token is a **static snapshot**. The copilot CLI normally refreshes tokens via
keytar.node, but a token in an env var bypasses that refresh mechanism. When the token
expires (~1-8 hours), the server loses auth → recovery fires → if recovery re-reads the
Keychain → password prompt to the user → **recurring hourly prompts**.

---

## The 9 Invariants

### INV-A1: Never read Keychain preemptively

**DO NOT** call `ResolveGitHubTokenForServer()` or `TryReadCopilotKeychainToken()` at
startup or on any automatic path. The Keychain read triggers a macOS password dialog.

- ❌ `InitializeAsync` calling `ResolveGitHubTokenForServer()` before checking if server can self-auth
- ✅ Start server without token → check auth → only resolve token after confirmed auth failure

**Why this matters:** Most users' servers self-authenticate fine. Preemptive reads cause
100% of users to see a password dialog for a problem only ~5% have.

### INV-A2: Keychain reads must be lazy AND cached

When the Keychain IS read (after auth failure), cache the result in `_resolvedGitHubToken`.
Do NOT re-read from Keychain on every recovery cycle.

- ❌ Auth polling loop calling `ResolveGitHubTokenForServer()` on every auth detection
- ✅ Only `ReauthenticateAsync` (explicit user action) re-reads Keychain
- ✅ `TryRecoverPersistentServerAsync` and polling use the cached `_resolvedGitHubToken`

**Why:** Each Keychain read = another password dialog. The polling loop runs every 10s.
If it re-reads Keychain, users get prompted every 10 seconds.

### INV-A3: Token expiration causes cascading failures

A static `COPILOT_GITHUB_TOKEN` will expire. When it does:
1. All sessions fail simultaneously (server-wide auth loss)
2. Watchdog fires after `WatchdogServerRecoveryThreshold` (2) consecutive timeouts
3. `TryRecoverPersistentServerAsync` restarts server with same stale token
4. New server also fails → more timeouts → recovery loop

**The fix:** On recovery, try starting the server WITHOUT a token first (clear
`_resolvedGitHubToken = null`). Let the server attempt its own Keychain access via
keytar.node. Only if that also fails, fall back to the cached token or show the banner.

### INV-A4: All AuthNotice writes must be inside InvokeOnUI

`AuthNotice` is read by Blazor UI components on the UI thread. SDK events arrive on
background threads. Every mutation must be marshaled.

**All write sites (verify after any change):**
- `ClearAuthNotice()` — inside `InvokeOnUI`
- `ReauthenticateAsync()` failure path — inside `InvokeOnUI`
- `ReconnectAsync()` — on UI thread (caller guarantee)
- `CheckAuthStatusAsync()` success/failure — inside `InvokeOnUI`
- `StartAuthPolling()` success/failure — inside `InvokeOnUI`
- `HandleSessionEvent` error handler — inside `InvokeOnUI` (line 792)

### INV-A5: CheckAuthStatusAsync must return bool, not set-and-read

`CheckAuthStatusAsync` sets `AuthNotice` via `InvokeOnUI(Post)` which is **asynchronous**.
Code that calls `CheckAuthStatusAsync()` and then reads `AuthNotice` will see the OLD value.

- ❌ `await CheckAuthStatusAsync(); if (AuthNotice == null) { /* "success" */ }`
- ✅ `var isAuthenticated = await CheckAuthStatusAsync(); if (isAuthenticated) { ... }`

**History:** This was a 3-review-round bug. R1 introduced it, R2 identified it, R3 fixed it.

### INV-A6: ResolveGitHubTokenForServer blocks the thread

This method spawns up to 4 child processes sequentially:
- 3× `security find-generic-password` (3s timeout each)
- 1× `gh auth token` (5s timeout)
- Worst case: **14 seconds** of blocking

**Every call site must be wrapped in `Task.Run()`** to avoid freezing the UI thread.

- ✅ `_resolvedGitHubToken = await Task.Run(() => ResolveGitHubTokenForServer());`
- ❌ `_resolvedGitHubToken = ResolveGitHubTokenForServer();` (blocks UI for up to 14s)

### INV-A7: RunProcessWithTimeout must drain readTask on Kill

When a subprocess times out, `proc.Kill()` is called. The `ReadToEndAsync()` task is still
pending. If the `using` block disposes the Process before the task completes, an unobserved
`ObjectDisposedException` fires on the finalizer thread.

```csharp
if (!proc.WaitForExit(timeoutMs))
{
    try { proc.Kill(); } catch { }
    try { readTask.GetAwaiter().GetResult(); } catch { }  // ← MUST drain
    return null;
}
```

Also: `RedirectStandardError` must be `false` (not `true`) since stderr is unused.
Redirecting without draining fills the OS pipe buffer (~64KB) and blocks the process.

### INV-A8: _resolvedGitHubToken must be cleared in ReconnectAsync

When the user changes connection settings or reconnects, the cached token may be stale
(different server, different auth state). `ReconnectAsync` must set
`_resolvedGitHubToken = null` to force re-resolution on the next server start that needs it.

### INV-A9: Auth polling must have proper lifecycle

The `_authPollLock` (object) guards `_authPollCts` (CancellationTokenSource).
Both `StartAuthPolling` and `StopAuthPolling` must hold this lock.

**Lifecycle hazards:**
- Polling loop calls `StopAuthPolling()` before recovery → sets `_authPollCts = null`
- If recovery fails → calls `StartAuthPolling()` again (new CTS)
- If user clicks Dismiss between stop and restart → `StopAuthPolling()` is a no-op
  (CTS already null) → restart creates new poll → dismiss silently fails
- Mitigation: acceptable narrow race; no user-visible harm (banner reappears briefly)

**Cleanup:** `DisposeAsync` must call `StopAuthPolling()`. The fire-and-forget polling
`Task` is not awaited (matches codebase pattern for `FetchGitHubUserInfoAsync` etc.).

---

## Token Resolution Chain

`ResolveGitHubTokenForServer()` tries sources in order:

| Priority | Source | Prompt? | Notes |
|----------|--------|---------|-------|
| 1 | `COPILOT_GITHUB_TOKEN` env var | No | Highest precedence per CLI docs |
| 2 | `GH_TOKEN` env var | No | GitHub CLI convention |
| 3 | `GITHUB_TOKEN` env var | No | CI/Actions convention |
| 4 | macOS Keychain (`security` CLI) | **YES** | Tries: "copilot-cli", "github-copilot", "GitHub Copilot" |
| 5 | `gh auth token` CLI | No | Only works if gh CLI installed and authed |

**Key insight:** Tiers 1-3 and 5 are safe (no user prompt). Tier 4 (Keychain) is the
dangerous one. Future changes should prefer making tier 4 lazy/opt-in.

---

## The Keychain Service Name

`copilot login` stores the token under service name `"copilot-cli"` (not `"github-copilot"`
as previously assumed). Verified by:
```bash
security find-generic-password -s "copilot-cli" -l  # ← this is the correct one
```
Account format: `"https://github.com:{username}"`. The token value is a `gho_*` OAuth token.

We also try `"github-copilot"` and `"GitHub Copilot"` as fallbacks for older CLI versions.

---

## macOS Keychain ACL — Why There's No Silent Read

- `security find-generic-password -w` requests the secret → triggers ACL check
- `security find-generic-password` (without `-w`) returns metadata only → no ACL dialog
- `SecItemCopyMatching` (native API) has the same ACL enforcement
- There is NO "silent/no-prompt" flag for reading foreign Keychain entries
- The only way to avoid the dialog is for the binary that created the entry to read it,
  or for the user to click "Always Allow" (persists the ACL grant)

---

## Server Recovery and Token Interaction

`TryRecoverPersistentServerAsync` restarts the headless server. It passes
`_resolvedGitHubToken` to `StartServerAsync`. Key paths that trigger it:

| Trigger | Token behavior | Keychain read? |
|---------|---------------|----------------|
| User clicks Re-authenticate | Re-resolves (direct assignment) | **Yes** (intentional) |
| Auth polling detects auth | Should use cached token | **Must NOT** re-read |
| Watchdog consecutive timeouts | Uses cached token | No |
| Health check ping failure | Uses cached token | No |
| Lazy session resume auth error | Uses cached token | No |

**Critical:** Only the user-initiated `ReauthenticateAsync` path should ever call
`ResolveGitHubTokenForServer()` with a fresh read. All automatic paths must use the cache.

---

## Common Regression Patterns

### Pattern 1: "Works on my machine"
The developer's machine has `gh` CLI authed, or has `GH_TOKEN` set, or clicked "Always Allow"
on the Keychain dialog. The Keychain prompt never appears for them. Test with:
- No env vars set
- No `gh` CLI
- Fresh Keychain ACL (never clicked "Always Allow" for PolyPilot/security)

### Pattern 2: Adding a new StartServerAsync call site
There are 8+ call sites that pass `_resolvedGitHubToken`. When adding a new one, ensure
the token is already resolved (don't add a new `ResolveGitHubTokenForServer()` call
without checking if it runs on the UI thread or in an automatic loop).

### Pattern 3: Wrapping Task.Run but missing one call site
R5 review wrapped `ResolveGitHubTokenForServer` in `Task.Run` in `ReauthenticateAsync` and
`InitializeAsync` but missed the auth polling loop (Utilities.cs:912). Always grep for ALL
call sites: `grep -rn "ResolveGitHubTokenForServer\|TryReadCopilotKeychainToken"`.

### Pattern 4: Token expiration → restart loop → re-prompt
Static env var token expires → server auth fails → recovery restarts server with same
stale token → fails again → polling detects auth → re-reads Keychain → password prompt.
**The loop repeats hourly** (aligned with `WatchdogMaxProcessingTimeSeconds = 3600`).

---

## Files Reference

| File | What's there |
|------|-------------|
| `CopilotService.cs` ~75 | `_resolvedGitHubToken` field |
| `CopilotService.cs` ~287 | `AuthNotice` property |
| `CopilotService.cs` ~300 | `ClearAuthNotice()` |
| `CopilotService.cs` ~311 | `GetLoginCommand()` |
| `CopilotService.cs` ~321 | `ReauthenticateAsync()` |
| `CopilotService.cs` ~932 | `InitializeAsync` token resolution |
| `CopilotService.cs` ~1192 | `ReconnectAsync` cache clear |
| `CopilotService.cs` ~1265 | `TryRecoverPersistentServerAsync` |
| `CopilotService.Utilities.cs` ~847 | `CheckAuthStatusAsync()` → returns bool |
| `CopilotService.Utilities.cs` ~887 | `StartAuthPolling()` / `StopAuthPolling()` |
| `CopilotService.Utilities.cs` ~970 | `ResolveGitHubTokenForServer()` |
| `CopilotService.Utilities.cs` ~1020 | `TryReadCopilotKeychainToken()` |
| `CopilotService.Utilities.cs` ~1030 | `RunProcessWithTimeout()` |
| `CopilotService.Events.cs` ~792 | SessionErrorEvent auth → AuthNotice |
| `CopilotService.Events.cs` ~2501 | Watchdog → server recovery |
| `ServerManager.cs` ~57 | `StartServerAsync` sets COPILOT_GITHUB_TOKEN env var |
| `IServerManager.cs` | Interface with `githubToken` param |
| `Dashboard.razor` ~57 | Auth banner UI |
| `ErrorMessageHelper.cs` | Auth error humanization |
| `ServerRecoveryTests.cs` | 54+ auth tests |

---

## PR History (for context, not action)

PR #446 went through 8 review rounds. Key lessons:
- R1: Initial implementation. 3 critical bugs found (TOCTOU, CTS leak, wrong-thread write)
- R2-R3: Fixed criticals. Discovered deeper issue: headless server can't access Keychain
- R4: Added Keychain token forwarding. Discovered correct service name is "copilot-cli"
- R5: Fixed UI freeze (Task.Run), stderr pipe, unquoted path
- R7: Fixed missed Task.Run call site in InitializeAsync, readTask cleanup
- R8: Approved
- Post-merge regression: User reports hourly password prompts. Root cause: polling loop
  re-reads Keychain on every auth detection cycle + token expiration creates hourly loop
