# Recommendation: Hybrid Architecture (Option C)

I recommend adopting **Option C (Hybrid)** as the architectural target, implemented in two phases.

## Phase 1 (Immediate PR): "Team Context"
Implement **Option A** behavior using the **Option C** data model.
*   **Mechanism:** When a user assigns a Repository/Worktree to a `SessionGroup`, propagate that `WorktreeId` to the `SessionMeta` of **every agent** in that group.
*   **Result:** All agents share the same directory and branch.
*   **User Experience:** "I assign this team to feature-branch-x."

## Phase 2 (Future): "Agent Independence"
Expose the existing per-agent `WorktreeId` in the UI for advanced scenarios.
*   **Mechanism:** Allow power users to override the `WorktreeId` for specific agents (e.g., "Reviewer Agent" checks out `main` while "Coder Agent" is on `feature-branch`).
*   **User Experience:** "I want this specific agent to look at a different version of the code."

## Reasoning & Tradeoffs

1.  **Future-Proofing (Why not A):** `SessionMeta` already has `WorktreeId`. Hardcoding a single `WorktreeId` on `SessionGroup` would restrict us later. By using the per-session field (even if they all point to the same ID initially), we keep the architecture flexible for free.
2.  **Complexity Management (Why not B):** Forcing per-agent worktrees now creates massive complexity (merging, disk space, synchronization). Shared worktrees are sufficient for 90% of current use cases (collaborative coding, pair programming).
3.  **Correct Abstraction:** A "Team" usually works on a "Project" (Repo/Branch). It is the natural default. Divergence is an exception.

## Implementation Plan

1.  **Update `CreateMultiAgentGroupAsync`:**
    *   Accept an optional `repoId` and `worktreeId`.
    *   If provided, assign `WorktreeId` to the `SessionMeta` of the Orchestrator and all Workers.
    *   Ensure the `SessionGroup` also stores the `RepoId` for context.

2.  **Update `RepoManager.LinkSessionToWorktree`:**
    *   Ensure it can handle multiple sessions linking to the same worktree (currently it has a single `SessionName` field, which might be a limitation if strict 1:1 mapping is enforced). **Crucial Check:** `WorktreeInfo.SessionName` is a single string. This needs to change to support multiple sessions (or be ignored for multi-agent groups).

## Interaction with Reflection/Orchestration

*   **Orchestrator Mode:** The Orchestrator agent typically plans and delegates. Sharing a worktree means the Orchestrator sees the *exact state* the workers are producing in real-time. This is generally beneficial for immediate feedback loops.
*   **OrchestratorReflect Mode:** In this mode, the system might benefit from an "isolation sandbox" where a worker tries a change in a separate worktree, runs tests, and only merges if successful. This is a strong argument for **Option C (Hybrid)** in the long term. A shared worktree (Option A) risks breaking the build for the whole team during experimental changes.
*   **Recommendation:** Start with shared worktrees for simplicity. For advanced reflection cycles that require safe experimentation, leverage the **Option C** capability later to spawn ephemeral worktrees for specific worker tasks.

## Critical Code Change Required
`WorktreeInfo` currently has `public string? SessionName { get; set; }`.
*   **Issue:** This implies 1 worktree = 1 session.
*   **Fix:** For Phase 1, treat `SessionName` as the "Primary/Owner" session (e.g., the Orchestrator). The UI should rely on `SessionMeta.WorktreeId` to find *all* sessions associated with a worktree, rather than relying on the back-pointer in `WorktreeInfo`.
