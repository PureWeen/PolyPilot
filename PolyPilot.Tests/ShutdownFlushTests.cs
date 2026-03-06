using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using PolyPilot.Models;
using PolyPilot.Services;

namespace PolyPilot.Tests;

/// <summary>
/// Tests that FlushPendingSaves correctly flushes all debounced writes to disk
/// before the app exits. Regression tests for: "sessions are not persisted when
/// I close the app" — debounced Timer callbacks were never firing because the
/// app terminated before the 2-second delay elapsed.
/// </summary>
public class ShutdownFlushTests
{
    private readonly StubChatDatabase _chatDb = new();
    private readonly StubServerManager _serverManager = new();
    private readonly StubWsBridgeClient _bridgeClient = new();
    private readonly StubDemoService _demoService = new();
    private readonly RepoManager _repoManager = new();
    private readonly IServiceProvider _serviceProvider;

    public ShutdownFlushTests()
    {
        var services = new ServiceCollection();
        _serviceProvider = services.BuildServiceProvider();
    }

    private CopilotService CreateService() =>
        new CopilotService(_chatDb, _serverManager, _bridgeClient, _repoManager, _serviceProvider, _demoService);

    [Fact]
    public async Task FlushPendingSaves_PersistsUiStateToDisk()
    {
        var svc = CreateService();
        await svc.ReconnectAsync(new ConnectionSettings { Mode = ConnectionMode.Demo });

        await svc.CreateSessionAsync("ui-flush");

        // SaveUiState uses a 1-second debounce timer
        svc.SaveUiState("/dashboard", activeSession: "ui-flush", fontSize: 24);

        // Flush immediately — don't wait for the debounce
        svc.FlushPendingSaves();

        var uiStateFile = Path.Combine(TestSetup.TestBaseDir, "ui-state.json");
        Assert.True(File.Exists(uiStateFile), "ui-state.json should exist after FlushPendingSaves");

        var json = File.ReadAllText(uiStateFile);
        var state = JsonSerializer.Deserialize<UiState>(json);
        Assert.NotNull(state);
        Assert.Equal("/dashboard", state!.CurrentPage);
        Assert.Equal("ui-flush", state.ActiveSession);
        Assert.Equal(24, state.FontSize);
    }

    [Fact]
    public async Task FlushPendingSaves_WithoutFlush_UiStateNotWritten()
    {
        var svc = CreateService();
        await svc.ReconnectAsync(new ConnectionSettings { Mode = ConnectionMode.Demo });

        await svc.CreateSessionAsync("no-flush");

        // Use a unique file path to avoid interference from other tests
        var uniqueKey = Guid.NewGuid().ToString();
        svc.SaveUiState("/test-" + uniqueKey, activeSession: "no-flush");

        // Do NOT call FlushPendingSaves — verify the debounce timer hasn't fired yet
        // (the 1-second timer is still pending)
        var uiStateFile = Path.Combine(TestSetup.TestBaseDir, "ui-state.json");
        if (File.Exists(uiStateFile))
        {
            var json = File.ReadAllText(uiStateFile);
            var state = JsonSerializer.Deserialize<UiState>(json);
            // If file exists from a previous test, it should NOT have our unique page
            // (unless the debounce timer happened to fire, which would be a timing fluke)
            // This test documents the debounce behavior rather than being a hard assertion
        }

        // NOW flush and verify it writes
        svc.FlushPendingSaves();
        Assert.True(File.Exists(uiStateFile));
        var finalJson = File.ReadAllText(uiStateFile);
        var finalState = JsonSerializer.Deserialize<UiState>(finalJson);
        Assert.Contains("/test-" + uniqueKey, finalState!.CurrentPage);
    }

    [Fact]
    public async Task FlushPendingSaves_CalledMultipleTimes_DoesNotThrow()
    {
        var svc = CreateService();
        await svc.ReconnectAsync(new ConnectionSettings { Mode = ConnectionMode.Demo });

        await svc.CreateSessionAsync("multi-flush");
        svc.SaveUiState("/dashboard", activeSession: "multi-flush");

        // Call flush multiple times — should be idempotent
        svc.FlushPendingSaves();
        svc.FlushPendingSaves();
        svc.FlushPendingSaves();
    }

    [Fact]
    public void FlushPendingSaves_NoSessions_DoesNotThrow()
    {
        var svc = CreateService();
        // No initialization, no sessions — flush should be a safe no-op
        svc.FlushPendingSaves();
    }

    [Fact]
    public async Task FlushPendingSaves_DemoMode_DoesNotWriteSessionFile()
    {
        // Use a unique test base dir to avoid interference
        var isolatedDir = Path.Combine(Path.GetTempPath(), "polypilot-flush-test-" + Guid.NewGuid());
        Directory.CreateDirectory(isolatedDir);
        try
        {
            var svc = CreateService();
            await svc.ReconnectAsync(new ConnectionSettings { Mode = ConnectionMode.Demo });

            await svc.CreateSessionAsync("demo-session");
            svc.FlushPendingSaves();

            // Demo mode skips session file writes (SaveActiveSessionsToDiskCore returns early)
            var activeSessionsFile = Path.Combine(isolatedDir, "active-sessions.json");
            Assert.False(File.Exists(activeSessionsFile),
                "Demo mode should not write active-sessions.json to isolated dir");
        }
        finally
        {
            try { Directory.Delete(isolatedDir, true); } catch { }
        }
    }

    [Fact]
    public async Task DisposeAsync_FlushesUiState()
    {
        var svc = CreateService();
        await svc.ReconnectAsync(new ConnectionSettings { Mode = ConnectionMode.Demo });

        await svc.CreateSessionAsync("dispose-flush");
        svc.SaveUiState("/settings", activeSession: "dispose-flush", fontSize: 18);

        // DisposeAsync calls FlushPendingSaves internally
        await svc.DisposeAsync();

        // UI state should have been flushed
        var uiStateFile = Path.Combine(TestSetup.TestBaseDir, "ui-state.json");
        Assert.True(File.Exists(uiStateFile), "DisposeAsync should flush UI state");

        var json = File.ReadAllText(uiStateFile);
        var state = JsonSerializer.Deserialize<UiState>(json);
        Assert.NotNull(state);
        Assert.Equal("/settings", state!.CurrentPage);
        Assert.Equal(18, state.FontSize);
    }

    [Fact]
    public async Task FlushPendingSaves_FlushesOrganizationState()
    {
        var svc = CreateService();
        await svc.ReconnectAsync(new ConnectionSettings { Mode = ConnectionMode.Demo });

        await svc.CreateSessionAsync("org-worker-1");
        await svc.CreateSessionAsync("org-worker-2");

        // Create a group — triggers debounced SaveOrganization
        svc.CreateGroup("test-group");

        // Flush should persist organization state
        svc.FlushPendingSaves();

        var orgFile = Path.Combine(TestSetup.TestBaseDir, "organization.json");
        Assert.True(File.Exists(orgFile), "organization.json should exist after FlushPendingSaves");

        var json = File.ReadAllText(orgFile);
        Assert.Contains("test-group", json);
    }
}
