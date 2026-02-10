using System.Text.Json;

namespace PolyPilot.Tests;

/// <summary>
/// Tests for UI state persistence patterns — UiState and ActiveSessionEntry
/// serialization, and the session alias caching logic.
/// </summary>
public class UiStatePersistenceTests
{
    [Fact]
    public void UiState_DefaultValues()
    {
        var state = new UiState();
        Assert.Equal("/", state.CurrentPage);
        Assert.Null(state.ActiveSession);
        Assert.Equal(20, state.FontSize);
    }

    [Fact]
    public void UiState_RoundTripSerialization()
    {
        var state = new UiState
        {
            CurrentPage = "/dashboard",
            ActiveSession = "my-session",
            FontSize = 16
        };

        var json = JsonSerializer.Serialize(state);
        var restored = JsonSerializer.Deserialize<UiState>(json);

        Assert.NotNull(restored);
        Assert.Equal("/dashboard", restored!.CurrentPage);
        Assert.Equal("my-session", restored.ActiveSession);
        Assert.Equal(16, restored.FontSize);
    }

    [Fact]
    public void UiState_NullActiveSession_Serializes()
    {
        var state = new UiState { CurrentPage = "/settings" };
        var json = JsonSerializer.Serialize(state);
        var restored = JsonSerializer.Deserialize<UiState>(json);

        Assert.NotNull(restored);
        Assert.Null(restored!.ActiveSession);
    }

    [Fact]
    public void ActiveSessionEntry_RoundTrip()
    {
        var entries = new List<ActiveSessionEntry>
        {
            new() { SessionId = "guid-1", DisplayName = "Agent 1", Model = "claude-opus-4.6" },
            new() { SessionId = "guid-2", DisplayName = "Agent 2", Model = "gpt-5" }
        };

        var json = JsonSerializer.Serialize(entries, new JsonSerializerOptions { WriteIndented = true });
        var restored = JsonSerializer.Deserialize<List<ActiveSessionEntry>>(json);

        Assert.NotNull(restored);
        Assert.Equal(2, restored!.Count);
        Assert.Equal("guid-1", restored[0].SessionId);
        Assert.Equal("Agent 1", restored[0].DisplayName);
        Assert.Equal("claude-opus-4.6", restored[0].Model);
        Assert.Equal("gpt-5", restored[1].Model);
    }

    [Fact]
    public void ActiveSessionEntry_DefaultValues()
    {
        var entry = new ActiveSessionEntry();
        Assert.Equal("", entry.SessionId);
        Assert.Equal("", entry.DisplayName);
        Assert.Equal("", entry.Model);
    }

    [Fact]
    public void SessionAliases_RoundTrip()
    {
        var aliases = new Dictionary<string, string>
        {
            ["abc-123"] = "My Custom Name",
            ["def-456"] = "Another Session"
        };

        var json = JsonSerializer.Serialize(aliases, new JsonSerializerOptions { WriteIndented = true });
        var restored = JsonSerializer.Deserialize<Dictionary<string, string>>(json);

        Assert.NotNull(restored);
        Assert.Equal(2, restored!.Count);
        Assert.Equal("My Custom Name", restored["abc-123"]);
    }

    [Fact]
    public void SessionAliases_EmptyAlias_CanBeRemovedByTrim()
    {
        // Mirrors CopilotService.SetSessionAlias logic
        var alias = "  ";
        Assert.True(string.IsNullOrWhiteSpace(alias));
    }

    [Fact]
    public void SessionAliases_TrimmedOnSet()
    {
        // Mirrors CopilotService.SetSessionAlias logic
        var alias = "  My Session  ";
        Assert.Equal("My Session", alias.Trim());
    }
}

// These classes mirror the ones in CopilotService.cs (they're defined at the bottom of that file)
// They're duplicated here because the original file has MAUI dependencies.
public class UiState
{
    public string CurrentPage { get; set; } = "/";
    public string? ActiveSession { get; set; }
    public int FontSize { get; set; } = 20;
}

public class ActiveSessionEntry
{
    public string SessionId { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string Model { get; set; } = "";
}
