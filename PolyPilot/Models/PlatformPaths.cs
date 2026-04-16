namespace PolyPilot.Models;

/// <summary>
/// Platform-specific path resolution.
/// 
/// On Mac App Store (MACCATALYST sandbox):
///   ~/.polypilot/ → FileSystem.AppDataDirectory/.polypilot/
/// 
/// On iOS/Android: FileSystem.AppDataDirectory/.polypilot/
/// On Developer ID and other desktop platforms: ~./.polypilot/ (home directory)
/// </summary>
public static class PlatformPaths
{
    /// <summary>
    /// Get the PolyPilot configuration directory.
    /// On sandboxed Mac Catalyst: uses FileSystem.AppDataDirectory
    /// On iOS/Android: uses FileSystem.AppDataDirectory
    /// On other platforms: uses home directory
    /// </summary>
    public static string GetPolyPilotDir()
    {
#if IOS || ANDROID || MACCATALYST
        try
        {
            return Path.Combine(FileSystem.AppDataDirectory, ".polypilot");
        }
        catch
        {
            var fallback = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrEmpty(fallback))
                fallback = Path.GetTempPath();
            return Path.Combine(fallback, ".polypilot");
        }
#else
        try
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (string.IsNullOrEmpty(home))
                home = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrEmpty(home))
                home = Path.GetTempPath();
            return Path.Combine(home, ".polypilot");
        }
        catch
        {
            return Path.Combine(Path.GetTempPath(), ".polypilot");
        }
#endif
    }
}

