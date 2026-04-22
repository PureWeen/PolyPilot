<#
.SYNOPSIS
    Scans gh-aw workflow .md files for dangerous trigger patterns and
    missing security gates.

.DESCRIPTION
    Checks each workflow source file (.github/workflows/*.md, excluding
    lock files and shared/) for:
    - pull_request_target without role restrictions
    - workflow_run without branch restrictions
    - push with overly broad branch patterns
    - roles: all on workflows that process PR content
    - slash_command with cancel-in-progress: true (agent-kill risk)
    - Missing allowed-events on submit-pull-request-review
    - steps: that execute workspace scripts after checkout
    - Missing protected-files on create-pull-request / push-to-pull-request-branch

    Outputs findings as a JSON array. Exit 0 = clean, exit 1 = findings.

.PARAMETER WorkflowDir
    Directory containing workflow .md files. Default: .github/workflows

.EXAMPLE
    pwsh Check-WorkflowSecurity.ps1
    pwsh Check-WorkflowSecurity.ps1 -WorkflowDir .github/workflows
#>

[CmdletBinding()]
param(
    [string]$WorkflowDir = ".github/workflows"
)

$ErrorActionPreference = 'Stop'

function Get-WorkflowFiles {
    Get-ChildItem -Path $WorkflowDir -Filter '*.md' -File |
        Where-Object { $_.Name -notmatch '\.lock\.' -and $_.Directory.Name -ne 'shared' }
}

function Test-Workflow {
    param(
        [string]$Path,
        [string]$Content
    )

    $findings = @()
    $name = Split-Path -Leaf $Path

    # Extract YAML frontmatter
    $frontmatter = ""
    if ($Content -match '(?s)^---\s*\n(.*?)\n---') {
        $frontmatter = $Matches[1]
    }

    # --- Trigger checks ---

    # pull_request_target without safety gates
    if ($frontmatter -match 'pull_request_target') {
        # Note: Do NOT check for min-integrity — compiler v0.62.2 emits incomplete
        # guard policy when min-integrity is hardcoded. The runtime determine-automatic-lockdown
        # step handles integrity filtering automatically for pull_request_target events.
        if ($frontmatter -match 'roles:\s*all') {
            $findings += @{
                file     = $name
                severity = "CRITICAL"
                rule     = "pull_request_target-roles-all"
                message  = "pull_request_target with roles: all — any user can trigger a workflow with write permissions and secrets access."
                fix      = "Remove roles: all or restrict to [admin, maintainer, write]"
            }
        }
    }

    # workflow_run without branch restrictions
    if ($frontmatter -match 'workflow_run') {
        if ($frontmatter -notmatch 'branches:') {
            $findings += @{
                file     = $name
                severity = "MODERATE"
                rule     = "workflow_run-no-branches"
                message  = "workflow_run trigger without branch restrictions. Any branch can trigger this privileged workflow."
                fix      = "Add branches: [main] under workflow_run"
            }
        }
    }

    # push with broad patterns
    if ($frontmatter -match 'push:') {
        if ($frontmatter -match 'branches:\s*\[\s*[''"]?\*\*[''"]?\s*\]') {
            $findings += @{
                file     = $name
                severity = "HIGH"
                rule     = "push-wildcard-branches"
                message  = "push trigger with branches: ['**'] — runs on every push to every branch with write permissions."
                fix      = "Narrow to specific branches: [main] or [main, 'release/*']"
            }
        }
    }

    # roles: all on PR-processing workflows
    if ($frontmatter -match 'roles:\s*all' -and $frontmatter -match '(pull_request|issue_comment|slash_command)') {
        $findings += @{
            file     = $name
            severity = "HIGH"
            rule     = "roles-all-pr-processing"
            message  = "roles: all on a workflow that processes PR content. Any user gets bot-level write access to whatever safe-outputs grant."
            fix      = "Restrict to roles: [admin, maintainer, write]"
        }
    }

    # --- Concurrency checks ---

    # slash_command with cancel-in-progress: true
    if ($frontmatter -match 'slash_command' -and $frontmatter -match 'cancel-in-progress:\s*true') {
        $findings += @{
            file     = $name
            severity = "MODERATE"
            rule     = "slash-command-cancel-in-progress"
            message  = "slash_command with cancel-in-progress: true. Non-matching comments can kill in-progress agent runs."
            fix      = "Use cancel-in-progress: false for slash_command workflows"
        }
    }

    # --- Safe output checks ---

    # submit-pull-request-review without allowed-events
    if ($frontmatter -match 'submit-pull-request-review' -and $frontmatter -notmatch 'allowed-events') {
        $findings += @{
            file     = $name
            severity = "HIGH"
            rule     = "review-no-allowed-events"
            message  = "submit-pull-request-review without allowed-events restriction. Agent can APPROVE PRs, bypassing branch protection."
            fix      = "Add allowed-events: [COMMENT] (or [COMMENT, REQUEST_CHANGES] if dismissal is acceptable)"
        }
    }

    # create-pull-request or push-to-pull-request-branch without protected-files
    if ($frontmatter -match '(create-pull-request|push-to-pull-request-branch)' -and $frontmatter -notmatch 'protected-files') {
        $findings += @{
            file     = $name
            severity = "LOW"
            rule     = "code-push-no-protected-files"
            message  = "Code push safe-output without explicit protected-files policy. Default is 'blocked' which is safe but may cause unexpected failures."
            fix      = "Add protected-files: fallback-to-issue (or blocked/allowed explicitly)"
        }
    }

    # --- Execution safety checks ---

    # Check for script execution patterns in steps/pre-agent-steps
    $stepsSection = ""
    if ($frontmatter -match '(?s)((?:pre-agent-)?steps:\s*\n(?:\s+-.*\n?)+)') {
        $stepsSection = $Matches[1]
    }

    if ($stepsSection -match '(run:|pwsh|bash|python|node)\s.*\.(ps1|sh|py|js)') {
        if ($frontmatter -match '(pull_request_target|pull_request|issue_comment|slash_command)') {
            $findings += @{
                file     = $name
                severity = "HIGH"
                rule     = "steps-execute-workspace-scripts"
                message  = "steps: or pre-agent-steps: execute workspace scripts (.ps1/.sh/.py/.js) after PR checkout. Fork PRs can inject malicious scripts that run with GITHUB_TOKEN."
                fix      = "Move script execution to before checkout, or only run scripts from the base branch (use Checkout-GhAwPr.ps1 pattern)"
            }
        }
    }

    return $findings
}

# --- Main ---

Write-Host "🔒 Workflow Security Scanner" -ForegroundColor Cyan
Write-Host "Directory: $WorkflowDir"
Write-Host ""

$files = Get-WorkflowFiles
Write-Host "Found $($files.Count) workflow source files" -ForegroundColor Green

$allFindings = @()

foreach ($file in $files) {
    $content = Get-Content -Path $file.FullName -Raw
    $findings = Test-Workflow -Path $file.FullName -Content $content

    if ($findings.Count -gt 0) {
        Write-Host "  ⚠️  $($file.Name) — $($findings.Count) finding(s)" -ForegroundColor Yellow
        foreach ($f in $findings) {
            $icon = switch ($f.severity) {
                "CRITICAL" { "🔴" }
                "HIGH"     { "🟡" }
                "MODERATE" { "🟠" }
                "LOW"      { "🟢" }
                default    { "⚪" }
            }
            Write-Host "    $icon [$($f.severity)] $($f.rule): $($f.message)" -ForegroundColor Gray
        }
        $allFindings += $findings
    }
    else {
        Write-Host "  ✅ $($file.Name)" -ForegroundColor Green
    }
}

Write-Host "`n=== Summary ===" -ForegroundColor Cyan

if ($allFindings.Count -eq 0) {
    Write-Host "✅ All workflows pass security checks" -ForegroundColor Green
    exit 0
}
else {
    $bySeverity = $allFindings | Group-Object -Property severity
    foreach ($g in $bySeverity) {
        Write-Host "  $($g.Name): $($g.Count)" -ForegroundColor Yellow
    }
    Write-Host "`n$($allFindings.Count) total finding(s)" -ForegroundColor Yellow

    $allFindings | ConvertTo-Json -Depth 5
    exit 1
}
