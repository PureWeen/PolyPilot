# gh-aw Architecture & Security Reference

Deep reference for gh-aw execution model, security boundaries, fork handling, safe outputs, and troubleshooting. Read this file when the quick-start guide's common patterns aren't sufficient.

## Execution Model

```
activation job  (renders prompt from base branch .md via runtime-import)
    ↓              ↳ saves .github/ and .agents/ as artifact for later restore
agent job:
  user steps:       (pre-agent, OUTSIDE firewall, has GITHUB_TOKEN)
    ↓
  platform steps:   (configure git → checkout_pr_branch.cjs → restore .github/ from artifact → install CLI)
    ↓
  pre-agent-steps:  (OPTIONAL, runs after checkout but before agent, OUTSIDE firewall)
    ↓
  agent:            (INSIDE sandboxed container, NO credentials)
    ↓
  post-steps:       (OPTIONAL, runs after agent completes, OUTSIDE firewall)
```

| Context | Has GITHUB_TOKEN | Has gh CLI | Has git creds | Can execute scripts |
|---------|-----------------|-----------|---------------|-------------------|
| `steps:` (user, pre-activation) | ✅ Yes | ✅ Yes | ✅ Yes | ✅ Yes — **be careful** |
| Platform steps | ✅ Yes | ✅ Yes | ✅ Yes | Platform-controlled |
| `pre-agent-steps:` | ✅ Yes | ✅ Yes | ✅ Yes | ✅ Yes — runs after checkout |
| Agent container | ❌ Scrubbed | ❌ Scrubbed | ❌ Scrubbed | ✅ But sandboxed |
| `post-steps:` | ✅ Yes | ✅ Yes | ✅ Yes | ✅ Yes — runs after agent |

**Agent container credential nuance:** `GITHUB_TOKEN` and `gh` CLI credentials are scrubbed inside the agent container. However, `COPILOT_TOKEN` (used for LLM inference) is present in the environment via `--env-all`. Any subprocess (e.g., `dotnet build`, `npm install`) inherits this variable. The AWF network firewall, `redact_secrets.cjs` (post-agent log scrubbing), and the threat detection agent limit the blast radius.

### Step Ordering

User `steps:` run in the **pre-activation job** (before the agent job starts). Within the agent job, the ordering is: platform steps → `pre-agent-steps:` → agent → `post-steps:`.

The platform's `checkout_pr_branch.cjs` runs with `if: (github.event.pull_request) || (github.event.issue.pull_request)` — it is **skipped** for `workflow_dispatch` triggers.

**`pre-agent-steps:`** run after platform checkout and `.github/` restore but before the agent starts. Use these for data preparation that needs the PR branch checked out (e.g., running analysis scripts on PR code). Declared in frontmatter:

```yaml
pre-agent-steps:
  - name: Analyze PR complexity
    run: |
      echo "Files changed: $(gh pr diff $PR_NUMBER --name-only | wc -l)" > complexity.txt
```

**`post-steps:`** run after the agent completes but before safe-outputs. Use these for cleanup, metrics, or post-processing.

### Prompt Rendering

The prompt is built in the **activation job** via `{{#runtime-import .github/workflows/<name>.md}}`. This reads the `.md` file from the **base branch** workspace (before any PR checkout). The rendered prompt is uploaded as an artifact and downloaded by the agent job.

- The agent prompt is always the base branch version — fork PRs cannot alter it
- The prompt references files on disk (e.g., `SKILL.md`) — those files must exist in the agent's workspace

### Fork PR Activation Gate

By default, `gh aw compile` automatically injects a fork guard into the activation job's `if:` condition: `head.repo.id == repository_id`. This blocks fork PRs on `pull_request` events.

To **allow fork PRs**, add `forks: ["*"]` to the `pull_request` trigger in the `.md` frontmatter. The compiler removes the auto-injected guard from the compiled `if:` conditions. This is safe when the workflow uses a checkout-then-restore pattern (checkout + trusted-infra restore) and the agent is sandboxed.

---

## Security Boundaries

### Key Principles (from [GitHub Security Lab](https://securitylab.github.com/resources/github-actions-preventing-pwn-requests/))

1. **Never execute untrusted PR code with elevated credentials.** The classic "pwn-request" attack is `pull_request_target` + checkout PR + run build scripts with `GITHUB_TOKEN`. The attack surface includes build scripts (`make`, `build.ps1`), package manager hooks (`npm postinstall`, MSBuild targets), and test runners.

2. **Treating PR contents as passive data is safe.** Reading, analyzing, or diffing PR code is fine — the danger is *executing* it. gh-aw workflows should read code for evaluation but never build or run it.

3. **`pull_request_target` grants write permissions and secrets access.** This is by design — the workflow YAML comes from the base branch (trusted). But any step that checks out and runs fork code in this context creates a vulnerability.

4. **`pull_request` from forks has no secrets access.** GitHub withholds secrets because the workflow YAML comes from the fork (untrusted). This is the safe default for CI builds on fork PRs.

5. **The `workflow_run` pattern separates privilege from code execution.** Build in an unprivileged `pull_request` job → pass artifacts → process in a privileged `workflow_run` job. This is architecturally what gh-aw does: agent runs read-only, `safe_outputs` job has write permissions.

### gh-aw Defense Layers

| Layer | What it does | What it doesn't do |
|-------|-------------|-------------------|
| **AWF network firewall** | Restricts outbound to allowlisted domains | Doesn't prevent reading env vars inside the container |
| **`redact_secrets.cjs`** | Scrubs known secret values from logs/artifacts post-agent | Doesn't catch encoded/obfuscated values |
| **Threat detection agent** | Reviews agent outputs before safe-outputs publishes them | Can miss novel exfiltration techniques |
| **Safe-outputs permission separation** | Write operations happen in separate job, not the agent | Agent can still request writes via safe-output tools |
| **Integrity filtering** | Filters untrusted GitHub content before agent sees it (DIFC proxy) | Runtime auto-lockdown varies by event type — verify for sensitive workflows |
| **Protected files** | Blocks agent from modifying package manifests, `.github/`, `.githooks/`, `.husky/`, `DESIGN.md`, etc. | Only applies to `create-pull-request` and `push-to-pull-request-branch` |
| **Container image digest pinning** (v0.70.0+) | Lock files pin built-in container images by digest for reproducible, tamper-resistant execution | Only covers images managed by `gh aw compile` — custom containers are not auto-pinned |
| **`max: N` on safe outputs** | Limits number of operations per type | That output could still contain sensitive data (mitigated by redaction) |
| **XPIA prompt** | Instructs LLM to resist prompt injection from untrusted content (hardened v0.70.0) | LLM compliance is probabilistic, not guaranteed; `disable-xpia-prompt` rejected at compile in strict mode |
| **`pre_activation` role check** | Gates on write-access collaborators | Does not apply if `roles: all` is set |

### Integrity Filtering

Integrity filtering (`tools.github.min-integrity`) controls which GitHub content an agent can access during a workflow run. The MCP gateway filters content by trust level before the agent sees it.

> **⚠️ Known Issue (compiler v0.62.2 + MCP Gateway v0.1.19):** Do NOT set `min-integrity` explicitly in workflow source. The compiler emits an incomplete guard policy (missing `repos` field) that crashes the MCP Gateway at startup with: `"invalid guard policy JSON: allow-only must include repos"`. Instead, **omit `min-integrity`** and rely on the runtime `determine-automatic-lockdown` step, which populates both `min-integrity` and `repos` dynamically based on event type, actor trust level, and repository context. This is the standard gh-aw pattern — most workflows don't set it explicitly.

```yaml
# ✅ CORRECT — omit min-integrity, let runtime handle it
# blocked-users, trusted-users, and approval-labels are still valid without min-integrity
tools:
  github:
    toolsets: [pull_requests, repos]
    blocked-users: ["known-spammer"]
    trusted-users: ["trusted-contributor"]
    approval-labels: ["approved-for-agent"]

# ❌ BROKEN (compiler v0.62.2 + MCP Gateway v0.1.19) — crashes gateway
# tools:
#   github:
#     min-integrity: approved  # Compiler omits required 'repos' field
```

**Integrity hierarchy** (highest to lowest):

| Level | What qualifies |
|-------|---------------|
| `merged` | Merged PRs, commits reachable from default branch |
| `approved` | `OWNER`, `MEMBER`, `COLLABORATOR`; non-fork PRs on public repos; all items in private repos; users in `trusted-users` |
| `unapproved` | `CONTRIBUTOR`, `FIRST_TIME_CONTRIBUTOR` |
| `none` | All content including `FIRST_TIMER` and no-association users |
| `blocked` | Users in `blocked-users` — always denied, cannot be promoted |

**Recommendation:** Omit `min-integrity` and rely on the automatic runtime lockdown (see Known Issue above). The `determine-automatic-lockdown` step applies appropriate integrity levels based on event type and actor trust.

### Protected Files (Auto-Enabled)

When `create-pull-request` or `push-to-pull-request-branch` is configured, protected files are automatically enforced. The agent cannot modify:
- Package manifests (`package.json`, `*.csproj` dependencies, etc.)
- `.github/` directory contents
- Agent instruction files
- `.githooks/`, `.husky/` (hook directories) — added in v0.70.0
- `DESIGN.md` — added in v0.70.0

Configure behavior with `protected-files:` on the safe output:
- `blocked` (default) — PR creation fails if protected files are modified
- `fallback-to-issue` — PR branch is pushed but an issue is created instead for review
  - `fallback-labels: ["needs-review"]` — Optional custom labels on the fallback issue (v0.70.0+)
- `allowed` — Disables protection (use with caution)

### Rules for gh-aw Workflow Authors

- ✅ **DO** treat PR contents as passive data (read, analyze, diff)
- ✅ **DO** run data-gathering scripts in `steps:` (pre-agent, trusted context) not inside the agent
- ✅ **DO** implement a checkout-then-restore step for `workflow_dispatch` to restore trusted `.github/` from base
- ✅ **DO** narrow `slash_command: events:` to the minimum needed (e.g., `[pull_request_comment]`)
- ✅ **DO** use `cancel-in-progress: false` for `slash_command:` workflows to prevent non-matching events from killing in-progress agent runs
- ✅ **DO** prefer `slash_command:` or `schedule` over `pull_request` trigger — `pull_request` causes the "Approve and run" gate that approves ALL workflows with a single click
- ❌ **DO NOT** run `dotnet build`, `npm install`, or any build command on untrusted PR code inside the agent — build tool hooks (MSBuild targets, postinstall scripts) can read `COPILOT_TOKEN` from the environment
- ❌ **DO NOT** execute workspace scripts (`.ps1`, `.sh`, `.py`) after checking out a fork PR in `steps:` — those run with `GITHUB_TOKEN`
- ❌ **DO NOT** set `roles: all` on workflows that process PR content — the agent's `permissions:` and `safe-outputs:` determine what actions are taken, NOT the actor's role. Setting `roles: all` gives any read-only user bot-level write access to anything the workflow grants.

### Authorization Model (`on.roles:`)

**What the agent can do is determined by the workflow's `permissions:` and `safe-outputs:` — NOT by the actor who fired it.** When a workflow accepts a read-only contributor as the trigger (via `roles: all`), that contributor effectively gets bot-level write access to anything the workflow grants.

`on.roles:` defaults to `[admin, maintainer, write]`. This deny-by-default gate prevents read-only users from inducing the bot to act with elevated permissions. Available roles: `admin`, `maintainer`/`maintain`, `write`, `triage`, `read`, `all`.

**Key interactions:**
- A `read` user can fire any `slash_command:`, `issues`, `issue_comment`, or `discussion` trigger — they just can't pass the default `roles:` check
- A `read` user **cannot** fire `label_command:` (requires `triage` to apply label) or `workflow_dispatch` (requires `write`)
- `triage` users can apply labels but are excluded from the default `roles:` allowlist — `label_command:` workflows need `roles: [admin, maintainer, write, triage]` to work for triagers

### Concurrency Race Conditions

With `cancel-in-progress: true` on `slash_command:` workflows, a **non-matching event** (e.g., an ordinary comment) in the same concurrency group can cancel an **in-progress matching run** (the actual `/command`). This happens because `slash_command:` compiles to broad `issue_comment` event subscriptions — every comment triggers the pre-activation job, which runs in the same concurrency group.

**Fix:** Always use `cancel-in-progress: false` for `slash_command:` workflows. Redundant runs from rapid re-invocation are preferable to killed agents.

### The "Approve and Run" Gate

The `pull_request` trigger causes an "Approve and run workflows" button for first-time fork contributors. **This gate is dangerous, not protective:**

1. **Alert fatigue** — After clicking through dozens of legitimate first-time PRs, the click becomes muscle memory
2. **No per-workflow granularity** — A single click approves ALL gated workflows, including any `pull_request_target` workflows with full secrets
3. **No diff preview** — The UI shows no preview of what will execute or which secrets are exposed

**Design rule**: Assume the approval gate will always be clicked. Prefer `slash_command:` or `schedule` over `pull_request` when possible.

---

## Fork PR Handling

### The "pwn-request" Threat Model

The classic attack requires **checkout + execution** of fork code with elevated credentials. Checkout alone is not dangerous — the vulnerability is executing workspace scripts with `GITHUB_TOKEN`.

Reference: https://securitylab.github.com/resources/github-actions-preventing-pwn-requests/

### Platform `.github/` Restore (gh-aw#23769 — Resolved)

The platform now **automatically preserves `.github/` and `.agents/` from the base branch**. The activation job saves these directories as an artifact, and after `checkout_pr_branch.cjs` checks out the PR branch, the platform restores them from the artifact. Additionally, `.mcp.json` is deleted from the workspace to prevent injection. This means fork PRs can no longer overwrite agent infrastructure (skills, instructions, copilot-instructions) by including modified copies in their branch.

**What #23769 fixed (and what it didn't):**

| Before #23769 | After #23769 |
|---------------|-------------|
| Fork PRs inject modified `.github/skills/`, `.github/instructions/`, `.agents/` | ✅ Platform restores these from base branch artifact |
| Fork PRs inject `.mcp.json` to add malicious MCP servers | ✅ Platform deletes `.mcp.json` after checkout |
| User `steps:` run BEFORE `checkout_pr_branch.cjs` — restore was overwritten | ✅ Platform restore happens AFTER checkout |
| `pre-agent-steps:` not available | ✅ `pre-agent-steps:` run after restore, before agent |

**Remaining risks (not fixed by #23769):**
- `steps:` and `pre-agent-steps:` that execute workspace code after checkout still run with `GITHUB_TOKEN` — if they run fork PR scripts, it's a pwn-request
- The agent container has `COPILOT_TOKEN` in the environment — build commands (`dotnet build`, `npm install`) executed by the agent on fork PR code can read it via build hooks
- `workflow_dispatch` skips `checkout_pr_branch.cjs` entirely — implement a checkout-then-restore step for defense-in-depth
- **Multi-repo `push_to_pull_request_branch`** (fixed v0.70.0): Previously, git operations were scoped to the wrong working directory in multi-repo checkout patterns. This is now fixed — side-repo push targets the correct directory automatically. Recompile affected workflows.

### Dangerous Triggers Checklist

Use this checklist when reviewing any workflow that uses high-risk triggers. The platform's #23769 restore makes these **safer** but not **safe** — the remaining risks require workflow-author discipline.

#### `pull_request_target`
- ⚠️ Grants **write permissions and secrets access** even for fork PRs
- ✅ `.github/` is now restored from base branch (gh-aw#23769)
- ❌ `steps:` and `pre-agent-steps:` still run fork code with `GITHUB_TOKEN`
- **Checks:**
  - [ ] No `steps:` or `pre-agent-steps:` execute workspace scripts after checkout
  - [ ] No build commands in the agent prompt (agent has `COPILOT_TOKEN`)
  - [ ] `roles:` is NOT `all` (gate on write-access minimum)
  - [ ] Do NOT set `min-integrity` explicitly (compiler bug — crashes MCP Gateway). Rely on automatic runtime lockdown.
  - [ ] `protected-files:` is set if `create-pull-request` or `push-to-pull-request-branch` is used

#### `workflow_run`
- ⚠️ Inherits secrets access, runs after another workflow completes
- ✅ Separates privilege from code execution (the pattern gh-aw uses internally)
- ❌ Artifacts from the first run could contain executable payloads
- **Checks:**
  - [ ] `branches:` is restricted (not open to all branches)
  - [ ] Artifact contents are treated as untrusted data (never `eval`, `source`, or execute)
  - [ ] The triggering workflow is pinned (not modifiable by fork PRs)

#### `push` with broad branch patterns
- ⚠️ Runs with write permissions on every push
- **Checks:**
  - [ ] `branches:` is narrowed to specific branches (e.g., `[main]`), never `['**']`
  - [ ] No `roles: all` — meaningless on push but indicates careless authoring

#### `issue_comment` / `slash_command` on fork PRs
- ✅ Platform restores `.github/` from base branch (gh-aw#23769)
- ✅ Only write-access users can trigger (default `roles:`)
- ⚠️ The PR code is checked out — agent reads it as passive data (safe), but `steps:` must not execute it
- **Checks:**
  - [ ] `events:` is narrowed (e.g., `[pull_request_comment]` not all events)
  - [ ] `cancel-in-progress: false` (prevent non-matching comments from killing agent)
  - [ ] No workspace script execution in `steps:` after checkout

### Fork PR Behavior by Trigger

| Trigger | `checkout_pr_branch.cjs` runs? | Fork handling |
|---------|-------------------------------|---------------|
| `pull_request` (default) | ✅ Yes | Blocked by auto-generated activation gate unless `forks: ["*"]` is set |
| `pull_request` + `forks: ["*"]` | ✅ Yes | ✅ Works — platform restores `.github/` from base branch artifact after checkout |
| `workflow_dispatch` | ❌ Skipped | ✅ Works — user steps handle checkout and restore is final |
| `issue_comment` (same-repo) | ✅ Yes | ✅ Works — files already on PR branch |
| `issue_comment` (fork) | ✅ Yes | ✅ Works — platform restores `.github/` from base branch artifact after checkout |
| `slash_command` | ✅ Yes (compiles to `issue_comment` internally) | Same behavior as `issue_comment` above, but with platform-managed command matching, emoji reactions, and sanitized input. Prefer `slash_command:` over manual `issue_comment` + `startsWith()`. |

### Safe Pattern: Checkout + Restore

Implement a checkout-then-restore step for `workflow_dispatch` workflows that evaluate PR branches. This pattern verifies write access, checks out the PR, and restores trusted agent infrastructure from the base branch:

```yaml
steps:
  - name: Checkout PR and restore agent infrastructure
    env:
      GH_TOKEN: ${{ github.token }}
      PR_NUMBER: ${{ github.event.pull_request.number || inputs.pr_number }}
    run: |
      # 1. Verify PR author has write access, reject forks
      AUTHOR=$(gh pr view "$PR_NUMBER" --json author --jq '.author.login')
      PERM=$(gh api "repos/$GITHUB_REPOSITORY/collaborators/$AUTHOR/permission" --jq '.permission')
      if [[ "$PERM" != "admin" && "$PERM" != "write" && "$PERM" != "maintain" ]]; then
        echo "::error::PR author $AUTHOR has $PERM access — requires write+"
        exit 1
      fi
      # 2. Capture base branch SHA before checkout
      BASE_SHA=$(gh pr view "$PR_NUMBER" --json baseRefOid --jq '.baseRefOid')
      # 3. Check out the PR branch
      gh pr checkout "$PR_NUMBER"
      # 4. Restore trusted agent infrastructure from base branch
      git checkout "$BASE_SHA" -- .github/ .agents/ 2>/dev/null || true
```

**Behavior by trigger:**
- **`workflow_dispatch`**: Platform checkout is skipped, so the restore step IS the final workspace state (trusted files from base branch)
- **`slash_command`** (same-repo): Platform's `checkout_pr_branch.cjs` handles checkout. Agent infrastructure typically matches main unless the PR modified it.
- **`slash_command`** (fork): Platform restores `.github/` from base branch artifact after checkout — fork cannot inject modified agent infrastructure

**Note:** While the platform now handles `.github/` restore automatically for fork PRs, a checkout-then-restore step still provides defense-in-depth for `workflow_dispatch` triggers (where platform checkout is skipped) and adds the write-access check that the platform doesn't enforce.

### Anti-Patterns

**Do NOT skip checkout for fork PRs:**

```bash
# ❌ ANTI-PATTERN: Makes fork PRs unevaluable
if [ "$HEAD_OWNER" != "$BASE_OWNER" ]; then
  echo "Skipping checkout for fork PR"
  exit 0  # Agent evaluates workflow branch instead of PR
fi
```

Skipping checkout means the agent evaluates the wrong files. The correct approach is: always check out the PR, then restore agent infrastructure from the base branch.

**Do NOT execute workspace code after fork checkout:**

```yaml
# ❌ DANGEROUS: runs fork code with GITHUB_TOKEN
- name: Checkout PR
  run: gh pr checkout "$PR_NUMBER" ...
- name: Run analysis
  run: pwsh .github/scripts/some-script.ps1
```

If you need to run scripts, either:
1. Run them **before** the checkout (from the base branch)
2. Run them **inside the agent container** (sandboxed, no tokens)

---

## Safe Outputs Quick Reference

Safe outputs enforce security through separation: agents run read-only and request actions via structured output, while separate permission-controlled jobs execute those requests.

### Available Safe Output Types

| Category | Types |
|----------|-------|
| **Issues & Discussions** | `create-issue`, `update-issue`, `close-issue`, `link-sub-issue`, `create-discussion`, `update-discussion`, `close-discussion` |
| **Pull Requests** | `create-pull-request`, `update-pull-request`, `close-pull-request`, `create-pull-request-review-comment`, `reply-to-pull-request-review-comment`, `resolve-pull-request-review-thread`, `push-to-pull-request-branch`, `add-reviewer` |
| **Labels & Assignments** | `add-comment`, `hide-comment`, `add-labels`, `remove-labels`, `assign-milestone`, `assign-to-agent`, `assign-to-user`, `unassign-from-user` |
| **Projects & Releases** | `create-project`, `update-project`, `create-project-status-update`, `update-release`, `upload-asset` |
| **Workflow & Security** | `dispatch-workflow`, `call-workflow`, `dispatch_repository`, `create-code-scanning-alert`, `autofix-code-scanning-alert`, `create-agent-session` |
| **System (auto-enabled)** | `noop`, `missing-tool`, `missing-data` |
| **Custom** | `jobs:` (custom post-processing with MCP tool access), `actions:` (GitHub Action wrappers) |

### Key Safe Output Features

**`create-pull-request` notable options:**
- `draft: true` — Enforced as policy (agent cannot override)
- `expires: 14` — Auto-close after 14 days (same-repo only)
- `excluded-files: ["**/*.lock"]` — Strip files from the patch entirely
- `github-token-for-extra-empty-commit:` — Push empty commit with separate token to trigger CI
- `protected-files: fallback-to-issue` — Create issue instead of failing when protected files modified; optionally add `fallback-labels:` to tag that issue (v0.70.0+)
- `base-branch: "vnext"` — Target non-default branch
- `allowed-base-branches: [main, "release/*"]` — Allow agent to override base branch at runtime (glob patterns)
- `auto-close-issue: false` — Don't add `Fixes #N` to PR description
- `preserve-branch-name: true` — Omit random hex suffix from branch name (fails if branch exists)
- `fallback-as-issue: false` — Disable issue fallback when PR creation fails (default: true)
- `team-reviewers: [platform-reviewers]` — Request team reviews
- `max: 3` — Multiple PRs per run (each on its own branch)

**`submit-pull-request-review` notable options:**
- `allowed-events: [COMMENT]` — Blocks APPROVE; non-blocking reviews. **Always use this** unless you need merge-gating.
- `allowed-events: [COMMENT, REQUEST_CHANGES]` — Blocking reviews. Pair with `supersede-older-reviews: true`.
- `supersede-older-reviews: true` — Auto-dismiss older blocking reviews from the same workflow after posting replacement. Solves the stale-review problem without needing `dismiss-pull-request-review` (which doesn't exist as a safe output).
- `footer: "if-body"` — Omit AI footer on approval-only reviews (cleaner)

**`add-comment` notable options:**
- `hide-older-comments: true` — Collapse previous comments from same workflow
- `max: N` — Limit comments per run (default: 1)
- `target: "*"` — Required for `workflow_dispatch` (no triggering PR context)
- `reply_to_id` — Thread replies in discussion threads
- **PR review thread routing** (v0.70.0+): On `pull_request_review_comment` triggers, `add_comment` now replies directly in the review thread instead of posting at the PR level.

---

## Limitations

| What | Behavior | Workaround |
|------|----------|------------|
| Agent-created PRs don't trigger CI | GitHub Actions ignores pushes from `GITHUB_TOKEN` | Use `github-token-for-extra-empty-commit:` with a PAT or GitHub App token on `create-pull-request`. See [Triggering CI](https://github.github.com/gh-aw/reference/triggering-ci/) |
| Stale `REQUEST_CHANGES` reviews | Agent reviews with `REQUEST_CHANGES` block PRs and can't be dismissed (no `dismiss-pull-request-review` safe output) | Use `allowed-events: [COMMENT]` — communicate severity via markers in the review body instead |
| `slash_command:` runs on every comment event | Default subscription listens to all comment-related events, not just the `/command` | Always narrow `events:` (e.g., `events: [pull_request_comment]`). Each skipped run costs ~5-30s of runner time. |
| Non-matching event cancels matching run | With `cancel-in-progress: true`, a non-matching comment in the same concurrency group cancels an in-progress `/command` run | Use `cancel-in-progress: false` for `slash_command:` workflows |
| `pull_request` trigger causes "Approve and run" gate | The button approves ALL gated workflows with a single click — no per-workflow granularity, no diff preview | Prefer `slash_command:` or `schedule` over `pull_request`; assume the gate will always be clicked |
| `--allow-all-tools` in lock.yml | Emitted by `gh aw compile` | Cannot override from `.md` source |
| `gh` CLI inside agent | Credentials scrubbed | Use `steps:` for API calls, or MCP tools |
| `issue_comment` trigger | Requires workflow on default branch | Must merge to `main` before `/slash-commands` work |
| Duplicate runs | gh-aw sometimes creates 2 runs per dispatch | Harmless, use concurrency groups |
| Actions not pinned to SHA (regression v0.68.3–v0.69.x) | `gh aw compile` stopped pinning actions to commit SHA hashes | Fixed in v0.70.0 — recompile any workflows compiled with v0.68.3–v0.69.x |
| `list_commits` on feature branch filters own commits | Own commits incorrectly excluded when listing commits on a feature branch | Fixed in v0.70.0 |
| `allowed-base-branches` compile validation | `gh aw compile` incorrectly reported `safe-outputs.create-pull-request.allowed-base-branches` as unknown field | Fixed in v0.70.0 |
| `update-project` missing permissions | The safe output lacked `issues: read` when using a GitHub App token | Fixed in v0.70.0 — recompile affected workflows |

---

## Upstream References (All Resolved)

These issues are now **all closed** — documented here for historical context:

| Issue | Status | Resolution |
|-------|--------|------------|
| [gh-aw#18481](https://github.com/github/gh-aw/issues/18481) | ✅ Closed | Fork support tracking — umbrella issue, all sub-items shipped |
| [gh-aw#18518](https://github.com/github/gh-aw/issues/18518) | ✅ Closed | `gh aw init` now warns in forks, lists required secrets |
| [gh-aw#18521](https://github.com/github/gh-aw/issues/18521) | ✅ Closed | Fork support docs created — forks are not supported by default; agents will not run on fork PRs unless `forks:` is configured |
| [gh-aw#23769](https://github.com/github/gh-aw/issues/23769) | ✅ Closed | Platform now auto-restores `.github/` and `.agents/` from base branch after checkout; `.mcp.json` deleted to prevent injection |
| [gh-aw#25439](https://github.com/github/gh-aw/issues/25439) | ✅ Closed | `submit-pull-request-review` safe output previously allowed agents to accidentally approve PRs, bypassing branch protection. Resolution: use `allowed-events: [COMMENT, REQUEST_CHANGES]` to block approvals at infrastructure level |

---

## Troubleshooting

| Symptom | Cause | Fix |
|---------|-------|-----|
| Agent evaluates wrong PR | `workflow_dispatch` checks out workflow branch | Add `gh pr checkout` in `steps:` |
| Agent can't find SKILL.md | Fork PR branch doesn't include `.github/skills/` | Platform now restores `.github/` from base branch; ensure workflow uses current compiler version |
| Fork PR skipped on `pull_request` | `forks: ["*"]` not in workflow frontmatter | Add `forks: ["*"]` under `pull_request:` in the `.md` source and recompile |
| `gh` commands fail in agent | Credentials scrubbed inside container | Move to `steps:` section |
| Lock file out of date | Forgot to recompile | Run `gh aw compile` |
| Agent-created PR has no CI checks | `GITHUB_TOKEN` pushes don't trigger Actions | Add `github-token-for-extra-empty-commit:` with a PAT or GitHub App |
| `/slash-command` doesn't trigger | Workflow not on default branch | Merge to `main` first |
| Agent sees stale issue/PR content | Integrity filtering removed it | Check `min-integrity` level; content from `FIRST_TIMER` is filtered at `approved` |
| Protected file error on PR creation | Agent modified `.github/` or package manifests | Set `protected-files: fallback-to-issue` or `allowed` if intentional |
| Stale blocking review after fixes | Agent posted `REQUEST_CHANGES` but can't dismiss it | Switch to `allowed-events: [COMMENT]`; use severity markers in body instead |
| Agent run killed by unrelated comment | `cancel-in-progress: true` + `slash_command:` with broad event subscription | Use `cancel-in-progress: false`; narrow `events:` to reduce concurrency group collisions |
| Hundreds of "skipped" runs per day | `slash_command:` default subscribes to ALL comment events | Narrow `events:` field; accept remaining noise as cost of low-latency invocation |
| `label_command:` denies triage users | `triage` role not in default `on.roles:` allowlist | Add `roles: [admin, maintainer, write, triage]` |
| Forked workflows fire unexpectedly in forks | Workflows copied to fork run on routine events inside the fork | Add job-level guard: `if: github.event.repository.fork == false \|\| github.event_name == 'workflow_dispatch'` |
