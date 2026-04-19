---
name: "Expert Code Review (auto)"
description: "Automatically runs the expert-reviewer agent when a PR is opened or marked ready for review."

on:
  pull_request:
    types: [opened, ready_for_review]
  roles: [admin, maintainer, write]

permissions:
  contents: read
  pull-requests: read

concurrency:
  group: "review-${{ github.event.pull_request.number || github.run_id }}"
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
