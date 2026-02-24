using System.Runtime.CompilerServices;
using PolyPilot.Services;

namespace PolyPilot.Tests;

/// <summary>
/// Redirects CopilotService file I/O to a temp directory so tests never
/// clobber the real ~/.polypilot/ files (organization.json, active-sessions.json, etc.).
/// </summary>
internal static class TestSetup
{
    [ModuleInitializer]
    internal static void Initialize()
    {
        var testBaseDir = Path.Combine(Path.GetTempPath(), "polypilot-tests-" + Environment.ProcessId);
        Directory.CreateDirectory(testBaseDir);
        CopilotService.SetBaseDirForTesting(testBaseDir);
    }
}
