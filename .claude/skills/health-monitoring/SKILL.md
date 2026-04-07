---
name: health-monitoring
description: >
  Real-time health monitoring for PolyPilot sessions. Use when the user asks to
  "monitor sessions", "watch for issues", "check health", "make sure everything is
  working", "keep an eye on things", or any request to continuously observe running
  PolyPilot sessions for reliability issues. Also use when the user reports a session
  is "stuck", "not responding", shows "Thinking..." forever, or threw a "Tool execution
  stuck" error — diagnose the issue using the techniques below. Covers: single sessions,
  multi-agent orchestration, sub-agent IDLE-DEFER, app restart recovery, and connection
  health.
---

# PolyPilot Health Monitoring

Continuously monitor all running PolyPilot sessions for reliability issues. Your job is
to be a vigilant observer — detect problems early, diagnose root causes, and either fix
them or report them clearly.

## Quick Start

### 1. Start the live event stream

```bash
tail -n0 -F ~/.polypilot/event-diagnostics.log
```

Run this in an async shell and read it periodically (every 2-5 minutes). This is your
primary signal source — every important state change is logged here.

### 2. Initial baseline check

Before monitoring, establish what "healthy" looks like right now:

```bash
# How many sessions exist?
python3 -c "import json; d=json.load(open('$HOME/.polypilot/active-sessions.json')); print(f'{len(d)} active sessions')"

# Any recent errors?
grep -E '\[ERROR\]|\[WATCHDOG\].*timeout|\[TOOL-HEALTH\].*recovery|\[RECONNECT\].*replacing' \
  ~/.polypilot/event-diagnostics.log | tail -10

# Any phantom sessions?
python3 -c "
import json
d=json.load(open('$HOME/.polypilot/active-sessions.json'))
phantoms=[s['DisplayName'] for s in d if '(previous)' in s.get('DisplayName','') or '(resumed)' in s.get('DisplayName','')]
print(f'{len(phantoms)} phantom sessions' + (': '+', '.join(phantoms) if phantoms else ''))
"
```

### 3. Periodic health checks

Every 2-5 minutes, read the event stream and look for problems. Filter for the
important events — skip the noisy `[EVT]` lines unless diagnosing a specific issue:

```bash
tail -200 ~/.polypilot/event-diagnostics.log | grep -E \
  'ERROR|WATCHDOG|RECONNECT|TOOL-HEALTH|COMPLETE|IDLE-DEFER|SEND|KEEPALIVE|HEALTH|DISPATCH|ABORT|INTERRUPTED'
```

## What Healthy Looks Like

A healthy session lifecycle follows this pattern:

```
[SEND] → [EVT] TurnStart → [EVT] TurnEnd → [EVT] TurnStart → ... → [EVT] SessionIdle → [IDLE] → [COMPLETE]
```

Key indicators of health:
- **SEND → COMPLETE** cycle completes (check `flushedLen` is non-zero for real responses)
- **IDLE-DEFER** fires when sub-agents are active, then COMPLETE fires when they finish
- **KEEPALIVE** pings appear every 15 minutes
- **No ERROR, WATCHDOG timeout, or RECONNECT events**

## What Problems Look Like

### Problem: Session stuck at "Thinking..."

**Symptom:** `[SEND]` logged but no `[COMPLETE]` after several minutes.

**Diagnosis:**
```bash
# Check the last events for the stuck session
grep "SessionName" ~/.polypilot/event-diagnostics.log | tail -10
```

**Possible causes:**
1. **No events flowing** → Connection may be dead. Look for `[WATCHDOG]` events — the
   watchdog should detect and recover within 2 minutes (120s inactivity timeout).
2. **Events flowing but no IDLE** → Session is legitimately working (tool execution).
   Check if `AssistantTurnStart/End` events are still arriving.
3. **IDLE-DEFER active** → Sub-agents are still running. Check `backgroundTasks` count
   in the `[IDLE-DIAG]` line. This is normal for multi-agent and sub-agent sessions.

### Problem: "Tool execution stuck" error message

**Symptom:** User sees "Tool execution stuck (reason). Session recovered automatically."

**Diagnosis:**
```bash
grep "TOOL-HEALTH" ~/.polypilot/event-diagnostics.log | tail -10
```

This fires when the `ToolHealthCheck` timer detects no events for 30s after a tool
starts, and the server is either dead or has been stale for multiple checks. The session
is auto-recovered — check if `[COMPLETE]` follows. If the response was meaningful
(`flushedLen > 0`), the recovery worked. If `flushedLen=0`, the tool died before
producing output.

### Problem: Client recreation / Blazor surface reset

**Symptom:** All sessions briefly reset in the UI, user sees a flash.

**Diagnosis:**
```bash
grep "RECONNECT.*replacing state\|Recreating client" ~/.polypilot/event-diagnostics.log | tail -5
```

This happens when `SendAsync` throws a connection error. The app recreates the
`CopilotClient` and re-resumes all sessions. Check:
- **What session triggered it** — the `[SEND]` just before the first `[RECONNECT]`
- **Did all sessions recover?** — Look for `Failed to re-resume sibling` entries
- **Was the server alive?** — Check if `[KEEPALIVE]` pings succeeded before/after

```bash
# Find sessions that failed to re-resume
grep "Failed to re-resume" ~/.polypilot/event-diagnostics.log | tail -10
```

Sessions that fail to re-resume with "Session not found" had their server-side session
expired. This is normal for very old idle sessions.

### Problem: Watchdog timeout

**Symptom:** `[WATCHDOG]` line with `IsProcessing=false` and a system message about
"Session appears stuck".

**Diagnosis:**
```bash
grep "WATCHDOG.*IsProcessing=false\|WATCHDOG.*timeout\|WATCHDOG.*stuck" \
  ~/.polypilot/event-diagnostics.log | tail -5
```

Check which timeout tier fired:
- **30s** (resume quiescence) — Session was resumed after restart but never received
  events. The turn likely completed before the restart. Normal.
- **120s** (inactivity) — No events for 2 minutes with no tool activity. Connection
  may have dropped.
- **600s** (tool execution) — Tool was running for 10 minutes with no events. Rare
  but can happen for very long builds.

### Problem: Phantom `(previous)` sessions

**Symptom:** Session list shows duplicates with `(previous)` suffix.

**Diagnosis:**
```bash
python3 -c "
import json
d=json.load(open('$HOME/.polypilot/active-sessions.json'))
for s in d:
    if '(previous)' in s.get('DisplayName',''):
        print(f'{s[\"DisplayName\"]}: group={s.get(\"GroupId\",\"\")}, recovered={s.get(\"RecoveredFromSessionId\",\"\")}')"
```

These are caused by worker revival creating a new session while the old one lingers in
`active-sessions.json`. The fix in PR #531 added `_closedSessionIds` tracking to prevent
this. If you still see phantoms, check `RecoveredFromSessionId` — the old session should
have been excluded from the merge.

### Problem: DISPATCH-RECOVER false positive

**Symptom:** `[DISPATCH-RECOVER]` in the log when workers completed normally.

**Diagnosis:**
```bash
grep "DISPATCH-RECOVER" ~/.polypilot/event-diagnostics.log | tail -5
```

This should be rare after PR #531's two-phase mtime check. If it fires, check:
1. Was the worker's `events.jsonl` actually written to during the grace period?
2. Did a `session.resume` event cause a false write?

## Multi-Agent Monitoring

Multi-agent sessions have extra complexity. Monitor these patterns:

### Healthy orchestrator flow
```
[DISPATCH-ROUTE] → [SEND] orchestrator → [IDLE-DEFER] (agents=N) → ... → [COMPLETE]
```

Between IDLE-DEFER and COMPLETE, workers are running. Check worker events:
```bash
grep "worker" ~/.polypilot/event-diagnostics.log | tail -20
```

### Worker completion timing
Workers should complete in 2-30s for simple tasks, 1-10 minutes for complex ones.
If a worker hasn't produced events in 5+ minutes, check its events.jsonl:

```bash
# Find worker session ID
python3 -c "
import json
d=json.load(open('$HOME/.polypilot/active-sessions.json'))
for s in d:
    if 'worker' in s.get('DisplayName','').lower():
        print(f'{s[\"DisplayName\"]}: {s[\"SessionId\"]}')" | head -10

# Check last event time for a specific worker
ls -la ~/.copilot/session-state/SESSION_ID/events.jsonl
```

### IDLE-DEFER-REARM
When `session.idle` arrives with active background tasks but `IsProcessing` was already
cleared (by watchdog or race), IDLE-DEFER-REARM re-arms processing. This is correct
behavior — it keeps the session alive until sub-agents finish:

```
[IDLE-DEFER-REARM] 'SessionName' re-arming IsProcessing — background tasks active but processing was cleared
```

## Post-Relaunch Recovery Check

After `./relaunch.sh`, do an exhaustive recovery check:

```bash
# 1. Wait for the app to restart (~15-20s)
sleep 20

# 2. Check the relaunch log
tail -5 ~/.polypilot/relaunch.log

# 3. Check for RECONNECT events (the new app reconnects all sessions)
grep "RECONNECT" ~/.polypilot/event-diagnostics.log | tail -20

# 4. Check for failed re-resumes
grep "Failed to re-resume" ~/.polypilot/event-diagnostics.log | tail -10

# 5. Verify no sessions are stuck processing
grep "IsProcessing=True" ~/.polypilot/event-diagnostics.log | tail -5

# 6. Check for phantom sessions
python3 -c "
import json
d=json.load(open('$HOME/.polypilot/active-sessions.json'))
phantoms=[s['DisplayName'] for s in d if '(previous)' in s.get('DisplayName','')]
stuck=[s['DisplayName'] for s in d if s.get('LastPrompt')]
print(f'Phantoms: {len(phantoms)}, Sessions with pending prompts: {len(stuck)}')
if phantoms: print('  Phantom:', phantoms)
if stuck: print('  Pending:', stuck)
"

# 7. If any sessions were actively processing during relaunch, verify they resumed
grep "SEND\|COMPLETE" ~/.polypilot/event-diagnostics.log | tail -10
```

**What to look for:**
- All sessions should show `[RECONNECT] Re-resumed sibling session` (except expired ones)
- Sessions that were processing should either complete or be detected by the watchdog
- No `(previous)` phantom sessions should appear
- The `[HEALTH] Connection healthy after resume/wake` event should appear

## Reporting

When monitoring, provide periodic status updates:

| Session | Status | Details |
|---------|--------|---------|
| Name | ✅/⚠️/❌ | Brief description |

Use:
- ✅ for clean SEND→COMPLETE cycles
- ⚠️ for IDLE-DEFER (expected but worth noting), RECONNECT (recovered), long-running tools
- ❌ for WATCHDOG timeouts, TOOL-HEALTH recovery, ERROR events, stuck sessions

If you detect an issue, diagnose it immediately using the techniques above. If it's a
code bug (not a transient connection issue), investigate the source code and propose a fix.

## Mobile App Monitoring (WsBridge + DevTunnel)

The mobile app connects to the desktop via WebSocket through a DevTunnel. Monitor this
entire chain for reliability issues.

### Architecture

```
Mobile App (iOS/Android)
  → DevTunnel (wss://TUNNEL_ID.usw3.devtunnels.ms)
    → WsBridgeServer (localhost:4322)
      → CopilotService (desktop)
```

### Quick Health Check

```bash
# 1. Is the bridge server listening?
lsof -i :4322

# 2. Is DevTunnel running?
ps aux | grep "devtunnel host" | grep -v grep

# 3. Is the tunnel reachable?
curl -s -o /dev/null -w "%{http_code}" https://TUNNEL_ID.usw3.devtunnels.ms/

# 4. Is the devtunnel login still valid?
devtunnel show TUNNEL_ID 2>&1 | head -5
# If "Login token expired" → tunnel may reject new connections

# 5. Check for connected mobile clients
# WsBridgeServer logs connect/disconnect to stdout (Console.WriteLine)
# Check app logs:
maui devflow MAUI logs --limit 30 --agent-port 9223 2>&1 | grep -i "client\|connect\|bridge"

# 6. Bridge-related errors in crash log
grep -i "bridge\|WebSocket\|tunnel" ~/.polypilot/crash.log | tail -5

# 7. Bridge-related diagnostic events
grep -E "BRIDGE|SyncRemote|SmartURL" ~/.polypilot/event-diagnostics.log | tail -10
```

### Common Mobile Issues

#### Mobile shows "Connecting..." or blank session list
1. Check DevTunnel is running (`ps aux | grep devtunnel`)
2. Check bridge port is listening (`lsof -i :4322`)
3. Check tunnel is reachable (`curl` the tunnel URL — expect 404, not connection error)
4. If `devtunnel show` says "Login token expired" — the tunnel host process may still
   work but new management operations fail. The existing tunnel should keep forwarding.

#### Mobile sends message but never gets response
The bridge proxies prompts via `DispatchBridgePromptAsync`. Check:
```bash
# Stack traces from bridge prompt dispatch
grep "DispatchBridgePromptAsync" ~/.polypilot/event-diagnostics.log | tail -5

# Bridge completion events
grep "BRIDGE-COMPLETE\|BRIDGE-SESSION" ~/.polypilot/event-diagnostics.log | tail -10
```

If `DispatchBridgePromptAsync` has stack traces, the desktop CopilotService failed to
process the prompt. The mobile will show "Working..." indefinitely. Check if the
triggering session had a connection error (look for RECONNECT events around the same time).

#### Mobile shows session as "Working" when desktop shows idle
The bridge syncs `IsProcessing` state. If a reconnect force-completed a session on
desktop but the bridge state-sync hasn't fired yet, mobile shows stale state. The
next `SyncRemoteSessions` call (triggered by `OnStateChanged`) should fix it. If not:
```bash
grep "SyncRemoteSessions" ~/.polypilot/event-diagnostics.log | tail -5
```

#### Mobile disconnects frequently
Check network quality. The WebSocket connection goes through DevTunnel → Azure →
mobile network. Each hop can drop. The `WsBridgeClient` on mobile has auto-reconnect
logic, but gaps cause temporary UI freezes.

```bash
# Check for SmartURL network change events (WiFi↔Cellular transitions)
grep "SmartURL" ~/.polypilot/event-diagnostics.log | tail -10

# Check HEALTH events (Mac wake/sleep affects tunnel)
grep "HEALTH" ~/.polypilot/event-diagnostics.log | tail -10
```

### Bridge-Specific Diagnostic Tags

| Tag | Meaning |
|-----|---------|
| `[BRIDGE-COMPLETE]` | Bridge `OnTurnEnd` cleared IsProcessing for a remote session |
| `[BRIDGE-SESSION-COMPLETE]` | Stale IsProcessing cleared during bridge sync |
| `[SmartURL]` | Network change detected (WiFi gain/loss) — may trigger reconnect |
| `[HEALTH]` | Connection health check after Mac wake/sleep |

### DevTunnel Management

```bash
# Re-login if token expired (interactive — opens browser)
devtunnel login

# Show tunnel details
devtunnel show TUNNEL_ID

# Restart tunnel hosting (if process died)
devtunnel host TUNNEL_ID --allow-anonymous &

# Check tunnel port forwarding
devtunnel list
```

Note: The DevTunnel host process runs independently from PolyPilot. If the Mac sleeps
and wakes, the tunnel process usually survives but the WebSocket connections through
it may need to re-establish.
