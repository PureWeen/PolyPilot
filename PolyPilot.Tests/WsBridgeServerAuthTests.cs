using System.Net;
using System.Net.WebSockets;
using System.Text.Json;
using PolyPilot.Models;
using PolyPilot.Services;

namespace PolyPilot.Tests;

[Collection("SocketIsolated")]
public class WsBridgeServerAuthTests
{
    [Fact]
    public void ConnectionSettings_ServerPassword_DefaultsToNull()
    {
        var settings = new ConnectionSettings();
        Assert.Null(settings.ServerPassword);
    }

    [Fact]
    public void ConnectionSettings_ServerPassword_Serializes()
    {
        var settings = new ConnectionSettings { ServerPassword = "my-secret-pw" };
        var json = JsonSerializer.Serialize(settings);
        var loaded = JsonSerializer.Deserialize<ConnectionSettings>(json);

        Assert.NotNull(loaded);
        Assert.Equal("my-secret-pw", loaded!.ServerPassword);
    }

    [Fact]
    public void ConnectionSettings_DirectSharingEnabled_DefaultsFalse()
    {
        var settings = new ConnectionSettings();
        Assert.False(settings.DirectSharingEnabled);
    }

    [Fact]
    public void ConnectionSettings_DirectSharingEnabled_Serializes()
    {
        var settings = new ConnectionSettings { DirectSharingEnabled = true };
        var json = JsonSerializer.Serialize(settings);
        var loaded = JsonSerializer.Deserialize<ConnectionSettings>(json);

        Assert.NotNull(loaded);
        Assert.True(loaded!.DirectSharingEnabled);
    }

    [Fact]
    public void BridgeAuthContract_PasswordNotInRemoteToken()
    {
        var settings = new ConnectionSettings
        {
            ServerPassword = "server-pw",
            RemoteToken = "remote-tok"
        };

        Assert.Equal("server-pw", settings.ServerPassword);
        Assert.Equal("remote-tok", settings.RemoteToken);

        // Changing one doesn't affect the other
        settings.ServerPassword = "changed";
        Assert.Equal("remote-tok", settings.RemoteToken);

        settings.RemoteToken = "changed-tok";
        Assert.Equal("changed", settings.ServerPassword);
    }

    [Fact]
    public void BridgeAuthContract_PasswordIndependentOfTunnelId()
    {
        var settings = new ConnectionSettings
        {
            ServerPassword = "pw123",
            TunnelId = "tunnel-xyz"
        };

        Assert.Equal("pw123", settings.ServerPassword);
        Assert.Equal("tunnel-xyz", settings.TunnelId);

        settings.TunnelId = "other-tunnel";
        Assert.Equal("pw123", settings.ServerPassword);

        settings.ServerPassword = null;
        Assert.Equal("other-tunnel", settings.TunnelId);
    }

    [Fact]
    public void ConnectionSettings_FullConfig_WithPassword_RoundTrips()
    {
        var original = new ConnectionSettings
        {
            Mode = ConnectionMode.Persistent,
            Host = "192.168.1.50",
            Port = 8080,
            ServerPassword = "secret",
            DirectSharingEnabled = true,
            RemoteUrl = "https://tunnel.example.com",
            RemoteToken = "tok-abc",
            TunnelId = "tun-001",
            AutoStartServer = true,
            AutoStartTunnel = true
        };

        var json = JsonSerializer.Serialize(original, new JsonSerializerOptions { WriteIndented = true });
        var loaded = JsonSerializer.Deserialize<ConnectionSettings>(json);

        Assert.NotNull(loaded);
        Assert.Equal(ConnectionMode.Persistent, loaded!.Mode);
        Assert.Equal("192.168.1.50", loaded.Host);
        Assert.Equal(8080, loaded.Port);
        Assert.Equal("secret", loaded.ServerPassword);
        Assert.True(loaded.DirectSharingEnabled);
        Assert.Equal("https://tunnel.example.com", loaded.RemoteUrl);
        Assert.Equal("tok-abc", loaded.RemoteToken);
        Assert.Equal("tun-001", loaded.TunnelId);
        Assert.True(loaded.AutoStartServer);
        Assert.True(loaded.AutoStartTunnel);
    }

    [Fact]
    public void ConnectionSettings_BackwardCompat_NoPassword()
    {
        // JSON from an older version that doesn't have ServerPassword or DirectSharingEnabled
        var json = """
        {
            "Mode": 1,
            "Host": "localhost",
            "Port": 4321,
            "AutoStartServer": false,
            "RemoteUrl": null,
            "RemoteToken": null,
            "TunnelId": null,
            "AutoStartTunnel": false
        }
        """;

        var loaded = JsonSerializer.Deserialize<ConnectionSettings>(json);

        Assert.NotNull(loaded);
        Assert.Null(loaded!.ServerPassword);
        Assert.False(loaded.DirectSharingEnabled);
        Assert.Equal(ConnectionMode.Persistent, loaded.Mode);
        Assert.Equal("localhost", loaded.Host);
        Assert.Equal(4321, loaded.Port);
    }

    // --- LAN probe and auth tests (bug fix: QR code 401 on Android) ---

    [Fact]
    public async Task ProbeLanAsync_ReturnsFalse_WhenServerReturns401()
    {
        // Simulate a server that rejects unauthenticated requests (the fix)
        var port = GetFreePort();
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://localhost:{port}/");
        listener.Start();

        var serverTask = Task.Run(async () =>
        {
            var ctx = await listener.GetContextAsync();
            ctx.Response.StatusCode = 401;
            ctx.Response.Close();
        });

        var result = await WsBridgeClient.ProbeLanAsync($"ws://localhost:{port}", "wrong-token", CancellationToken.None);

        Assert.False(result);
        await serverTask;
    }

    [Fact]
    public async Task ProbeLanAsync_ReturnsTrue_WhenServerReturns200()
    {
        var port = GetFreePort();
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://localhost:{port}/");
        listener.Start();

        var serverTask = Task.Run(async () =>
        {
            var ctx = await listener.GetContextAsync();
            ctx.Response.StatusCode = 200;
            ctx.Response.Close();
        });

        var result = await WsBridgeClient.ProbeLanAsync($"ws://localhost:{port}", null, CancellationToken.None);

        Assert.True(result);
        await serverTask;
    }

    [Fact]
    public async Task ProbeLanAsync_SendsTokenInQueryString()
    {
        var port = GetFreePort();
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://localhost:{port}/");
        listener.Start();

        var seenToken = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var serverTask = Task.Run(async () =>
        {
            var ctx = await listener.GetContextAsync();
            seenToken.TrySetResult(ctx.Request.QueryString["token"]);
            ctx.Response.StatusCode = 200;
            ctx.Response.Close();
        });

        var result = await WsBridgeClient.ProbeLanAsync($"ws://localhost:{port}", "query-token", CancellationToken.None);

        Assert.True(result);
        Assert.Equal("query-token", await seenToken.Task);
        await serverTask;
    }

    [Fact]
    public async Task ProbeLanAsync_ReturnsFalse_WhenServerUnreachable()
    {
        // Port 19998 should not have anything listening
        var result = await WsBridgeClient.ProbeLanAsync("ws://localhost:19998", null, CancellationToken.None);
        Assert.False(result);
    }

    [Fact]
    public void QrPayload_ExcludesLanUrl_WhenNoServerPassword()
    {
        // Reproduces the bug scenario: DevTunnel running, no ServerPassword configured.
        // Before fix: QR included lanUrl without lanToken → 401 on Android.
        // After fix: QR should not include lanUrl when ServerPassword is empty.
        var settings = new ConnectionSettings
        {
            ServerPassword = null,
            RemoteUrl = "https://tunnel.devtunnels.ms",
            RemoteToken = "jwt-token"
        };

        var payload = ConnectionSettings.BuildTunnelQrPayload(
            settings.RemoteUrl!,
            settings.RemoteToken,
            "192.168.1.5",
            4322,
            settings.ServerPassword,
            allowLan: true);

        Assert.False(payload.ContainsKey("lanUrl"), "lanUrl should not be in QR when no ServerPassword");
        Assert.False(payload.ContainsKey("lanToken"), "lanToken should not be in QR when no ServerPassword");
    }

    [Fact]
    public void QrPayload_IncludesLanUrl_WhenServerPasswordSet()
    {
        // When ServerPassword is configured, QR should include lanUrl with token
        var settings = new ConnectionSettings
        {
            ServerPassword = "my-password",
            RemoteUrl = "https://tunnel.devtunnels.ms",
            RemoteToken = "jwt-token"
        };

        var payload = ConnectionSettings.BuildTunnelQrPayload(
            settings.RemoteUrl!,
            settings.RemoteToken,
            "192.168.1.5",
            4322,
            settings.ServerPassword,
            allowLan: true);

        Assert.True(payload.ContainsKey("lanUrl"));
        Assert.Equal("my-password", payload["lanToken"]);
    }

    [Fact]
    public void QrPayload_ExcludesLanUrl_WhenBridgeIsLoopbackOnly()
    {
        var payload = ConnectionSettings.BuildTunnelQrPayload(
            "https://tunnel.devtunnels.ms",
            "jwt-token",
            "192.168.1.5",
            4322,
            "my-password",
            allowLan: false);

        Assert.Equal("https://tunnel.devtunnels.ms", payload["url"]);
        Assert.Equal("jwt-token", payload["token"]);
        Assert.False(payload.ContainsKey("lanUrl"));
        Assert.False(payload.ContainsKey("lanToken"));
    }

    [Fact]
    public async Task WsBridgeServer_HttpProbe_RequiresToken_WhenTokenConfigured()
    {
        // Verifies that the HTTP probe endpoint requires auth even from loopback when a token is set
        var port = GetFreePort();
        using var server = new WsBridgeServer();
        server.AccessToken = "some-secret-token";
        server.Start(port, 0);
        await Task.Delay(100);

        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };

            // No token → rejected
            var rejected = await client.GetAsync($"http://localhost:{port}/");
            Assert.Equal(HttpStatusCode.Unauthorized, rejected.StatusCode);

            // Correct token via X-Bridge-Authorization → accepted
            var request = new HttpRequestMessage(HttpMethod.Get, $"http://localhost:{port}/");
            request.Headers.Add("X-Bridge-Authorization", "some-secret-token");
            var accepted = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
            Assert.Equal("WsBridge OK", await accepted.Content.ReadAsStringAsync());
        }
        finally
        {
            server.Stop();
        }
    }

    [Fact]
    public async Task WsBridgeServer_AcceptsTunnelHostHeader_WhenTokenConfigured()
    {
        var port = GetFreePort();
        using var server = new WsBridgeServer();
        server.AccessToken = "some-secret-token";
        server.Start(port, 0);
        await Task.Delay(100);

        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
            var request = new HttpRequestMessage(HttpMethod.Get, $"http://localhost:{port}/");
            request.Headers.Host = "example.devtunnels.ms";
            request.Headers.Add("X-Bridge-Authorization", "some-secret-token");

            var response = await client.SendAsync(request);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("WsBridge OK", await response.Content.ReadAsStringAsync());
        }
        finally
        {
            server.Stop();
        }
    }

    [Fact]
    public async Task WsBridgeClient_ConnectAsync_SendsTokenInQueryString()
    {
        var port = GetFreePort();
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://localhost:{port}/");
        listener.Start();

        var seenToken = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var serverTask = Task.Run(async () =>
        {
            var ctx = await listener.GetContextAsync();
            seenToken.TrySetResult(ctx.Request.QueryString["token"]);
            Assert.True(ctx.Request.IsWebSocketRequest);

            var wsContext = await ctx.AcceptWebSocketAsync(null);
            var ws = wsContext.WebSocket;
            try
            {
                var buffer = new byte[16];
                while (ws.State == WebSocketState.Open)
                {
                    WebSocketReceiveResult result;
                    try
                    {
                        result = await ws.ReceiveAsync(buffer, CancellationToken.None);
                    }
                    catch (WebSocketException)
                    {
                        break;
                    }

                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
                        break;
                    }
                }
            }
            finally
            {
                ws.Dispose();
            }
        });

        using var client = new WsBridgeClient();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await client.ConnectAsync($"ws://localhost:{port}/", "query-token", cts.Token);

        Assert.Equal("query-token", await seenToken.Task);

        client.Stop();
        await serverTask;
    }

    [Fact]
    public void ScanQrCode_LanUrlWithoutToken_ProbeWouldFail()
    {
        // End-to-end scenario: QR code has lanUrl but no lanToken.
        // The ConnectSmartAsync would probe LAN first.
        // After the server fix, the probe returns 401 → falls back to tunnel.
        var qrJson = """{"url":"https://tunnel.devtunnels.ms","token":"jwt","lanUrl":"http://192.168.1.5:4322"}""";

        var doc = JsonDocument.Parse(qrJson);
        var settings = new ConnectionSettings();

        if (doc.RootElement.TryGetProperty("url", out var urlProp))
            settings.RemoteUrl = urlProp.GetString();
        if (doc.RootElement.TryGetProperty("token", out var tokenProp))
            settings.RemoteToken = tokenProp.GetString();
        if (doc.RootElement.TryGetProperty("lanUrl", out var lanUrlProp))
            settings.LanUrl = lanUrlProp.GetString();
        if (doc.RootElement.TryGetProperty("lanToken", out var lanTokenProp))
            settings.LanToken = lanTokenProp.GetString();

        // lanUrl is present but lanToken is null — LAN probe should fail (server returns 401)
        Assert.NotNull(settings.LanUrl);
        Assert.Null(settings.LanToken);
        // Tunnel URL and token are available as fallback
        Assert.NotNull(settings.RemoteUrl);
        Assert.NotNull(settings.RemoteToken);
    }

    private static int GetFreePort()
    {
        using var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
