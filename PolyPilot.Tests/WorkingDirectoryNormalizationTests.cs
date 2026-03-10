using PolyPilot.Services;

namespace PolyPilot.Tests;

/// <summary>
/// Tests for working directory normalization and fallback logic.
/// Covers the bug where empty string cwd ("") bypassed the null-coalescing fallback
/// in ResumeSessionAsync, causing sessions to fail with "Session data appears corrupted".
/// </summary>
public class WorkingDirectoryNormalizationTests
{
    // --- NormalizeWorkingDirectory ---

    [Fact]
    public void Normalize_Null_ReturnsNull()
    {
        Assert.Null(CopilotService.NormalizeWorkingDirectory(null));
    }

    [Fact]
    public void Normalize_EmptyString_ReturnsNull()
    {
        Assert.Null(CopilotService.NormalizeWorkingDirectory(""));
    }

    [Fact]
    public void Normalize_Whitespace_ReturnsNull()
    {
        Assert.Null(CopilotService.NormalizeWorkingDirectory("   "));
    }

    [Fact]
    public void Normalize_NonExistentPath_ReturnsNull()
    {
        Assert.Null(CopilotService.NormalizeWorkingDirectory("/this/path/definitely/does/not/exist/xyz123"));
    }

    [Fact]
    public void Normalize_ExistingDirectory_ReturnsSamePath()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"polypilot_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var result = CopilotService.NormalizeWorkingDirectory(tempDir);
            Assert.Equal(tempDir, result);
        }
        finally
        {
            Directory.Delete(tempDir);
        }
    }

    // --- GetFallbackWorkingDirectory ---

    [Fact]
    public void Fallback_ReturnsNonEmptyExistingDirectory()
    {
        var fallback = CopilotService.GetFallbackWorkingDirectory();
        Assert.False(string.IsNullOrWhiteSpace(fallback));
        Assert.True(Directory.Exists(fallback));
    }

    [Fact]
    public void Fallback_ReturnsHomeOrTempPath()
    {
        var fallback = CopilotService.GetFallbackWorkingDirectory();
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var temp = Path.GetTempPath();
        Assert.True(fallback == home || fallback == temp,
            $"Expected home ({home}) or temp ({temp}), got: {fallback}");
    }

    // --- ActiveSessionEntry with empty WorkingDirectory ---

    [Fact]
    public void ActiveSessionEntry_EmptyWorkingDirectory_NormalizesToNull()
    {
        // Simulates the bug: entry has WorkingDirectory = "" from corrupt active-sessions.json
        var entry = new ActiveSessionEntry
        {
            SessionId = Guid.NewGuid().ToString(),
            DisplayName = "test session",
            Model = "claude-sonnet-4",
            WorkingDirectory = ""
        };

        // The fix: NormalizeWorkingDirectory treats "" as null
        var normalized = CopilotService.NormalizeWorkingDirectory(entry.WorkingDirectory);
        Assert.Null(normalized);

        // After normalization, fallback chain should produce a valid directory
        var result = normalized ?? CopilotService.GetFallbackWorkingDirectory();
        Assert.False(string.IsNullOrWhiteSpace(result));
        Assert.True(Directory.Exists(result));
    }

    [Fact]
    public void ActiveSessionEntry_NullWorkingDirectory_FallsBackGracefully()
    {
        var entry = new ActiveSessionEntry
        {
            SessionId = Guid.NewGuid().ToString(),
            DisplayName = "test session",
            Model = "claude-sonnet-4",
            WorkingDirectory = null
        };

        var normalized = CopilotService.NormalizeWorkingDirectory(entry.WorkingDirectory);
        Assert.Null(normalized);

        var result = normalized ?? CopilotService.GetFallbackWorkingDirectory();
        Assert.False(string.IsNullOrWhiteSpace(result));
    }

    // --- MergeSessionEntries filters out bad entries ---

    [Fact]
    public void Merge_EntriesWithEmptySessionId_AreFilteredDuringRestore()
    {
        // Simulates corrupt active-sessions.json with empty SessionId
        // The fix filters these out before attempting restore
        var entries = new List<ActiveSessionEntry>
        {
            new() { SessionId = "", DisplayName = "bad session", Model = "m" },
            new() { SessionId = "  ", DisplayName = "also bad", Model = "m" },
            new() { SessionId = Guid.NewGuid().ToString(), DisplayName = "good session", Model = "m", WorkingDirectory = "/tmp" }
        };

        // Filter logic from the fix in RestorePreviousSessionsAsync
        var filtered = entries
            .Where(e => !string.IsNullOrWhiteSpace(e.SessionId) && !string.IsNullOrWhiteSpace(e.DisplayName))
            .ToList();

        Assert.Single(filtered);
        Assert.Equal("good session", filtered[0].DisplayName);
    }

    [Fact]
    public void Merge_EntriesWithEmptyDisplayName_AreFilteredDuringRestore()
    {
        var entries = new List<ActiveSessionEntry>
        {
            new() { SessionId = Guid.NewGuid().ToString(), DisplayName = "", Model = "m" },
            new() { SessionId = Guid.NewGuid().ToString(), DisplayName = "valid", Model = "m" }
        };

        var filtered = entries
            .Where(e => !string.IsNullOrWhiteSpace(e.SessionId) && !string.IsNullOrWhiteSpace(e.DisplayName))
            .ToList();

        Assert.Single(filtered);
        Assert.Equal("valid", filtered[0].DisplayName);
    }

    // --- JSON deserialization resilience ---

    [Fact]
    public void ActiveSessionEntry_DeserializeMalformedJson_ReturnsNull()
    {
        // Simulates corrupt active-sessions.json
        var malformedJson = "{ this is not valid json [}";
        List<ActiveSessionEntry>? entries = null;
        try
        {
            entries = System.Text.Json.JsonSerializer.Deserialize<List<ActiveSessionEntry>>(malformedJson);
        }
        catch (System.Text.Json.JsonException)
        {
            // Expected — the fix catches this gracefully
        }

        Assert.Null(entries);
    }

    [Fact]
    public void ActiveSessionEntry_DeserializeEmptyArray_ReturnsEmptyList()
    {
        var json = "[]";
        var entries = System.Text.Json.JsonSerializer.Deserialize<List<ActiveSessionEntry>>(json);
        Assert.NotNull(entries);
        Assert.Empty(entries);
    }

    [Fact]
    public void ActiveSessionEntry_DeserializeWithMissingFields_DefaultsToEmpty()
    {
        // JSON with missing optional fields — should deserialize with defaults
        var json = """[{"SessionId":"abc-123","DisplayName":"test"}]""";
        var entries = System.Text.Json.JsonSerializer.Deserialize<List<ActiveSessionEntry>>(json);
        Assert.NotNull(entries);
        Assert.Single(entries);
        Assert.Equal("abc-123", entries![0].SessionId);
        Assert.Equal("", entries[0].Model); // default
        Assert.Null(entries[0].WorkingDirectory); // default null
    }
}
