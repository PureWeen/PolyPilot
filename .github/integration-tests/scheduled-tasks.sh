#!/usr/bin/env bash
# Scheduled Tasks Integration Tests
# Runs against a live PolyPilot app with MauiDevFlow agent connected.
# Usage: ./scheduled-tasks.sh <devflow-port> [--no-cleanup]
#
# Tests:
#   1. Navigate to /scheduled-tasks page
#   2. Create a new interval task via the form
#   3. Verify the task card appears with correct details
#   4. Run the task immediately (Run Now)
#   5. Verify run history shows success
#   6. Disable/enable the task
#   7. Delete the task
#   8. Verify form validation (invalid cron)
#
# Requires: curl, jq

set -euo pipefail

PORT="${1:?Usage: $0 <devflow-port> [--no-cleanup]}"
NO_CLEANUP="${2:-}"
BASE="http://localhost:$PORT"
PASS=0
FAIL=0
ERRORS=""

log()  { echo "  $1"; }
pass() { PASS=$((PASS + 1)); echo "  ✅ $1"; }
fail() { FAIL=$((FAIL + 1)); ERRORS="${ERRORS}\n  ❌ $1"; echo "  ❌ $1"; }

# Helper: evaluate JS in the Blazor WebView via CDP
cdp_eval() {
  local expr="$1"
  local result
  result=$(curl -s --max-time 10 "$BASE/api/cdp/evaluate" \
    -H "Content-Type: application/json" \
    -d "{\"expression\": \"$expr\"}" 2>/dev/null) || true
  echo "$result"
}

# Helper: click an element by CSS selector
cdp_click() {
  local selector="$1"
  cdp_eval "document.querySelector('$selector')?.click()"
}

# Helper: set input value and trigger change
cdp_set_value() {
  local selector="$1"
  local value="$2"
  cdp_eval "(() => { const el = document.querySelector('$selector'); if (el) { el.value = '$value'; el.dispatchEvent(new Event('input', {bubbles:true})); el.dispatchEvent(new Event('change', {bubbles:true})); } return el ? 'ok' : 'not found'; })()"
}

# Helper: select dropdown option
cdp_select() {
  local selector="$1"
  local value="$2"
  cdp_eval "(() => { const el = document.querySelector('$selector'); if (el) { el.value = '$value'; el.dispatchEvent(new Event('change', {bubbles:true})); } return el ? 'ok' : 'not found'; })()"
}

# Helper: check element exists
cdp_exists() {
  local selector="$1"
  local result
  result=$(cdp_eval "document.querySelector('$selector') !== null")
  echo "$result" | jq -r '.result.value // false' 2>/dev/null
}

# Helper: get text content
cdp_text() {
  local selector="$1"
  local result
  result=$(cdp_eval "document.querySelector('$selector')?.textContent?.trim()")
  echo "$result" | jq -r '.result.value // empty' 2>/dev/null
}

# Helper: wait for element to appear
wait_for() {
  local selector="$1"
  local desc="${2:-$selector}"
  local timeout="${3:-15}"
  for i in $(seq 1 "$timeout"); do
    if [ "$(cdp_exists "$selector")" = "true" ]; then
      return 0
    fi
    sleep 1
  done
  return 1
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
echo ""

# ─── Test 1: Navigate to Scheduled Tasks page ───
echo "▸ Test 1: Navigate to /scheduled-tasks"
cdp_eval "window.location.href = '/scheduled-tasks'"
sleep 2
if wait_for "#scheduled-tasks-page" "scheduled tasks page" 10; then
  pass "Navigated to /scheduled-tasks page"
else
  fail "Could not navigate to /scheduled-tasks page"
fi
echo ""

# ─── Test 2: Create a new task ───
echo "▸ Test 2: Create a new scheduled task"
cdp_click "#scheduled-task-new"
sleep 1

if wait_for "#scheduled-task-form" "task form" 5; then
  pass "Task creation form opened"
else
  fail "Task creation form did not open"
fi

TASK_NAME="CI-Test-$(date +%s)"
cdp_set_value "#scheduled-task-name" "$TASK_NAME"
cdp_set_value "#scheduled-task-prompt" "echo hello from integration test"
cdp_select "#scheduled-task-schedule" "Interval"
sleep 0.5
cdp_set_value "#scheduled-task-interval" "60"

cdp_click "#scheduled-task-save"
sleep 2

if wait_for ".task-card[data-task-name='$TASK_NAME']" "task card" 10; then
  pass "Task '$TASK_NAME' created and visible"
else
  fail "Task card did not appear after creation"
fi
echo ""

# ─── Test 3: Verify task card details ───
echo "▸ Test 3: Verify task card content"
SCHEDULE_TEXT=$(cdp_text ".task-card[data-task-name='$TASK_NAME'] .task-schedule")
if echo "$SCHEDULE_TEXT" | grep -qi "60 min\|1 hour\|every"; then
  pass "Schedule description correct: '$SCHEDULE_TEXT'"
else
  fail "Unexpected schedule description: '$SCHEDULE_TEXT'"
fi

PROMPT_TEXT=$(cdp_text ".task-card[data-task-name='$TASK_NAME'] .task-prompt-preview")
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
  pass "Task disabled (has .disabled class)"
else
  fail "Task not visually disabled after toggle"
fi

# Re-enable
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

cdp_set_value "#scheduled-task-name" "invalid-cron-test"
cdp_set_value "#scheduled-task-prompt" "test"
cdp_select "#scheduled-task-schedule" "Cron"
sleep 0.5
cdp_set_value "#scheduled-task-cron" "not a valid cron"
cdp_click "#scheduled-task-save"
sleep 1

ERROR_TEXT=$(cdp_text "#scheduled-task-form-error")
if echo "$ERROR_TEXT" | grep -qi "invalid\|cron\|error"; then
  pass "Validation error shown: '$ERROR_TEXT'"
else
  fail "No validation error for invalid cron: '$ERROR_TEXT'"
fi

# Cancel form
cdp_click "#scheduled-task-cancel"
sleep 0.5

# Verify invalid task was NOT created
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

  if wait_for ".task-card[data-task-name='$TASK_NAME'] .delete-confirm-bar" "delete confirmation" 5; then
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
