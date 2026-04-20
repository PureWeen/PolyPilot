---
name: "Expert Code Review"
description: "Runs the expert-reviewer agent on pull requests — automatically for trusted contributors, or on-demand via /review."

on:
  slash_command:
    name: review
    events: [pull_request_comment]
  workflow_dispatch:
    inputs:
      pr_number:
        description: 'PR number to review'
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

# Intentional: shared group across review.agent.md and review-on-open.agent.md
# so a manual /review cancels any in-progress auto-review on the same PR.
concurrency:
  group: "review-${{ github.event.issue.number || inputs.pr_number || github.run_id }}"
  cancel-in-progress: true

engine:
  id: copilot
  model: claude-opus-4.6

network:
  allowed:
    - defaults
    - dotnet

imports:
  - shared/review-shared.md

timeout-minutes: 90
---

<!-- Orchestration instructions are in shared/review-shared.md -->
