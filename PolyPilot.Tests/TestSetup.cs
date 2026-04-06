using System.Runtime.CompilerServices;
using PolyPilot.Models;
using PolyPilot.Services;

namespace PolyPilot.Tests;

/// <summary>
/// Redirects CopilotService file I/O to a temp directory so tests never
/// clobber the real ~/.polypilot/ files (organization.json, active-sessions.json, etc.).
///
/// ⚠️ CRITICAL: Without this, tests that call CreateGroup, SaveOrganization,
/// SaveActiveSessionsToDisk, etc. will OVERWRITE the user's real data files.
/// This has caused production data loss (squad groups destroyed) multiple times.
///
/// This runs automatically via [ModuleInitializer] before any test executes.
/// If you add new file paths to CopilotService or any service that persists state,
/// you MUST also redirect them in Initialize() or they will leak to the real filesystem.
///
/// Currently isolated: CopilotService BaseDir/CaptureDir, RepoManager, AuditLogService,
/// PromptLibraryService, FiestaService state file, ConnectionSettings settings file.
/// </summary>
internal static class TestSetup
{
    internal static string TestBaseDir { get; private set; } = "";

    [ModuleInitializer]
    internal static void Initialize()
    {
        TestBaseDir = Path.Combine(Path.GetTempPath(), "polypilot-tests-" + Environment.ProcessId);
        Directory.CreateDirectory(TestBaseDir);
        CopilotService.SetBaseDirForTesting(TestBaseDir);
        CopilotService.SetCaptureDirForTesting(Path.Combine(TestBaseDir, "zero-idle-captures"));
        RepoManager.SetBaseDirForTesting(TestBaseDir);
        AuditLogService.SetLogDirForTesting(Path.Combine(TestBaseDir, "audit_logs"));
        PromptLibraryService.SetUserPromptsDirForTesting(Path.Combine(TestBaseDir, "prompts"));
        FiestaService.SetStateFilePathForTesting(Path.Combine(TestBaseDir, "fiesta.json"));
        ConnectionSettings.SetSettingsFilePathForTesting(Path.Combine(TestBaseDir, "settings.json"));
        ScheduledTaskService.SetTasksFilePathForTesting(Path.Combine(TestBaseDir, "scheduled-tasks.json"));
    }
}
