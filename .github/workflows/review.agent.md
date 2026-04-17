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

engine:
  id: copilot
  model: claude-sonnet-4.6

network:
  allowed:
    - defaults
    - dotnet

imports:
  - shared/review-shared.md

timeout-minutes: 90
---

# Expert Code Review

Review pull request #${{ github.event.pull_request.number || github.event.issue.number || inputs.pr_number }} using the `expert-reviewer` agent defined at `.github/agents/expert-reviewer.agent.md`.

## Instructions

> **🚨 No test messages.** Never call any safe-output tool (`create_pull_request_review_comment`, `submit_pull_request_review`, `add_comment`) with placeholder or test content like "test", "hello", or "test inline comment". Every call posts permanently on the PR. This applies to you and all sub-agents.

1. Fetch the full diff for the pull request.
2. Call the `expert-reviewer` agent. Make sure to call it as subagent (`task` tool, `agent_type: "general-purpose"`, `model: "claude-opus-4.6"`). Pass along the "no test messages" rule explicitly in your sub-agent prompt. We expect 2+ levels of agents to be called.
3. Do **not** post comments or reviews yourself, except for the fallback in step 4 if the subagent posts nothing. The subagent will post its own comments using the available safe-output tools:
   - **Inline review comments** on specific diff lines via `create_pull_request_review_comment`
   - **Design-level concerns** (not tied to a line) via `add_comment`
   - **Final review verdict** (COMMENT or REQUEST_CHANGES) via `submit_pull_request_review`
   - **Never use APPROVE** — the agent must not count as a PR approval. Use COMMENT for clean reviews.
4. If the subagent does not post anything (e.g. no issues found), this is the only exception to step 3: post a brief fallback review using `submit_pull_request_review` with event `COMMENT` (not `APPROVE`). Do not use `add_comment` for this fallback.
5. If the subagent posts inline comments but does **not** submit the final review verdict (e.g. due to a timeout or error), detect this by checking whether `submit_pull_request_review` was called. If not, submit a fallback review with event `COMMENT` summarizing that the review was partial and inline comments were posted.
