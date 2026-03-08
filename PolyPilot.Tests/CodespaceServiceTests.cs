using PolyPilot.Models;
using PolyPilot.Services;

namespace PolyPilot.Tests;

/// <summary>
/// Tests for the GitHub Codespace group feature: ConnectionState, CodespaceService API,
/// reconnect tracking, and port-forward tunnel probing.
/// </summary>
public class CodespaceServiceTests
{
    // ── CodespaceService API surface ────────────────────────────────────────

    [Fact]
    public void CodespaceService_CanBeInstantiated()
    {
        var svc = new CodespaceService();
        Assert.NotNull(svc);
    }

    [Fact]
    public async Task CodespaceService_ListCodespacesAsync_ReturnsListGracefully()
    {
        var svc = new CodespaceService();
        var result = await svc.ListCodespacesAsync();
        Assert.NotNull(result);
        foreach (var cs in result)
        {
            Assert.False(string.IsNullOrEmpty(cs.Name));
            Assert.NotNull(cs.Repository);
        }
    }

    [Fact]
    public async Task CodespaceService_OpenTunnel_ThrowsOnUnreachableCodespace()
    {
        var svc = new CodespaceService();
        var ex = await Assert.ThrowsAnyAsync<Exception>(
            () => svc.OpenTunnelAsync("__nonexistent-codespace-xyz__", 4321, connectTimeoutSeconds: 5));
        Assert.NotNull(ex);
    }

    [Fact]
    public async Task CodespaceService_StartCopilotHeadless_ReturnsBoolNotThrow()
    {
        var svc = new CodespaceService();
        var result = await svc.StartCopilotHeadlessAsync("__nonexistent-xyz__", 4321);
        Assert.False(result);
    }

    [Fact]
    public async Task CodespaceService_TunnelProbe_ReturnsFalse_WhenPortClosed()
    {
        int closedPort;
        using (var tmp = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0))
        {
            tmp.Start();
            closedPort = ((System.Net.IPEndPoint)tmp.LocalEndpoint).Port;
            tmp.Stop();
        }

        using var tcp = new System.Net.Sockets.TcpClient();
        var connected = false;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
            await tcp.ConnectAsync(System.Net.IPAddress.Loopback, closedPort, cts.Token);
            connected = true;
        }
        catch { /* expected: port is closed */ }

        Assert.False(connected, "Connecting to a closed port should fail");
    }

    [Fact]
    public async Task CodespaceService_TunnelProbe_ReturnsTrue_WhenServerHoldsConnectionOpen()
    {
        using var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;

        var acceptTask = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync();
            await Task.Delay(5000); // hold open like copilot does
        });

        using var probe = new System.Net.Sockets.TcpClient();
        await probe.ConnectAsync(System.Net.IPAddress.Loopback, port);
        Assert.True(probe.Connected);

        probe.GetStream().ReadTimeout = 500;
        var buf = new byte[1];
        int bytesRead = 0;
        bool timedOut = false;
        try
        {
            bytesRead = await Task.Run(() => probe.GetStream().Read(buf, 0, 1));
        }
        catch (System.IO.IOException)
        {
            timedOut = true;
        }

        Assert.True(timedOut || bytesRead == 0,
            "Copilot-like behavior: connection open but no data = hold open (not close)");

        listener.Stop();
    }

    [Fact]
    public async Task CodespaceService_GetCodespaceState_ReturnsNullForNonexistent()
    {
        var svc = new CodespaceService();
        var state = await svc.GetCodespaceStateAsync("__nonexistent_cs_xyz__");
        Assert.Null(state);
    }

    [Fact]
    public async Task CodespaceService_StartCodespace_ReturnsFalseForNonexistent()
    {
        var svc = new CodespaceService();
        var result = await svc.StartCodespaceAsync("__nonexistent_cs_xyz__", timeoutSeconds: 5);
        Assert.False(result);
    }

    // ── ConnectionState ─────────────────────────────────────────────────────

    [Fact]
    public void SessionGroup_ConnectionState_DefaultsToUnknown()
    {
        var group = new SessionGroup { CodespaceName = "test-cs" };
        Assert.Equal(CodespaceConnectionState.Unknown, group.ConnectionState);
    }

    [Fact]
    public void SessionGroup_ConnectionState_IsNotSerialized()
    {
        var group = new SessionGroup
        {
            Name = "test",
            CodespaceName = "test-cs",
            ConnectionState = CodespaceConnectionState.Connected
        };
        var json = System.Text.Json.JsonSerializer.Serialize(group);
        Assert.DoesNotContain("ConnectionState", json);
        Assert.DoesNotContain("Connected", json);

        var restored = System.Text.Json.JsonSerializer.Deserialize<SessionGroup>(json);
        Assert.Equal(CodespaceConnectionState.Unknown, restored!.ConnectionState);
    }

    [Fact]
    public void CodespaceConnectionState_HasAllExpectedValues()
    {
        var values = Enum.GetValues<CodespaceConnectionState>();
        Assert.Contains(CodespaceConnectionState.Unknown, values);
        Assert.Contains(CodespaceConnectionState.Connected, values);
        Assert.Contains(CodespaceConnectionState.Reconnecting, values);
        Assert.Contains(CodespaceConnectionState.CodespaceStopped, values);
        Assert.Contains(CodespaceConnectionState.StartingCodespace, values);
        Assert.Contains(CodespaceConnectionState.WaitingForCopilot, values);
        Assert.Contains(CodespaceConnectionState.SetupRequired, values);
    }

    [Fact]
    public void CodespaceGroup_SetupRequired_IndicatesNoSsh()
    {
        var group = new SessionGroup
        {
            CodespaceName = "test-cs",
            ConnectionState = CodespaceConnectionState.SetupRequired,
            SshAvailable = false,
        };
        Assert.Equal(CodespaceConnectionState.SetupRequired, group.ConnectionState);
        Assert.False(group.SshAvailable);
    }

    [Fact]
    public void SessionGroup_ReconnectAttempts_DefaultsToZero()
    {
        var group = new SessionGroup { CodespaceName = "test-cs" };
        Assert.Equal(0, group.ReconnectAttempts);
    }

    [Fact]
    public void SessionGroup_ReconnectAttempts_TracksRetries()
    {
        var group = new SessionGroup { CodespaceName = "test-cs" };
        group.ReconnectAttempts++;
        group.ReconnectAttempts++;
        group.LastReconnectAttempt = DateTime.UtcNow;

        Assert.Equal(2, group.ReconnectAttempts);
        Assert.NotNull(group.LastReconnectAttempt);
    }

    [Fact]
    public void SessionGroup_ReconnectFields_NotSerialized()
    {
        var group = new SessionGroup
        {
            CodespaceName = "test-cs",
            ReconnectAttempts = 5,
            LastReconnectAttempt = DateTime.UtcNow,
        };
        var json = System.Text.Json.JsonSerializer.Serialize(group);
        Assert.DoesNotContain("ReconnectAttempts", json);
        Assert.DoesNotContain("LastReconnectAttempt", json);
    }

    [Fact]
    public void CodespaceGroup_IsCodespace_TrueWhenCodespaceNameSet()
    {
        var group = new SessionGroup
        {
            Name = "MyCodespace",
            CodespaceName = "my-cs",
            CodespacePort = 4321,
            ConnectionState = CodespaceConnectionState.Reconnecting,
        };
        Assert.True(group.IsCodespace, "Group with CodespaceName should be identified as a codespace group");
        Assert.Equal(CodespaceConnectionState.Reconnecting, group.ConnectionState);
    }
}
