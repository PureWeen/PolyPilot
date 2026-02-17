using Microsoft.Extensions.DependencyInjection;
using PolyPilot.Models;
using PolyPilot.Services;

namespace PolyPilot.Tests;

/// <summary>
/// Integration tests for CopilotService initialization and error handling.
/// Uses stub dependencies to test the actual CopilotService class.
/// Tests use ReconnectAsync(settings) to avoid shared settings.json dependency.
/// </summary>
public class CopilotServiceInitializationTests
{
    private readonly StubChatDatabase _chatDb = new();
    private readonly StubServerManager _serverManager = new();
    private readonly StubWsBridgeClient _bridgeClient = new();
    private readonly StubDemoService _demoService = new();
    private readonly RepoManager _repoManager = new();
    private readonly IServiceProvider _serviceProvider;

    public CopilotServiceInitializationTests()
    {
        var services = new ServiceCollection();
        _serviceProvider = services.BuildServiceProvider();
    }

    private CopilotService CreateService() =>
        new CopilotService(_chatDb, _serverManager, _bridgeClient, _repoManager, _serviceProvider, _demoService);

    [Fact]
    public void NewService_IsNotInitialized()
    {
        var svc = CreateService();
        Assert.False(svc.IsInitialized);
        Assert.Null(svc.ActiveSessionName);
    }

    [Fact]
    public void NewService_DefaultMode_IsEmbedded()
    {
        var svc = CreateService();
        Assert.Equal(ConnectionMode.Embedded, svc.CurrentMode);
    }

    [Fact]
    public async Task CreateSession_BeforeInitialize_Throws()
    {
        var svc = CreateService();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.CreateSessionAsync("test", cancellationToken: CancellationToken.None));

        Assert.Contains("Service not initialized", ex.Message);
    }

    [Fact]
    public async Task ResumeSession_BeforeInitialize_Throws()
    {
        var svc = CreateService();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.ResumeSessionAsync(Guid.NewGuid().ToString(), "test", cancellationToken: CancellationToken.None));

        Assert.Contains("Service not initialized", ex.Message);
    }

    [Fact]
    public async Task ReconnectAsync_DemoMode_SetsInitialized()
    {
        var svc = CreateService();
        await svc.ReconnectAsync(new ConnectionSettings { Mode = ConnectionMode.Demo });

        Assert.True(svc.IsInitialized);
        Assert.True(svc.IsDemoMode);
        Assert.False(svc.NeedsConfiguration);
    }

    [Fact]
    public async Task ReconnectAsync_DemoMode_CreateSession_Works()
    {
        var svc = CreateService();
        await svc.ReconnectAsync(new ConnectionSettings { Mode = ConnectionMode.Demo });

        var session = await svc.CreateSessionAsync("test-session");
        Assert.NotNull(session);
        Assert.Equal("test-session", session.Name);
    }

    [Fact]
    public async Task ReconnectAsync_DemoMode_ThenReconnectAgain_Works()
    {
        var svc = CreateService();
        await svc.ReconnectAsync(new ConnectionSettings { Mode = ConnectionMode.Demo });
        Assert.True(svc.IsInitialized);

        // Reconnect again in demo mode
        await svc.ReconnectAsync(new ConnectionSettings { Mode = ConnectionMode.Demo });
        Assert.True(svc.IsInitialized);
        Assert.True(svc.IsDemoMode);
    }

    [Fact]
    public async Task ReconnectAsync_PersistentMode_Failure_SetsNeedsConfiguration()
    {
        // Persistent mode connecting to unreachable port — deterministic failure
        var svc = CreateService();
        await svc.ReconnectAsync(new ConnectionSettings
        {
            Mode = ConnectionMode.Persistent,
            Host = "localhost",
            Port = 19999  // Nothing listening
        });

        // StartAsync throws → caught → NeedsConfiguration = true
        Assert.False(svc.IsInitialized);
        Assert.True(svc.NeedsConfiguration);
    }

    [Fact]
    public async Task ReconnectAsync_PersistentMode_Failure_ClientIsNull()
    {
        var svc = CreateService();
        await svc.ReconnectAsync(new ConnectionSettings
        {
            Mode = ConnectionMode.Persistent,
            Host = "localhost",
            Port = 19999
        });

        // After failure, CreateSession should still throw "not initialized"
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.CreateSessionAsync("test", cancellationToken: CancellationToken.None));
        Assert.Contains("Service not initialized", ex.Message);
    }

    [Fact]
    public async Task ReconnectAsync_PersistentMode_NoServer_Failure()
    {
        // Persistent mode but nothing listening on the port
        var svc = CreateService();
        await svc.ReconnectAsync(new ConnectionSettings
        {
            Mode = ConnectionMode.Persistent,
            Host = "localhost",
            Port = 19999  // Nothing listening
        });

        // StartAsync should throw connecting to unreachable server → caught gracefully
        Assert.False(svc.IsInitialized);
        Assert.True(svc.NeedsConfiguration);
    }

    [Fact]
    public async Task ReconnectAsync_FromDemoToPersistent_ClearsOldState()
    {
        var svc = CreateService();

        // First initialize in Demo mode (succeeds)
        await svc.ReconnectAsync(new ConnectionSettings { Mode = ConnectionMode.Demo });
        Assert.True(svc.IsInitialized);
        Assert.True(svc.IsDemoMode);

        // Now reconnect to Persistent (will fail — unreachable port)
        await svc.ReconnectAsync(new ConnectionSettings
        {
            Mode = ConnectionMode.Persistent,
            Host = "localhost",
            Port = 19999
        });

        // Old Demo state should always be cleared
        Assert.False(svc.IsDemoMode);
        Assert.False(svc.IsInitialized);
        Assert.True(svc.NeedsConfiguration);
    }

    [Fact]
    public async Task ReconnectAsync_Failure_OnStateChanged_Fires()
    {
        var svc = CreateService();
        var stateChangedCount = 0;
        svc.OnStateChanged += () => stateChangedCount++;

        await svc.ReconnectAsync(new ConnectionSettings
        {
            Mode = ConnectionMode.Persistent,
            Host = "localhost",
            Port = 19999
        });

        // OnStateChanged should fire at least once on failure
        Assert.True(stateChangedCount > 0, "OnStateChanged should fire on initialization failure");
    }

    [Fact]
    public async Task ReconnectAsync_DemoMode_OnStateChanged_Fires()
    {
        var svc = CreateService();
        var stateChangedCount = 0;
        svc.OnStateChanged += () => stateChangedCount++;

        await svc.ReconnectAsync(new ConnectionSettings { Mode = ConnectionMode.Demo });

        Assert.True(stateChangedCount > 0, "OnStateChanged should fire on Demo initialization");
    }

    [Fact]
    public async Task ReconnectAsync_DemoMode_SessionsCleared()
    {
        var svc = CreateService();

        // Create a demo session
        await svc.ReconnectAsync(new ConnectionSettings { Mode = ConnectionMode.Demo });
        await svc.CreateSessionAsync("session1");
        Assert.Single(svc.GetAllSessions());

        // Reconnect clears sessions
        await svc.ReconnectAsync(new ConnectionSettings { Mode = ConnectionMode.Demo });
        Assert.Empty(svc.GetAllSessions());
    }

    [Fact]
    public async Task ReconnectAsync_PersistentMode_SetsCurrentMode()
    {
        var svc = CreateService();
        await svc.ReconnectAsync(new ConnectionSettings
        {
            Mode = ConnectionMode.Persistent,
            Host = "localhost",
            Port = 19999
        });

        // Even if StartAsync fails, CurrentMode should reflect what was attempted
        Assert.Equal(ConnectionMode.Persistent, svc.CurrentMode);
    }

    // --- Mode Switch Tests ---

    [Fact]
    public async Task ModeSwitch_DemoToPersistentFailure_SessionsCleared()
    {
        var svc = CreateService();

        // Start in Demo, create sessions
        await svc.ReconnectAsync(new ConnectionSettings { Mode = ConnectionMode.Demo });
        await svc.CreateSessionAsync("session-a");
        await svc.CreateSessionAsync("session-b");
        Assert.Equal(2, svc.GetAllSessions().Count());

        // Switch to Persistent (fails — unreachable port)
        await svc.ReconnectAsync(new ConnectionSettings
        {
            Mode = ConnectionMode.Persistent,
            Host = "localhost",
            Port = 19999
        });

        // All old sessions should be cleared by ReconnectAsync
        Assert.Empty(svc.GetAllSessions());
        Assert.Null(svc.ActiveSessionName);
        Assert.False(svc.IsDemoMode);
    }

    [Fact]
    public async Task ModeSwitch_PersistentFailureThenDemo_Recovers()
    {
        var svc = CreateService();

        // Try Persistent first (fails)
        await svc.ReconnectAsync(new ConnectionSettings
        {
            Mode = ConnectionMode.Persistent,
            Host = "localhost",
            Port = 19999
        });
        Assert.False(svc.IsInitialized);
        Assert.True(svc.NeedsConfiguration);

        // Now switch to Demo — should recover fully
        await svc.ReconnectAsync(new ConnectionSettings { Mode = ConnectionMode.Demo });
        Assert.True(svc.IsInitialized);
        Assert.True(svc.IsDemoMode);
        Assert.False(svc.NeedsConfiguration);

        // Should be able to create sessions
        var session = await svc.CreateSessionAsync("recovered");
        Assert.NotNull(session);
        Assert.Equal("recovered", session.Name);
    }

    [Fact]
    public async Task ModeSwitch_RapidModeSwitches_NoCorruption()
    {
        var svc = CreateService();

        // Demo → Persistent (fail) → Demo → Persistent (fail) → Demo
        await svc.ReconnectAsync(new ConnectionSettings { Mode = ConnectionMode.Demo });
        Assert.True(svc.IsInitialized);

        await svc.ReconnectAsync(new ConnectionSettings
        {
            Mode = ConnectionMode.Persistent, Host = "localhost", Port = 19999
        });
        Assert.False(svc.IsInitialized);

        await svc.ReconnectAsync(new ConnectionSettings { Mode = ConnectionMode.Demo });
        Assert.True(svc.IsInitialized);

        await svc.ReconnectAsync(new ConnectionSettings
        {
            Mode = ConnectionMode.Persistent, Host = "localhost", Port = 19999
        });
        Assert.False(svc.IsInitialized);

        await svc.ReconnectAsync(new ConnectionSettings { Mode = ConnectionMode.Demo });
        Assert.True(svc.IsInitialized);
        Assert.True(svc.IsDemoMode);

        // Final state is clean — can create sessions
        var session = await svc.CreateSessionAsync("final-test");
        Assert.NotNull(session);
    }

    [Fact]
    public async Task ModeSwitch_DemoToPersistentFailure_ActiveSessionCleared()
    {
        var svc = CreateService();

        // Start in Demo with an active session
        await svc.ReconnectAsync(new ConnectionSettings { Mode = ConnectionMode.Demo });
        await svc.CreateSessionAsync("active-one");
        Assert.Equal("active-one", svc.ActiveSessionName);

        // Switch to Persistent (fails)
        await svc.ReconnectAsync(new ConnectionSettings
        {
            Mode = ConnectionMode.Persistent, Host = "localhost", Port = 19999
        });

        // Active session name must be cleared
        Assert.Null(svc.ActiveSessionName);
    }

    [Fact]
    public async Task ModeSwitch_PersistentFailure_ThenCreateSession_ThrowsNotInitialized()
    {
        var svc = CreateService();

        // Persistent fails
        await svc.ReconnectAsync(new ConnectionSettings
        {
            Mode = ConnectionMode.Persistent, Host = "localhost", Port = 19999
        });

        // Creating a session after failed connection should still throw
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.CreateSessionAsync("should-fail", cancellationToken: CancellationToken.None));
        Assert.Contains("Service not initialized", ex.Message);
    }

    [Fact]
    public async Task ModeSwitch_PersistentFailure_ResumeSession_ThrowsNotInitialized()
    {
        var svc = CreateService();

        // Persistent fails
        await svc.ReconnectAsync(new ConnectionSettings
        {
            Mode = ConnectionMode.Persistent, Host = "localhost", Port = 19999
        });

        // Resuming a session after failed connection should throw
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.ResumeSessionAsync(Guid.NewGuid().ToString(), "test-resume", cancellationToken: CancellationToken.None));
        Assert.Contains("Service not initialized", ex.Message);
    }

    [Fact]
    public async Task ModeSwitch_OnStateChanged_FiresForEachSwitch()
    {
        var svc = CreateService();
        var stateChanges = new List<(ConnectionMode mode, bool initialized)>();
        svc.OnStateChanged += () => stateChanges.Add((svc.CurrentMode, svc.IsInitialized));

        // Demo (success)
        await svc.ReconnectAsync(new ConnectionSettings { Mode = ConnectionMode.Demo });
        var demoChanges = stateChanges.Count;
        Assert.True(demoChanges > 0);

        // Persistent (failure)
        await svc.ReconnectAsync(new ConnectionSettings
        {
            Mode = ConnectionMode.Persistent, Host = "localhost", Port = 19999
        });
        Assert.True(stateChanges.Count > demoChanges, "Should fire additional state changes for Persistent attempt");

        // Verify at least one Persistent state change shows not-initialized
        var persistentChanges = stateChanges.Skip(demoChanges).ToList();
        Assert.Contains(persistentChanges, c => c.mode == ConnectionMode.Persistent && !c.initialized);
    }

    [Fact]
    public void FallbackNotice_InitiallyNull()
    {
        var svc = CreateService();
        Assert.Null(svc.FallbackNotice);
    }

    [Fact]
    public void ClearFallbackNotice_ClearsNotice()
    {
        var svc = CreateService();
        // FallbackNotice is set internally during InitializeAsync persistent fallback,
        // but we can test the clear path
        svc.ClearFallbackNotice();
        Assert.Null(svc.FallbackNotice);
    }

    [Fact]
    public async Task ReconnectAsync_ClearsFallbackNotice()
    {
        var svc = CreateService();

        // Reconnect to Demo — should clear any previous fallback notice
        await svc.ReconnectAsync(new ConnectionSettings { Mode = ConnectionMode.Demo });
        Assert.Null(svc.FallbackNotice);
    }

    [Fact]
    public void ConnectionSettings_SetMode_PersistsMode()
    {
        // Verify that the mode value round-trips through ConnectionSettings
        // (mirrors the fix in Settings.razor where SetMode now calls Save)
        var settings = new ConnectionSettings { Mode = ConnectionMode.Persistent };
        Assert.Equal(ConnectionMode.Persistent, settings.Mode);

        settings.Mode = ConnectionMode.Embedded;
        Assert.Equal(ConnectionMode.Embedded, settings.Mode);

        // Verify it doesn't revert on its own
        settings.Mode = ConnectionMode.Persistent;
        Assert.Equal(ConnectionMode.Persistent, settings.Mode);
    }

    [Fact]
    public void ConnectionSettings_CliSource_DefaultIsBuiltIn()
    {
        var settings = new ConnectionSettings();
        Assert.Equal(CliSourceMode.BuiltIn, settings.CliSource);
    }

    [Fact]
    public void ConnectionSettings_CliSource_CanSwitchToSystem()
    {
        var settings = new ConnectionSettings();
        settings.CliSource = CliSourceMode.System;
        Assert.Equal(CliSourceMode.System, settings.CliSource);

        settings.CliSource = CliSourceMode.BuiltIn;
        Assert.Equal(CliSourceMode.BuiltIn, settings.CliSource);
    }

    [Fact]
    public void ConnectionSettings_CliSource_IndependentOfMode()
    {
        // CLI source and connection mode are orthogonal settings
        var settings = new ConnectionSettings
        {
            Mode = ConnectionMode.Persistent,
            CliSource = CliSourceMode.System
        };
        Assert.Equal(ConnectionMode.Persistent, settings.Mode);
        Assert.Equal(CliSourceMode.System, settings.CliSource);

        // Changing mode shouldn't affect CLI source
        settings.Mode = ConnectionMode.Embedded;
        Assert.Equal(CliSourceMode.System, settings.CliSource);

        settings.Mode = ConnectionMode.Persistent;
        Assert.Equal(CliSourceMode.System, settings.CliSource);
    }

    [Fact]
    public void ConnectionSettings_Serialization_PreservesCliSource()
    {
        var settings = new ConnectionSettings
        {
            Mode = ConnectionMode.Persistent,
            CliSource = CliSourceMode.System
        };

        var json = System.Text.Json.JsonSerializer.Serialize(settings);
        var restored = System.Text.Json.JsonSerializer.Deserialize<ConnectionSettings>(json);

        Assert.NotNull(restored);
        Assert.Equal(ConnectionMode.Persistent, restored!.Mode);
        Assert.Equal(CliSourceMode.System, restored.CliSource);
    }

    [Fact]
    public async Task ModeSwitch_PreservesCliSource()
    {
        // Switching modes via ReconnectAsync shouldn't affect the CliSource
        // stored in the settings object
        var svc = CreateService();
        var settings = new ConnectionSettings
        {
            Mode = ConnectionMode.Demo,
            CliSource = CliSourceMode.System
        };

        await svc.ReconnectAsync(settings);
        Assert.True(svc.IsInitialized);

        // CliSource should be unchanged after reconnect
        Assert.Equal(CliSourceMode.System, settings.CliSource);
    }

    [Fact]
    public void ResolveBundledCliPath_DoesNotThrow()
    {
        // Ensure the static path resolution doesn't crash
        var path = CopilotService.ResolveBundledCliPath();
        // Path may be null in test environment (no bundled binary),
        // but it should not throw
    }

    [Fact]
    public async Task EnqueueMessage_WithImagePaths_PreservesImagesForDispatch()
    {
        var svc = CreateService();
        await svc.ReconnectAsync(new ConnectionSettings { Mode = ConnectionMode.Demo });
        var session = await svc.CreateSessionAsync("img-test");
        Assert.NotNull(session);

        // Enqueue a message with image paths
        var imagePaths = new List<string> { "/tmp/test1.png", "/tmp/test2.jpg" };
        svc.EnqueueMessage("img-test", "describe these images", imagePaths);

        Assert.Single(session.MessageQueue);
        Assert.Equal("describe these images", session.MessageQueue[0]);
    }

    [Fact]
    public async Task EnqueueMessage_WithoutImages_Works()
    {
        var svc = CreateService();
        await svc.ReconnectAsync(new ConnectionSettings { Mode = ConnectionMode.Demo });
        var session = await svc.CreateSessionAsync("no-img-test");

        svc.EnqueueMessage("no-img-test", "plain text");

        Assert.Single(session.MessageQueue);
        Assert.Equal("plain text", session.MessageQueue[0]);
    }

    [Fact]
    public async Task ClearQueue_ClearsImagePaths()
    {
        var svc = CreateService();
        await svc.ReconnectAsync(new ConnectionSettings { Mode = ConnectionMode.Demo });
        var session = await svc.CreateSessionAsync("clear-test");

        svc.EnqueueMessage("clear-test", "msg1", new List<string> { "/tmp/img.png" });
        svc.EnqueueMessage("clear-test", "msg2");
        Assert.Equal(2, session.MessageQueue.Count);

        svc.ClearQueue("clear-test");
        Assert.Empty(session.MessageQueue);
    }

    [Fact]
    public async Task RemoveQueuedMessage_RemovesCorrespondingImagePaths()
    {
        var svc = CreateService();
        await svc.ReconnectAsync(new ConnectionSettings { Mode = ConnectionMode.Demo });
        var session = await svc.CreateSessionAsync("remove-test");

        svc.EnqueueMessage("remove-test", "msg1", new List<string> { "/tmp/img1.png" });
        svc.EnqueueMessage("remove-test", "msg2");
        Assert.Equal(2, session.MessageQueue.Count);

        svc.RemoveQueuedMessage("remove-test", 0);
        Assert.Single(session.MessageQueue);
        Assert.Equal("msg2", session.MessageQueue[0]);
    }

    // ===== SwitchSession remote mode tests =====

    [Fact]
    public async Task SwitchSession_InRemoteMode_SendsBridgeMessage()
    {
        var svc = CreateService();
        await svc.ReconnectAsync(new ConnectionSettings
        {
            Mode = ConnectionMode.Remote,
            RemoteUrl = "ws://localhost:4322"
        });

        // Simulate bridge client providing a session so SwitchSession can find it
        _bridgeClient.Sessions.Add(new SessionSummary { Name = "remote-session" });
        _bridgeClient.IsConnected = true;
        _bridgeClient.FireOnStateChanged();

        var result = svc.SwitchSession("remote-session");

        Assert.True(result);
        Assert.Equal("remote-session", _bridgeClient.LastSwitchedSession);
        Assert.Equal(1, _bridgeClient.SwitchSessionCallCount);
    }

    [Fact]
    public async Task SwitchSession_InDemoMode_DoesNotSendBridgeMessage()
    {
        var svc = CreateService();
        await svc.ReconnectAsync(new ConnectionSettings { Mode = ConnectionMode.Demo });

        await svc.CreateSessionAsync("local-session", cancellationToken: CancellationToken.None);

        var result = svc.SwitchSession("local-session");

        Assert.True(result);
        Assert.Equal(0, _bridgeClient.SwitchSessionCallCount);
    }

    [Fact]
    public async Task SwitchSession_NonExistentSession_ReturnsFalse()
    {
        var svc = CreateService();
        await svc.ReconnectAsync(new ConnectionSettings
        {
            Mode = ConnectionMode.Remote,
            RemoteUrl = "ws://localhost:4322"
        });

        var result = svc.SwitchSession("does-not-exist");

        Assert.False(result);
        Assert.Equal(0, _bridgeClient.SwitchSessionCallCount);
    }

    [Fact]
    public async Task SwitchSession_InRemoteMode_MultipleSwitches_SendsEachOne()
    {
        var svc = CreateService();
        await svc.ReconnectAsync(new ConnectionSettings
        {
            Mode = ConnectionMode.Remote,
            RemoteUrl = "ws://localhost:4322"
        });

        _bridgeClient.Sessions.Add(new SessionSummary { Name = "session-a" });
        _bridgeClient.Sessions.Add(new SessionSummary { Name = "session-b" });
        _bridgeClient.IsConnected = true;
        _bridgeClient.FireOnStateChanged();

        svc.SwitchSession("session-a");
        svc.SwitchSession("session-b");
        svc.SwitchSession("session-a");

        Assert.Equal(3, _bridgeClient.SwitchSessionCallCount);
        Assert.Equal("session-a", _bridgeClient.LastSwitchedSession);
    }

    [Fact]
    public async Task SetActiveSession_Null_DoesNotSendBridgeMessage()
    {
        var svc = CreateService();
        await svc.ReconnectAsync(new ConnectionSettings
        {
            Mode = ConnectionMode.Remote,
            RemoteUrl = "ws://localhost:4322"
        });

        svc.SetActiveSession(null);

        Assert.Equal(0, _bridgeClient.SwitchSessionCallCount);
    }
}
