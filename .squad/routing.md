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
Discover and run the repo's test suite. Look for test projects, Makefiles, CI scripts, or package.json test scripts. Run them and verify only pre-existing failures remain.

### 5. Commit
```bash
git add <specific-files>   # Never git add -A blindly
git commit -m "fix: <description>

Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

### 6. Push to the correct remote
```bash
git push
```
`gh pr checkout` sets branch tracking correctly, so bare `git push` lands on the right remote.

If `git push` fails, verify the remote first:
```bash
gh pr view <N> --json headRepositoryOwner,headRefName
```
Then push explicitly: `git push <owner-remote> HEAD:<branch>`

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

`gh pr checkout` reads PR metadata and configures the branch to track the correct remote (fork or origin). Bare `git fetch pull/<N>/head:...` creates a local branch with no upstream — `git push` then defaults to `origin`, silently pushing to the base repository instead of the author's fork.
