# Bug Fix: Worktree Sessions Running in Wrong Directory

## Problem

Sessions created for worktrees were not running in the worktree's directory. Instead, they inherited PolyPilot's working directory, causing:

❌ Wrong `.github/copilot-instructions.md` loaded (or none)
❌ Wrong `.github/skills/` loaded (or none)  
❌ Git context showed PolyPilot repo instead of worktree repo
❌ AI had incorrect context about the codebase

## Example

```
Worktree created at: /Users/username/.polypilot/worktrees/dotnet-maui-e446d3b9
Expected working dir:  /Users/username/.polypilot/worktrees/dotnet-maui-e446d3b9
Actual working dir:    /Users/username/Projects/AutoPilot/PolyPilot  ❌
```

## Root Cause

In `Services/CopilotService.cs`, the `CreateClient()` method was setting a **global working directory** for the entire `CopilotClient`:

```csharp
var options = new CopilotClientOptions { Cwd = ProjectDir };  // ❌ Wrong!
```

This global `Cwd` was overriding the per-session `WorkingDirectory` specified in `SessionConfig` when creating individual sessions.

## The Fix

**Removed the global `Cwd` from `CopilotClientOptions`**:

```diff
- var options = new CopilotClientOptions { Cwd = ProjectDir };
+ // Note: Don't set Cwd here - each session sets its own WorkingDirectory in SessionConfig
+ var options = new CopilotClientOptions();
```

## Why This Works

Each session already specifies its working directory in two places:

1. **When creating a session** (`CreateSessionAsync`):
   ```csharp
   var sessionDir = string.IsNullOrWhiteSpace(workingDirectory) ? ProjectDir : workingDirectory;
   var config = new SessionConfig {
       WorkingDirectory = sessionDir,  // ✅ Per-session directory
       // ...
   };
   ```

2. **When resuming a session** (`ResumeSessionAsync`):
   ```csharp
   WorkingDirectory = GetSessionWorkingDirectory(sessionId)
   ```

With the global `Cwd` removed, each session now correctly uses its own `WorkingDirectory` from the `SessionConfig`.

## Impact

### ✅ Fixed
- **Worktree sessions** now run in the worktree directory
- **Worktree-specific instructions** now load correctly
- **Worktree-specific skills** now available to AI
- **Git context** shows correct repository

### ✅ Still Works
- **Regular sessions** (no directory specified) still default to PolyPilot directory
- **PolyPilot development sessions** still load PolyPilot's instructions
- **Remote mode** unaffected (uses WsBridgeClient, not CopilotClient)
- **Demo mode** unaffected (doesn't use CopilotClient)

## Testing

### Test Case 1: Worktree Session
1. Create a worktree session for `dotnet/maui` on branch `Agency`
2. Session should run in `/Users/username/.polypilot/worktrees/dotnet-maui-e446d3b9/`
3. Verify: Ask Copilot "What repository am I in?" → Should say "dotnet/maui"
4. Verify: Check if `.github/copilot-instructions.md` loads (check for MAUI-specific instructions)
5. Verify: Check if `.github/skills/` are available (e.g., maui-ai-debugging)

### Test Case 2: Regular Session
1. Create a regular session (no directory specified)
2. Session should run in `/Users/username/Projects/AutoPilot/PolyPilot/`
3. Verify: Ask Copilot "What project am I working on?" → Should say "PolyPilot"
4. Verify: Relaunch instructions should be injected (PolyPilot-specific)

### Test Case 3: Resume Worktree Session
1. Create worktree session, close PolyPilot
2. Reopen PolyPilot, resume session
3. Verify: Still runs in worktree directory
4. Verify: Context maintained

## Files Changed

- `PolyPilot/Services/CopilotService.cs` (line 406)
  - Removed `Cwd = ProjectDir` from `CopilotClientOptions`
  - Added comment explaining why

## Related Code

Other places where working directory is correctly handled:
- `CreateSessionAsync()` line 878: Default fallback to ProjectDir
- `CreateSessionAsync()` line 904: SessionConfig.WorkingDirectory
- `ResumeSessionAsync()` line 755: Restore from disk
- `GetSessionWorkingDirectory()`: Reads from events.jsonl

## Future Considerations

If we ever need a true "global default" working directory for new clients, it should:
1. Only apply when session doesn't specify WorkingDirectory
2. Be documented clearly that sessions can override it
3. Consider making it configurable in settings

For now, the current behavior (default to ProjectDir if not specified) is correct.

---

**Branch**: `fix-worktree-working-directory`  
**Commit**: 8efdfbd  
**Date**: 2026-02-13
