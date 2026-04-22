#!/usr/bin/env bash
# Scheduled Tasks Integration Tests
# Runs against a live PolyPilot app with MauiDevFlow agent connected.
# Usage: ./scheduled-tasks.sh <devflow-port> [--no-cleanup]
#
# Tests:
#   1. Navigate to /scheduled-tasks page
#   2. Create a new interval task via the form
#   3. Verify the task card appears with correct details
#   4. Disable/enable the task
#   5. Verify form validation (invalid cron)
#   6. Delete the task
#
# Requires: curl, python3

set -uo pipefail

PORT="${1:?Usage: $0 <devflow-port> [--no-cleanup]}"
NO_CLEANUP="${2:-}"
BASE="http://localhost:$PORT"
CDP="$BASE/api/cdp"
PASS=0
FAIL=0
ERRORS=""

log()  { echo "  $1"; }
pass() { PASS=$((PASS + 1)); echo "  ✅ $1"; }
fail() { FAIL=$((FAIL + 1)); ERRORS="${ERRORS}\n  ❌ $1"; echo "  ❌ $1"; }

# Helper: evaluate JS via CDP — matches the proven pattern from smoke tests
cdp_eval() {
  local expr="$1"
  local payload
  payload=$(python3 -c "import json,sys; print(json.dumps({'method':'Runtime.evaluate','params':{'expression':sys.argv[1]}}))" "$expr")
  curl -s --max-time 10 -X POST "$CDP" \
    -H "Content-Type: application/json" \
    -d "$payload" 2>/dev/null || echo "{}"
}

# Helper: extract string value from CDP response
# Response format: {"id":N,"result":{"result":{"type":"string","value":"..."}}}
cdp_value() {
  python3 -c "
import sys,json
try:
  r = json.load(sys.stdin)
  v = r.get('result',{}).get('result',{}).get('value','')
  print(v if v else '')
except: print('')
" 2>/dev/null
}

# Helper: click element by running JS that finds and clicks it
cdp_click() {
  local selector="$1"
  cdp_eval "const el = document.querySelector('$selector'); el?.click(); el ? 'clicked' : 'not found'" | cdp_value
}

# Helper: set input value and dispatch events for Blazor binding
cdp_set_input() {
  local selector="$1"
  local value="$2"
  cdp_eval "const el = document.querySelector('$selector'); if (el) { el.value = '$value'; el.dispatchEvent(new Event('input', {bubbles:true})); el.dispatchEvent(new Event('change', {bubbles:true})); 'set'; } else { 'not found'; }" | cdp_value
}

# Helper: check if element exists, returns "true" or "false"
cdp_exists() {
  local selector="$1"
  cdp_eval "document.querySelector('$selector') !== null ? 'true' : 'false'" | cdp_value
}

# Helper: get text content of element
cdp_text() {
  local selector="$1"
  cdp_eval "document.querySelector('$selector')?.textContent?.trim() || ''" | cdp_value
}

# Helper: wait for element, returns 0 on success
wait_for() {
  local selector="$1"
  local timeout="${2:-15}"
  for i in $(seq 1 "$timeout"); do
    if [ "$(cdp_exists "$selector")" = "true" ]; then
      return 0
    fi
    sleep 1
  done
  return 1
}

# Helper: take a screenshot for debugging
screenshot() {
  local name="$1"
  curl -s "$BASE/api/screenshot" -o "/tmp/polypilot-scheduled-tasks-${name}.png" 2>/dev/null
  log "📸 Screenshot: $name ($(wc -c < "/tmp/polypilot-scheduled-tasks-${name}.png" 2>/dev/null || echo 0) bytes)"
}

echo "═══════════════════════════════════════════"
echo "  Scheduled Tasks Integration Tests"
echo "  DevFlow agent: $BASE"
echo "═══════════════════════════════════════════"
echo ""

# Verify agent is connected
echo "▸ Verifying DevFlow agent..."
STATUS=$(curl -s --max-time 5 "$BASE/api/status" 2>/dev/null)
if [ -z "$STATUS" ]; then
  echo "  ❌ DevFlow agent not reachable at $BASE"
  exit 1
fi
pass "DevFlow agent connected"

# Check CDP is ready
CDP_STATUS=$(echo "$STATUS" | python3 -c "import sys,json; r=json.load(sys.stdin); print(r.get('cdpReady','false'))" 2>/dev/null)
if [ "$CDP_STATUS" != "True" ] && [ "$CDP_STATUS" != "true" ]; then
  log "Waiting for CDP to become ready..."
  for i in $(seq 1 30); do
    CDP_STATUS=$(curl -s "$BASE/api/status" 2>/dev/null | python3 -c "import sys,json; r=json.load(sys.stdin); print(r.get('cdpReady','false'))" 2>/dev/null)
    if [ "$CDP_STATUS" = "True" ] || [ "$CDP_STATUS" = "true" ]; then break; fi
    sleep 1
  done
fi
pass "CDP ready"
echo ""

# ─── Test 1: Navigate to Scheduled Tasks page ───
echo "▸ Test 1: Navigate to /scheduled-tasks"
# Click the sidebar link — use the anchor text since selectors with href need escaping
NAV_RESULT=$(cdp_eval "const link = [...document.querySelectorAll('a')].find(a => a.href?.includes('/scheduled-tasks') || a.textContent?.includes('Scheduled Tasks')); link?.click(); link ? 'clicked: ' + link.textContent?.trim()?.substring(0,30) : 'no link found'" | cdp_value)
log "Navigation: $NAV_RESULT"
sleep 3
screenshot "01-after-nav"

if wait_for "#scheduled-tasks-page" "page" 10; then
  pass "Navigated to /scheduled-tasks page"
else
  # Fallback: try Blazor NavigationManager
  cdp_eval "Blazor?.navigateTo?.('/scheduled-tasks') || DotNet?.invokeMethodAsync?.('PolyPilot', 'NavigateTo', '/scheduled-tasks') || 'no blazor nav'"
  sleep 2
  screenshot "01b-fallback-nav"
  if wait_for "#scheduled-tasks-page" "page" 5; then
    pass "Navigated via fallback"
  else
    # Debug: what page are we on?
    PAGE_TEXT=$(cdp_eval "document.body?.innerText?.substring(0,200)" | cdp_value)
    log "Current page text: $PAGE_TEXT"
    fail "Could not navigate to /scheduled-tasks page"
  fi
fi
echo ""

# ─── Test 2: Create a new task ───
echo "▸ Test 2: Create a new scheduled task"
CLICK_NEW=$(cdp_click "#scheduled-task-new")
log "Click new: $CLICK_NEW"
sleep 1
screenshot "02-after-new-click"

if wait_for "#scheduled-task-form" "form" 5; then
  pass "Task creation form opened"
else
  fail "Task creation form did not open"
fi

TASK_NAME="CI-Test-$(date +%s)"
log "Task name: $TASK_NAME"

SET_NAME=$(cdp_set_input "#scheduled-task-name" "$TASK_NAME")
SET_PROMPT=$(cdp_set_input "#scheduled-task-prompt" "echo hello from integration test")
log "Set name: $SET_NAME, prompt: $SET_PROMPT"

# Select Interval schedule type
cdp_eval "const sel = document.querySelector('#scheduled-task-schedule'); if (sel) { sel.value = 'Interval'; sel.dispatchEvent(new Event('change', {bubbles:true})); 'selected Interval'; } else { 'no select'; }" | cdp_value
sleep 1

SET_INTERVAL=$(cdp_set_input "#scheduled-task-interval" "60")
log "Set interval: $SET_INTERVAL"
screenshot "02b-form-filled"

CLICK_SAVE=$(cdp_click "#scheduled-task-save")
log "Click save: $CLICK_SAVE"
sleep 2
screenshot "02c-after-save"

if wait_for ".task-card[data-task-name='$TASK_NAME']" "card" 10; then
  pass "Task '$TASK_NAME' created and visible"
else
  # Debug
  CARDS=$(cdp_eval "document.querySelectorAll('.task-card')?.length || 0" | cdp_value)
  ALL_NAMES=$(cdp_eval "[...document.querySelectorAll('.task-card')].map(c => c.dataset?.taskName).join(', ')" | cdp_value)
  log "Cards found: $CARDS, names: $ALL_NAMES"
  fail "Task card did not appear after creation"
fi
echo ""

# ─── Test 3: Verify task card details ───
echo "▸ Test 3: Verify task card content"
SCHEDULE_TEXT=$(cdp_text ".task-card[data-task-name='$TASK_NAME'] .task-schedule")
log "Schedule text: '$SCHEDULE_TEXT'"
if echo "$SCHEDULE_TEXT" | grep -qi "60\|minute\|hour\|every"; then
  pass "Schedule description correct: '$SCHEDULE_TEXT'"
else
  fail "Unexpected schedule description: '$SCHEDULE_TEXT'"
fi

PROMPT_TEXT=$(cdp_text ".task-card[data-task-name='$TASK_NAME'] .task-prompt-preview")
log "Prompt text: '$PROMPT_TEXT'"
if echo "$PROMPT_TEXT" | grep -qi "hello"; then
  pass "Prompt preview shows task prompt"
else
  fail "Prompt preview unexpected: '$PROMPT_TEXT'"
fi
echo ""

# ─── Test 4: Disable/Enable toggle ───
echo "▸ Test 4: Toggle task enabled/disabled"
cdp_click ".task-card[data-task-name='$TASK_NAME'] [data-task-action='toggle-enabled']"
sleep 1

DISABLED=$(cdp_exists ".task-card[data-task-name='$TASK_NAME'].disabled")
if [ "$DISABLED" = "true" ]; then
  pass "Task disabled"
else
  fail "Task not visually disabled after toggle"
fi

cdp_click ".task-card[data-task-name='$TASK_NAME'] [data-task-action='toggle-enabled']"
sleep 1

ENABLED=$(cdp_exists ".task-card[data-task-name='$TASK_NAME']:not(.disabled)")
if [ "$ENABLED" = "true" ]; then
  pass "Task re-enabled"
else
  fail "Task not re-enabled after second toggle"
fi
echo ""

# ─── Test 5: Form validation (invalid cron) ───
echo "▸ Test 5: Form validation — invalid cron"
cdp_click "#scheduled-task-new"
sleep 1

cdp_set_input "#scheduled-task-name" "invalid-cron-test"
cdp_set_input "#scheduled-task-prompt" "test"
cdp_eval "const sel = document.querySelector('#scheduled-task-schedule'); if (sel) { sel.value = 'Cron'; sel.dispatchEvent(new Event('change', {bubbles:true})); 'selected Cron'; } else { 'no select'; }" | cdp_value
sleep 1
cdp_set_input "#scheduled-task-cron" "not a valid cron"
cdp_click "#scheduled-task-save"
sleep 1

ERROR_TEXT=$(cdp_text "#scheduled-task-form-error")
log "Error text: '$ERROR_TEXT'"
if echo "$ERROR_TEXT" | grep -qi "invalid\|cron\|error"; then
  pass "Validation error shown: '$ERROR_TEXT'"
else
  fail "No validation error for invalid cron"
fi

cdp_click "#scheduled-task-cancel"
sleep 0.5

INVALID_EXISTS=$(cdp_exists ".task-card[data-task-name='invalid-cron-test']")
if [ "$INVALID_EXISTS" = "false" ]; then
  pass "Invalid task was not created"
else
  fail "Invalid task was created despite validation error"
fi
echo ""

# ─── Test 6: Delete task ───
echo "▸ Test 6: Delete the test task"
if [ "$NO_CLEANUP" != "--no-cleanup" ]; then
  cdp_click ".task-card[data-task-name='$TASK_NAME'] [data-task-action='delete']"
  sleep 1

  if wait_for ".task-card[data-task-name='$TASK_NAME'] .delete-confirm-bar" "confirm" 5; then
    pass "Delete confirmation bar appeared"
  else
    fail "Delete confirmation bar did not appear"
  fi

  cdp_click ".task-card[data-task-name='$TASK_NAME'] [data-task-action='confirm-delete']"
  sleep 2

  DELETED=$(cdp_exists ".task-card[data-task-name='$TASK_NAME']")
  if [ "$DELETED" = "false" ]; then
    pass "Task deleted successfully"
  else
    fail "Task still visible after deletion"
  fi
else
  log "Skipping cleanup (--no-cleanup)"
fi

screenshot "final"
echo ""

# ─── Summary ───
echo "═══════════════════════════════════════════"
echo "  Results: $PASS passed, $FAIL failed"
if [ $FAIL -gt 0 ]; then
  echo ""
  echo "  Failures:"
  echo -e "$ERRORS"
fi
echo "═══════════════════════════════════════════"

exit $FAIL
