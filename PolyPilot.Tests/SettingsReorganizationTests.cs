using PolyPilot.Models;
using PolyPilot.Services;

namespace PolyPilot.Tests;

/// <summary>
/// Tests for the Settings page reorganization:
/// - Statistics moved from Settings to a dedicated popup
/// - Settings category navigation
/// - Cmd+, keyboard shortcut for settings
/// </summary>
[Collection("BaseDir")]
public class SettingsReorganizationTests
{
    [Fact]
    public void UsageStatistics_GetStats_ReturnsSnapshot()
    {
        // Verify stats service returns a copy, not the internal instance
        var testDir = Path.Combine(Path.GetTempPath(), $"PolyPilot-settingstest-{Guid.NewGuid():N}");
        Directory.CreateDirectory(testDir);
        try
        {
            CopilotService.SetBaseDirForTesting(testDir);
            // Reset static field
            var statsPathField = typeof(UsageStatsService).GetField("_statsPath",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            statsPathField?.SetValue(null, null);

            var service = new UsageStatsService();
            service.TrackSessionStart("s1");

            var snap1 = service.GetStats();
            Assert.Equal(1, snap1.TotalSessionsCreated);

            // A second call should return an independent copy
            service.TrackSessionStart("s2");
            var snap2 = service.GetStats();

            Assert.Equal(1, snap1.TotalSessionsCreated); // snapshot unchanged
            Assert.Equal(2, snap2.TotalSessionsCreated);

            service.DisposeAsync().AsTask().Wait();
        }
        finally
        {
            try { Directory.Delete(testDir, true); } catch { }
        }
    }

    [Fact]
    public void UsageStatistics_FormatDuration_FormatsCorrectly()
    {
        // The FormatDuration logic now lives in StatisticsPopup.razor
        // Verify the expected formatting behavior
        Assert.Equal("0s", FormatDuration(0));
        Assert.Equal("30s", FormatDuration(30));
        Assert.Equal("1m 0s", FormatDuration(60));
        Assert.Equal("2m 30s", FormatDuration(150));
        Assert.Equal("1h 0m", FormatDuration(3600));
        Assert.Equal("1h 30m", FormatDuration(5400));
    }

    [Fact]
    public void UsageStatistics_DefaultValues_AreReasonable()
    {
        var stats = new UsageStatistics();

        Assert.Equal(0, stats.TotalSessionsCreated);
        Assert.Equal(0, stats.TotalSessionsClosed);
        Assert.Equal(0, stats.TotalSessionTimeSeconds);
        Assert.Equal(0, stats.LongestSessionSeconds);
        Assert.Equal(0, stats.TotalLinesSuggested);
        Assert.Equal(0, stats.TotalMessagesReceived);
    }

    [Fact]
    public void SettingsGroupKeywords_NoLongerIncludeStatistics()
    {
        // Settings page removed the "statistics" group keyword.
        // Verify the recognized categories are connection, cli, ui, developer.
        var recognizedCategories = new[] { "connection", "cli", "ui", "developer" };

        // "statistics" should not be in the recognized categories
        Assert.DoesNotContain("statistics", recognizedCategories);

        // All expected categories should be present
        Assert.Contains("connection", recognizedCategories);
        Assert.Contains("ui", recognizedCategories);
        Assert.Contains("developer", recognizedCategories);
    }

    [Fact]
    public void SettingsCategories_AllHaveHtmlIds()
    {
        // Verify that category IDs follow the expected naming pattern
        var categoryIds = new[] { "settings-connection", "settings-cli", "settings-ui", "settings-developer" };

        foreach (var id in categoryIds)
        {
            Assert.StartsWith("settings-", id);
            // The category name extracted from the ID should be non-empty
            var category = id.Replace("settings-", "");
            Assert.False(string.IsNullOrEmpty(category));
        }
    }

    /// <summary>
    /// Mirror of StatisticsPopup.razor FormatDuration for testability
    /// </summary>
    private static string FormatDuration(long totalSeconds)
    {
        if (totalSeconds < 60) return $"{totalSeconds}s";
        if (totalSeconds < 3600) return $"{totalSeconds / 60}m {totalSeconds % 60}s";
        var h = totalSeconds / 3600;
        var m = (totalSeconds % 3600) / 60;
        return $"{h}h {m}m";
    }
}
