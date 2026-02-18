namespace PolyPilot;

/// <summary>
/// Provides build-time information for debugging version mismatches.
/// The BuildTimestamp is set during compilation to help identify stale binaries.
/// </summary>
public static class BuildInfo
{
    /// <summary>
    /// UTC timestamp when this assembly was compiled, or version+commit hash.
    /// Format: "yyyy-MM-dd HH:mm:ss UTC" or "1.0.0+abc1234"
    /// </summary>
    public static string BuildTimestamp { get; } = GetBuildTimestamp();

    /// <summary>
    /// Short identifier derived from build timestamp for quick version checks.
    /// For timestamps: MMdd-HHmm (e.g., "0218-1430" for Feb 18 at 14:30)
    /// For versions: commit hash (e.g., "abc1234")
    /// </summary>
    public static string ShortBuildId => GetShortBuildId();

    private static string GetShortBuildId()
    {
        var ts = BuildTimestamp;
        
        // If version string with commit hash (e.g., "1.0.0+abc1234")
        if (ts.Contains('+'))
        {
            var plusIndex = ts.IndexOf('+');
            return ts.Length > plusIndex + 1 ? ts[(plusIndex + 1)..] : "unknown";
        }
        
        // If standard timestamp format (yyyy-MM-dd HH:mm:ss)
        if (ts.Length >= 16 && ts != "unknown")
        {
            return $"{ts[5..7]}{ts[8..10]}-{ts[11..13]}{ts[14..16]}";
        }
        
        return "unknown";
    }

    private static string GetBuildTimestamp()
    {
        // Use assembly metadata if available, otherwise fall back to file timestamp
        var assembly = typeof(BuildInfo).Assembly;
        
        // Try to get InformationalVersion which may contain git commit info
        var infoVersion = assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .OfType<System.Reflection.AssemblyInformationalVersionAttribute>()
            .FirstOrDefault()?.InformationalVersion;
        
        if (!string.IsNullOrEmpty(infoVersion) && infoVersion.Contains('+'))
        {
            // Return version with commit hash like "1.0.0+abc1234"
            return infoVersion;
        }

        // Fall back to assembly file write time
        try
        {
            var location = assembly.Location;
            if (!string.IsNullOrEmpty(location) && File.Exists(location))
            {
                var writeTime = File.GetLastWriteTimeUtc(location);
                return writeTime.ToString("yyyy-MM-dd HH:mm:ss") + " UTC";
            }
        }
        catch
        {
            // Ignore file access errors
        }

        return "unknown";
    }
}
