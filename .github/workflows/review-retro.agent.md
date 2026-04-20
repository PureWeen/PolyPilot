---
name: "Review Retrospective"
description: "After a PR merges, analyzes all expert review workflow runs on that PR to identify missed skill invocations, false positives, missed bugs, and improvement opportunities."

on:
  pull_request:
    types: [closed]
  roles: [admin, maintainer, write]

# Only run when PR was actually merged, not just closed
if: github.event.pull_request.merged == true

permissions:
  contents: read
  pull-requests: read

concurrency:
  group: "retro-${{ github.event.pull_request.number || github.run_id }}"
  cancel-in-progress: true

engine:
  id: copilot
  model: claude-sonnet-4.6

network:
  allowed:
    - defaults
    - dotnet

safe-outputs:
  create-issue:
    max: 1
    title-prefix: "[review-retro] "
    labels: [review-retrospective]
    expires: 30
    close-older-issues: true
  add-comment:
    max: 1
  noop:
    max: 1

timeout-minutes: 30
---

# Review Retrospective

Analyze the expert review history of merged PR #${{ github.event.pull_request.number }} to find improvement opportunities for the review workflow.

> **🚨 No test messages.** Never call any safe-output tool with placeholder or test content. Every call posts permanently. This applies to you and all sub-agents.

## Step 1: Gather Data

Collect all review activity on this PR:

```bash
# PR metadata
gh pr view ${{ github.event.pull_request.number }} --json title,body,mergedAt,mergedBy,commits,files

# All reviews (bot and human)
gh api repos/${{ github.repository }}/pulls/${{ github.event.pull_request.number }}/reviews --jq '.[] | {id, state, user: .user.login, submitted_at, body}'

# All review comments (inline)
gh api repos/${{ github.repository }}/pulls/${{ github.event.pull_request.number }}/comments --jq '.[] | {id, path, line, body, created_at, user: .user.login}'

# All issue comments (design-level, status)
gh api repos/${{ github.repository }}/issues/${{ github.event.pull_request.number }}/comments --jq '.[] | {id, user: .user.login, created_at, body}'

# The final merged diff
gh pr diff ${{ github.event.pull_request.number }}

# Changed file list
gh pr diff ${{ github.event.pull_request.number }} --name-only
```

## Step 2: Identify Expert Review Runs

Look for comments and reviews from `github-actions[bot]` that contain the `gh-aw-agentic-workflow` HTML comment marker or "Expert Code Review" in the body. These are the automated review runs.

For each run, extract:
- Which workflow triggered it (review-on-open.agent vs review.agent)
- The findings (severity, file, line, description)
- Whether findings were addressed in subsequent commits
- The final verdict

## Step 3: Analyze Skill Usage

Read `.github/copilot-instructions.md` and scan for all skill references (`.claude/skills/*/SKILL.md`). For each skill mentioned:

1. **Was the skill relevant to this PR?** — Check if the changed files touch areas the skill covers:
   - `CopilotService.cs`, `Events.cs` → `processing-state-safety`, `copilot-sdk-reference`
   - `CopilotService.Persistence.cs`, `Organization.cs` → `performance-optimization`
   - `SendViaOrchestrator*` → `multi-agent-orchestration`
   - `.github/workflows/*.md` → `gh-aw-guide`
   - Android deploy → `android-wifi-deploy`
   - Blazor/UI components → `maui-ai-debugging`

2. **Did the review mention or apply knowledge from that skill?** — Check if the review findings reference invariants, patterns, or rules from the skill.

3. **Did the review MISS something the skill would have caught?** — Read the relevant skill files and check if they document patterns or invariants that apply to the changed code but weren't flagged.

## Step 4: Check for False Positives

For each finding in the automated review:
1. Was the finding actually fixed before merge? (Compare finding line/file with final merged diff)
2. Was the finding dismissed by a human reviewer as incorrect?
3. Did the finding correctly identify a real issue?

Classify each finding as:
- **True Positive** — real issue, correctly identified
- **True Positive (Fixed)** — real issue, fixed before merge
- **False Positive** — not actually a bug, wasted reviewer time
- **Unresolved** — real issue that merged without being fixed

## Step 5: Check for False Negatives

Look at the final merged diff and check for patterns that SHOULD have been caught:
- Any `IsProcessing` mutation without `ClearProcessingState()`?
- Any new state fields on `SessionState` or `AgentSessionInfo`?
- Any `static readonly` fields calling platform APIs?
- Any `@bind:event="oninput"` in Razor components?
- Any missing `InvokeOnUI()` for background-thread state mutations?
- Any calls to `ConnectionSettings.Save()` or `Load()` in test files?
- Any `.github/workflows/*.md` changes without recompiling lock files?

Read the relevant skill files to find additional patterns to check.

## Step 6: Generate Report

**If there are actionable findings** (missed skills, false negatives, improvement suggestions), create an issue with `create_issue`:

```markdown
## Review Retrospective — PR #<number>

**PR:** <title>
**Merged:** <date> by <user>
**Review runs:** <count> automated reviews

### Skill Coverage Analysis

| Skill | Relevant? | Referenced? | Gap? |
|-------|-----------|-------------|------|
| processing-state-safety | ✅ Yes | ✅ Yes | — |
| copilot-sdk-reference | ✅ Yes | ❌ No | ⚠️ Should have checked SDK types |
| performance-optimization | ❌ No | — | — |

### Review Accuracy

| Metric | Count |
|--------|-------|
| True Positives | N |
| True Positives (Fixed) | N |
| False Positives | N |
| False Negatives (missed) | N |
| Unresolved | N |

### Missed Findings (False Negatives)
- <description of what the review should have caught>

### False Positives
- <description of incorrect findings that wasted time>

### Improvement Suggestions
- <concrete suggestion for improving the review prompt, skill content, or workflow>
```

**If there are NO actionable findings** (review was accurate, skills were used correctly, nothing missed), use `noop` — do not create an unnecessary issue.

## Rules

1. **Only create an issue if there's something actionable.** Good reviews don't need a retrospective issue.
2. **Be specific about false negatives.** Don't say "should have checked more" — say exactly what was missed and which skill/invariant would have caught it.
3. **Don't count style/formatting findings.** The review is explicitly told to skip those.
4. **Respect the expires: 30 field.** Retro issues auto-close after 30 days if not addressed.
5. **close-older-issues: true** ensures only the latest retro per workflow is open.
