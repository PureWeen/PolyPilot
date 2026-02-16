using System.Text.Json;
using PolyPilot.Models;

namespace PolyPilot.Tests;

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
}
