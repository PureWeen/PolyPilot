---
name: release-notes
description: >
  Generate polished release notes and update the README for PolyPilot releases.
  Use when: (1) A new version tag has been pushed and needs release notes,
  (2) The user asks to prepare release notes for a version,
  (3) The README needs updating with new features or a "What's New" section,
  (4) Preparing a release announcement or changelog entry.
  Covers: commit categorization, highlight extraction, README update strategy,
  GitHub Release body formatting, and the release workflow integration.
---

# Release Notes & README Update Skill

## Overview

PolyPilot releases follow semantic versioning (`v1.0.x`) with tags pushed to `main`.
The GitHub Actions workflow (`.github/workflows/build.yml`) creates releases via
`softprops/action-gh-release@v1` with `generate_release_notes: false` — meaning
the release body must be explicitly provided or updated after creation.

Currently, release notes default to GitHub's auto-generated "What's Changed" PR list,
which is noisy and hard to scan. This skill produces **curated, highlight-driven
release notes** and keeps the README current.

---

## Step 1: Gather Changes Since Last Release

**Start with commits, then ALWAYS read PR bodies for the headline features.**
The commit subject line is never enough — the PR body has the "why", the architecture
decisions, and the user-facing description you need for highlights.

```bash
# Find the previous tag
PREV_TAG=$(git tag --sort=-v:refname | head -2 | tail -1)
CURR_TAG=$(git tag --sort=-v:refname | head -1)

# List commits between tags (overview)
git --no-pager log ${PREV_TAG}..${CURR_TAG} --oneline

# CRITICAL: Read PR bodies for any feat: commits — this is where the good stuff is
gh pr view <PR_NUMBER> --json body -q '.body'

# Batch: get all merged PRs with titles and bodies
gh pr list --state merged --search "merged:>=$(git log -1 --format=%ci $PREV_TAG | cut -d' ' -f1)" --limit 50 --json number,title,labels,body
```

If preparing notes for a release that hasn't been tagged yet, compare against `HEAD`:
```bash
git --no-pager log ${PREV_TAG}..HEAD --oneline
```

**Do not skip reading PR bodies.** A commit that says `feat: mixed-model PR Review Squad`
tells you nothing. The PR body explains that 5 Opus workers each dispatch 3 sub-agents
across Opus/Sonnet/Codex and synthesize 2-of-3 consensus reports. That's the highlight.

---

## Step 2: Categorize Changes

Group commits by type using conventional commit prefixes and PR content:

### Categories (in display order)

| Emoji | Category | Commit Prefixes | Description |
|-------|----------|----------------|-------------|
| 🚀 | **Highlights** | (manually curated) | Top 1-3 most impactful changes — the "headline" |
| ✨ | **New Features** | `feat:` | New capabilities, commands, UI elements |
| 🐛 | **Bug Fixes** | `fix:` | Corrections to existing behavior |
| ⚡ | **Performance** | `perf:`, `fix:` (perf-related) | Speed, memory, render improvements |
| 📱 | **Mobile** | any mobile/bridge-related | Remote mode, bridge, mobile UI |
| 🤖 | **Multi-Agent** | orchestrat*, squad, worker | Orchestration, presets, squad teams |
| 🔧 | **Infrastructure** | `chore:`, `ci:`, `build:` | CI/CD, deps, tooling |
| 📝 | **Documentation** | `docs:` | README, docs/, skill updates |

### Rules for Highlights

Highlights are the **most user-visible, exciting changes**. Pick 1-3 from the full list.
Good highlights:
- New features users will immediately notice (new UI, new command, new integration)
- Major reliability improvements that fix frequent pain points
- New platform support or significant UX redesign

Bad highlights:
- Internal refactors (even big ones)
- Test fixes
- CI/CD changes
- Minor bug fixes

### Writing Style

- **Active voice, present tense**: "Adds Fiesta mode for multi-machine orchestration"
- **User-focused**: Describe what users can do, not what code changed
- **Concise**: One line per item, 10-15 words max
- **Include PR numbers**: `(#123)` at end for traceability
- **Group related PRs**: If 3 PRs fix mobile streaming, combine into one line

---

## Step 3: Write the Release Notes

### Template

```markdown
## 🚀 Highlights

- **[Headline feature]** — one-sentence description of the most exciting change (#PR)
- **[Second highlight]** — another major improvement (#PR)

## ✨ New Features

- Feature description in active voice (#PR)
- Another feature (#PR)

## 🐛 Bug Fixes

- Fix description — what was broken and what works now (#PR)
- Another fix (#PR)

## ⚡ Performance

- What got faster and by how much (#PR)

## 🔧 Infrastructure

- CI/CD, build, or tooling change (#PR)

---

**Full Changelog**: https://github.com/PureWeen/PolyPilot/compare/vPREV...vCURR

**Install / Update:**
- **macOS:** `brew upgrade polypilot` — or download [PolyPilot.zip](https://github.com/PureWeen/PolyPilot/releases/download/vCURR/PolyPilot.zip)
- **Windows:** [PolyPilot-Windows.zip](https://github.com/PureWeen/PolyPilot/releases/download/vCURR/PolyPilot-Windows.zip)
- **Android:** [PolyPilot-Android.apk](https://github.com/PureWeen/PolyPilot/releases/download/vCURR/PolyPilot-Android.apk)
- **Linux:** [.deb](https://github.com/PureWeen/PolyPilot/releases/download/vCURR/polypilot_CURR_amd64.deb) · [.AppImage](https://github.com/PureWeen/PolyPilot/releases/download/vCURR/PolyPilot-CURR-x86_64.AppImage) · [.flatpak](https://github.com/PureWeen/PolyPilot/releases/download/vCURR/com.microsoft.PolyPilot.flatpak)
```

Replace `vCURR`/`vPREV` with actual tag names (e.g., `v1.0.17`) and `CURR` with the
bare version (e.g., `1.0.17`). The asset filenames match what `.github/workflows/build.yml`
uploads (lines 719-727). Actual assets per release: `PolyPilot.zip` (macOS),
`PolyPilot-Windows.zip`, `PolyPilot-Android.apk`, `polypilot_{ver}_amd64.deb`,
`PolyPilot-{ver}-x86_64.AppImage`, `com.microsoft.PolyPilot.flatpak`.

### Example (real v1.0.15 notes, rewritten)

```markdown
## 🚀 Highlights

- **Fiesta Mode** — Discover and orchestrate across multiple PolyPilot instances on your LAN with push-to-pair (#322)
- **Mixed-Model PR Review Squad** — New preset combining Opus, Sonnet, and Codex for comprehensive code review (#451)

## ✨ New Features

- Surface CLI subagent and skill events directly in chat + `/agent` command (#445)
- Add sync button for mobile with diagnostic logging (#438)
- Surface auth errors with actionable guidance messages (#446)

## 🐛 Bug Fixes

- Fix mobile streaming: bypass render throttle, debounce broadcasts, fix stale IsProcessing (#447, #449)
- Fix cleared input fields re-filling with stale draft text on re-render (#435)
- Fix orchestrator over-dispatching workers for single-task prompts (#429)
- Forward full system environment to CLI child process (#439)
- Recover restored session model and expose gpt-5.4 (#448)
- Use `.git/info/exclude` instead of `.gitignore` for worktree exclusions (#434)
- Never abort sessions on resume — removes RESUME-ABORT behavior (#452)

## 🔧 Infrastructure

- Fix structural tests to find call sites instead of method definitions (#453)
- Fix flaky ProcessHelper test catching leaked exceptions (#454)

---

**Full Changelog**: https://github.com/PureWeen/PolyPilot/compare/v1.0.14...v1.0.15
```

---

## Step 4: Publish the Release Notes

### Option A: Update an existing GitHub Release

```bash
# Update the release body for an existing tag
gh release edit v1.0.16 --notes-file /tmp/release-notes.md
```

### Option B: Create a new release (if not auto-created by CI)

```bash
gh release create v1.0.17 \
  --title "v1.0.17" \
  --notes-file /tmp/release-notes.md \
  --draft
```

### Workflow

1. Write the notes to a temp file (never commit temp files)
2. Use `gh release edit` to update the existing release
3. Verify: `gh release view v1.0.16`

---

## Step 5: Update the README

### Philosophy: The README Sells, It Doesn't Document

The README is a **landing page**, not a technical spec. It should make someone excited
to try PolyPilot in 30 seconds of scrolling. Every sentence should answer "what can I do?"
not "how is it implemented?"

**Good README voice:**
- "Launch pre-built agent teams with one click — 5 workers dispatch sub-agents across models and synthesize consensus reviews"
- "Scan a QR code, control your agent fleet from your pocket"
- "Set a goal and let the agent loop: execute → evaluate → refine → repeat"

**Bad README voice (too implementation-focused):**
- "Uses ConcurrentDictionary<string, SessionState> for thread-safe session management"
- "Events from the SDK arrive on background threads and are marshaled via SynchronizationContext.Post"
- "The 3-tier timeout system handles quiescent sessions (30s), active tool execution (10min), and general inactivity (2min)"

### What to Update (and when)

| Trigger | README Section | Action |
|---------|---------------|--------|
| New major feature | Key Features | Add or update a feature block |
| Feature significantly reworked | Key Features | Rewrite the block with new capabilities |
| New platform supported | Supported Platforms | Update the table |
| New install method | Getting Started | Add install instructions |
| New slash command | Slash Commands | Add to the list |
| Test count milestone | Testing | Update the number |

### Structure

The README follows this flow (preserve it):

```
1. Logo + badges + tagline
2. Screenshot
3. "What is PolyPilot?" — one paragraph + quick-scan table
4. "Key Features" — the showcase (biggest section, most visual)
5. "Supported Platforms" — table with install links
6. "Getting Started" — Homebrew + build from source
7. "Self-Building Workflow" — the meta story
8. "Testing" — brief
9. Footer
```

### Adding a New Feature to Key Features

Follow this pattern — short heading, 1-3 sentences max, focus on what the user gets:

```markdown
### 🎉 Feature Name
What it lets you do in one sentence. Maybe a second sentence with a concrete example
or the "wow" detail. Never mention implementation classes or internal architecture.
```

Consolidate related capabilities. Don't add a separate section for every PR — group
"CLI Agent Visibility" and "/agent command" into one "Any Model, Any Task" section
rather than three separate entries.

### What NOT to Put in the README

- Version-specific changelog (goes in GitHub Releases)
- "What's New in v1.0.x" sections (gets stale instantly)
- Internal architecture (ConcurrentDictionary, SynchronizationContext, event marshaling)
- Timeout values, watchdog tiers, or other implementation constants
- Dates or timestamps

---

## Step 6: Verify

After updating:

```bash
# Verify release notes look right
gh release view $CURR_TAG

# Verify README renders correctly (check for broken markdown)
# Look for: unclosed code blocks, broken links, missing images
cat README.md | head -5   # should start with <p align="center">

# Ensure no temp files are left
git status --short
```

---

## Automation Integration

The release workflow in `.github/workflows/build.yml` (line ~717) uses:
```yaml
- name: Create Release
  uses: softprops/action-gh-release@v1
  with:
    generate_release_notes: false
    draft: false
```

To integrate automated release notes, the workflow could be updated to:
1. Add a step before `Create Release` that generates the notes body
2. Pass the body to the `softprops/action-gh-release` action via the `body` or `body_path` input
3. This could use a script that calls `gh api` to get merged PRs and formats them

However, the **recommended approach** is to run this skill manually before or after
tagging a release. Curated notes are always better than fully automated ones.

---

## Quick Reference Commands

```bash
# List recent tags
git tag --sort=-v:refname | head -5

# Commits since last tag
git --no-pager log $(git tag --sort=-v:refname | head -1)..HEAD --oneline

# Merged PRs since a date
gh pr list --state merged --search "merged:>=2026-03-25" --limit 30

# Update release notes
gh release edit v1.0.16 --notes-file /tmp/release-notes.md

# View current release notes  
gh release view v1.0.16

# README preview (if you have grip installed)
# grip README.md
```
