#!/usr/bin/env pwsh
# Checkout-GhAwPr.ps1 — Security-checked PR checkout for gh-aw workflow_dispatch.
#
# Verifies the PR author has write access to the repo, checks out the PR branch,
# then restores .github/ from the base branch (main) to prevent prompt injection
# via modified workflow files in the PR.

$ErrorActionPreference = 'Stop'

$prNumber = $env:PR_NUMBER
if (-not $prNumber) {
    Write-Error "PR_NUMBER environment variable is required"
    exit 1
}

Write-Host "Checking out PR #$prNumber..."

# Get PR info
$prJson = gh pr view $prNumber --json headRefName,headRepository,headRepositoryOwner,author,baseRefName --repo $env:GITHUB_REPOSITORY 2>&1
if ($LASTEXITCODE -ne 0) {
    Write-Error "Failed to get PR #$prNumber info: $prJson"
    exit 1
}
$pr = $prJson | ConvertFrom-Json

$branch = $pr.headRefName
$baseBranch = $pr.baseRefName
$author = $pr.author.login

Write-Host "PR #$prNumber by $author, branch: $branch, base: $baseBranch"

# Check author has write access (skip for bots)
if ($author -notmatch '\[bot\]$') {
    $permJson = gh api "repos/$($env:GITHUB_REPOSITORY)/collaborators/$author/permission" --jq '.permission' 2>&1
    if ($LASTEXITCODE -ne 0) {
        Write-Warning "Could not verify author permissions: $permJson"
    } else {
        $perm = $permJson.Trim()
        if ($perm -notin @('admin', 'maintain', 'write')) {
            Write-Error "Author '$author' has '$perm' permission — write access required for workflow_dispatch review"
            exit 1
        }
        Write-Host "Author '$author' has '$perm' access — OK"
    }
}

# Fetch and checkout the PR branch
git fetch origin "pull/$prNumber/head:pr-$prNumber" 2>&1 | Write-Host
git checkout "pr-$prNumber" 2>&1 | Write-Host

# Save the PR HEAD SHA
$prSha = git rev-parse HEAD
Write-Host "PR HEAD: $prSha"

# Restore .github/ from the base branch to prevent workflow tampering
Write-Host "Restoring .github/ from $baseBranch..."
git checkout "origin/$baseBranch" -- .github/ 2>&1 | Write-Host

Write-Host "Checkout complete — PR #$prNumber on branch $branch, .github/ from $baseBranch"
