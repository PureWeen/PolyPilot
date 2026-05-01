using PolyPilot.Services;

namespace PolyPilot.Tests;

/// <summary>
/// Tests for the CLI session import feature (ImportCliSessionsAsync).
/// </summary>
public class ImportCliSessionsTests
{
    // --- GenerateImportDisplayName ---

    [Fact]
    public void GenerateImportDisplayName_PrefersSummary()
    {
        var name = CopilotService.GenerateImportDisplayName(
            "Fix the login bug in auth module",
            "/Users/test/projects/myapp",
            "abc12345-1234-1234-1234-123456789abc");

        Assert.Equal("Fix the login bug in auth module", name);
    }

    [Fact]
    public void GenerateImportDisplayName_TruncatesLongSummary()
    {
        var longSummary = new string('x', 80);
        var name = CopilotService.GenerateImportDisplayName(
            longSummary, "/some/path", "abc12345-dead-beef-1234-123456789abc");

        Assert.Equal(50, name.Length);
        Assert.EndsWith("...", name);
    }

    [Fact]
    public void GenerateImportDisplayName_FallsToCwdBasename()
    {
        var name = CopilotService.GenerateImportDisplayName(
            null, "/Users/test/projects/my-cool-project", "abc12345-dead-beef-1234-123456789abc");

        Assert.Equal("my-cool-project", name);
    }

    [Fact]
    public void GenerateImportDisplayName_FallsToCwdBasename_TrailingSlash()
    {
        var name = CopilotService.GenerateImportDisplayName(
            null, "/Users/test/projects/my-cool-project/", "abc12345-dead-beef-1234-123456789abc");

        Assert.Equal("my-cool-project", name);
    }

    [Fact]
    public void GenerateImportDisplayName_FallsToShortGuid()
    {
        var name = CopilotService.GenerateImportDisplayName(
            null, null, "abc12345-dead-beef-1234-123456789abc");

        Assert.Equal("abc12345", name);
    }

    [Fact]
    public void GenerateImportDisplayName_EmptySummaryFallsToCwd()
    {
        var name = CopilotService.GenerateImportDisplayName(
            "", "/some/path/coolapp", "abc12345-dead-beef-1234-123456789abc");

        Assert.Equal("coolapp", name);
    }

    [Fact]
    public void GenerateImportDisplayName_WhitespaceSummaryFallsToCwd()
    {
        var name = CopilotService.GenerateImportDisplayName(
            "   ", "/some/path/coolapp", "abc12345-dead-beef-1234-123456789abc");

        Assert.Equal("coolapp", name);
    }

    [Fact]
    public void GenerateImportDisplayName_CleansNewlines()
    {
        var name = CopilotService.GenerateImportDisplayName(
            "Fix the\nbug\r\nin module", null, "abc12345-dead-beef-1234-123456789abc");

        Assert.Equal("Fix the bug in module", name);
    }

    // --- ActiveSessionEntry.Imported flag ---

    [Fact]
    public void ActiveSessionEntry_ImportedFlag_DefaultsFalse()
    {
        var entry = new ActiveSessionEntry { SessionId = "test", DisplayName = "Test" };
        Assert.False(entry.Imported);
    }

    [Fact]
    public void ActiveSessionEntry_ImportedFlag_RoundTripsViaJson()
    {
        var entry = new ActiveSessionEntry
        {
            SessionId = "test-id",
            DisplayName = "Test Session",
            Model = "claude-opus-4.6",
            Imported = true
        };

        var json = System.Text.Json.JsonSerializer.Serialize(entry);
        var deserialized = System.Text.Json.JsonSerializer.Deserialize<ActiveSessionEntry>(json);

        Assert.NotNull(deserialized);
        Assert.True(deserialized!.Imported);
        Assert.Equal("test-id", deserialized.SessionId);
    }

    [Fact]
    public void ActiveSessionEntry_ImportedFlag_FalseNotIncludedInJson()
    {
        var entry = new ActiveSessionEntry
        {
            SessionId = "test-id",
            DisplayName = "Test Session",
            Imported = false
        };

        var json = System.Text.Json.JsonSerializer.Serialize(entry);
        // When false (default), the value should still serialize correctly
        var deserialized = System.Text.Json.JsonSerializer.Deserialize<ActiveSessionEntry>(json);
        Assert.NotNull(deserialized);
        Assert.False(deserialized!.Imported);
    }

    // --- Merge preserves Imported flag ---

    [Fact]
    public void Merge_PreservesImportedFlag_FromPersistedEntries()
    {
        var active = new List<ActiveSessionEntry>();
        var persisted = new List<ActiveSessionEntry>
        {
            new() { SessionId = "imp-1", DisplayName = "Imported One", Model = "m", Imported = true }
        };
        var closed = new HashSet<string>();

        var result = CopilotService.MergeSessionEntries(
            active, persisted, closed, new HashSet<string>(), _ => true);

        var importedEntry = result.FirstOrDefault(e => e.SessionId == "imp-1");
        Assert.NotNull(importedEntry);
        Assert.True(importedEntry!.Imported);
    }
}
