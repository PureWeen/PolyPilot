using PolyPilot.Models;
using PolyPilot.Services;
using Microsoft.Extensions.DependencyInjection;

namespace PolyPilot.Tests;

public class ScheduledPromptServiceTests : IDisposable
{
    private readonly string _testDir;
    private readonly string _storeFile;

    public ScheduledPromptServiceTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), $"polypilot-sched-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testDir);
        _storeFile = Path.Combine(_testDir, "scheduled-prompts.json");
        ScheduledPromptService.SetStorePathForTesting(_storeFile);
    }

    public void Dispose()
    {
        // Reset to an isolated non-shared path so parallel tests are unaffected
        ScheduledPromptService.SetStorePathForTesting(Path.Combine(_testDir, "noop.json"));
        try { Directory.Delete(_testDir, true); } catch { }
    }

    private static CopilotService CreateCopilotService()
    {
        var sp = new ServiceCollection().BuildServiceProvider();
        return new CopilotService(
            new StubChatDatabase(),
            new StubServerManager(),
            new StubWsBridgeClient(),
            new RepoManager(),
            sp,
            new StubDemoService());
    }

    [Fact]
    public void ScheduledPrompt_DefaultValues()
    {
        var p = new ScheduledPrompt();
        Assert.False(string.IsNullOrEmpty(p.Id));
        Assert.Equal("", p.Label);
        Assert.Equal("", p.Prompt);
        Assert.Equal("", p.SessionName);
        Assert.Null(p.NextRunAt);
        Assert.Equal(0, p.RepeatIntervalMinutes);
        Assert.Null(p.LastRunAt);
        Assert.True(p.IsEnabled);
    }

    [Fact]
    public void DisplayName_UsesLabel_WhenSet()
    {
        var p = new ScheduledPrompt { Label = "My check", Prompt = "How are things going?" };
        Assert.Equal("My check", p.DisplayName);
    }

    [Fact]
    public void DisplayName_TruncatesLongPrompt_WhenNoLabel()
    {
        var p = new ScheduledPrompt { Label = "", Prompt = "This is a very long prompt that exceeds forty characters in length" };
        Assert.EndsWith("…", p.DisplayName);
        Assert.True(p.DisplayName.Length <= 40);
    }

    [Fact]
    public void DisplayName_ShortPrompt_UsedDirectly()
    {
        var p = new ScheduledPrompt { Label = "", Prompt = "Short prompt" };
        Assert.Equal("Short prompt", p.DisplayName);
    }

    [Fact]
    public void Add_AddsPromptAndPersists()
    {
        var svc = new ScheduledPromptService(CreateCopilotService());
        var p = svc.Add("session1", "Check status", "Status check", DateTime.UtcNow.AddMinutes(5));

        Assert.Single(svc.Prompts);
        Assert.Equal("session1", p.SessionName);
        Assert.Equal("Check status", p.Prompt);
        Assert.Equal("Status check", p.Label);
        Assert.True(p.IsEnabled);
        Assert.True(File.Exists(_storeFile));
    }

    [Fact]
    public void Add_WithRepeat_SetsRepeatInterval()
    {
        var svc = new ScheduledPromptService(CreateCopilotService());
        var p = svc.Add("session1", "Daily check", repeatIntervalMinutes: 1440);
        Assert.Equal(1440, p.RepeatIntervalMinutes);
    }

    [Fact]
    public void Remove_RemovesPromptAndPersists()
    {
        var svc = new ScheduledPromptService(CreateCopilotService());
        var p = svc.Add("session1", "Check status");
        Assert.True(svc.Remove(p.Id));
        Assert.Empty(svc.Prompts);
    }

    [Fact]
    public void Remove_ReturnsFalse_WhenNotFound()
    {
        var svc = new ScheduledPromptService(CreateCopilotService());
        Assert.False(svc.Remove("nonexistent-id"));
    }

    [Fact]
    public void SetEnabled_TogglesEnabledState()
    {
        var svc = new ScheduledPromptService(CreateCopilotService());
        var p = svc.Add("session1", "Check status");

        svc.SetEnabled(p.Id, false);
        Assert.False(svc.Prompts[0].IsEnabled);

        svc.SetEnabled(p.Id, true);
        Assert.True(svc.Prompts[0].IsEnabled);
    }

    [Fact]
    public void Persistence_RoundTrip()
    {
        var svc = new ScheduledPromptService(CreateCopilotService());
        var p = svc.Add("session1", "Hello world", "Test", DateTime.UtcNow.AddMinutes(10), 60);

        var svc2 = new ScheduledPromptService(CreateCopilotService());
        Assert.Single(svc2.Prompts);
        var loaded = svc2.Prompts[0];
        Assert.Equal(p.Id, loaded.Id);
        Assert.Equal("session1", loaded.SessionName);
        Assert.Equal("Hello world", loaded.Prompt);
        Assert.Equal("Test", loaded.Label);
        Assert.Equal(60, loaded.RepeatIntervalMinutes);
        Assert.True(loaded.IsEnabled);
    }

    [Fact]
    public void FirePromptAsync_OneShot_DisablesAfterFiring_WhenSessionNotFound_Retries()
    {
        // When session doesn't exist, the prompt should stay enabled for retry
        var svc = new ScheduledPromptService(CreateCopilotService());
        var p = svc.Add("nonexistent-session", "One-shot", runAt: DateTime.UtcNow, repeatIntervalMinutes: 0);

        svc.FirePromptAsync(p).GetAwaiter().GetResult();

        // Session not found → still enabled (will retry on next check)
        Assert.True(p.IsEnabled);
        Assert.NotNull(p.NextRunAt);
    }

    [Fact]
    public void FirePromptAsync_Repeat_WhenSessionNotFound_Retries()
    {
        // When session doesn't exist, keeps the original NextRunAt for retry
        var svc = new ScheduledPromptService(CreateCopilotService());
        var runAt = DateTime.UtcNow;
        var p = svc.Add("nonexistent-session", "Repeating", runAt: runAt, repeatIntervalMinutes: 30);

        svc.FirePromptAsync(p).GetAwaiter().GetResult();

        // Session not found → stays enabled, NextRunAt unchanged
        Assert.True(p.IsEnabled);
        Assert.Equal(runAt, p.NextRunAt);
    }

    [Fact]
    public void OnStateChanged_FiresOnAdd()
    {
        var svc = new ScheduledPromptService(CreateCopilotService());
        var fired = false;
        svc.OnStateChanged += () => fired = true;
        svc.Add("s", "p");
        Assert.True(fired);
    }

    [Fact]
    public void OnStateChanged_FiresOnRemove()
    {
        var svc = new ScheduledPromptService(CreateCopilotService());
        var p = svc.Add("s", "p");
        var fired = false;
        svc.OnStateChanged += () => fired = true;
        svc.Remove(p.Id);
        Assert.True(fired);
    }
}
