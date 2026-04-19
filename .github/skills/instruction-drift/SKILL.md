---
name: instruction-drift
description: >-
  Detect and report staleness in instruction files that mirror upstream
  documentation or cross-repo guidance. Use when checking whether
  .github/instructions/*.md or .github/skills/*/SKILL.md are still in sync
  with their declared upstream sources. Trigger words: "instruction drift",
  "stale instructions", "sync check", "are instructions up to date",
  "check freshness", "upstream changes".
---

# Instruction-Drift Detection

This skill provides tooling to detect when instruction files (`.github/instructions/`, `.github/skills/`) have drifted from their declared upstream sources.

## How It Works

Each instruction or skill file that mirrors upstream content has a companion `.sync.yaml` file that declares:
- **target**: The local file to check
- **secondary_targets**: Additional files in the same skill directory
- **reference_urls**: Upstream documentation pages to compare against
- **tracked_issues**: GitHub issues whose resolution may require instruction updates
- **divergence_sections**: Sections that intentionally differ from upstream (repo-specific customizations)
- **releases_source**: GitHub releases feed for the upstream project

## Running a Staleness Check

```powershell
# Check a specific sync manifest
pwsh .github/skills/instruction-drift/scripts/Check-Staleness.ps1 `
  -SyncManifest .github/instructions/gh-aw-workflows.sync.yaml

# Check all sync manifests in the repo
Get-ChildItem -Recurse -Filter '*.sync.yaml' .github/ |
  ForEach-Object { pwsh .github/skills/instruction-drift/scripts/Check-Staleness.ps1 -SyncManifest $_.FullName }
```

## Sync Manifest Schema

```yaml
# .github/instructions/example.sync.yaml
sync:
  target: "../skills/my-skill/SKILL.md"
  secondary_targets:
    - "../skills/my-skill/references/deep-dive.md"
  reference_urls:
    - https://docs.example.com/guide
    - https://docs.example.com/api
  tracked_issues:
    - url: https://github.com/org/repo/issues/123
      status: open
      note: "Waiting for upstream fix"
  divergence_sections:
    - "Known Limitation: Custom Section"
    - "Repo-Specific Configuration"
  releases_source: https://github.com/org/repo/releases.atom
  last_reviewed: "2025-07-01"
```

## Output

The script produces a structured report:
- **FRESH**: All reference URLs respond 200, no tracked issues changed status, no new releases
- **STALE**: One or more signals indicate the instructions may need updating
- **ERROR**: Could not reach one or more reference URLs

Each signal includes actionable guidance on what to review and update.

## When to Run

- Before any PR that modifies gh-aw workflow files
- Periodically (weekly recommended) to catch upstream documentation changes
- After any gh-aw platform release
- When a tracked upstream issue is closed
