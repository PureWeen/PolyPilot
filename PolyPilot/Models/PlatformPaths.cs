namespace PolyPilot.Models;

/// <summary>
/// Platform-specific path resolution for Mac Catalyst sandbox.
/// 
/// On Mac App Store (MACCATALYST sandbox):
///   ~/.polypilot/ → {AppDataDirectory}/.polypilot/
///   ~/.copilot/   → {UserProfile}/.copilot/ (sandbox remaps HOME into container)
/// 
/// On all other platforms: returns null (callers use their existing logic unchanged).
/// </summary>
public static class PlatformPaths
{
    private static string? _testPolyPilotDir;
    private static string? _testCopilotDir;

    /// <summary>Test-only: override returned paths to prevent tests from touching real filesystem.</summary>
    internal static void SetForTesting(string? polyPilotDir, string? copilotDir)
    {
        _testPolyPilotDir = polyPilotDir;
        _testCopilotDir = copilotDir;
    }

    /// <summary>
    /// Get the PolyPilot configuration directory (~/.polypilot/) for the current platform.
    /// On MACCATALYST: returns FileSystem.AppDataDirectory/.polypilot/
    /// On other platforms: returns null (caller uses existing logic).
    /// </summary>
    public static string? GetPolyPilotDirOverride()
    {
        if (_testPolyPilotDir != null) return _testPolyPilotDir;
#if MACCATALYST
        try
        {
            return Path.Combine(FileSystem.AppDataDirectory, ".polypilot");
        }
        catch
        {
            return null;
        }
#else
        return null;
#endif
    }

    /// <summary>
    /// Get the Copilot SDK state directory (~/.copilot/) for the current platform.
    /// On MACCATALYST: the sandbox remaps UserProfile to the container,
    /// so ~/.copilot/ resolves inside the sandbox automatically.
    /// Returns null on all platforms (existing logic is correct everywhere).
    /// </summary>
    public static string? GetCopilotDirOverride()
    {
        if (_testCopilotDir != null) return _testCopilotDir;
        // The sandbox remaps HOME (UserProfile) into the container on Mac Catalyst.
        // ~/.copilot/ already resolves inside the sandbox. No override needed.
        return null;
    }
}


