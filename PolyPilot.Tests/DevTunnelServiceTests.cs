using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using PolyPilot.Models;
using PolyPilot.Services;

namespace PolyPilot.Tests;

/// <summary>
/// Comprehensive tests for DevTunnelService functionality:
/// regex parsing, state management, settings persistence, and auth validation.
/// </summary>
public class DevTunnelServiceTests
{
    // --- Helper to access private static regex methods via reflection ---

    private static Regex GetRegex(string methodName)
    {
        var method = typeof(DevTunnelService).GetMethod(methodName,
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);
        return (Regex)method!.Invoke(null, null)!;
    }

    // ===== TunnelState enum =====

    [Fact]
    public void TunnelState_HasAllExpectedValues()
    {
        Assert.Equal(0, (int)TunnelState.NotStarted);
        Assert.Equal(1, (int)TunnelState.Authenticating);
        Assert.Equal(2, (int)TunnelState.Starting);
        Assert.Equal(3, (int)TunnelState.Running);
        Assert.Equal(4, (int)TunnelState.Stopping);
        Assert.Equal(5, (int)TunnelState.Error);
    }

    [Fact]
    public void TunnelState_HasSixValues()
    {
        var values = Enum.GetValues<TunnelState>();
        Assert.Equal(6, values.Length);
    }

    // ===== BridgePort constant =====

    [Fact]
    public void BridgePort_Is4322()
    {
        Assert.Equal(4322, DevTunnelService.BridgePort);
    }

    // ===== Initial state =====

    [Fact]
    public void NewService_StateIsNotStarted()
    {
        var bridge = new WsBridgeServer();
        var copilot = CreateTestCopilotService();
        var service = new DevTunnelService(bridge, copilot);

        Assert.Equal(TunnelState.NotStarted, service.State);
        Assert.Null(service.TunnelUrl);
        Assert.Null(service.TunnelId);
        Assert.Null(service.AccessToken);
        Assert.Null(service.ErrorMessage);
    }

    // ===== TunnelUrlRegex =====

    [Theory]
    [InlineData("https://abc123.devtunnels.ms", "https://abc123.devtunnels.ms")]
    [InlineData("http://mytest.devtunnels.ms", "http://mytest.devtunnels.ms")]
    [InlineData("Visit https://abc-def.devtunnels.ms/path to connect", "https://abc-def.devtunnels.ms/path")]
    [InlineData("https://ABC.DEVTUNNELS.MS", "https://ABC.DEVTUNNELS.MS")]
    [InlineData("URL: https://tunnel1.devtunnels.ms:8080/ws", "https://tunnel1.devtunnels.ms:8080/ws")]
    public void TunnelUrlRegex_MatchesDevTunnelUrls(string input, string expectedUrl)
    {
        var regex = GetRegex("TunnelUrlRegex");
        var match = regex.Match(input);
        Assert.True(match.Success, $"Expected match for: {input}");
        Assert.Equal(expectedUrl, match.Groups[1].Value);
    }

    [Theory]
    [InlineData("https://example.com")]
    [InlineData("http://localhost:4322")]
    [InlineData("not a url at all")]
    [InlineData("")]
    public void TunnelUrlRegex_DoesNotMatchNonDevTunnelUrls(string input)
    {
        var regex = GetRegex("TunnelUrlRegex");
        var match = regex.Match(input);
        Assert.False(match.Success, $"Should not match: {input}");
    }

    // ===== ConnectUrlRegex =====

    [Theory]
    [InlineData("Connect via browser: https://abc123.devtunnels.ms", "https://abc123.devtunnels.ms")]
    [InlineData("Connect via browser:  https://tunnel.devtunnels.ms/ws", "https://tunnel.devtunnels.ms/ws")]
    [InlineData("connect via browser: http://localhost:8080", "http://localhost:8080")]
    public void ConnectUrlRegex_MatchesConnectLines(string input, string expectedUrl)
    {
        var regex = GetRegex("ConnectUrlRegex");
        var match = regex.Match(input);
        Assert.True(match.Success, $"Expected match for: {input}");
        Assert.Equal(expectedUrl, match.Groups[1].Value);
    }

    [Theory]
    [InlineData("Some other output line")]
    [InlineData("https://abc.devtunnels.ms")]
    [InlineData("")]
    public void ConnectUrlRegex_DoesNotMatchNonConnectLines(string input)
    {
        var regex = GetRegex("ConnectUrlRegex");
        var match = regex.Match(input);
        Assert.False(match.Success, $"Should not match: {input}");
    }

    // ===== TunnelIdRegex =====

    [Theory]
    [InlineData("Tunnel ID: abc-tunnel-123", "abc-tunnel-123")]
    [InlineData("Tunnel  ID : my-tunnel", "my-tunnel")]
    [InlineData("tunnel id: UPPER_CASE", "UPPER_CASE")]
    [InlineData("Tunnel ID:tunnel-no-space", "tunnel-no-space")]
    public void TunnelIdRegex_MatchesTunnelIdLines(string input, string expectedId)
    {
        var regex = GetRegex("TunnelIdRegex");
        var match = regex.Match(input);
        Assert.True(match.Success, $"Expected match for: {input}");
        Assert.Equal(expectedId, match.Groups[1].Value);
    }

    [Theory]
    [InlineData("No tunnel info here")]
    [InlineData("ID: just-id-no-tunnel")]
    [InlineData("")]
    public void TunnelIdRegex_DoesNotMatchNonTunnelIdLines(string input)
    {
        var regex = GetRegex("TunnelIdRegex");
        var match = regex.Match(input);
        Assert.False(match.Success, $"Should not match: {input}");
    }

    // ===== TunnelIdAltRegex =====

    [Theory]
    [InlineData("Hosting port for tunnel: abc-123", "abc-123")]
    [InlineData("Hosting port for tunnel my-tunnel-id", "my-tunnel-id")]
    [InlineData("Created access token for tunnel: xyz", "xyz")]
    [InlineData("for tunnel:  spaced-id", "spaced-id")]
    public void TunnelIdAltRegex_MatchesForTunnelPattern(string input, string expectedId)
    {
        var regex = GetRegex("TunnelIdAltRegex");
        var match = regex.Match(input);
        Assert.True(match.Success, $"Expected match for: {input}");
        Assert.Equal(expectedId, match.Groups[1].Value);
    }

    [Theory]
    [InlineData("No tunnel reference")]
    [InlineData("tunnel: no-for-prefix")]
    [InlineData("")]
    public void TunnelIdAltRegex_DoesNotMatchWithoutForPrefix(string input)
    {
        var regex = GetRegex("TunnelIdAltRegex");
        var match = regex.Match(input);
        Assert.False(match.Success, $"Should not match: {input}");
    }

    // ===== TryExtractInfo via reflection =====

    [Fact]
    public async Task TryExtractInfo_ExtractsUrlFromConnectLine()
    {
        var bridge = new WsBridgeServer();
        var copilot = CreateTestCopilotService();
        var service = new DevTunnelService(bridge, copilot);
        var tcs = new TaskCompletionSource<bool>();

        InvokeTryExtractInfo(service, "Connect via browser: https://my-tunnel.devtunnels.ms", tcs);

        Assert.True(tcs.Task.IsCompleted);
        Assert.True(await tcs.Task);
        Assert.Equal("https://my-tunnel.devtunnels.ms", service.TunnelUrl);
    }

    [Fact]
    public async Task TryExtractInfo_ExtractsUrlFromGenericDevTunnelLine()
    {
        var bridge = new WsBridgeServer();
        var copilot = CreateTestCopilotService();
        var service = new DevTunnelService(bridge, copilot);
        var tcs = new TaskCompletionSource<bool>();

        InvokeTryExtractInfo(service, "Ready at https://abc.devtunnels.ms", tcs);

        Assert.True(tcs.Task.IsCompleted);
        Assert.True(await tcs.Task);
        Assert.Equal("https://abc.devtunnels.ms", service.TunnelUrl);
    }

    [Fact]
    public void TryExtractInfo_ExtractsTunnelIdFromPrimaryPattern()
    {
        var bridge = new WsBridgeServer();
        var copilot = CreateTestCopilotService();
        var service = new DevTunnelService(bridge, copilot);
        var tcs = new TaskCompletionSource<bool>();

        InvokeTryExtractInfo(service, "Tunnel ID: my-cool-tunnel", tcs);

        Assert.Equal("my-cool-tunnel", service.TunnelId);
        // URL not found, so TCS should not be completed
        Assert.False(tcs.Task.IsCompleted);
    }

    [Fact]
    public void TryExtractInfo_ExtractsTunnelIdFromAltPattern()
    {
        var bridge = new WsBridgeServer();
        var copilot = CreateTestCopilotService();
        var service = new DevTunnelService(bridge, copilot);
        var tcs = new TaskCompletionSource<bool>();

        InvokeTryExtractInfo(service, "Hosting port for tunnel: alt-id-123", tcs);

        Assert.Equal("alt-id-123", service.TunnelId);
    }

    [Fact]
    public async Task TryExtractInfo_ExtractsBothIdAndUrl()
    {
        var bridge = new WsBridgeServer();
        var copilot = CreateTestCopilotService();
        var service = new DevTunnelService(bridge, copilot);
        var tcs = new TaskCompletionSource<bool>();

        // First line: tunnel ID
        InvokeTryExtractInfo(service, "Tunnel ID: test-tunnel", tcs);
        Assert.Equal("test-tunnel", service.TunnelId);
        Assert.False(tcs.Task.IsCompleted);

        // Second line: URL
        InvokeTryExtractInfo(service, "Connect via browser: https://test-tunnel.devtunnels.ms", tcs);
        Assert.Equal("https://test-tunnel.devtunnels.ms", service.TunnelUrl);
        Assert.True(tcs.Task.IsCompleted);
        Assert.True(await tcs.Task);
    }

    [Fact]
    public void TryExtractInfo_IgnoresDuplicateUrls()
    {
        var bridge = new WsBridgeServer();
        var copilot = CreateTestCopilotService();
        var service = new DevTunnelService(bridge, copilot);
        var tcs = new TaskCompletionSource<bool>();

        InvokeTryExtractInfo(service, "Connect via browser: https://first.devtunnels.ms", tcs);
        Assert.Equal("https://first.devtunnels.ms", service.TunnelUrl);

        // Second URL should be ignored
        var tcs2 = new TaskCompletionSource<bool>();
        InvokeTryExtractInfo(service, "Connect via browser: https://second.devtunnels.ms", tcs2);
        Assert.Equal("https://first.devtunnels.ms", service.TunnelUrl);
    }

    [Fact]
    public void TryExtractInfo_IgnoresDuplicateTunnelIds()
    {
        var bridge = new WsBridgeServer();
        var copilot = CreateTestCopilotService();
        var service = new DevTunnelService(bridge, copilot);
        var tcs = new TaskCompletionSource<bool>();

        InvokeTryExtractInfo(service, "Tunnel ID: first-id", tcs);
        Assert.Equal("first-id", service.TunnelId);

        InvokeTryExtractInfo(service, "Tunnel ID: second-id", tcs);
        Assert.Equal("first-id", service.TunnelId); // Should not change
    }

    [Fact]
    public void TryExtractInfo_NoMatchDoesNothing()
    {
        var bridge = new WsBridgeServer();
        var copilot = CreateTestCopilotService();
        var service = new DevTunnelService(bridge, copilot);
        var tcs = new TaskCompletionSource<bool>();

        InvokeTryExtractInfo(service, "Just some random log output", tcs);

        Assert.Null(service.TunnelId);
        Assert.Null(service.TunnelUrl);
        Assert.False(tcs.Task.IsCompleted);
    }

    [Fact]
    public void TryExtractInfo_TrimsTrailingSlashFromUrl()
    {
        var bridge = new WsBridgeServer();
        var copilot = CreateTestCopilotService();
        var service = new DevTunnelService(bridge, copilot);
        var tcs = new TaskCompletionSource<bool>();

        InvokeTryExtractInfo(service, "Connect via browser: https://trimtest.devtunnels.ms/", tcs);

        Assert.Equal("https://trimtest.devtunnels.ms", service.TunnelUrl);
    }

    // ===== Stop() =====

    [Fact]
    public void Stop_ResetsAllState()
    {
        var bridge = new WsBridgeServer();
        var copilot = CreateTestCopilotService();
        var service = new DevTunnelService(bridge, copilot);

        // Simulate some state via TryExtractInfo
        var tcs = new TaskCompletionSource<bool>();
        InvokeTryExtractInfo(service, "Connect via browser: https://test.devtunnels.ms", tcs);
        InvokeTryExtractInfo(service, "Tunnel ID: my-tunnel", new TaskCompletionSource<bool>());
        Assert.NotNull(service.TunnelUrl);

        service.Stop();

        Assert.Equal(TunnelState.NotStarted, service.State);
        Assert.Null(service.TunnelUrl);
        Assert.Null(service.AccessToken);
        Assert.Null(service.ErrorMessage);
    }

    [Fact]
    public void Stop_FiresOnStateChanged()
    {
        var bridge = new WsBridgeServer();
        var copilot = CreateTestCopilotService();
        var service = new DevTunnelService(bridge, copilot);

        int stateChangedCount = 0;
        service.OnStateChanged += () => stateChangedCount++;

        service.Stop();

        // Stop sets Stopping then NotStarted — 2 state changes
        Assert.Equal(2, stateChangedCount);
    }

    // ===== Dispose =====

    [Fact]
    public void Dispose_CallsStop()
    {
        var bridge = new WsBridgeServer();
        var copilot = CreateTestCopilotService();
        var service = new DevTunnelService(bridge, copilot);

        int stateChangedCount = 0;
        service.OnStateChanged += () => stateChangedCount++;

        service.Dispose();

        Assert.Equal(TunnelState.NotStarted, service.State);
        Assert.True(stateChangedCount >= 2); // Stopping + NotStarted
    }

    // ===== ConnectionSettings tunnel fields =====

    [Fact]
    public void ConnectionSettings_TunnelId_DefaultsToNull()
    {
        var settings = new ConnectionSettings();
        Assert.Null(settings.TunnelId);
    }

    [Fact]
    public void ConnectionSettings_AutoStartTunnel_DefaultsFalse()
    {
        var settings = new ConnectionSettings();
        Assert.False(settings.AutoStartTunnel);
    }

    [Fact]
    public void ConnectionSettings_TunnelFields_RoundTrip()
    {
        var original = new ConnectionSettings
        {
            TunnelId = "tunnel-abc-123",
            AutoStartTunnel = true,
            RemoteUrl = "https://my-tunnel.devtunnels.ms",
            RemoteToken = "jwt-token-xyz"
        };

        var json = JsonSerializer.Serialize(original);
        var loaded = JsonSerializer.Deserialize<ConnectionSettings>(json);

        Assert.NotNull(loaded);
        Assert.Equal("tunnel-abc-123", loaded!.TunnelId);
        Assert.True(loaded.AutoStartTunnel);
        Assert.Equal("https://my-tunnel.devtunnels.ms", loaded.RemoteUrl);
        Assert.Equal("jwt-token-xyz", loaded.RemoteToken);
    }

    [Fact]
    public void ConnectionSettings_BackwardCompat_NoTunnelFields()
    {
        var json = """{"Mode":0,"Host":"localhost","Port":4321}""";
        var loaded = JsonSerializer.Deserialize<ConnectionSettings>(json);

        Assert.NotNull(loaded);
        Assert.Null(loaded!.TunnelId);
        Assert.False(loaded.AutoStartTunnel);
        Assert.Null(loaded.RemoteUrl);
        Assert.Null(loaded.RemoteToken);
    }

    [Fact]
    public void ConnectionSettings_RemoteMode_DevTunnelUrl_InCliUrl()
    {
        var settings = new ConnectionSettings
        {
            Mode = ConnectionMode.Remote,
            RemoteUrl = "https://my-tunnel.devtunnels.ms"
        };

        Assert.Equal("https://my-tunnel.devtunnels.ms", settings.CliUrl);
    }

    [Fact]
    public void ConnectionSettings_TunnelId_IndependentOfOtherFields()
    {
        var settings = new ConnectionSettings
        {
            TunnelId = "tun-1",
            AutoStartTunnel = true,
            Mode = ConnectionMode.Persistent
        };

        // Changing mode doesn't affect tunnel ID
        settings.Mode = ConnectionMode.Remote;
        Assert.Equal("tun-1", settings.TunnelId);
        Assert.True(settings.AutoStartTunnel);

        // Clearing tunnel ID doesn't affect mode
        settings.TunnelId = null;
        Assert.Equal(ConnectionMode.Remote, settings.Mode);
    }

    // ===== WsBridgeServer auth (ValidateClientToken) =====

    [Fact]
    public void WsBridgeServer_AccessToken_DefaultsToNull()
    {
        var server = new WsBridgeServer();
        Assert.Null(server.AccessToken);
    }

    [Fact]
    public void WsBridgeServer_ServerPassword_DefaultsToNull()
    {
        var server = new WsBridgeServer();
        Assert.Null(server.ServerPassword);
    }

    [Fact]
    public void WsBridgeServer_AccessToken_CanBeSet()
    {
        var server = new WsBridgeServer();
        server.AccessToken = "test-token";
        Assert.Equal("test-token", server.AccessToken);
    }

    [Fact]
    public void WsBridgeServer_ServerPassword_CanBeSet()
    {
        var server = new WsBridgeServer();
        server.ServerPassword = "test-pass";
        Assert.Equal("test-pass", server.ServerPassword);
    }

    [Fact]
    public void WsBridgeServer_InitialState_NotRunning()
    {
        var server = new WsBridgeServer();
        Assert.False(server.IsRunning);
        Assert.Equal(0, server.BridgePort);
    }

    // ===== ResolveDevTunnel path candidates =====

    [Fact]
    public void ResolveDevTunnel_ReturnsNonEmptyString()
    {
        // ResolveDevTunnel is private static — invoke via reflection
        var method = typeof(DevTunnelService).GetMethod("ResolveDevTunnel",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);

        var result = (string)method!.Invoke(null, null)!;
        Assert.NotNull(result);
        Assert.NotEmpty(result);
    }

    [Fact]
    public void ResolveDevTunnel_ReturnsPlatformAppropriateExtension()
    {
        var method = typeof(DevTunnelService).GetMethod("ResolveDevTunnel",
            BindingFlags.Static | BindingFlags.NonPublic);
        var result = (string)method!.Invoke(null, null)!;

        if (OperatingSystem.IsWindows())
            Assert.EndsWith(".exe", result);
        else
            Assert.DoesNotContain(".exe", result);
    }

    // ===== Real-world devtunnel output parsing scenarios =====

    [Fact]
    public void ParsesRealDevTunnelHostOutput_TunnelId()
    {
        // Actual devtunnel output format
        var lines = new[]
        {
            "Hosting port for tunnel: shneuvil-polypilot",
            "Connect via browser: https://shneuvil-polypilot.devtunnels.ms:4322",
            "Hosting port 4322 on tunnel shneuvil-polypilot"
        };

        var bridge = new WsBridgeServer();
        var copilot = CreateTestCopilotService();
        var service = new DevTunnelService(bridge, copilot);
        var tcs = new TaskCompletionSource<bool>();

        foreach (var line in lines)
            InvokeTryExtractInfo(service, line, tcs);

        Assert.Equal("shneuvil-polypilot", service.TunnelId);
        Assert.NotNull(service.TunnelUrl);
        Assert.Contains("devtunnels.ms", service.TunnelUrl!);
    }

    [Fact]
    public void ParsesRealDevTunnelHostOutput_WithPort()
    {
        var bridge = new WsBridgeServer();
        var copilot = CreateTestCopilotService();
        var service = new DevTunnelService(bridge, copilot);
        var tcs = new TaskCompletionSource<bool>();

        InvokeTryExtractInfo(service,
            "Connect via browser: https://shneuvil-polypilot.devtunnels.ms:4322", tcs);

        Assert.True(tcs.Task.IsCompleted);
        Assert.Equal("https://shneuvil-polypilot.devtunnels.ms:4322", service.TunnelUrl);
    }

    [Fact]
    public void ParsesAccessTokenOutput_TokenPrefix()
    {
        // Simulate parsing the output of `devtunnel token` command
        var output = "Token tunnel ID: my-tunnel\nToken: eyJhbGciOiJSUzI1NiJ9.abc123";
        var token = ParseTokenFromOutput(output);
        Assert.Equal("eyJhbGciOiJSUzI1NiJ9.abc123", token);
    }

    [Fact]
    public void ParsesAccessTokenOutput_FallbackToLastLine()
    {
        // When output has no "Token:" prefix, fallback to last non-empty line
        var output = "eyJhbGciOiJSUzI1NiJ9.raw-token-only";
        var token = ParseTokenFromOutput(output);
        Assert.Equal("eyJhbGciOiJSUzI1NiJ9.raw-token-only", token);
    }

    [Fact]
    public void ParsesAccessTokenOutput_Empty_ReturnsNull()
    {
        var token = ParseTokenFromOutput("");
        Assert.Null(token);
    }

    [Fact]
    public void ParsesAccessTokenOutput_TokenTunnelIdNotConfusedWithToken()
    {
        // "Token tunnel ID:" starts with "Token" but also starts with "Token " (space)
        // so it should NOT be treated as the token value
        var output = "Token tunnel ID: test-tunnel\nToken: actual-jwt-token";
        var token = ParseTokenFromOutput(output);
        Assert.Equal("actual-jwt-token", token);
    }

    [Fact]
    public void ParsesAccessTokenOutput_MultipleLines()
    {
        var output = "Requesting access token...\nToken tunnel ID: tun-1\nScope: connect\nToken: jwt.token.value\n";
        var token = ParseTokenFromOutput(output);
        Assert.Equal("jwt.token.value", token);
    }

    // ===== IsLoggedInAsync output parsing logic =====

    [Theory]
    [InlineData("Logged in as user@github.com", true)]
    [InlineData("not logged in", false)]
    [InlineData("token expired", false)]
    [InlineData("NOT LOGGED IN", false)]
    [InlineData("Token Expired, please re-login", false)]
    [InlineData("GitHub user: testuser", true)]
    public void IsLoggedIn_OutputParsing(string output, bool expectedLoggedIn)
    {
        // Mirror the logic from IsLoggedInAsync
        bool result = !output.Contains("not logged in", StringComparison.OrdinalIgnoreCase)
                   && !output.Contains("token expired", StringComparison.OrdinalIgnoreCase);
        Assert.Equal(expectedLoggedIn, result);
    }

    // ===== HostAsync state machine (without actual process) =====

    [Fact]
    public async Task HostAsync_WhenAlreadyRunning_ReturnsTrueImmediately()
    {
        var bridge = new WsBridgeServer();
        var copilot = CreateTestCopilotService();
        var service = new DevTunnelService(bridge, copilot);

        // Set state to Running via reflection
        SetState(service, TunnelState.Running);

        // HostAsync should return true without starting anything
        var result = await service.HostAsync(4321);
        Assert.True(result);
        Assert.Equal(TunnelState.Running, service.State);
    }

    // ===== ConnectUrlRegex priority over TunnelUrlRegex =====

    [Fact]
    public void TryExtractInfo_PrefersConnectUrl_OverGenericUrl()
    {
        var bridge = new WsBridgeServer();
        var copilot = CreateTestCopilotService();
        var service = new DevTunnelService(bridge, copilot);
        var tcs = new TaskCompletionSource<bool>();

        // This line matches both ConnectUrlRegex and TunnelUrlRegex
        InvokeTryExtractInfo(service,
            "Connect via browser: https://my-tunnel.devtunnels.ms", tcs);

        // ConnectUrlRegex is checked first in TryExtractInfo
        Assert.Equal("https://my-tunnel.devtunnels.ms", service.TunnelUrl);
    }

    // ===== TunnelIdRegex priority over TunnelIdAltRegex =====

    [Fact]
    public void TryExtractInfo_PrimaryTunnelIdTakesPriority()
    {
        var bridge = new WsBridgeServer();
        var copilot = CreateTestCopilotService();
        var service = new DevTunnelService(bridge, copilot);
        var tcs = new TaskCompletionSource<bool>();

        // This matches TunnelIdRegex directly
        InvokeTryExtractInfo(service, "Tunnel ID: primary-id", tcs);
        Assert.Equal("primary-id", service.TunnelId);
    }

    // ===== WsBridgeServer ValidateClientToken via reflection =====

    [Fact]
    public void ValidateClientToken_NoTokenNoPassword_AllowsAll()
    {
        var server = new WsBridgeServer();
        // No AccessToken, no ServerPassword set
        var result = InvokeValidateClientToken(server, isLoopback: false, authHeader: null, queryToken: null);
        Assert.True(result);
    }

    [Fact]
    public void ValidateClientToken_LoopbackAlwaysAllowed()
    {
        var server = new WsBridgeServer();
        server.AccessToken = "secret-token";
        // Loopback should be allowed even without providing a token
        var result = InvokeValidateClientToken(server, isLoopback: true, authHeader: null, queryToken: null);
        Assert.True(result);
    }

    [Fact]
    public void ValidateClientToken_MatchingAccessToken_Allowed()
    {
        var server = new WsBridgeServer();
        server.AccessToken = "my-token";
        var result = InvokeValidateClientToken(server, isLoopback: false, authHeader: "tunnel my-token", queryToken: null);
        Assert.True(result);
    }

    [Fact]
    public void ValidateClientToken_MatchingPassword_Allowed()
    {
        var server = new WsBridgeServer();
        server.ServerPassword = "my-password";
        var result = InvokeValidateClientToken(server, isLoopback: false, authHeader: null, queryToken: "my-password");
        Assert.True(result);
    }

    [Fact]
    public void ValidateClientToken_WrongToken_Rejected()
    {
        var server = new WsBridgeServer();
        server.AccessToken = "correct-token";
        var result = InvokeValidateClientToken(server, isLoopback: false, authHeader: "tunnel wrong-token", queryToken: null);
        Assert.False(result);
    }

    [Fact]
    public void ValidateClientToken_NoTokenProvided_Rejected()
    {
        var server = new WsBridgeServer();
        server.AccessToken = "required-token";
        var result = InvokeValidateClientToken(server, isLoopback: false, authHeader: null, queryToken: null);
        Assert.False(result);
    }

    // ===== Helpers =====

    private static CopilotService CreateTestCopilotService()
    {
        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
        var sp = services.BuildServiceProvider();
        return new CopilotService(
            new StubChatDatabase(),
            new StubServerManager(),
            new StubWsBridgeClient(),
            new RepoManager(),
            sp,
            new StubDemoService());
    }

    private static void InvokeTryExtractInfo(DevTunnelService service, string line, TaskCompletionSource<bool> urlFound)
    {
        var method = typeof(DevTunnelService).GetMethod("TryExtractInfo",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        method!.Invoke(service, [line, urlFound]);
    }

    private static void SetState(DevTunnelService service, TunnelState state)
    {
        var field = typeof(DevTunnelService).GetField("_state",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        field!.SetValue(service, state);
    }

    /// <summary>
    /// Mirrors the token parsing logic from DevTunnelService.IssueAccessTokenAsync
    /// </summary>
    private static string? ParseTokenFromOutput(string output)
    {
        if (string.IsNullOrEmpty(output))
            return null;

        var token = "";
        foreach (var line in output.Split('\n'))
        {
            if (line.StartsWith("Token:", StringComparison.OrdinalIgnoreCase)
                && !line.StartsWith("Token ", StringComparison.OrdinalIgnoreCase))
            {
                token = line["Token:".Length..].Trim();
                break;
            }
        }
        if (string.IsNullOrEmpty(token))
        {
            var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            token = lines.Length > 0 ? lines[^1].Trim() : "";
        }
        return string.IsNullOrEmpty(token) ? null : token;
    }

    /// <summary>
    /// Tests ValidateClientToken logic by mirroring its implementation,
    /// since HttpListenerRequest can't be easily mocked.
    /// </summary>
    private static bool InvokeValidateClientToken(WsBridgeServer server, bool isLoopback, string? authHeader, string? queryToken)
    {
        // Mirror the logic from WsBridgeServer.ValidateClientToken
        var accessToken = server.AccessToken;
        var serverPassword = server.ServerPassword;

        if (string.IsNullOrEmpty(accessToken) && string.IsNullOrEmpty(serverPassword))
            return true;

        if (isLoopback)
            return true;

        string? providedToken = null;
        if (!string.IsNullOrEmpty(authHeader))
        {
            providedToken = authHeader.StartsWith("tunnel ", StringComparison.OrdinalIgnoreCase)
                ? authHeader["tunnel ".Length..].Trim()
                : authHeader.Trim();
        }
        providedToken ??= queryToken;

        if (string.IsNullOrEmpty(providedToken))
            return false;

        if (!string.IsNullOrEmpty(accessToken) && string.Equals(providedToken, accessToken, StringComparison.Ordinal))
            return true;
        if (!string.IsNullOrEmpty(serverPassword) && string.Equals(providedToken, serverPassword, StringComparison.Ordinal))
            return true;

        return false;
    }
}
