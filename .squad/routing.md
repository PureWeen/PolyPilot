# PR Review Squad — Work Routing

## Fix Process (when told to fix a PR)

> **Critical:** Follow this process exactly. Deviating — especially using rebase or force push — causes commits to land on the wrong remote.

### 1. Check out the PR branch
```bash
gh pr checkout <number>
```
This sets the branch tracking to the correct remote automatically (fork or origin).  
**Never** use `git fetch origin pull/<N>/head:...` — that creates a branch with no tracking.

> **Worktree conflict?** If `gh pr checkout` fails with "already checked out at...", run:
> ```bash
> git worktree list                      # find which worktree has the branch
> git worktree remove <path>             # remove stale worktree if safe, OR
> gh pr checkout <number> -b pr-<number>-fix  # use a unique local branch name
> ```

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
Discover and run the repo's test suite. Look for test projects, Makefiles, CI scripts, or package.json test scripts. Run them and verify only pre-existing failures remain.

### 5. Commit
```bash
git add <specific-files>   # Never git add -A blindly
git commit -m "fix: <description>

Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

### 6. Push to the correct remote

**Always verify the push target before pushing:**
```bash
# Get expected owner/branch from the PR
gh pr view <N> --json headRepositoryOwner,headRefName \
  --jq '"Expected: " + .headRepositoryOwner.login + "/" + .headRefName'

# Confirm branch tracking matches
git config branch.$(git branch --show-current).remote
```
These must agree. If they don't, stop and investigate before pushing.

Once verified:
```bash
git push
```
`gh pr checkout` sets branch tracking correctly, so bare `git push` lands on the right remote.

**If `git push` fails** (e.g., tracking not set up correctly), push explicitly using the owner's remote.
`gh pr checkout` registers the fork owner's GitHub login as a named remote — use it directly:
```bash
# Discover the owner's remote name
OWNER=$(gh pr view <N> --json headRepositoryOwner --jq '.headRepositoryOwner.login')
BRANCH=$(gh pr view <N> --json headRefName --jq '.headRefName')
git remote -v | grep "$OWNER"   # confirm remote exists

git push "$OWNER" HEAD:"$BRANCH"
```
Alternatively, use `.squad/push-to-pr.sh <N>` which automates the above.

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

**IMPORTANT: Assign each PR to exactly ONE reviewer worker.** Do NOT spread a single PR review across multiple workers. One worker reviews one PR — that worker handles multi-model consensus internally.

If multiple PRs need reviewing, assign one PR per worker (up to the number of available workers).

---

## Why `gh pr checkout` + merge beats manual fetch + rebase

`gh pr checkout` reads PR metadata and configures the branch to track the correct remote (fork or origin). Bare `git fetch pull/<N>/head:...` creates a local branch with no upstream — `git push` then defaults to `origin`, silently pushing to the base repository instead of the author's fork.
