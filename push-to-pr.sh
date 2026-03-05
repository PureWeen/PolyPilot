#!/usr/bin/env bash
# push-to-pr.sh — Push the current branch to the correct PR remote.
#
# Usage: ./push-to-pr.sh <PR_NUMBER>
#
# This script derives the correct remote and branch name from GitHub PR metadata
# so commits always land on the PR author's fork (not always origin).
# It NEVER uses --force or --force-with-lease.
#
# Why this exists:
#   - `gh pr checkout <N>` correctly sets branch tracking (fork or origin)
#   - Manual `git fetch pull/<N>/head:...` creates a branch with NO tracking
#   - Without correct tracking, `git push` silently defaults to origin,
#     pushing to PureWeen/PolyPilot even when the PR comes from a fork.

set -euo pipefail

if [[ $# -ne 1 ]]; then
    echo "Usage: $0 <PR_NUMBER>" >&2
    exit 1
fi

PR_NUMBER="$1"

# Resolve PR metadata
echo "🔍 Resolving PR #${PR_NUMBER} metadata..."
PR_INFO=$(gh pr view "$PR_NUMBER" --json headRefName,headRepositoryOwner,headRepository \
    --jq '{owner: .headRepositoryOwner.login, branch: .headRefName, repo: .headRepository.name}')

OWNER=$(echo "$PR_INFO" | python3 -c "import sys,json; d=json.load(sys.stdin); print(d['owner'])")
BRANCH=$(echo "$PR_INFO" | python3 -c "import sys,json; d=json.load(sys.stdin); print(d['branch'])")
REPO=$(echo "$PR_INFO" | python3 -c "import sys,json; d=json.load(sys.stdin); print(d['repo'])")

echo "   PR owner:  $OWNER"
echo "   PR branch: $BRANCH"
echo "   PR repo:   $REPO"

# Find the git remote whose URL contains the owner
REMOTE=""
while IFS= read -r line; do
    REMOTE_NAME=$(echo "$line" | awk '{print $1}')
    REMOTE_URL=$(echo "$line" | awk '{print $2}')
    if echo "$REMOTE_URL" | grep -qi "github.com[/:]${OWNER}/"; then
        REMOTE="$REMOTE_NAME"
        break
    fi
done < <(git remote -v | grep '(push)')

if [[ -z "$REMOTE" ]]; then
    echo "❌ ERROR: No git remote found for owner '$OWNER'." >&2
    echo "   Available remotes:" >&2
    git remote -v | grep '(push)' >&2
    echo "" >&2
    echo "   Add the remote first:" >&2
    echo "   git remote add $OWNER https://github.com/$OWNER/$REPO.git" >&2
    exit 1
fi

echo "   Resolved remote: $REMOTE"

# Verify current branch matches the PR branch
CURRENT_BRANCH=$(git branch --show-current)
if [[ "$CURRENT_BRANCH" != "$BRANCH" ]]; then
    echo "" >&2
    echo "⚠️  WARNING: Current branch '$CURRENT_BRANCH' does not match PR branch '$BRANCH'." >&2
    echo "   Did you run: gh pr checkout $PR_NUMBER ?" >&2
    echo "   Refusing to push — checkout the PR branch first." >&2
    exit 1
fi

# Safety check: ensure no unstaged changes
if ! git diff --quiet || ! git diff --cached --quiet; then
    echo "❌ ERROR: You have uncommitted changes. Commit or stash them first." >&2
    exit 1
fi

# Show what will be pushed
echo ""
echo "📦 About to push:"
echo "   $(git log --oneline -3 | head -3)"
echo ""
echo "🎯 Target: $REMOTE/$BRANCH (PR #$PR_NUMBER by $OWNER)"
echo ""

# Push (no --force, no --force-with-lease)
echo "🚀 Pushing..."
git push "$REMOTE" "HEAD:${BRANCH}"

echo ""
echo "✅ Push complete. Verifying..."
git fetch "$REMOTE" "$BRANCH" --quiet

REMOTE_HEAD=$(git log --oneline "${REMOTE}/${BRANCH}" -1 2>/dev/null || echo "UNKNOWN")
LOCAL_HEAD=$(git log --oneline HEAD -1)
echo "   Local HEAD:  $LOCAL_HEAD"
echo "   Remote HEAD: $REMOTE_HEAD"

if [[ "$REMOTE_HEAD" == "$LOCAL_HEAD" ]]; then
    echo "✅ Verified: remote matches local HEAD."
else
    echo "⚠️  Remote HEAD does not match local HEAD — check for concurrent pushes."
    exit 1
fi
