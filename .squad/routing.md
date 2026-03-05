# PR Review Squad — Work Routing

## Fix Process (when told to fix a PR)

> **Critical:** Follow this process exactly. Deviating — especially using rebase or force push — causes commits to land on the wrong remote.

### 1. Check out the PR branch
```bash
gh pr checkout <number>
```
This sets the branch tracking to the correct remote automatically (fork or origin).  
**Never** use `git fetch origin pull/<N>/head:...` — that creates a branch with no tracking.

### 2. Integrate with main (MERGE, not rebase)
```bash
git fetch origin main
git merge origin/main
```
**Never** use `git rebase origin/main`. Merge adds a merge commit; no force push needed.  
If there are conflicts, resolve them, then `git add <files> && git merge --continue`.

### 3. Make the fix
- Use the `edit` tool for file changes, never `sed`
- Make minimal, surgical changes

### 4. Run tests
```bash
cd PolyPilot.Tests && dotnet test
```
Verify only pre-existing failures fail (e.g., `PopupThemeTests`).

### 5. Commit
```bash
git add <specific-files>   # Never git add -A blindly
git commit -m "fix: <description>

Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

### 6. Push to the correct remote
```bash
./push-to-pr.sh <number>
```
This script derives the correct remote from `gh pr view` and pushes without `--force`.

**Or manually:**
```bash
# Determine the correct remote
OWNER=$(gh pr view <N> --json headRepositoryOwner --jq '.headRepositoryOwner.login')
BRANCH=$(gh pr view <N> --json headRefName --jq '.headRefName')
git push $OWNER HEAD:$BRANCH
```

### 7. Verify the push landed
```bash
gh pr view <N> --json commits --jq '.commits[-1].messageHeadline'
```
The last commit headline should match your fix commit message.

### 8. Re-review
Dispatch 5 parallel sub-agent reviews with the updated diff (include previous findings for status tracking).

---

## Review Process (no fix)

Use `gh pr diff <N>` — **never** check out the branch for review-only tasks.

Dispatch 5 parallel reviews:
- claude-opus-4.6
- claude-opus-4.6
- claude-sonnet-4.6
- gemini-3-pro-preview
- gpt-5.3-codex

Synthesize with 2+ model consensus filter.

---

## Why `gh pr checkout` + merge beats manual fetch + rebase

| Approach | Tracking set? | Force push needed? | Risk |
|----------|--------------|-------------------|------|
| `gh pr checkout <N>` + `git merge` | ✅ Yes (correct remote) | ❌ No | Low |
| `git fetch pull/<N>/head:...` + `git rebase` | ❌ No (NONE) | ✅ Yes | Pushes to wrong remote |

`gh pr checkout` reads the PR metadata and configures the branch to track the fork remote when applicable. Bare `git fetch pull/<N>/head:...` creates a detached local branch with no upstream — when you then `git push`, git picks `origin` as the default, silently pushing to PureWeen/PolyPilot instead of the author's fork.
