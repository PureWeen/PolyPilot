namespace PolyPilot.Models;

/// <summary>
/// Platform-specific path resolution for Mac Catalyst sandbox.
/// 
/// On Mac App Store (MACCATALYST sandbox):
///   ~/.polypilot/ → FileSystem.AppDataDirectory/.polypilot/
/// 
/// On all other platforms (Developer ID, iOS, Android):
///   Uses default path behavior (unchanged)
/// </summary>
public static class PlatformPaths
{
    /// <summary>
    /// Get the PolyPilot configuration directory for sandboxed Mac Catalyst.
    /// On MACCATALYST: uses FileSystem.AppDataDirectory
    /// On other platforms: returns null (caller uses existing logic)
    /// </summary>
    public static string? GetPolyPilotDirForMacCatalyst()
    {
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
}


