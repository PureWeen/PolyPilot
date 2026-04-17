---
# Shared configuration for expert-review workflows.
#
# Imported by review.agent.md (slash command) and any future
# review-on-open.agent.md (pull request opened). Keeps permissions,
# tools, and safe-outputs in one place.

description: "Shared configuration for expert-review workflows"

permissions:
  contents: read
  pull-requests: read

tools:
  github:
    toolsets: [pull_requests, repos]

safe-outputs:
  create-pull-request-review-comment:
    max: 30
  submit-pull-request-review:
    max: 1
    allowed-events: [COMMENT, REQUEST_CHANGES]
  add-comment:
    max: 5
---

<!-- Body provided by shared/review-shared.md -->
