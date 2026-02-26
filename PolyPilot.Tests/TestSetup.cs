using System.Runtime.CompilerServices;
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
/// If you add new file paths to CopilotService, you MUST also clear them
/// in SetBaseDirForTesting() or they will leak to the real filesystem.
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
        RepoManager.SetBaseDirForTesting(TestBaseDir);
    }
}
