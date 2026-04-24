---
name: gh-aw-guide
description: >-
  Comprehensive guide for building and maintaining GitHub Agentic Workflows (gh-aw).
  Covers architecture, security boundaries, fork handling, safe outputs, anti-patterns,
  compilation, and troubleshooting. Use when creating or editing gh-aw workflow .md files,
  writing safe-outputs configurations, configuring fork PR handling, setting up integrity
  filtering, debugging "why doesn't my workflow trigger", or any task involving
  .github/workflows/*.md or .lock.yml files. Also use when asked about gh-aw features,
  slash commands, pre-agent-steps, protected files, or agentic workflow security.
---

# gh-aw (GitHub Agentic Workflows) Guide

This skill provides the complete reference for building, securing, and maintaining GitHub Agentic Workflows in this repository. It covers the gh-aw platform's architecture, security model, and all available features.

## Quick Start

gh-aw workflows are authored as `.md` files with YAML frontmatter, compiled to `.lock.yml` via `gh aw compile`. The lock file is auto-generated — **never edit it manually**.

```bash
# Compile after every change to the .md source
gh aw compile .github/workflows/<name>.md

# This updates:
# - .github/workflows/<name>.lock.yml (auto-generated)
# - .github/aw/actions-lock.json
```

**Always commit the compiled lock file alongside the source `.md`.**

### CLI Commands

```bash
gh aw compile <name>          # Compile .md → .lock.yml
gh aw run <name>              # Trigger a workflow_dispatch run
gh aw run <name> --ref main   # Run on a specific branch
gh aw status                  # List all workflows and their status
gh aw trial ./<name>.md --clone-repo owner/repo  # Test a workflow before merging to main
gh aw audit <run-id>          # Analyze a completed workflow run
gh aw upgrade                 # Upgrade gh-aw CLI extension
```

**`gh aw trial`** — Test workflows that aren't on main yet. Creates a temporary private repo, installs the workflow, and runs it. Essential for validating new workflows before merge, since `workflow_dispatch` requires the lock file on the default branch.

**`gh aw run --ref`** — Trigger a workflow on a specific branch. The workflow must already exist on that branch (registered by GitHub after the lock file is pushed).

## 🚨 Before You Build: Prefer Built-in gh-aw Features

**CRITICAL RULE:** Before implementing any trigger, output, scheduling, or interaction mechanism in a gh-aw workflow, check whether gh-aw has a built-in feature that does it. gh-aw extends GitHub Actions with many convenience features — manually reimplementing them is always worse (more code, more bugs, missing platform integration like emoji reactions, sanitized inputs, and noise reduction).

### Step 1: Check the anti-patterns table below
### Step 2: If not listed, check the [triggers reference](https://github.github.com/gh-aw/reference/triggers/), [frontmatter reference](https://github.github.com/gh-aw/reference/frontmatter/), and [safe-outputs reference](https://github.github.com/gh-aw/reference/safe-outputs/)
### Step 3: If a built-in exists, use it. If not, proceed with manual implementation.

### Anti-Patterns: Manual Reimplementations to Avoid

| If you're about to implement... | Use this built-in instead | Docs |
|---------------------------------|--------------------------|------|
| `issue_comment` + `startsWith(comment.body, '/cmd')` | `slash_command:` trigger | [Command Triggers](https://github.github.com/gh-aw/reference/command-triggers/) |
| Manual emoji reaction on triggering comment | `reaction:` field under `on:` | [Frontmatter](https://github.github.com/gh-aw/reference/frontmatter/) |
| Posting "workflow started/completed" status comments | `status-comment: true` under `on:` | [Frontmatter](https://github.github.com/gh-aw/reference/frontmatter/) |
| Fixed cron schedule (`0 9 * * 1`) for non-critical timing | `schedule: weekly on monday around 9:00` (fuzzy) | [Triggers](https://github.github.com/gh-aw/reference/triggers/) |
| Manual `if:` to skip bot-authored PRs | `skip-bots:` under `on:` | [Triggers](https://github.github.com/gh-aw/reference/triggers/) |
| Manual `if:` to skip by author role | `skip-roles:` under `on:` | [Triggers](https://github.github.com/gh-aw/reference/triggers/) |
| Manual label check + removal for one-shot commands | `label_command:` trigger | [Triggers](https://github.github.com/gh-aw/reference/triggers/) |
| Editing old comments to collapse them | `hide-older-comments: true` on `add-comment:` | [Safe Outputs](https://github.github.com/gh-aw/reference/safe-outputs/) |
| Creating no-op report issues | `noop: report-as-issue: false` | [Safe Outputs / Monitoring](https://github.github.com/gh-aw/patterns/monitoring/) |
| Auto-closing older issues from same workflow | `close-older-issues: true` on `create-issue:` | [Safe Outputs](https://github.github.com/gh-aw/reference/safe-outputs/) |
| Disabling workflow after a date | `stop-after:` under `on:` | [Triggers](https://github.github.com/gh-aw/reference/triggers/) |
| Manual approval gating | `manual-approval:` under `on:` | [Triggers](https://github.github.com/gh-aw/reference/triggers/) |
| Search-based skip logic in `steps:` | `skip-if-match:` / `skip-if-no-match:` under `on:` | [Triggers](https://github.github.com/gh-aw/reference/triggers/) |
| Locking issues to prevent concurrent edits | `lock-for-agent: true` under trigger | [Triggers](https://github.github.com/gh-aw/reference/triggers/) |
| Manually hiding agent comments | `hide-comment:` safe output | [Safe Outputs](https://github.github.com/gh-aw/reference/safe-outputs/) |
| Custom post-processing jobs for agent output | `safe-outputs.jobs:` custom jobs with MCP tool access | [Custom Safe Outputs](https://github.github.com/gh-aw/reference/custom-safe-outputs/) |
| Wrapping GitHub Actions as agent-callable tools | `safe-outputs.actions:` action wrappers | [Custom Safe Outputs](https://github.github.com/gh-aw/reference/custom-safe-outputs/) |
| Triggering CI on agent-created PRs | `github-token-for-extra-empty-commit:` on `create-pull-request` | [Triggering CI](https://github.github.com/gh-aw/reference/triggering-ci/) |
| No guard against agent approving PRs | `allowed-events: [COMMENT]` on `submit-pull-request-review` (prefer over `[COMMENT, REQUEST_CHANGES]` to avoid stale blocking reviews) | [Safe Outputs](https://github.github.com/gh-aw/reference/safe-outputs/) |
| `slash_command:` without `events:` filter (subscribes to ALL comment events) | `events: [pull_request_comment]` or `events: [issue_comment]` | [Command Triggers](https://github.github.com/gh-aw/reference/command-triggers/) |
| `cancel-in-progress: true` on `slash_command:` workflows | `cancel-in-progress: false` — non-matching events cancel in-progress agent runs | [Concurrency](https://docs.github.com/en/actions/writing-workflows/choosing-what-your-workflow-does/using-concurrency) |
| Using `pull_request` trigger for agentic workflows | `slash_command:` or `schedule` — `pull_request` causes the "Approve and run" gate for ALL workflows | [Triggers](https://github.github.com/gh-aw/reference/triggers/) |

**Note:** gh-aw is actively developed. If a capability feels like something a framework would provide natively, check the reference docs — it probably exists even if it's not in this table yet.

For full architecture, security, fork handling, safe outputs, and troubleshooting details, read `references/architecture.md` in this skill directory.

## Common Patterns

### Pre-Agent Data Prep (the `steps:` pattern)

Use `steps:` for any operation requiring GitHub API access that the agent needs:

```yaml
steps:
  - name: Fetch PR data
    env:
      GH_TOKEN: ${{ github.token }}
    run: |
      gh pr view "$PR_NUMBER" --json title,body > pr-metadata.json
      gh pr diff "$PR_NUMBER" --name-only > changed-files.txt
```

### Payload Sanitization

Comment bodies, issue titles, and PR descriptions are **user-controlled untrusted input**. In pre-agent `steps:`, always use `steps.<id>.outputs.text` (sanitized) instead of raw `${{ github.event.comment.body }}`. Within the agent job itself, unsanitized input is acceptable because the agent runs in a sandboxed container — but pair with tight `safe-outputs:`.

> 🛑 **Recursive workflow triggering**: Actions performed via `GITHUB_TOKEN` do **NOT** fire new workflow events (prevents infinite loops). Actions via GitHub App installation tokens or PATs **DO** fire events. This is why `github-token-for-extra-empty-commit:` requires a PAT — `GITHUB_TOKEN` pushes won't trigger CI on agent-created PRs.

### Safe Outputs (Posting Comments)

```yaml
safe-outputs:
  add-comment:
    max: 1
    hide-older-comments: true
    target: "*"    # Required for workflow_dispatch (no triggering PR context)
```

### Concurrency

Include all trigger-specific PR number sources. **Use `cancel-in-progress: false` for `slash_command:` workflows** — a non-matching event (ordinary comment) in the same concurrency group can cancel an in-progress matching run (the actual `/command`), killing the agent mid-execution:

```yaml
# For slash_command workflows — never cancel in-progress
concurrency:
  group: "my-workflow-${{ github.event.issue.number || github.event.pull_request.number || inputs.pr_number || github.run_id }}"
  cancel-in-progress: false

# For schedule/workflow_dispatch only — safe to cancel
concurrency:
  group: "my-workflow-${{ github.ref || github.run_id }}"
  cancel-in-progress: true
```

> ⚠️ **Pre-cancellation race**: Cancellation is asynchronous — GitHub sends `SIGTERM`, waits up to 7500ms, then `SIGKILL`. Already-running steps may complete. An agent that already posted a comment cannot un-post it. A `create-pull-request` that already ran cannot un-create the PR. **Concurrency is not a substitute for idempotency.**

### `slash_command:` Event Subscription

`slash_command:` compiles to broad event subscriptions — by default it listens to **all** comment-related events (issue open/edit, PR open/edit, every comment, every review comment, every discussion comment), then filters post-activation. This means:

- **Runner cost**: The pre-activation job runs on every matching event (~5-30s each), even when skipped. On busy repos this can be hundreds of skipped runs per day.
- **Actions UI noise**: Operators learn to ignore "skipped" runs and may miss real failures.
- **Concurrency collisions**: Non-matching events in the same concurrency group can cancel matching ones (see above).

**Always narrow `events:`** to the minimum needed:

```yaml
on:
  slash_command:
    name: review
    events: [pull_request_comment]  # Only PR comments, not issues/discussions
```

### The "Approve and Run Workflows" Gate

The `pull_request` trigger causes an "Approve and run workflows" button for first-time fork contributors. **This gate is dangerous, not protective**:

1. **Alert fatigue** — After clicking through dozens of legitimate first-time PRs, the click becomes muscle memory
2. **No per-workflow granularity** — A single click approves ALL gated workflows, including any `pull_request_target` workflows with full secrets
3. **No diff preview** — The UI shows no preview of what will execute or which secrets are exposed

**Design rule**: Assume the approval gate will always be clicked. The only safe workflows are ones that produce the same outcome whether the actor is trusted or untrusted. Prefer `issue_comment`/`slash_command:` (not subject to the gate) or `schedule`/`workflow_dispatch` over `pull_request` when possible.

### LabelOps

gh-aw provides label-based triggering patterns for both one-shot commands and persistent state tracking:

- **`label_command:`** — One-shot command triggered by applying a label. The label is auto-removed after the workflow fires, making it self-resetting. Use for operations like "apply this label to trigger a review".
- **`names:` filtering** — Filter label events to specific label names for persistent label-state awareness.
- **`remove_label: false`** — Keep the label after triggering (for persistent state markers rather than one-shot commands).

See the [LabelOps pattern guide](https://github.github.com/gh-aw/patterns/label-ops/) for detailed examples and best practices.

### Noise Reduction

Filter `pull_request` triggers to relevant paths and add a gate step:

```yaml
on:
  pull_request:
    paths:
      - 'src/**/tests/**'

steps:
  - name: Gate — skip if no relevant files
    if: github.event_name == 'pull_request'
    run: |
      FILES=$(gh pr diff "$PR_NUMBER" --name-only | grep -E '\.cs$' || true)
      if [ -z "$FILES" ]; then exit 1; fi
```

Manual triggers (`workflow_dispatch`, `issue_comment`) should bypass the gate. Note: `exit 1` causes a red ❌ on non-matching PRs — this is intentional (no built-in "skip" mechanism in gh-aw steps).

### Fork PR Checkout (workflow_dispatch)

For `workflow_dispatch` workflows that need to evaluate a PR branch: use the shared `Checkout-GhAwPr.ps1` script. It (1) verifies the PR author has write access and rejects fork PRs, (2) checks out the PR branch, and (3) restores `.github/skills/`, `.github/instructions/`, and `.github/copilot-instructions.md` from the base branch SHA — defense-in-depth even though the platform also does this restore automatically.

```yaml
steps:
  - name: Checkout PR and restore agent infrastructure
    env:
      GH_TOKEN: ${{ github.token }}
      PR_NUMBER: ${{ inputs.pr_number }}
    run: pwsh .github/scripts/Checkout-GhAwPr.ps1
```

For `pull_request` + fork support (not `workflow_dispatch`): add `forks: ["*"]` to the trigger frontmatter. The platform automatically preserves `.github/` and `.agents/` as a base-branch artifact in the activation job, then restores them after `checkout_pr_branch.cjs` — fork PRs cannot overwrite agent infrastructure (gh-aw#23769, resolved).

### Operating Within a Fork

When you fork a repository, all workflow files come with it. Events inside your fork fire the workflows inside your fork, with your fork's secrets. This is separate from cross-fork PRs and is frequently a surprise.

**Guard pattern** — prevent workflows from running in forks (with a manual escape hatch):

```yaml
jobs:
  guard:
    if: github.event_name == 'workflow_dispatch' || !github.event.repository.fork
```

> ⚠️ **YAML gotcha**: Don't start a bare `if:` value with `!` — it's a YAML tag indicator. Either wrap in `${{ }}` or use parentheses: `if: (!github.event.repository.fork)`.

### Security-Critical Patterns

These patterns are the most commonly missed when building secure workflows. Use all where applicable.

**1. Role-based access control** — `roles:` controls who can trigger the workflow. Without it, any user (including the PR author) can trigger `/review` on a malicious PR designed to prompt-inject the reviewer. The default `[admin, maintainer, write]` is injected automatically for workflows with "unsafe" events (issues, comments, PRs, discussions):

```yaml
on:
  slash_command:
    name: review
    events: [pull_request_comment]
  roles: [admin, maintainer, write]  # Only committers can trigger — NEVER use 'all' unless you've audited every safe-output
```

> ⚠️ **`triage` role footgun**: `triage` is excluded from the default allowlist. A `label_command:` workflow (which requires triage to apply the label) will _fire_ but the activation job will _deny_ a triage user unless `roles:` is broadened. Add `triage` explicitly when triage users are the primary operators.

**2. Prevent accidental PR approvals** — always restrict review workflows; otherwise the agent can approve PRs and bypass branch protection rules (gh-aw#25439):

```yaml
safe-outputs:
  submit-pull-request-review:
    # COMMENT-only: no stale blocking reviews, safe for iterative /review re-runs
    allowed-events: [COMMENT]
    # Or allow REQUEST_CHANGES for stronger merge-gating (but stale reviews persist until manually dismissed):
    # allowed-events: [COMMENT, REQUEST_CHANGES]
```

**3. Integrity filtering** — controls what content the agent can **see** (vs. `roles:` which controls who can **trigger**). The MCP gateway filters content by author trust level:

| Level | Who qualifies |
|-------|--------------|
| `merged` | Merged PRs; commits on default branch (any author) |
| `approved` | `OWNER`, `MEMBER`, `COLLABORATOR`; non-fork PRs on public repos; all items in private repos; platform bots; `trusted-users` |
| `unapproved` | `CONTRIBUTOR`, `FIRST_TIME_CONTRIBUTOR` |
| `none` | All content including `FIRST_TIMER` and users with no association |
| `blocked` | Users in `blocked-users` — always denied, cannot be promoted |

```yaml
tools:
  github:
    min-integrity: approved        # Default for public repos — only trusted author content
    toolsets: [pull_requests, repos]
    # trusted-users: [contractor-1]   # Elevate specific users to 'approved'
    # blocked-users: [spam-bot]       # Unconditionally block specific users
    # approval-labels: [human-reviewed]  # Labels that promote items to 'approved'
```

**Interaction with `roles:`:**

| `roles:` | `min-integrity` | Effect |
|----------|----------------|--------|
| Default `[admin, maintainer, write]` | `approved` | **Most restrictive.** Only trusted actors trigger; agent sees only trusted content |
| Default | `unapproved`/`none` | Trusted actors only, but agent reads community content. Good for post-merge scans |
| `all` | `approved` | **Two-layer defense.** Any actor triggers, but agent only sees trusted content |
| `all` | `none` | **Widest exposure.** Must pair with minimal `safe-outputs` — only remaining constraint |

> ⚠️ **Compiler bug**: Hardcoded `min-integrity` in source emits an incomplete guard policy (missing `repos` field) that crashes the MCP Gateway (first observed in v0.62.2; unconfirmed whether fixed in later versions — test before hardcoding). Rely on the automatic `determine-automatic-lockdown` step instead, which applies `approved` for public repos by default.

**4. CI triggering + protected file safety** for agent-created PRs — `GITHUB_TOKEN` pushes don't trigger CI; a PAT/App token is required. `protected-files` controls what happens when the agent modifies package manifests or `.github/`:

```yaml
safe-outputs:
  create-pull-request:
    github-token-for-extra-empty-commit: ${{ secrets.PAT_OR_APP_TOKEN }}  # Required to trigger CI
    protected-files: fallback-to-issue   # Create issue instead of failing if agent touches .github/ or package manifests
    # protected-files: blocked (default) | allowed (disables protection)
```

**5. Fork PR checkout for `workflow_dispatch`** — the platform's `checkout_pr_branch.cjs` is skipped for `workflow_dispatch`, so you **must** use `.github/scripts/Checkout-GhAwPr.ps1` to check out the PR branch, verify write access, reject fork PRs, and restore trusted `.github/` from the base branch. Without it, the agent evaluates the workflow branch instead of the PR:

```yaml
steps:
  - name: Checkout PR and restore agent infrastructure
    env:
      GH_TOKEN: ${{ github.token }}
      PR_NUMBER: ${{ inputs.pr_number }}
    run: pwsh .github/scripts/Checkout-GhAwPr.ps1
```

### Idempotency and the Edited-Comment Time-Bomb

**Slash command workflows MUST be idempotent.** Treat every activation as if the same command might already be running for the same target. Check before acting, claim a lock, no-op if already in progress or done.

gh-aw provides `lock-for-agent: true` to automatically lock/unlock the issue during execution, but use with caution — it prevents genuine users from interacting on the issue/PR while the workflow runs.

**State tracking for scheduled pollers:** Use comment-based state to track what's been processed. Edit a comment's visible markdown to reflect status (⏳ in progress / ✅ done), and append invisible `<!-- state-machine -->` HTML comments as an append-only audit trail. This gives human-readable status and machine-parseable history in one artifact.

> 🛑 **The edited-comment time-bomb**: An attacker can edit a 6-month-old comment on a closed issue or PR, injecting `/command` or any payload — `issue_comment.edited` fires TODAY against today's secrets, today's `permissions:`, today's `safe-outputs:`. The workflow has no concept of "this comment was created when our security model was different." For raw `issue_comment`, use `types: [created]` — add `edited` only if you've designed for this attack vector.

### Read-Only Contributor Write Surface

> **What the agent can do is determined by `permissions:` and `safe-outputs:` — NOT by the actor who fired it.** When a workflow accepts a read-only contributor as the trigger (`roles: all`), that contributor effectively gets bot-level write access to anything the workflow grants the agent.

**What a read-only user can fire:**

| Action | Can fire? |
|--------|----------|
| Open an issue, comment, react with emoji | ✅ |
| `/slash-command` in any comment/body they author | ✅ |
| Open a PR (from fork) | ✅ |
| Apply a label | ❌ (requires triage) |
| Invoke `workflow_dispatch` | ❌ (requires write) |
| Click "Approve and run workflows" | ❌ (requires write) |

**Defenses, in priority order:**
1. Leave `roles:` at its default `[admin, maintainer, write]`
2. Minimize `permissions:` to the smallest set the agent needs
3. Minimize `safe-outputs:` to only the mutations the workflow needs
4. For PR-touching workflows: never check out the PR head SHA in a job that has secrets
5. Add an explicit fork guard: `if: github.event.pull_request.head.repo.fork == false`
6. Configure `min-integrity` to control what content the agent can see

## Trigger Selection Guide

Choose the right trigger for your workflow. Triggers are grouped by recommended usage level.

### ✅ Recommended

| Trigger | Best for | Key advantage |
|---------|----------|---------------|
| `workflow_dispatch` | Manual escape hatch, debugging, ad-hoc runs | Write+ required; auto-paired with most triggers. ⚠️ Branch selection is user-controlled — a write user can dispatch against a stale branch with weaker `permissions:`, different `safe-outputs:`, or a friendlier prompt |
| `schedule` | Periodic housekeeping, polling-based PR operations | Best concurrency story; no event spamming; no approval gate |
| `labeled` / `label_command:` | Human-in-the-loop gate via label application | Triage+ required to apply label; one-shot with auto-remove. Set `min-integrity: none` — the label application IS the human gate, replacing integrity filtering |
| `issues` | Community-facing issue workflows | Immediate; `roles: all` acceptable with tight safe-outputs |
| `release` / `milestone` | Post-release/milestone automation | Trusted trigger (write+) |

### ⚠️ Use with Caution

| Trigger | Headline risk |
|---------|--------------|
| `push` | **Always** use explicit `branches:` — bare `on: push` fires on every branch including bot/dependency/codeflow branches. The trigger most likely to turn a PoC into a billing surprise. Rapid pushes (rebasing, force-pushing) stack runs unless `cancel-in-progress: true` |
| `issue_comment` / `slash_command:` | Broad underlying subscription; concurrency catastrophe; edited-comment time-bomb |
| `pull_request_review` | Fires for ALL review types including COMMENT from any user, not just approvals |
| `discussion` / `discussion_comment` | Most-open untrusted-input surface; no approval gate; lower visibility than issues |

### `pull_request.synchronize` Gotchas

`synchronize` fires once per push to a PR branch (not per commit). Things that do **NOT** fire `synchronize`:

- **Draft → ready-for-review**: Fires `ready_for_review`, not `synchronize`. Workflows using default `types: [opened, synchronize, reopened]` won't re-run CI when a draft is marked ready
- **Base-ref edits**: Changing the PR's base branch fires `edited` (with `changes.base`), not `synchronize`
- **Pushes to the base branch**: Someone merging to `main` while your PR targets `main` does NOT fire `synchronize` on your PR — it fires `push` on `main`. Your CI won't re-run against the new base unless you push to your branch
- **Approval dismissal**: Branch protection's "Dismiss stale approvals on new commits" fires on the same head-SHA-changed event. A force-push that doesn't change file contents (rebase onto current `main`) still invalidates all prior approvals

### ⛔ Avoid

| Trigger | Why |
|---------|-----|
| `pull_request` | Causes "Approve and run" gate for ALL workflows; clicking approves everything including `pull_request_target` with full secrets. Prefer `slash_command:`, `schedule`, or `label_command:` |
| `pull_request_target` | Runs on base ref with full secrets and write token — most exploited vulnerability class. Never check out PR head SHA |
| `workflow_run` | `pull_request_target`'s quieter sibling — launders untrusted fork artifacts into privileged context with no approval gate. Classic pwn: sandboxed `pull_request` workflow uploads artifacts (e.g., `coverage.json`), then `workflow_run` downloads and acts on them with full secrets. Artifact may contain shell-injection or prompt-injection payloads. No UI signal connects the upstream PR to the downstream run. **Treat all downloaded artifacts as untrusted** |

### Design Principles

1. **Deterministic by default.** Use deterministic Actions and reusable workflows; agentic workflows only when the input is unstructured or AI unlocks a capability deterministic code cannot provide.
2. **Limitations ARE the security model.** Don't engineer bypasses (`pull_request_target` for write access, PAT pools to evade bot attribution, `workflow_run` to escape approval gates, `roles: all` to widen the actor pool). When a boundary blocks a legitimate goal, escalate to platform owners.
3. **Limit the agent job to agent-suitable work.** Keep filtering/skipping in pre-agent steps. Execute deterministic scripts before and after the agent job.
4. **Apply least privilege on every dimension.** Minimum `permissions:`, `safe-outputs:`, `network.allowed:`, secrets, `tools:`. The agent sandbox makes untrusted input safe to process _inside_ the agent; the same operation in pre/post-agent steps runs on the runner host with full secret access.
5. **Mind the signal-to-noise ratio.** Convenience triggers compile to broad subscriptions. Every event spawns a workflow run consuming a runner slot. The activation step must be cheap, and the worst-case invocation rate must be estimated and acceptable.
6. **Understand the `GITHUB_TOKEN` recursion boundary.** Actions via `GITHUB_TOKEN` do NOT fire new workflow events (prevents infinite loops). GitHub App tokens and PATs DO fire events. This is by design — it's why `github-token-for-extra-empty-commit:` needs a PAT, and why bot comments via `GITHUB_TOKEN` don't trigger `issue_comment` workflows.

### Frontmatter Features

```yaml
source: "githubnext/agentics/workflows/ci-doctor.md@v1.0.0"  # Track workflow origin
private: true                                                    # Prevent installation via gh aw add
resources:                                                       # Companion files fetched with gh aw add
  - triage-issue.md
  - shared/helper-action.yml
labels: ["automation", "ci"]                                     # For gh aw status --label filtering
checkout: false                                                  # Skip repo checkout (for workflows that only use MCP/API, no source needed)

rate-limit:                  # Throttle slash commands to prevent abuse
  max: 5                     # Max invocations per window
  window: 60                 # Window in seconds

runtimes:                    # Override default runtime versions
  dotnet:
    version: "9.0"
  node:
    version: "22"

imports:                     # APM package dependencies
  - uses: shared/apm.md
    with:
      packages:
        - microsoft/apm-sample-package
```

**`checkout: false`** — Skip the default repository checkout when the workflow doesn't need source code (e.g., ChatOps commands that only call APIs via `web-fetch`). Saves ~10-30s of runner time.

**`rate-limit:`** — Throttle slash command invocations to prevent abuse or accidental spam. The `max` field limits invocations per `window` seconds. Useful for commands that call external APIs or create issues.

**Available tools:** `web-fetch` (fetch URLs), `bash` (shell commands), GitHub MCP toolsets (`pull_requests`, `repos`, `issues`, etc.). Use `tools: [web-fetch]` for workflows that call external APIs.

Supported runtimes: `node`, `python`, `go`, `uv`, `bun`, `deno`, `ruby`, `java`, `dotnet`, `elixir`.

## When to Read the Full Reference

Read `.claude/skills/gh-aw-guide/references/architecture.md` when you need:
- **Execution model** details (step ordering, credential availability, pre-agent-steps/post-steps)
- **Security boundaries** (defense layers, integrity filtering, protected files, rules for authors)
- **Fork PR handling** (platform restore, threat model, trigger-by-trigger behavior)
- **Safe outputs** (complete list of 30+ types, key options for each)
- **Troubleshooting** specific errors
- **Upstream issue history** (all 5 tracked issues and their resolutions)
