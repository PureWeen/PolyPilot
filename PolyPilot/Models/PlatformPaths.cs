namespace PolyPilot.Models;

/// <summary>
/// Platform-specific path resolution and test isolation for file paths.
/// 
/// On Mac App Store (MACCATALYST sandbox):
///   The sandbox remaps HOME (UserProfile) into ~/Library/Containers/&lt;bundle-id&gt;/Data/,
///   so both ~/.polypilot/ and ~/.copilot/ resolve inside the container automatically.
///   No override is needed — callers use their existing UserProfile-based logic unchanged.
/// 
/// On all other platforms: returns null (callers use their existing logic unchanged).
/// 
/// The primary value of this class is providing a centralized test override via
/// <see cref="SetForTesting"/> so tests never touch the real filesystem.
/// </summary>
public static class PlatformPaths
{
    private static string? _testPolyPilotDir;

    /// <summary>
    /// Test-only: override returned path to prevent tests from touching real filesystem.
    /// Also clears static caches in services that use <c>??=</c> patterns (PluginLoader, ShowImageTool)
    /// so the override takes effect even if the cache was previously populated.
    /// </summary>
    internal static void SetForTesting(string? polyPilotDir)
    {
        _testPolyPilotDir = polyPilotDir;
        // Clear static caches that use ??= so the override takes effect
        Services.PluginLoader.ResetCachedPathForTesting();
        Services.ShowImageTool.ResetCachedPathForTesting();
    }

    /// <summary>
    /// Get the PolyPilot configuration directory (~/.polypilot/) override for the current platform.
    /// Returns a test override if set, otherwise null on all platforms.
    /// On MACCATALYST the sandbox remaps UserProfile into the container, so the
    /// existing UserProfile-based paths resolve correctly without an override.
    /// </summary>
    public static string? GetPolyPilotDirOverride()
    {
        if (_testPolyPilotDir != null) return _testPolyPilotDir;
        // Mac Catalyst sandbox remaps HOME (UserProfile) into the container.
        // ~/.polypilot/ already resolves inside the sandbox. No override needed.
        return null;
    }
}
