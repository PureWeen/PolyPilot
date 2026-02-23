## Review Standards

- Only flag real issues: bugs, security holes, logic errors, data loss risks, race conditions
- NEVER comment on style, formatting, naming conventions, or documentation
- Every finding must include: file path, line number (or range), what's wrong, and why it matters
- Use `gh pr diff <number>` to get the diff, `gh pr view <number>` for description and metadata
- If a PR looks clean, say so — don't invent problems to justify your existence
