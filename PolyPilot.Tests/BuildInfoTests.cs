using PolyPilot;

namespace PolyPilot.Tests;

/// <summary>
/// Tests for BuildInfo which provides build-time metadata for debugging version mismatches.
/// This helps identify when a running app binary doesn't match the expected source code.
/// </summary>
public class BuildInfoTests
{
    [Fact]
    public void BuildTimestamp_IsNotEmpty()
    {
        // BuildTimestamp should always return something, even if fallback
        Assert.False(string.IsNullOrWhiteSpace(BuildInfo.BuildTimestamp),
            "BuildTimestamp should not be empty");
    }

    [Fact]
    public void BuildTimestamp_IsNotNull()
    {
        Assert.NotNull(BuildInfo.BuildTimestamp);
    }

    [Fact]
    public void ShortBuildId_IsNotEmpty()
    {
        // ShortBuildId should always return something
        Assert.False(string.IsNullOrWhiteSpace(BuildInfo.ShortBuildId),
            "ShortBuildId should not be empty");
    }

    [Fact]
    public void ShortBuildId_FormatIsCorrectWhenTimestampAvailable()
    {
        // If BuildTimestamp is in the expected format (yyyy-MM-dd HH:mm:ss), ShortBuildId should be MMdd-HHmm
        // If it's a version string with commit hash (e.g., "1.0.0+abc123"), ShortBuildId may vary
        var timestamp = BuildInfo.BuildTimestamp;
        
        if (timestamp.Length >= 16 && timestamp != "unknown" && !timestamp.Contains("+"))
        {
            // Standard timestamp format - ShortBuildId should be "MMdd-HHmm"
            Assert.Matches(@"^\d{4}-\d{4}$", BuildInfo.ShortBuildId);
        }
        else
        {
            // Version or unknown format - just verify it's not empty
            Assert.False(string.IsNullOrWhiteSpace(BuildInfo.ShortBuildId));
        }
    }

    [Fact]
    public void BuildTimestamp_ContainsYearOrVersionInfo()
    {
        // Should either contain a year (from timestamp) or version info (from InformationalVersion)
        var timestamp = BuildInfo.BuildTimestamp;
        var hasYear = timestamp.Contains("202") || timestamp.Contains("203"); // Years 2020-2039
        var hasVersionFormat = timestamp.Contains("+") || timestamp.Contains(".");
        var isUnknown = timestamp == "unknown";

        Assert.True(hasYear || hasVersionFormat || isUnknown,
            $"BuildTimestamp should contain year, version info, or be 'unknown'. Got: {timestamp}");
    }

    [Fact]
    public void BuildInfo_IsStatic()
    {
        // BuildInfo should be a static class - verify by checking type attributes
        var type = typeof(BuildInfo);
        Assert.True(type.IsAbstract && type.IsSealed,
            "BuildInfo should be a static class (abstract and sealed)");
    }

    [Fact]
    public void BuildTimestamp_IsDeterministic()
    {
        // Multiple accesses should return the same value (it's computed once)
        var first = BuildInfo.BuildTimestamp;
        var second = BuildInfo.BuildTimestamp;
        Assert.Equal(first, second);
    }

    [Fact]
    public void ShortBuildId_IsDeterministic()
    {
        var first = BuildInfo.ShortBuildId;
        var second = BuildInfo.ShortBuildId;
        Assert.Equal(first, second);
    }
}
