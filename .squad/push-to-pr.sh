#!/usr/bin/env bash
# push-to-pr.sh — Safe push helper for PR review workers
#
# Usage: .squad/push-to-pr.sh <PR-number>
#
# This script:
#   1. Reads PR metadata to find the correct remote and branch
#   2. Verifies the current branch matches the PR branch
#   3. Pushes to the correct remote (handles forks transparently)
#   4. Verifies the push landed by comparing local and remote HEADs

set -euo pipefail

PR_NUMBER="${1:?Usage: push-to-pr.sh <PR-number>}"

echo "==> Fetching PR #${PR_NUMBER} metadata..."
PR_JSON=$(gh pr view "$PR_NUMBER" --json headRefName,headRepositoryOwner,headRepository)
BRANCH=$(echo "$PR_JSON" | jq -r '.headRefName')
OWNER=$(echo "$PR_JSON" | jq -r '.headRepositoryOwner.login')
REPO=$(echo "$PR_JSON" | jq -r '.headRepository.name')

echo "    PR branch:  ${BRANCH}"
echo "    PR owner:   ${OWNER}"
echo "    PR repo:    ${REPO}"

# Verify current branch matches the PR branch
CURRENT_BRANCH=$(git rev-parse --abbrev-ref HEAD)
if [ "$CURRENT_BRANCH" != "$BRANCH" ]; then
    echo "ERROR: Current branch '${CURRENT_BRANCH}' does not match PR branch '${BRANCH}'"
    echo "       Run: gh pr checkout ${PR_NUMBER}"
    exit 1
fi

# Find the remote that points to owner/repo
# gh pr checkout registers the fork owner's login as the remote name
REMOTE=$(git remote -v | grep "${OWNER}/${REPO}" | head -1 | awk '{print $1}' || true)
if [ -z "$REMOTE" ]; then
    echo "ERROR: No remote found matching ${OWNER}/${REPO}"
    echo "Available remotes:"
    git remote -v
    exit 1
fi

echo "==> Pushing to remote '${REMOTE}' (${OWNER}/${REPO}), branch '${BRANCH}'..."
git push "$REMOTE" HEAD:"$BRANCH"

# Verify push succeeded by comparing SHAs
LOCAL_SHA=$(git rev-parse HEAD)
REMOTE_SHA=$(git ls-remote "$REMOTE" "refs/heads/${BRANCH}" | awk '{print $1}')

if [ "$LOCAL_SHA" = "$REMOTE_SHA" ]; then
    echo "✅ Push verified: ${LOCAL_SHA}"
else
    echo "❌ Push verification failed!"
    echo "   Local:  ${LOCAL_SHA}"
    echo "   Remote: ${REMOTE_SHA}"
    exit 1
fi
