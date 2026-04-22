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

# Both Check-Staleness.ps1 and Scan-GhAwUpdates.ps1 call `gh api` and `gh issue view`.
# Inside the agent container, gh CLI credentials are scrubbed — all those calls return
# "authentication required" errors, producing a report where every source has
# status:"error". The agent can't distinguish "sources clean" from "couldn't check",
# so it can't call noop (doesn't know if things are fresh) and can't create an issue
# (no actionable finding), causing the workflow to exit without calling any safe-output tool.
#
# Fix: run both scripts in `steps:` (runs before the agent, with gh authenticated).
# The agent reads pre-computed staleness-report.json and scan-report.json.
steps:
  - name: Run staleness check
    env:
      GH_TOKEN: ${{ github.token }}
    run: |
      echo "::group::Check-Staleness.ps1"
      pwsh .github/skills/instruction-drift/scripts/Check-Staleness.ps1 \
        -OutputFile staleness-report.json
      echo "::endgroup::"

  - name: Scan upstream knowledge (only when drift detected)
    env:
      GH_TOKEN: ${{ github.token }}
    run: |
      CHANGES=$(jq -r '.changes_detected // false' staleness-report.json 2>/dev/null || echo "false")
      if [ "$CHANGES" = "true" ]; then
        echo "::group::Scan-GhAwUpdates.ps1"
        pwsh .github/skills/instruction-drift/scripts/Scan-GhAwUpdates.ps1 \
          -MaxCommits 50 \
          -OutputFile scan-report.json || true
        echo "::endgroup::"
      else
        echo '{"changes_detected":false,"new_features":[],"feature_summary":{}}' > scan-report.json
        echo "Skipping upstream scan — no drift detected."
      fi

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

Check whether gh-aw skill files are stale relative to upstream documentation, and if so, create a PR with updates.

> **🚨 No test messages.** Never call any safe-output tool with placeholder or test content. Every call posts permanently.

> **🚨 Security:** This workflow has `protected-files: allowed` because it intentionally updates `.github/skills/` files. Review the generated PR carefully before merging.

## Step 1: Read the pre-computed staleness report

The staleness check already ran in the `steps:` phase with an authenticated `gh` CLI.
Read the output file:

```bash
cat staleness-report.json
```

> **If `staleness-report.json` is missing or empty:** The pre-agent step failed.
> Call the `noop` tool: `noop(message="Pre-agent staleness check failed — see workflow logs.")` and stop.
>
> **Do NOT attempt to run `Check-Staleness.ps1` yourself.** It calls `gh api` and `gh issue view`
> which require `gh` CLI authentication that is not available inside the agent container.
> The `steps:` phase is the only place these scripts run correctly.

The report has this structure:

```json
{
  "checked_at": "...",
  "changes_detected": true | false,
  "manifests": [
    {
      "manifest": ".github/instructions/gh-aw-workflows.sync.yaml",
      "target": "../skills/gh-aw-guide/SKILL.md",
      "sources": [
        { "type": "web",      "url": "...",  "result": { "status": "ok", "content_hash": "..." } },
        { "type": "issue",    "ref": "...",  "resolution_expected": true, "result": { "status": "ok", "state": "closed" } },
        { "type": "releases", "repo": "...", "result": { "status": "ok", "latest": { "tag": "...", "release_notes": "..." } } }
      ],
      "untracked_pages": [...],
      "untracked_closed_issues": [...]
    }
  ]
}
```

**`changes_detected: false`** — All sources fetched successfully and no actionable signals found.
**`changes_detected: true`** — At least one source has a stale or actionable signal.

## 🚨 CRITICAL — Step 2: Decide based on `changes_detected`

### If `changes_detected` is `false` → call `noop` IMMEDIATELY

You have a tool called `noop` in your available tools. Call it now with this message:

```
noop(message="All instruction files are fresh — no drift detected.")
```

**Do not** create issues, PRs, or comments. **Do not** do any further analysis. Call the `noop` tool and stop.

### If `changes_detected` is `true` → continue to Step 3

---

## Step 3: Read the upstream scan report (only when drift detected)

```bash
cat scan-report.json
```

The scan report contains `new_features` (categorized by type: safe-output, trigger, compiler, security, engine, breaking) and `safe_output_samples` from real-world shared/ configs.

## Step 4: Analyze and classify

Read the current skill files:
- `.github/skills/gh-aw-guide/SKILL.md`
- `.github/skills/gh-aw-guide/references/architecture.md`
- `.github/instructions/gh-aw-workflows.instructions.md`
- `.github/instructions/gh-aw-workflows.sync.yaml`

For each staleness signal, cross-reference with the upstream scan to determine what needs updating.

### Update Rules

1. **Respect `divergence` sections** declared in the sync manifest — NEVER remove or rewrite these:
   - "Security Boundaries"
   - "Safe Pattern: Checkout + Restore"
   - "Common Patterns"

2. **Classify using P0-P3 before editing:**
   - **P0 (factually wrong)** — Fix immediately. Example: issue referenced as open but now closed.
   - **P1 (security-relevant)** — Fix immediately. Example: new anti-pattern or protection mechanism.
   - **P2 (new features)** — Add if straightforward. Example: new safe-output type, new frontmatter field.
   - **P3 (nice-to-have)** — Skip. Example: doc reorganization, new examples.

3. **Only make P0 and P1 changes automatically.** For P2, add a note to the PR description.

4. **Update the sync manifest** — Update `resolution_expected` fields to `true` for any issues that closed since last check, and add newly discovered issues.

5. **Match existing style** — Read the `style:` field in the sync manifest.

### Making Edits

Use the `edit` tool. Make surgical changes — don't rewrite entire sections.

Commit each logical change separately:
```bash
git add <specific-files>
git commit -m "docs: update <what changed>

Source: <upstream commit SHA or issue URL>

Co-authored-by: copilot-agentic-workflow[bot] <224017+copilot-agentic-workflow[bot]@users.noreply.github.com>"
```

## Step 5: Create PR

After all edits are committed, the `create-pull-request` safe output packages the changes into a draft PR.

The PR description should include:
- Which staleness signals triggered the update
- Which upstream changes were detected (with commit SHAs or issue URLs)
- What P0/P1 changes were made
- What P2 features were noted but NOT added (for human review)
