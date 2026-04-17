---
name: "Expert Code Review"
description: "Runs the expert-reviewer agent on pull requests — automatically for trusted contributors, or on-demand via /review."

on:
  pull_request:
    types: [opened, ready_for_review, labeled]
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
  # Auto-triggers: PR opened, draft→ready, or "review" label added.
  # On-demand: /review comment or workflow_dispatch. Does NOT trigger on every push.

# slash_command compiles to issue_comment; workflow_dispatch is always allowed.
if: >-
  github.event_name == 'issue_comment' ||
  github.event_name == 'workflow_dispatch' ||
  github.event_name == 'pull_request'

engine:
  id: copilot
  model: claude-sonnet-4.6

imports:
  - shared/review-shared.md

timeout-minutes: 90
---

# Expert Code Review

Review pull request #${{ github.event.pull_request.number || github.event.issue.number }} using the `expert-reviewer` agent defined at `.github/agents/expert-reviewer.agent.md`.

## Instructions

1. Fetch the full diff for the pull request.
1b. **Label guard**: If this workflow was triggered by a `labeled` event and the label name is not `review` (check `${{ github.event.label.name }}`), exit immediately without running the review. This prevents unrelated labels from triggering the pipeline.
2. Call the `expert-reviewer` agent. Make sure to call it as subagent (`task` tool, `agent_type: "general-purpose"`, `model: "claude-opus-4.6"`). And make sure to follow the guidance on subagent calls from within the `expert-reviewer` agent. We expect 2+ levels of agents to be called.
3. Do **not** post comments or reviews yourself, except for the fallback in step 4 if the subagent posts nothing. The subagent will post its own comments using the available safe-output tools:
   - **Inline review comments** on specific diff lines via `create_pull_request_review_comment`
   - **Design-level concerns** (not tied to a line) via `add_comment`
   - **Final review verdict** (COMMENT or REQUEST_CHANGES) via `submit_pull_request_review`
   - **Never use APPROVE** — the agent must not count as a PR approval. Use COMMENT for clean reviews.
4. If the subagent does not post anything (e.g. no issues found), this is the only exception to step 3: post a brief fallback review using `submit_pull_request_review` with event `COMMENT` (not `APPROVE`). Do not use `add_comment` for this fallback.
5. If the subagent posts inline comments but does **not** submit the final review verdict (e.g. due to a timeout or error), detect this by checking whether `submit_pull_request_review` was called. If not, submit a fallback review with event `COMMENT` summarizing that the review was partial and inline comments were posted.
6. If this workflow was triggered by the `review` label being added (check `${{ github.event.action }}` == `labeled` and `${{ github.event.label.name }}` == `review`), remove the `review` label after the review is complete using `remove_label`. This makes the label re-addable for future reviews.
