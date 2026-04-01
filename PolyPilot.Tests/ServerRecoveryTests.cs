using Microsoft.Extensions.DependencyInjection;
using PolyPilot.Models;
using PolyPilot.Services;

namespace PolyPilot.Tests;

/// <summary>
/// Tests for automatic persistent server recovery when the Copilot CLI's auth token
/// expires or the server enters a bad state. Covers:
/// - IsAuthError detection helper
/// - Service-level consecutive watchdog timeout tracking
/// - TryRecoverPersistentServerAsync server restart logic
/// - Counter reset on successful CompleteResponse
/// Regression tests for: all sessions get stuck due to server-wide auth failure.
/// </summary>
public class ServerRecoveryTests
{
    private readonly StubChatDatabase _chatDb = new();
    private readonly StubServerManager _serverManager = new();
    private readonly StubWsBridgeClient _bridgeClient = new();
    private readonly StubDemoService _demoService = new();
    private readonly RepoManager _repoManager = new();
    private readonly IServiceProvider _serviceProvider;

    public ServerRecoveryTests()
    {
        var services = new ServiceCollection();
        _serviceProvider = services.BuildServiceProvider();
    }

    private CopilotService CreateService() =>
        new CopilotService(_chatDb, _serverManager, _bridgeClient, _repoManager, _serviceProvider, _demoService);

    // ===== IsAuthError detection tests =====

    [Theory]
    [InlineData("Unauthorized")]
    [InlineData("Not authenticated")]
    [InlineData("Authentication failed")]
    [InlineData("Authentication required")]
    [InlineData("Token expired")]
    [InlineData("Token is invalid")]
    [InlineData("Invalid token")]
    [InlineData("Auth token has expired")]
    [InlineData("403 Forbidden")]
    [InlineData("HTTP 401 Unauthorized")]
    [InlineData("Not authorized to access this resource")]
    [InlineData("Bad credentials")]
    [InlineData("Login required")]
    public void IsAuthError_DetectsAuthMessages(string message)
    {
        var ex = new Exception(message);
        Assert.True(CopilotService.IsAuthError(ex));
    }

    [Theory]
    [InlineData("Session not found")]
    [InlineData("Connection refused")]
    [InlineData("Invalid model name")]
    [InlineData("Service not initialized")]
    [InlineData("Session appears stuck")]
    [InlineData("Timeout expired")]
    public void IsAuthError_ReturnsFalseForNonAuthErrors(string message)
    {
        var ex = new Exception(message);
        Assert.False(CopilotService.IsAuthError(ex));
    }

    [Fact]
    public void IsAuthError_DetectsInnerAuthException()
    {
        var inner = new Exception("Token expired");
        var outer = new InvalidOperationException("Operation failed", inner);
        Assert.True(CopilotService.IsAuthError(outer));
    }

    [Fact]
    public void IsAuthError_DetectsAggregateAuthException()
    {
        var inner = new Exception("Not authenticated");
        var agg = new AggregateException("Multiple errors", inner);
        Assert.True(CopilotService.IsAuthError(agg));
    }

    [Fact]
    public void IsAuthError_ReturnsFalseForEmptyAggregate()
    {
        var agg = new AggregateException("No inners");
        Assert.False(CopilotService.IsAuthError(agg));
    }

    // ===== IsAuthError string overload =====

    [Theory]
    [InlineData("Unauthorized")]
    [InlineData("Not authenticated")]
    [InlineData("not created with authentication info")]
    [InlineData("Token expired")]
    [InlineData("HTTP 401")]
    public void IsAuthError_StringOverload_DetectsAuthMessages(string message)
    {
        Assert.True(CopilotService.IsAuthError(message));
    }

    [Theory]
    [InlineData("Session not found")]
    [InlineData("Connection refused")]
    [InlineData("")]
    public void IsAuthError_StringOverload_ReturnsFalseForNonAuth(string message)
    {
        Assert.False(CopilotService.IsAuthError(message));
    }

    // ===== GetLoginCommand =====

    [Fact]
    public void GetLoginCommand_ReturnsFallback_WhenNoSettings()
    {
        var svc = CreateService();
        var cmd = svc.GetLoginCommand();
        // Without settings or resolved path, returns the generic fallback
        Assert.Contains("login", cmd);
    }

    // ===== ClearAuthNotice =====

    [Fact]
    public async Task ClearAuthNotice_ClearsNoticeAndStopsPolling()
    {
        var svc = CreateService();
        await svc.ReconnectAsync(new ConnectionSettings { Mode = ConnectionMode.Demo });
        // ClearAuthNotice should not throw even when no notice is set
        svc.ClearAuthNotice();
        Assert.Null(svc.AuthNotice);
    }

    // ===== ReauthenticateAsync =====

    [Fact]
    public async Task ReauthenticateAsync_NonPersistentMode_SetsFailureNotice()
    {
        var svc = CreateService();
        // Initialize in Demo mode — TryRecoverPersistentServerAsync returns false
        await svc.ReconnectAsync(new ConnectionSettings { Mode = ConnectionMode.Demo });
        await svc.ReauthenticateAsync();
        // Should set a failure notice since recovery isn't available in demo mode
        Assert.NotNull(svc.AuthNotice);
        Assert.Contains("restart failed", svc.AuthNotice!, StringComparison.OrdinalIgnoreCase);
    }

    // ===== ResolveGitHubTokenFromEnv =====

    private static readonly string[] TokenEnvVars = { "COPILOT_GITHUB_TOKEN", "GH_TOKEN", "GITHUB_TOKEN" };

    [Fact]
    public void ResolveGitHubTokenFromEnv_ReturnsNull_WhenNoEnvVarsSet()
    {
        // Save and clear all token env vars to isolate the test
        var saved = TokenEnvVars.Select(v => (v, Environment.GetEnvironmentVariable(v))).ToArray();
        try
        {
            foreach (var v in TokenEnvVars)
                Environment.SetEnvironmentVariable(v, null);
            Assert.Null(CopilotService.ResolveGitHubTokenFromEnv());
        }
        finally
        {
            foreach (var (v, val) in saved)
                Environment.SetEnvironmentVariable(v, val);
        }
    }

    [Fact]
    public void ResolveGitHubTokenFromEnv_ReturnsToken_WhenEnvVarSet()
    {
        var saved = Environment.GetEnvironmentVariable("COPILOT_GITHUB_TOKEN");
        try
        {
            Environment.SetEnvironmentVariable("COPILOT_GITHUB_TOKEN", "test-token-abc");
            Assert.Equal("test-token-abc", CopilotService.ResolveGitHubTokenFromEnv());
        }
        finally
        {
            Environment.SetEnvironmentVariable("COPILOT_GITHUB_TOKEN", saved);
        }
    }

    [Fact]
    public void ResolveGitHubTokenFromEnv_RespectsPrecedence()
    {
        // COPILOT_GITHUB_TOKEN should win over GH_TOKEN
        var saved = TokenEnvVars.Select(v => (v, Environment.GetEnvironmentVariable(v))).ToArray();
        try
        {
            foreach (var v in TokenEnvVars)
                Environment.SetEnvironmentVariable(v, null);
            Environment.SetEnvironmentVariable("GH_TOKEN", "gh-token");
            Environment.SetEnvironmentVariable("COPILOT_GITHUB_TOKEN", "copilot-token");
            Assert.Equal("copilot-token", CopilotService.ResolveGitHubTokenFromEnv());
        }
        finally
        {
            foreach (var (v, val) in saved)
                Environment.SetEnvironmentVariable(v, val);
        }
    }

    [Fact]
    public void ServerManager_AcceptsGitHubToken_InStartServerAsync()
    {
        // Verify the stub properly records the token parameter
        var mgr = new StubServerManager();
        mgr.StartServerResult = true;
        mgr.StartServerAsync(4321, "test-token-123").GetAwaiter().GetResult();
        Assert.Equal("test-token-123", mgr.LastGitHubToken);
    }

    [Fact]
    public void ServerManager_AcceptsNullGitHubToken_InStartServerAsync()
    {
        var mgr = new StubServerManager();
        mgr.StartServerResult = true;
        mgr.StartServerAsync(4321).GetAwaiter().GetResult();
        Assert.Null(mgr.LastGitHubToken);
    }

    // ===== IsConnectionError now catches auth errors =====

    [Theory]
    [InlineData("Unauthorized")]
    [InlineData("Token expired")]
    [InlineData("Authentication failed")]
    public void IsConnectionError_NowDetectsAuthErrors(string message)
    {
        var ex = new Exception(message);
        Assert.True(CopilotService.IsConnectionError(ex));
    }

    // ===== WatchdogServerRecoveryThreshold constant =====

    [Fact]
    public void WatchdogServerRecoveryThreshold_IsReasonable()
    {
        // Must be at least 2 to avoid false positives from single-session issues.
        // Must be small enough to detect server-wide failures quickly.
        Assert.InRange(CopilotService.WatchdogServerRecoveryThreshold, 2, 5);
    }

    // ===== TryRecoverPersistentServerAsync tests =====

    [Fact]
    public async Task TryRecoverPersistentServer_NotPersistentMode_ReturnsFalse()
    {
        var svc = CreateService();

        // Initialize in Demo mode (not Persistent)
        await svc.ReconnectAsync(new ConnectionSettings { Mode = ConnectionMode.Demo });

        var result = await svc.TryRecoverPersistentServerAsync();
        Assert.False(result);
    }

    [Fact]
    public async Task TryRecoverPersistentServer_ServerStartFails_ReturnsFalse()
    {
        var svc = CreateService();

        // Simulate being in Persistent mode
        _serverManager.IsServerRunning = false;
        _serverManager.StartServerResult = false;
        await svc.ReconnectAsync(new ConnectionSettings
        {
            Mode = ConnectionMode.Persistent,
            Host = "localhost",
            Port = 19999
        });

        // Try recovery — server can't start
        _serverManager.StartServerResult = false;
        var result = await svc.TryRecoverPersistentServerAsync();
        Assert.False(result);

        // Should set FallbackNotice for user
        Assert.NotNull(svc.FallbackNotice);
        Assert.Contains("recovery failed", svc.FallbackNotice, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TryRecoverPersistentServer_StopsOldServer()
    {
        var svc = CreateService();

        // Simulate being in Persistent mode with a running server
        _serverManager.IsServerRunning = true;
        _serverManager.StartServerResult = false;
        await svc.ReconnectAsync(new ConnectionSettings
        {
            Mode = ConnectionMode.Persistent,
            Host = "localhost",
            Port = 19999
        });

        // Server was initially "running" — after recovery attempt it should be stopped
        await svc.TryRecoverPersistentServerAsync();

        // After recovery attempt, old server should have been stopped
        Assert.False(_serverManager.IsServerRunning);
    }

    // ===== Watchdog timeout counter behavior (unit-level) =====

    [Fact]
    public void ConsecutiveWatchdogTimeouts_TrackedAcrossSessions()
    {
        // Verify the counter field exists and is accessible via reflection
        var svc = CreateService();
        var field = typeof(CopilotService).GetField("_consecutiveWatchdogTimeouts",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        Assert.NotNull(field);

        // Initially zero
        Assert.Equal(0, (int)field.GetValue(svc)!);
    }

    [Fact]
    public async Task CompleteResponse_ResetsServiceLevelTimeoutCounter()
    {
        // When a session completes successfully, the service-level counter should reset.
        var svc = CreateService();
        await svc.ReconnectAsync(new ConnectionSettings { Mode = ConnectionMode.Demo });

        // Simulate some watchdog timeouts via reflection
        var counterField = typeof(CopilotService).GetField("_consecutiveWatchdogTimeouts",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        Assert.NotNull(counterField);
        counterField.SetValue(svc, 5);
        Assert.Equal(5, (int)counterField.GetValue(svc)!);

        // Create a session and put it in a processing state
        var session = await svc.CreateSessionAsync("test-reset", "gpt-4o", cancellationToken: CancellationToken.None);
        session.IsProcessing = true;

        // Get the internal SessionState via reflection and invoke CompleteResponse directly
        var sessionsField = typeof(CopilotService).GetField("_sessions",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        var sessions = sessionsField.GetValue(svc)!;
        var tryGetMethod = sessions.GetType().GetMethod("TryGetValue")!;
        var args = new object?[] { "test-reset", null };
        tryGetMethod.Invoke(sessions, args);
        var state = args[1]!;

        // Set up a ResponseCompletion TCS (required by CompleteResponse)
        var tcsProperty = state.GetType().GetProperty("ResponseCompletion",
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        tcsProperty.SetValue(state, new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously));

        // Invoke CompleteResponse via reflection (same as ChatExperienceSafetyTests pattern)
        var completeMethod = typeof(CopilotService).GetMethod("CompleteResponse",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        completeMethod.Invoke(svc, new object?[] { state, null });

        // Counter should be reset to 0 after successful completion
        var counterAfter = (int)counterField.GetValue(svc)!;
        Assert.Equal(0, counterAfter);
    }

    [Fact]
    public async Task TryRecoverPersistentServer_ConcurrentCallerAfterSuccessfulRecovery_ReturnsTrue()
    {
        // Verifies that a second concurrent caller that "loses" the semaphore but arrives after
        // recovery completed recently returns true (not false), preventing false-permanent errors
        // for multi-session startups when a token has expired.
        var svc = CreateService();
        _serverManager.IsServerRunning = false;
        _serverManager.StartServerResult = true;
        await svc.ReconnectAsync(new ConnectionSettings
        {
            Mode = ConnectionMode.Persistent,
            Host = "localhost",
            Port = 19999
        });

        // Grab the semaphore and timestamp fields via reflection
        var lockField = typeof(CopilotService).GetField("_recoveryLock",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        var tsField = typeof(CopilotService).GetField("_lastRecoveryCompletedAt",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;

        var semaphore = (SemaphoreSlim)lockField.GetValue(svc)!;

        // Simulate: recovery just completed (timestamp fresh), but lock is still held (in-flight)
        tsField.SetValue(svc, DateTime.UtcNow);
        await semaphore.WaitAsync(); // hold the lock

        try
        {
            // This call should see WaitAsync(0) == false AND timestamp is recent → return true
            var result = await svc.TryRecoverPersistentServerAsync();
            Assert.True(result);

            // Server must NOT have been stopped (the real recovery path was skipped)
            Assert.Equal(0, _serverManager.StopServerCallCount);
        }
        finally
        {
            semaphore.Release();
        }
    }

    [Fact]
    public async Task TryRecoverPersistentServer_ConcurrentCallerLongAfterRecovery_ReturnsFalse()
    {
        // Verifies that a second caller arriving > 30s after the last recovery returns false
        // (i.e., the timestamp guard only applies within the 30s window).
        var svc = CreateService();
        _serverManager.IsServerRunning = false;
        _serverManager.StartServerResult = true;
        await svc.ReconnectAsync(new ConnectionSettings
        {
            Mode = ConnectionMode.Persistent,
            Host = "localhost",
            Port = 19999
        });

        var lockField = typeof(CopilotService).GetField("_recoveryLock",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        var tsField = typeof(CopilotService).GetField("_lastRecoveryCompletedAt",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;

        var semaphore = (SemaphoreSlim)lockField.GetValue(svc)!;

        // Simulate: recovery last happened 60s ago, lock is held (another recovery in progress)
        tsField.SetValue(svc, DateTime.UtcNow.AddSeconds(-60));
        await semaphore.WaitAsync(); // hold the lock

        try
        {
            // This call should see WaitAsync(0) == false AND timestamp is stale → return false
            var result = await svc.TryRecoverPersistentServerAsync();
            Assert.False(result);
        }
        finally
        {
            semaphore.Release();
        }
    }

    [Fact]
    public async Task TryRecoverPersistentServer_InFlightConcurrentCaller_ReturnsFalseImmediately()
    {
        // Documents that a concurrent caller arriving WHILE recovery is in-progress (timestamp still
        // MinValue) returns false from TryRecoverPersistentServerAsync. The EnsureSessionConnectedAsync
        // caller handles this by waiting on _recoveryLock with a timeout before retrying.
        var svc = CreateService();
        _serverManager.IsServerRunning = false;
        _serverManager.StartServerResult = true;
        await svc.ReconnectAsync(new ConnectionSettings
        {
            Mode = ConnectionMode.Persistent,
            Host = "localhost",
            Port = 19999
        });

        var lockField = typeof(CopilotService).GetField("_recoveryLock",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        var semaphore = (SemaphoreSlim)lockField.GetValue(svc)!;

        // Simulate: recovery is in-flight (lock held, timestamp not set yet)
        await semaphore.WaitAsync();

        try
        {
            // Concurrent caller sees lock held and timestamp = MinValue → returns false immediately
            var result = await svc.TryRecoverPersistentServerAsync();
            Assert.False(result);
        }
        finally
        {
            semaphore.Release();
        }
    }
}
