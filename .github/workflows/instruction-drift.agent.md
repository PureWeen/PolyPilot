---
name: "Instruction Drift Check"
description: "Weekly check for stale gh-aw instructions. If drift is detected, scans upstream commits and creates a PR updating the skills."

on:
  schedule: weekly on monday around 9:00
  workflow_dispatch:

permissions:
  contents: read
  pull-requests: read

# Scripts need gh CLI auth (scrubbed inside agent container).
# Run in steps:, pass results via $GITHUB_OUTPUT → template substitution into prompt.
steps:
  - name: Run staleness check
    id: staleness
    env:
      GH_TOKEN: ${{ github.token }}
    run: |
      RESULT=$(pwsh .claude/skills/instruction-drift/scripts/Check-Staleness.ps1 \
        -SyncManifest .github/instructions/gh-aw-workflows.sync.yaml 2>/dev/null \
        || echo '{"changes_detected":false,"error":"script failed"}')
      CHANGES=$(echo "$RESULT" | python3 -c "import json,sys; d=json.load(sys.stdin); print(str(d.get('changes_detected',False)).lower())" 2>/dev/null || echo "false")
      echo "changes_detected=$CHANGES" >> "$GITHUB_OUTPUT"
      echo "report<<REPORT_EOF" >> "$GITHUB_OUTPUT"
      echo "$RESULT" | head -c 60000 >> "$GITHUB_OUTPUT"
      echo "" >> "$GITHUB_OUTPUT"
      echo "REPORT_EOF" >> "$GITHUB_OUTPUT"

  - name: Run upstream scan (if stale)
    id: upstream
    if: steps.staleness.outputs.changes_detected == 'true'
    env:
      GH_TOKEN: ${{ github.token }}
    run: |
      RESULT=$(pwsh .claude/skills/instruction-drift/scripts/Scan-GhAwUpdates.ps1 \
        -MaxCommits 50 2>/dev/null \
        || echo '{"changes_detected":false,"error":"script failed"}')
      echo "report<<SCAN_EOF" >> "$GITHUB_OUTPUT"
      echo "$RESULT" | head -c 60000 >> "$GITHUB_OUTPUT"
      echo "" >> "$GITHUB_OUTPUT"
      echo "SCAN_EOF" >> "$GITHUB_OUTPUT"

engine:
  id: copilot
  model: claude-sonnet-4.6

network:
  allowed:
    - defaults
    - dotnet

safe-outputs:
  create-pull-request:
    title-prefix: "[drift] "
    labels: [instruction-drift, automation]
    draft: true
    expires: 14
    protected-files: allowed
  create-issue:
    title-prefix: "[drift] "
    labels: [instruction-drift]
    close-older-issues: true
    expires: 30
  noop:
    max: 1

concurrency:
  group: "instruction-drift-${{ github.run_id }}"
  cancel-in-progress: false

timeout-minutes: 30
---

# Instruction Drift — Detect & Update

> **🚨 No test messages.** Never call any safe-output tool with placeholder or test content.

## Pre-computed Staleness Data

The staleness check ran in `steps:` (before you started) with authenticated `gh` CLI.
**Do NOT run Check-Staleness.ps1 yourself** — `gh` CLI is not authenticated in this container.

**Changes detected: `${{ steps.staleness.outputs.changes_detected }}`**

### Staleness Report
```json
${{ steps.staleness.outputs.report }}
```

### Upstream Scan (only present if changes detected)
```json
${{ steps.upstream.outputs.report }}
```

## 🚨 CRITICAL — What To Do

### If changes_detected is `false` → call `noop` NOW

Call the `noop` tool immediately:
```
noop(message="All instruction files are fresh — no drift detected.")
```
Do not create issues, PRs, or comments. Stop after calling noop.

### If changes_detected is `true` → continue below

---

## Analyze and Update

Read the staleness report above. For each signal, read the affected skill files and make updates:
- `.claude/skills/gh-aw-guide/SKILL.md`
- `.claude/skills/gh-aw-guide/references/architecture.md`
- `.github/instructions/gh-aw-workflows.instructions.md`
- `.github/instructions/gh-aw-workflows.sync.yaml`

### Rules
1. **Respect `divergence` sections** — NEVER remove: "Security Boundaries", "Safe Pattern: Checkout + Restore", "Common Patterns"
2. **Classify P0-P3:** P0=factually wrong, P1=security, P2=new features, P3=nice-to-have
3. **Only auto-fix P0 and P1.** Note P2 in PR description. Skip P3.
4. **Update sync manifest** — `resolution_expected`, `last_reviewed`, new issues

Commit each change:
```bash
git add <specific-files>
git commit -m "docs: update <what>

Co-authored-by: copilot-agentic-workflow[bot] <224017+copilot-agentic-workflow[bot]@users.noreply.github.com>"
```

## Create PR

The `create-pull-request` safe output packages changes into a draft PR.
