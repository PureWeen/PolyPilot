<#
.SYNOPSIS
    Extracts gh-aw knowledge from the github/gh-aw repo by scanning
    recent commits for high-signal changes to docs, compiler, and
    safe-outputs infrastructure.

.DESCRIPTION
    Tracks a watermark (last checked commit SHA) and on each run:
    1. Fetches commits since the watermark
    2. Filters to high-signal commits (docs, features, safe-outputs,
       triggers, security, compiler, breaking changes)
    3. For each high-signal commit, fetches the diff and extracts
       relevant changes (new safe-output types, new frontmatter fields,
       new anti-patterns, breaking changes)
    4. Scans shared/ workflow configs for real-world safe-output patterns
    5. Outputs a JSON knowledge update report
    6. Updates the watermark

.PARAMETER WatermarkFile
    Path to the watermark file (stores last checked SHA + timestamp).
    Default: .github/skills/gh-aw-guide/.gh-aw-watermark.json

.PARAMETER MaxCommits
    Maximum number of commits to scan per run. Default: 50.

.PARAMETER DryRun
    If set, don't update the watermark file.

.EXAMPLE
    pwsh Scan-GhAwUpdates.ps1
    pwsh Scan-GhAwUpdates.ps1 -MaxCommits 100 -DryRun
#>

[CmdletBinding()]
param(
    [string]$WatermarkFile = ".github/skills/gh-aw-guide/.gh-aw-watermark.json",
    [int]$MaxCommits = 50,
    [switch]$DryRun
)

$ErrorActionPreference = 'Stop'
$Repo = "github/gh-aw"

# High-signal commit patterns — only these are worth extracting
$HighSignalPatterns = @(
    'feat:',
    'BREAKING',
    'safe.output', 'safe-output',
    'trigger', 'slash.command', 'label.command',
    'compiler',
    'security', 'integrity', 'protected.file',
    'merge-pull-request',
    'engine',
    '^docs:.*(?:reference|pattern|guide|troubleshoot)'
)
$HighSignalRegex = ($HighSignalPatterns -join '|')

# File path patterns that indicate knowledge-relevant changes
$KnowledgePaths = @(
    '^docs/',
    '^actions/safe_outputs/',
    '^actions/.*\.cjs$',
    'schema',
    '^\.github/workflows/shared/'
)
$KnowledgePathRegex = ($KnowledgePaths -join '|')

function Get-Watermark {
    if (Test-Path $WatermarkFile) {
        $content = Get-Content $WatermarkFile -Raw | ConvertFrom-Json
        return $content
    }
    return @{
        last_sha      = $null
        last_checked  = $null
        known_features = @()
    }
}

function Save-Watermark {
    param($Watermark)
    if (-not $DryRun) {
        $dir = Split-Path -Parent $WatermarkFile
        if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
        $Watermark | ConvertTo-Json -Depth 5 | Set-Content -Path $WatermarkFile
    }
}

function Get-CommitsSinceWatermark {
    param([string]$SinceShа)

    $args_ = @("api", "repos/$Repo/commits", "--per-page", $MaxCommits)
    if ($SinceShа) {
        # Use since parameter with the commit's date
        $dateJson = gh api "repos/$Repo/commits/$SinceShа" --jq '.commit.committer.date' 2>&1
        if ($LASTEXITCODE -eq 0 -and $dateJson) {
            $args_ += @("--jq", ".[].sha")
            $args_ += @("-f", "since=$dateJson")
        }
    }

    $shas = gh @args_ 2>&1
    if ($LASTEXITCODE -ne 0) {
        Write-Warning "Failed to fetch commits: $shas"
        return @()
    }

    $commitList = $shas -split "`n" | Where-Object { $_ -and $_ -ne $SinceShа }
    return $commitList
}

function Get-CommitInfo {
    param([string]$Sha)
    $json = gh api "repos/$Repo/commits/$Sha" --jq '{
        sha: .sha,
        message: .commit.message,
        date: .commit.committer.date,
        files: [.files[] | {filename, status, additions, deletions}]
    }' 2>&1

    if ($LASTEXITCODE -ne 0) { return $null }
    return $json | ConvertFrom-Json
}

function Test-HighSignal {
    param($CommitMessage)
    return $CommitMessage -match $HighSignalRegex
}

function Test-KnowledgePath {
    param([string]$Path)
    return $Path -match $KnowledgePathRegex
}

function Extract-SafeOutputPatterns {
    <#
    .SYNOPSIS
        Scans shared/ workflow configs for real-world safe-output patterns.
    #>
    Write-Host "📦 Scanning shared workflow configs for safe-output patterns..." -ForegroundColor Cyan

    $sharedFiles = gh api "repos/$Repo/git/trees/main?recursive=1" --jq '
        [.tree[] | select(.path | test("^.github/workflows/shared/.*\\.md$")) | .path]
    ' 2>&1 | ConvertFrom-Json

    $patterns = @()
    $sampleCount = [Math]::Min(20, $sharedFiles.Count)
    $sampled = $sharedFiles | Get-Random -Count $sampleCount

    foreach ($file in $sampled) {
        $content = gh api "repos/$Repo/contents/$file" --jq '.content' 2>&1
        if ($LASTEXITCODE -ne 0) { continue }

        $decoded = [System.Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($content))

        # Extract YAML frontmatter
        if ($decoded -match '(?s)^---\s*\n(.*?)\n---') {
            $frontmatter = $Matches[1]

            # Look for safe-outputs block
            if ($frontmatter -match '(?s)safe-outputs:\s*\n(.+?)(?=\n\S|\z)') {
                $patterns += @{
                    file = $file
                    safe_outputs = $Matches[1].Trim()
                }
            }
        }
    }

    return $patterns
}

function Extract-NewFeatures {
    param($Commit)

    $features = @()
    $message = $Commit.message
    $firstLine = ($message -split "`n")[0]

    # Detect new safe-output types
    if ($message -match 'safe.output|safe-output') {
        $features += @{
            type = "safe-output"
            summary = $firstLine
            sha = $Commit.sha.Substring(0, 8)
        }
    }

    # Detect new trigger types
    if ($message -match 'trigger|slash.command|label.command') {
        $features += @{
            type = "trigger"
            summary = $firstLine
            sha = $Commit.sha.Substring(0, 8)
        }
    }

    # Detect compiler changes
    if ($message -match 'compiler|compile') {
        $features += @{
            type = "compiler"
            summary = $firstLine
            sha = $Commit.sha.Substring(0, 8)
        }
    }

    # Detect security changes
    if ($message -match 'security|integrity|protected.file|XPIA') {
        $features += @{
            type = "security"
            summary = $firstLine
            sha = $Commit.sha.Substring(0, 8)
        }
    }

    # Detect breaking changes
    if ($message -match 'BREAKING|breaking.change') {
        $features += @{
            type = "breaking"
            summary = $firstLine
            sha = $Commit.sha.Substring(0, 8)
        }
    }

    # Detect new engine support
    if ($message -match 'engine|OpenCode|Codex|Claude') {
        $features += @{
            type = "engine"
            summary = $firstLine
            sha = $Commit.sha.Substring(0, 8)
        }
    }

    return $features
}

# --- Main ---

Write-Host "🔍 gh-aw Knowledge Extraction" -ForegroundColor Cyan
Write-Host "Repository: $Repo"
Write-Host ""

$watermark = Get-Watermark
if ($watermark.last_sha) {
    Write-Host "Watermark: $($watermark.last_sha.Substring(0, 8)) ($($watermark.last_checked))" -ForegroundColor Green
} else {
    Write-Host "No watermark — first run, scanning last $MaxCommits commits" -ForegroundColor Yellow
}

# Fetch commits
$commits = Get-CommitsSinceWatermark -SinceShа $watermark.last_sha
Write-Host "Found $($commits.Count) new commits" -ForegroundColor Cyan

if ($commits.Count -eq 0) {
    Write-Host "✅ No new commits since last check" -ForegroundColor Green
    @{ checked_at = (Get-Date -Format 'o'); new_features = @(); changes_detected = $false } | ConvertTo-Json -Depth 5
    exit 0
}

# Filter to high-signal commits
$highSignalCommits = @()
$allFeatures = @()

foreach ($sha in $commits) {
    $info = Get-CommitInfo -Sha $sha
    if (-not $info) { continue }

    $firstLine = ($info.message -split "`n")[0]

    if (Test-HighSignal -CommitMessage $info.message) {
        $hasKnowledgeFiles = $info.files | Where-Object { Test-KnowledgePath -Path $_.filename }

        Write-Host "  ⚡ $($sha.Substring(0, 8)) $firstLine" -ForegroundColor Yellow
        $highSignalCommits += $info

        $features = Extract-NewFeatures -Commit $info
        $allFeatures += $features
    }
}

Write-Host "`n📊 High-signal commits: $($highSignalCommits.Count) / $($commits.Count)" -ForegroundColor Cyan

# Extract safe-output patterns from shared configs
$safeOutputPatterns = Extract-SafeOutputPatterns

# Categorize features
$byType = $allFeatures | Group-Object -Property type

Write-Host "`n=== Knowledge Update Summary ===" -ForegroundColor Cyan
foreach ($group in $byType) {
    Write-Host "  $($group.Name): $($group.Count) changes" -ForegroundColor Yellow
    foreach ($f in $group.Group) {
        Write-Host "    $($f.sha) $($f.summary)" -ForegroundColor Gray
    }
}

# Build report
$report = @{
    checked_at         = (Get-Date -Format 'o')
    watermark_from     = $watermark.last_sha
    watermark_to       = $commits[0]
    commits_scanned    = $commits.Count
    high_signal_count  = $highSignalCommits.Count
    new_features       = $allFeatures
    safe_output_samples = $safeOutputPatterns
    changes_detected   = ($allFeatures.Count -gt 0)
    feature_summary    = @{}
}

foreach ($group in $byType) {
    $report.feature_summary[$group.Name] = @{
        count = $group.Count
        items = $group.Group | ForEach-Object { "$($_.sha) $($_.summary)" }
    }
}

# Update watermark
$watermark.last_sha = $commits[0]
$watermark.last_checked = (Get-Date -Format 'o')
$watermark.known_features += $allFeatures | ForEach-Object { "$($_.type):$($_.sha)" }
Save-Watermark -Watermark $watermark

Write-Host "`n📄 Report:" -ForegroundColor Cyan
$report | ConvertTo-Json -Depth 10
