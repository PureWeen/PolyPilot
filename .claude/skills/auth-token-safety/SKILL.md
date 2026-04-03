---
name: auth-token-safety
description: >
  Design rationale for PolyPilot's authentication approach. The copilot headless server
  authenticates on its own via its native credential store. PolyPilot NEVER reads the
  macOS Keychain directly — doing so triggers password dialogs and corrupts ACLs.
  Use when modifying auth-related code paths or StartServerAsync callers.
---

# Auth Token Safety

## The Rule

**PolyPilot MUST NEVER read the macOS Keychain.** No `/usr/bin/security` calls, no
`SecKeychainFindGenericPassword`, no keytar. The headless copilot server authenticates
on its own at startup via its native credential store.

## Why

PR #446 added Keychain-reading code (`TryReadCopilotKeychainToken`, `ResolveGitHubTokenForServer`)
to help users whose headless server couldn't self-authenticate. This caused a 4-PR regression
chain (#446 → #456 → #462 → #463) and corrupted Keychain ACLs:

1. Each `/usr/bin/security find-generic-password` call triggers a macOS password dialog
   (PolyPilot isn't in the `copilot-cli` Keychain entry's ACL)
2. Clicking "Allow" rewrites the ACL, breaking the server's own native keytar access
3. The server falls back to its own `/usr/bin/security` calls → more dialogs → spiral
4. `TryReadCopilotKeychainToken` tried 3 service names × 3s timeout = 3 dialogs per call

PR #465 removed all Keychain code. The server self-authenticates fine — proven by months
of use across dozens of worktree switches with different binary paths.

## Why the Server Can Self-Authenticate (Code-Signing Identity)

The critical insight: **macOS Keychain ACLs are keyed by code-signing identity, not binary path.**

- The bundled copilot binary and the Homebrew copilot binary both carry the same GitHub Developer ID certificate (signed by GitHub, not by PolyPilot).
- The Keychain entry `copilot-cli` has an ACL that grants access to anything signed with that GitHub Developer ID.
- The copilot **server** reads the Keychain via keytar (which uses Apple's Security.framework, a native library).
  - Security.framework authenticates the calling process by its code-signing identity.
  - It matches the identity against the ACL and grants access.
- PolyPilot's attempted `/usr/bin/security` calls use Apple's built-in tool, which is **signed by Apple**, not GitHub.
  - Apple's signature ≠ GitHub's signature → ACL mismatch → "Not in ACL" error.
  - PolyPilot then tries to prompt the user to add itself to the ACL.
  - User clicking "Allow" corrupts the ACL by adding PolyPilot's signature instead of GitHub's.

This is why the server's native authentication (via keytar) works reliably: it already has the correct code-signing identity baked in. PolyPilot cannot fix an ACL mismatch by trying to read the Keychain itself.

## What's Allowed

- `ResolveGitHubTokenFromEnv()` — reads COPILOT_GITHUB_TOKEN, GH_TOKEN, GITHUB_TOKEN env vars. Safe, no prompt.
- Forwarding env var tokens to `StartServerAsync` via `_resolvedGitHubToken`. Safe.
- `CheckAuthStatusAsync` — checks if the server is authenticated, shows banner if not. Safe.
- `TryRecoverPersistentServerAsync` — restarts the server (which re-authenticates on its own). Safe.
- Auth banner telling user to run `copilot login`. Correct UX.

## What's Forbidden

- Reading macOS Keychain (any method, any library)
- Spawning `/usr/bin/security` for any reason
- Spawning `gh auth token` (unnecessary — server handles its own auth)
- Any automatic token resolution beyond env vars

## For Users Who Can't Self-Authenticate

Show the auth banner: "Not authenticated — run `copilot login` in a terminal, then click Re-authenticate."
The Re-authenticate button restarts the server, which picks up the fresh credentials from `copilot login`.
