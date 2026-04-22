---
name: "Review & Fix"
description: "Reviews a PR with 3-model adversarial consensus, applies fixes, re-reviews, and iterates until clean or max rounds reached. Triggered via /fix slash command."

on:
  slash_command:
    name: fix
    events: [pull_request_comment]
  workflow_dispatch:
    inputs:
      pr_number:
        description: 'PR number to review and fix'
        required: true
        type: number
  roles: [admin, maintainer, write]

# slash_command compiles to issue_comment; workflow_dispatch is always allowed.
if: >-
  github.event_name == 'issue_comment' ||
  github.event_name == 'workflow_dispatch'

permissions:
  contents: read
  pull-requests: read

# Intentional: shared prefix with review workflows — /fix cancels in-progress /review.
concurrency:
  group: "review-${{ github.event.issue.number || inputs.pr_number || github.run_id }}"
  cancel-in-progress: false

engine:
  id: copilot
  model: claude-opus-4.6

network:
  allowed:
    - defaults
    - dotnet

# workflow_dispatch skips platform checkout — handle it in steps.
steps:
  - name: Checkout PR branch (workflow_dispatch only)
    if: github.event_name == 'workflow_dispatch'
    env:
      GH_TOKEN: ${{ github.token }}
      PR_NUMBER: ${{ inputs.pr_number }}
    run: |
      echo "::group::Checkout PR #$PR_NUMBER"
      gh pr checkout "$PR_NUMBER" --recurse-submodules
      echo "::endgroup::"

imports:
  - shared/fix-shared.md

timeout-minutes: 120
---

<!-- Orchestration instructions are in shared/fix-shared.md -->
