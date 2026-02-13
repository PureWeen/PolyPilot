# Investigation Notes

## Problem Confirmed
Session `7c6245ae-fa99-48d2-a6f7-b56e2cd252b8` was created AFTER PR #90 was merged (session: 3:40 AM, PR merged: 3:06 AM), but still loaded PolyPilot's instructions instead of the MAUI worktree's instructions.

## Evidence
1. Session events show correct CWD: `/Users/shneuvil/.polypilot/worktrees/dotnet-maui-c6081824`
2. Worktree HAS its own `.github/copilot-instructions.md` (starts with ".NET MAUI is a cross-platform framework...")
3. Agent reported loading PolyPilot instructions (not MAUI instructions)

## Conclusion
**PR #90 did NOT actually fix the problem.** The SDK is NOT respecting `SessionConfig.WorkingDirectory`.

## Next Steps
Need to investigate why SDK ignores WorkingDirectory in SessionConfig. Possible causes:
1. SDK bug - might be using process CWD instead of session config
2. Resume vs Create difference - maybe only CreateSession respects it, not ResumeSession?
3. Timing issue - instructions loaded before WorkingDirectory is set?

## Current PR Status
The visual CWD display will show the *intended* directory (what we told the SDK), not necessarily what it's *actually* using. This is still useful for debugging - we can see the disconnect between intent and reality.

The persistence fix is still valid and necessary.
