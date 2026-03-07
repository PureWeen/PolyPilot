# PR Review Squad — Shared Decisions

These rules apply to every worker on every PR fix task. Deviating from them causes commits landing on the wrong remote or history rewrites that break collaborators.

## Push Safety

1. **NEVER force push** — no `--force`, `--force-with-lease`, or any force variant. Force pushing after a rebase is what caused wrong-remote pushes in the first place.

2. **ALWAYS use `gh pr checkout <N>`** to check out a PR branch — never `git fetch origin pull/<N>/head:pr-<N>`. The `gh` tool sets the branch tracking to the correct remote (fork or origin) automatically. A manually fetched branch has no tracking and `git push` will default to `origin`, silently pushing to the wrong repo.

3. **ALWAYS integrate with `git merge origin/main`** — never `git rebase origin/main`. Merge adds a merge commit (no history rewrite, no force push needed).

4. **ALWAYS verify the push target before pushing**:
   ```bash
   gh pr view <N> --json headRepositoryOwner,headRefName \
     --jq '"Expected: " + .headRepositoryOwner.login + "/" + .headRefName'
   git config branch.$(git branch --show-current).remote
   ```
   These must agree. If they don't, something is wrong — stop and investigate.

## Review Workflow

5. When reviewing only (no fix), use `gh pr diff <N>` — never check out the branch.

6. Consensus filter: include a finding in the final report only if flagged by 2+ of the 5 sub-agent models.

7. Do not comment on style, naming, or formatting. Flag only: bugs, data loss, race conditions, security issues, logic errors.
