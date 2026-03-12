# PolyPilot Typography Design Guide

This guide defines **when to use each type-scale variable** so every view has a clear, consistent visual hierarchy. It is the single source of truth for font sizing decisions across the app.

## Status

✅ **Fully applied** — All CSS files follow this guide as of March 2026.
- `--type-subhead` has been removed from the codebase entirely (variable definitions and all usages)
- `--type-small` (non-standard) has been removed
- Enforcement tests in `FontSizingEnforcementTests.cs` prevent regression

## Design Principles

1. **3–4 sizes per view** — Any single screen should use at most 3–4 distinct text sizes. More than that creates visual noise and makes nothing feel prioritized.

2. **Clear hierarchy jumps** — Adjacent levels in the hierarchy should differ by at least 2–3px to be perceptible. `--type-body` (17px) and `--type-callout` (16px) are NOT visually distinct enough to convey hierarchy — use them for the same semantic level, distinguished by weight/color instead.

3. **Semantic roles, not pixel values** — Choose a variable based on what the text IS, not how big you want it. A label is always `--type-body`, regardless of which component it's in.

4. **Weight and color create hierarchy too** — Don't reach for a smaller font-size when `font-weight: 400` + muted color achieves the same subordination. Reserve size changes for structural hierarchy (heading vs. body), not emphasis within a level.

5. **Compact views use the same roles, not smaller sizes** — Sidebar items and card views shouldn't invent their own size scale. They use the same variables, just with tighter spacing.

---

## The Type Scale

These are the CSS variables defined in `app.css :root`. Values shown are base (20px root); desktop and mobile breakpoints adjust proportionally.

| Variable | Base | Desktop (≥1024px) | Role |
|---|---|---|---|
| `--type-large-title` | 1.7rem (34px) | — | Page titles, hero numbers |
| `--type-title1` | 1.4rem (28px) | — | Major section groups |
| `--type-title2` | 1.1rem (22px) | — | Section headers |
| `--type-title3` | 1.0rem (20px) | — | Sub-section headers, prominent controls |
| `--type-headline` | 0.85rem (17px) | 0.95rem | Emphasized body text (always use with `font-weight: 600`) |
| `--type-body` | 0.85rem (17px) | 0.95rem | Primary readable content |
| `--type-callout` | 0.8rem (16px) | 0.85rem | Secondary content, form labels |
| ~~`--type-subhead`~~ **(REMOVED)** | 0.75rem (15px) | 0.8rem | Tertiary metadata — **removed, do not use.** Use `--type-callout` for secondary content or `--type-footnote` for metadata instead. |
| `--type-footnote` | 0.65rem (13px) | 0.75rem | Timestamps, auxiliary info |
| `--type-caption1` | 0.6rem (12px) | 0.65rem | Badges, status indicators |
| `--type-caption2` | 0.55rem (11px) | 0.6rem | Absolute minimum readable size |

### Font stacks
| Variable | Usage |
|---|---|
| `--font-base` | All UI text (set on `html, body`) |
| `--font-mono` | Code, paths, hashes, session IDs |

---

## Semantic Role Assignments

Use this table when choosing a variable. **Look up by what the element IS**, not by which component it's in.

### Headings & Titles

| Role | Variable | Notes |
|---|---|---|
| Page title ("Settings", "Dashboard") | `--type-large-title` | One per page only |
| Category group title | `--type-title1` | Groups of related sections |
| Section header (`<h3>`) | `--type-title2` | Within a category |
| Sub-section header | `--type-title3` | Rare; within a section |

### Body Content

| Role | Variable | Notes |
|---|---|---|
| Primary body text | `--type-body` | Chat messages, descriptions, setting values |
| Emphasized body text | `--type-headline` | Same size as body, distinguished by `font-weight: 600` |
| Form labels, input text, button text | `--type-body` | Labels and inputs should match — users read them as one unit |
| Secondary descriptions, help text | `--type-callout` | Subordinate to labels; use lighter color too |

### Metadata & Auxiliary

| Role | Variable | Notes |
|---|---|---|
| Timestamps, model names, status text | `--type-footnote` | Clearly auxiliary; pair with muted color |
| Badges, pill labels, counts | `--type-caption1` | Small but readable |
| Absolute-minimum labels | `--type-caption2` | Use sparingly; only for decorative/non-essential text |

### Navigation

| Role | Variable | Notes |
|---|---|---|
| Sidebar nav items | `--type-body` | Nav labels are primary UI — they need to be readable |
| Active nav indicator text | `--type-body` | Same size, distinguished by color/weight/border |
| Breadcrumbs, secondary nav | `--type-callout` | Subordinate navigation |

### Special Elements

| Role | Variable | Notes |
|---|---|---|
| Decorative icons (mode cards, empty states) | Raw `rem` (e.g. `2rem`) | Beyond the type scale; allowlisted in tests |
| Icons within text (chevrons, toggles) | `em` units (e.g. `0.6em`) | Scale relative to their text context |
| Inline `<code>` in markdown | `0.85em` | Standard CSS convention for inline code |

---

## Per-View Reference

### Settings Page
The Settings page should use exactly **4 sizes**:

| Element | Variable | Current | Correct? |
|---|---|---|---|
| "Settings" page title | `--type-large-title` | ✅ | — |
| Category headers (Connection, UI, etc.) | `--type-title1` | ✅ | — |
| Section headers (Transport Mode, etc.) | `--type-title2` | ✅ | — |
| **Sidebar nav items** | `--type-body` | ❌ `--type-footnote` | **Too small — should be `--type-body`** |
| Setting labels | `--type-body` | ❌ `--type-callout` | **Should match form labels at `--type-body`** |
| Setting descriptions / help text | `--type-callout` | ❌ `--type-footnote` | **Should be `--type-callout`** |
| Form inputs, selects | `--type-body` | ❌ mixed | **Should be `--type-body`** |
| Buttons | `--type-body` | ✅ | — |
| Version/path metadata | `--type-footnote` | ✅ | — |
| Token values, hashes | `--type-footnote` + `--font-mono` | ✅ | — |

**Key fixes needed:**
- Nav items: `--type-footnote` → `--type-body` (nav labels are primary UI, not metadata)
- Setting labels + inputs: standardize on `--type-body`
- Setting descriptions: `--type-footnote` → `--type-callout` (they're help text, not timestamps)

### Dashboard / Main Chat View
The dashboard should use **4 sizes**:

| Element | Variable |
|---|---|
| Session titles | `--type-title2` |
| Chat message text, input textarea | `--type-body` |
| Model/mode indicators, timestamps | `--type-footnote` |
| Status badges, queue counts | `--type-caption1` |

### Sidebar (Session List)
The sidebar should use **3 sizes**:

| Element | Variable |
|---|---|
| Session name | `--type-body` |
| Session metadata (model, time, preview) | `--type-footnote` |
| Badges (unread count, status) | `--type-caption1` |

---

## Anti-Patterns

❌ **Don't use `--type-subhead`** for labels or descriptions. It sits between `--type-callout` and `--type-footnote` and creates ambiguity about whether something is secondary content or metadata.

❌ **Don't use `--type-caption2`** for any text the user needs to read. Reserve it for decorative or supplementary content only.

❌ **Don't use different sizes to distinguish elements at the same hierarchy level.** Use weight (`400` vs `600`) or color (`--text-primary` vs `--text-secondary`) instead.

❌ **Don't use `em` units for text content.** `em` compounds when nested and creates unpredictable sizes. Only use for icons that should scale with their text context.

❌ **Don't add new type-scale variables.** The existing 11 levels are more than sufficient. If you need a new size, you're probably misidentifying the semantic role.

---

## Enforcement

- **`FontSizingEnforcementTests.cs`** — Sweeping tests scan all CSS and Razor files; any `font-size` not using `var(--type-*)` fails unless explicitly allowlisted with a reason.
- **Allowlist** in the test file documents every intentional exception (icons, decorative elements, inline code).
- **This guide** is the reference for code review — if a PR uses `--type-footnote` for a label, point to this guide.

---

## Changelog

| Date | Change |
|---|---|
| 2026-03-11 | Initial version — established semantic role assignments, identified Settings page inconsistencies |
