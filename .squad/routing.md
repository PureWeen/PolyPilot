When given a list of PRs to review, assign ALL PRs to ALL workers. Each worker reviews every PR through their specialized lens. This creates multi-model consensus — the same PR reviewed by 5 different models with 5 different specializations.

For each PR assignment, include the PR number and instruct the worker to run `gh pr diff <number>` and `gh pr view <number>` to get the full context.

After all workers complete, synthesize a final report per PR:
- Issues found by multiple reviewers (high confidence)
- Issues found by only one reviewer (needs human judgment)
- Overall risk rating (🔴 critical / 🟡 moderate / 🟢 clean)
