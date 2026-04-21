<#
.SYNOPSIS
    Checks whether instruction/skill files are stale relative to their
    declared upstream sources.

.DESCRIPTION
    Reads a .sync.yaml manifest and checks:
    1. Target file(s) exist on disk
    2. Reference URLs are reachable (HTTP 200)
    3. Tracked issues have not changed status vs. manifest expected state
    Reports FRESH, STALE, or ERROR with actionable details.
    Note: releases_source is declared in the manifest schema but not yet
    checked by this script — planned for a future enhancement.

.PARAMETER SyncManifest
    Path to the .sync.yaml file to check.

.PARAMETER Verbose
    Show detailed output for each check.

.EXAMPLE
    pwsh Check-Staleness.ps1 -SyncManifest .github/instructions/gh-aw-workflows.sync.yaml
#>

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$SyncManifest
)

$ErrorActionPreference = 'Stop'

# --- Helpers ---

function Read-Yaml {
    param([string]$Path)
    # Minimal YAML parser for flat sync manifests — handles the subset we use.
    # For full YAML, install powershell-yaml module.
    $content = Get-Content -Path $Path -Raw
    # Return raw content for manual parsing below
    return $content
}

function Test-UrlReachable {
    param([string]$Url)
    try {
        $response = Invoke-WebRequest -Uri $Url -Method Head -TimeoutSec 10 -UseBasicParsing -ErrorAction Stop
        return @{ Url = $Url; Status = $response.StatusCode; Ok = ($response.StatusCode -eq 200) }
    }
    catch {
        return @{ Url = $Url; Status = $_.Exception.Message; Ok = $false }
    }
}

function Get-TrackedIssueStatus {
    param([string]$Url)
    # Extract owner/repo/number from GitHub issue URL
    if ($Url -match 'github\.com/([^/]+)/([^/]+)/issues/(\d+)') {
        $owner = $Matches[1]
        $repo = $Matches[2]
        $number = $Matches[3]
        try {
            $json = gh issue view $number --repo "$owner/$repo" --json state,title --jq '{state: .state, title: .title}' 2>&1
            if ($LASTEXITCODE -eq 0) {
                $data = $json | ConvertFrom-Json
                return @{ Url = $Url; State = $data.state; Title = $data.title; Ok = $true }
            }
        }
        catch {
            Write-Verbose "Failed to check issue $Url`: $_"
        }
    }
    return @{ Url = $Url; State = 'UNKNOWN'; Title = ''; Ok = $false }
}

# --- Main ---

if (-not (Test-Path $SyncManifest)) {
    Write-Error "Sync manifest not found: $SyncManifest"
    exit 1
}

$manifestDir = Split-Path -Parent (Resolve-Path $SyncManifest)
$raw = Get-Content -Path $SyncManifest -Raw

Write-Host "=== Instruction Drift Check ===" -ForegroundColor Cyan
Write-Host "Manifest: $SyncManifest"
Write-Host ""

$signals = @()
$errors = @()

# Check target file exists
if ($raw -match 'target:\s*"([^"]+)"') {
    $targetPath = Join-Path $manifestDir $Matches[1]
    if (Test-Path $targetPath) {
        Write-Host "✅ Target exists: $targetPath" -ForegroundColor Green
    }
    else {
        Write-Host "❌ Target MISSING: $targetPath" -ForegroundColor Red
        $errors += "Target file missing: $targetPath"
    }
}

# Check secondary targets
$secondaryMatches = [regex]::Matches($raw, 'secondary_targets:\s*\n((?:\s*-\s*"[^"]+"\s*\n?)+)')
if ($secondaryMatches.Count -gt 0) {
    $secondaryPaths = [regex]::Matches($secondaryMatches[0].Value, '"([^"]+)"')
    foreach ($m in $secondaryPaths) {
        $secPath = Join-Path $manifestDir $m.Groups[1].Value
        if (Test-Path $secPath) {
            Write-Host "✅ Secondary target exists: $secPath" -ForegroundColor Green
        }
        else {
            Write-Host "❌ Secondary target MISSING: $secPath" -ForegroundColor Red
            $errors += "Secondary target missing: $secPath"
        }
    }
}

# Check reference URLs
$urlMatches = [regex]::Matches($raw, 'reference_urls:\s*\n((?:\s*-\s*https?://[^\s]+\s*\n?)+)')
if ($urlMatches.Count -gt 0) {
    $urls = [regex]::Matches($urlMatches[0].Value, '(https?://[^\s]+)')
    Write-Host "`nChecking reference URLs..." -ForegroundColor Cyan
    foreach ($u in $urls) {
        $result = Test-UrlReachable -Url $u.Groups[1].Value
        if ($result.Ok) {
            Write-Host "  ✅ $($result.Url)" -ForegroundColor Green
        }
        else {
            Write-Host "  ⚠️  $($result.Url) — $($result.Status)" -ForegroundColor Yellow
            $signals += "Reference URL unreachable (may have moved or been removed): $($result.Url)"
        }
    }
}

# Check tracked issues — compare actual state vs. manifest expected state
$issueMatches = [regex]::Matches($raw, 'url:\s*(https://github\.com/[^\s]+)\s*\n\s*status:\s*(\w+)')
if ($issueMatches.Count -gt 0) {
    Write-Host "`nChecking tracked issues..." -ForegroundColor Cyan
    foreach ($im in $issueMatches) {
        $issueUrl = $im.Groups[1].Value
        $expectedStatus = $im.Groups[2].Value.ToUpper()
        $issueResult = Get-TrackedIssueStatus -Url $issueUrl
        if ($issueResult.Ok) {
            $actualState = $issueResult.State.ToUpper()
            $stateIcon = if ($actualState -eq 'CLOSED') { '🔒' } else { '🔓' }
            if ($actualState -eq $expectedStatus) {
                Write-Host "  $stateIcon $($issueResult.Url) — $($issueResult.State) (matches expected)" -ForegroundColor Green
            }
            elseif ($actualState -eq 'CLOSED' -and $expectedStatus -eq 'OPEN') {
                Write-Host "  $stateIcon $($issueResult.Url) — CLOSED (was expected OPEN): $($issueResult.Title)" -ForegroundColor Yellow
                $signals += "Tracked issue just CLOSED — may need instruction update: $($issueResult.Url)"
            }
            elseif ($actualState -eq 'OPEN' -and $expectedStatus -eq 'CLOSED') {
                Write-Host "  $stateIcon $($issueResult.Url) — REOPENED (was expected CLOSED): $($issueResult.Title)" -ForegroundColor Yellow
                $signals += "Tracked issue REOPENED — was recorded as closed: $($issueResult.Url)"
            }
            else {
                Write-Host "  ⚠️  $($issueResult.Url) — $actualState (expected $expectedStatus)" -ForegroundColor Yellow
                $signals += "Tracked issue state mismatch ($actualState vs expected $expectedStatus): $($issueResult.Url)"
            }
        }
        else {
            Write-Host "  ❓ $issueUrl — could not check" -ForegroundColor Yellow
        }
    }
}
# Fallback: issues without status field (legacy format)
# Skip URLs already captured by the primary regex above to avoid truncated matches
$primaryUrls = @()
foreach ($pm in $issueMatches) { $primaryUrls += $pm.Groups[1].Value }
$allIssueUrls = [regex]::Matches($raw, '-\s*url:\s*(https://github\.com/[^\s]+)')
if ($allIssueUrls.Count -gt 0) {
    foreach ($au in $allIssueUrls) {
        $issueUrl = $au.Groups[1].Value
        if ($primaryUrls -contains $issueUrl) { continue }
        $issueResult = Get-TrackedIssueStatus -Url $issueUrl
        if ($issueResult.Ok -and $issueResult.State -eq 'CLOSED') {
            Write-Host "  🔒 $($issueResult.Url) — CLOSED (no expected status declared): $($issueResult.Title)" -ForegroundColor Yellow
            $signals += "Tracked issue CLOSED (no expected status in manifest): $($issueResult.Url)"
        }
    }
}

# Check last_reviewed date
if ($raw -match 'last_reviewed:\s*"(\d{4}-\d{2}-\d{2})"') {
    $lastReviewed = [datetime]::Parse($Matches[1])
    $daysSince = ([datetime]::UtcNow - $lastReviewed).Days
    Write-Host "`nLast reviewed: $($Matches[1]) ($daysSince days ago)" -ForegroundColor Cyan
    if ($daysSince -gt 30) {
        $signals += "Last reviewed $daysSince days ago (threshold: 30 days)"
        Write-Host "  ⚠️  Over 30 days since last review" -ForegroundColor Yellow
    }
    else {
        Write-Host "  ✅ Within 30-day review window" -ForegroundColor Green
    }
}

# Summary
Write-Host "`n=== Summary ===" -ForegroundColor Cyan
if ($errors.Count -gt 0) {
    Write-Host "Status: ERROR" -ForegroundColor Red
    foreach ($e in $errors) { Write-Host "  ❌ $e" -ForegroundColor Red }
    exit 2
}
elseif ($signals.Count -gt 0) {
    Write-Host "Status: STALE" -ForegroundColor Yellow
    foreach ($s in $signals) { Write-Host "  ⚠️  $s" -ForegroundColor Yellow }
    Write-Host "`nAction: Review the signals above and update instructions if needed." -ForegroundColor Yellow
    exit 1
}
else {
    Write-Host "Status: FRESH ✅" -ForegroundColor Green
    Write-Host "All checks passed — instructions appear up to date." -ForegroundColor Green
    exit 0
}
