---
name: "Instruction Drift Check"
description: "Weekly check for stale gh-aw instructions. If drift is detected, scans upstream commits and creates a PR updating the skills."

on:
  schedule: weekly on monday around 9:00
  workflow_dispatch:

permissions:
  contents: read
  pull-requests: read

engine:
  id: copilot
  model: claude-sonnet-4.6

network:
  allowed:
    - defaults
    - dotnet

tools:
  github:
    toolsets: [repos, pull_requests]

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
  add-comment:
    max: 1
  noop:
    max: 1

concurrency:
  group: "instruction-drift-${{ github.run_id }}"
  cancel-in-progress: false

timeout-minutes: 30
---

# Instruction Drift — Detect & Update

Check whether gh-aw skill files are stale relative to upstream documentation, and if so, create a PR with updates.

> **🚨 No test messages.** Never call any safe-output tool with placeholder or test content. Every call posts permanently.

> **🚨 Security:** This workflow has `protected-files: allowed` because it intentionally updates `.github/skills/` files. Review the generated PR carefully before merging.

## Step 1: Run Staleness Check

Run the staleness checker against all sync manifests:

```bash
pwsh .github/skills/instruction-drift/scripts/Check-Staleness.ps1 \
  -SyncManifest .github/instructions/gh-aw-workflows.sync.yaml
```

Capture the exit code and full output:
- **Exit 0 (FRESH)** → Go to Step 5 (noop — nothing to do)
- **Exit 1 (STALE)** → Continue to Step 2
- **Exit 2 (ERROR)** → Create an issue reporting the error, then stop

## Step 2: Run Upstream Knowledge Extraction

If stale, scan the github/gh-aw repo for what specifically changed:

```bash
pwsh .github/skills/instruction-drift/scripts/Scan-GhAwUpdates.ps1 -MaxCommits 50
```

Parse the JSON output. Focus on:
- `new_features` — categorized changes (safe-output, trigger, compiler, security, engine, breaking)
- `safe_output_samples` — real-world patterns from shared/ configs
- `feature_summary` — grouped counts by type

## Step 3: Analyze and Update

Read the current skill files:
- `.github/skills/gh-aw-guide/SKILL.md`
- `.github/skills/gh-aw-guide/references/architecture.md`
- `.github/instructions/gh-aw-workflows.instructions.md`
- `.github/instructions/gh-aw-workflows.sync.yaml`

For each staleness signal from Step 1, cross-reference with the upstream changes from Step 2 to determine what needs updating.

### Update Rules

1. **Respect `divergence_sections`** from the sync manifest — NEVER remove or rewrite these sections:
   - "Known Limitation: Stale Blocking Reviews"
   - "Security Boundaries"
   - "Safe Pattern: Checkout + Restore"
   - "Common Patterns"

2. **Classify changes using P0-P3** before editing:
   - **P0 (factually wrong)** — Fix immediately. Example: an issue we reference as "open" is now closed.
   - **P1 (security-relevant)** — Fix immediately. Example: new anti-pattern or protection mechanism.
   - **P2 (new features)** — Add if straightforward. Example: new safe-output type or frontmatter field.
   - **P3 (nice-to-have)** — Skip for now. Example: doc reorganization, new examples.

3. **Only make P0 and P1 changes automatically.** For P2, add a brief note to the PR description listing what could be added. Skip P3 entirely.

4. **Update the sync manifest** — After making changes:
   - Update `last_reviewed` date to today
   - Update any `status:` fields on tracked issues that changed state
   - Add new tracked issues if the upstream scan discovered relevant ones

5. **Match existing style** — Use the same formatting, heading structure, and table layout as the existing files. Read the `style:` field in the sync manifest.

6. **Run the security scanner** after edits to verify no regressions:
   ```bash
   pwsh .github/skills/gh-aw-guide/scripts/Check-WorkflowSecurity.ps1
   ```

### Making Edits

Use the `edit` tool for all file changes. Make surgical changes — don't rewrite entire sections unless the content is factually wrong.

Commit each logical change separately:
```bash
git add <specific-files>
git commit -m "docs: update <what changed>

Source: <upstream commit SHA or issue URL>

Co-authored-by: copilot-agentic-workflow[bot] <224017+copilot-agentic-workflow[bot]@users.noreply.github.com>"
```

## Step 4: Create PR

After all edits are committed, the `create-pull-request` safe output will package the changes into a draft PR.

The PR description should include:
- Which staleness signals triggered the update
- What upstream changes were detected (with commit SHAs)
- What P0/P1 changes were made
- What P2 features were noted but NOT added (for human review)
- The full staleness report output

## Step 5: No Changes Needed

If the staleness check returned FRESH (exit 0), call `noop` with a message:

```
noop: "All instruction files are fresh — no drift detected. Last checked: <date>"
```

Do NOT create issues, PRs, or comments when nothing needs updating.
