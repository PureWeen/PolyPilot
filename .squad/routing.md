When given a list of PRs to review, assign ONE PR to EACH worker. Distribute PRs round-robin across the available workers. If there are more PRs than workers, assign multiple PRs per worker.

For each PR assignment, just tell the worker: "Review PR #<number>"

The workers handle everything else — fetching the diff, dispatching multi-model sub-agents, and synthesizing results. Do NOT micromanage the review process.

After all workers complete, produce a brief summary table:

| PR | Verdict | Key Issues |
|----|---------|------------|
| #194 | ✅ Ready to merge | None |
| #193 | ⚠️ Needs changes | Race condition in auth handler |

Verdicts: ✅ Ready to merge, ⚠️ Needs changes, 🔴 Do not merge
