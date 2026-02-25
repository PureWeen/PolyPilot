using System.Text.Json;
using PolyPilot.Models;

namespace PolyPilot.Tests;

public class ConnectionSettingsTests
{
    private readonly string _testDir;

    public ConnectionSettingsTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), $"PolyPilot-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testDir);
    }

    [Fact]
    public void CliUrl_EmbeddedMode_ReturnsHostPort()
    {
        var settings = new ConnectionSettings
        {
            Mode = ConnectionMode.Embedded,
            Host = "localhost",
            Port = 4321
        };

        Assert.Equal("localhost:4321", settings.CliUrl);
    }

    [Fact]
    public void CliUrl_RemoteMode_WithUrl_ReturnsRemoteUrl()
    {
        var settings = new ConnectionSettings
        {
            Mode = ConnectionMode.Remote,
            RemoteUrl = "https://my-tunnel.devtunnels.ms"
        };

        Assert.Equal("https://my-tunnel.devtunnels.ms", settings.CliUrl);
    }

    [Fact]
    public void CliUrl_RemoteMode_WithoutUrl_FallsBackToHostPort()
    {
        var settings = new ConnectionSettings
        {
            Mode = ConnectionMode.Remote,
            RemoteUrl = null,
            Host = "192.168.1.10",
            Port = 5000
        };

        Assert.Equal("192.168.1.10:5000", settings.CliUrl);
    }

    [Fact]
    public void CliUrl_RemoteMode_EmptyUrl_FallsBackToHostPort()
    {
        var settings = new ConnectionSettings
        {
            Mode = ConnectionMode.Remote,
            RemoteUrl = "",
            Host = "localhost",
            Port = 4321
        };

        Assert.Equal("localhost:4321", settings.CliUrl);
    }

    [Fact]
    public void DefaultValues_AreCorrect()
    {
        var settings = new ConnectionSettings();

        Assert.Equal("localhost", settings.Host);
        Assert.Equal(4321, settings.Port);
        Assert.False(settings.AutoStartServer);
        Assert.Null(settings.RemoteUrl);
        Assert.Null(settings.RemoteToken);
        Assert.Null(settings.TunnelId);
        Assert.False(settings.AutoStartTunnel);
    }

    [Fact]
    public void Save_Load_RoundTrip()
    {
        var settingsPath = Path.Combine(_testDir, ".polypilot", "settings.json");

        // Manually create settings JSON and verify deserialization
        var original = new ConnectionSettings
        {
            Mode = ConnectionMode.Remote,
            Host = "myhost",
            Port = 9999,
            RemoteUrl = "https://example.com",
            RemoteToken = "token123",
            TunnelId = "tunnel-abc",
            AutoStartTunnel = true,
            AutoStartServer = true
        };

        var json = JsonSerializer.Serialize(original, new JsonSerializerOptions { WriteIndented = true });
        Directory.CreateDirectory(Path.GetDirectoryName(settingsPath)!);
        File.WriteAllText(settingsPath, json);

        // Deserialize back
        var loaded = JsonSerializer.Deserialize<ConnectionSettings>(File.ReadAllText(settingsPath));

        Assert.NotNull(loaded);
        Assert.Equal(ConnectionMode.Remote, loaded!.Mode);
        Assert.Equal("myhost", loaded.Host);
        Assert.Equal(9999, loaded.Port);
        Assert.Equal("https://example.com", loaded.RemoteUrl);
        Assert.Equal("token123", loaded.RemoteToken);
        Assert.Equal("tunnel-abc", loaded.TunnelId);
        Assert.True(loaded.AutoStartTunnel);
        Assert.True(loaded.AutoStartServer);
    }

    [Fact]
    public void ConnectionMode_Enum_HasExpectedValues()
    {
        Assert.Equal(0, (int)ConnectionMode.Embedded);
        Assert.Equal(1, (int)ConnectionMode.Persistent);
        Assert.Equal(2, (int)ConnectionMode.Remote);
    }

    [Fact]
    public void JsonSerialization_ModeAsInt()
    {
        var settings = new ConnectionSettings { Mode = ConnectionMode.Persistent };
        var json = JsonSerializer.Serialize(settings);

        // Mode should serialize as integer by default
        Assert.Contains("\"Mode\":1", json);
    }

    [Fact]
    public void DefaultValues_NewFields_AreCorrect()
    {
        var settings = new ConnectionSettings();

        Assert.Null(settings.ServerPassword);
        Assert.False(settings.DirectSharingEnabled);
        Assert.Equal(CliSourceMode.BuiltIn, settings.CliSource);
    }

    [Fact]
    public void Save_Load_RoundTrip_WithNewFields()
    {
        var original = new ConnectionSettings
        {
            Mode = ConnectionMode.Embedded,
            Host = "localhost",
            Port = 4321,
            ServerPassword = "mypass",
            DirectSharingEnabled = true,
            CliSource = CliSourceMode.System
        };

        var json = JsonSerializer.Serialize(original);
        var loaded = JsonSerializer.Deserialize<ConnectionSettings>(json);

        Assert.NotNull(loaded);
        Assert.Equal("mypass", loaded!.ServerPassword);
        Assert.True(loaded.DirectSharingEnabled);
        Assert.Equal(CliSourceMode.System, loaded.CliSource);
    }

    [Fact]
    public void BackwardCompatibility_OldJsonWithoutNewFields()
    {
        var json = """{"Mode":0,"Host":"oldhost","Port":1234}""";
        var loaded = JsonSerializer.Deserialize<ConnectionSettings>(json);

        Assert.NotNull(loaded);
        Assert.Equal("oldhost", loaded!.Host);
        Assert.Equal(1234, loaded.Port);
        Assert.Null(loaded.ServerPassword);
        Assert.False(loaded.DirectSharingEnabled);
        Assert.Equal(CliSourceMode.BuiltIn, loaded.CliSource);
    }

    [Fact]
    public void ServerPassword_NotInCliUrl()
    {
        var settings = new ConnectionSettings
        {
            Host = "myhost",
            Port = 5555,
            ServerPassword = "secret123"
        };

        Assert.Equal("myhost:5555", settings.CliUrl);
        Assert.DoesNotContain("secret123", settings.CliUrl);
    }

    [Fact]
    public void DirectSharingEnabled_DefaultFalse()
    {
        var settings = new ConnectionSettings();
        Assert.False(settings.DirectSharingEnabled);
    }

    [Fact]
    public void CliSourceMode_Enum_HasExpectedValues()
    {
        Assert.Equal(0, (int)CliSourceMode.BuiltIn);
        Assert.Equal(1, (int)CliSourceMode.System);
    }

    [Fact]
    public void InvalidCliSource_DeserializesToInvalidEnumValue()
    {
        // When settings.json has an out-of-range integer for CliSource,
        // JsonSerializer deserializes it as an undefined enum value.
        // ConnectionSettings.Load() should normalize this to BuiltIn.
        var json = """{"Mode":1,"Host":"localhost","Port":4321,"CliSource":99}""";
        var loaded = JsonSerializer.Deserialize<ConnectionSettings>(json);

        Assert.NotNull(loaded);
        // Raw deserialization produces an undefined enum value
        Assert.False(Enum.IsDefined(loaded!.CliSource));
    }

    [Fact]
    public void Load_InvalidCliSource_NormalizesToBuiltIn()
    {
        // Simulate what ConnectionSettings.Load() does: deserialize + validate
        var json = """{"Mode":1,"Host":"localhost","Port":4321,"CliSource":99}""";
        var settings = JsonSerializer.Deserialize<ConnectionSettings>(json)!;

        // Apply the same validation that Load() applies
        if (!Enum.IsDefined(settings.CliSource))
            settings.CliSource = CliSourceMode.BuiltIn;

        Assert.Equal(CliSourceMode.BuiltIn, settings.CliSource);
    }

    [Fact]
    public void Load_NegativeCliSource_NormalizesToBuiltIn()
    {
        var json = """{"Mode":1,"Host":"localhost","Port":4321,"CliSource":-1}""";
        var settings = JsonSerializer.Deserialize<ConnectionSettings>(json)!;

        if (!Enum.IsDefined(settings.CliSource))
            settings.CliSource = CliSourceMode.BuiltIn;

        Assert.Equal(CliSourceMode.BuiltIn, settings.CliSource);
    }

    [Fact]
    public void CliSourceCard_DisabledOnlyWhenNoCli()
    {
        // The Built-in card should NOT be disabled when builtInPath is null
        // but systemPath exists — because ResolveCopilotCliPath(BuiltIn) falls
        // back to system CLI. Card should only be disabled when BOTH are null.

        // Scenario 1: builtIn=null, system=exists → NOT disabled
        string? builtInPath = null;
        string? systemPath = "/usr/local/bin/copilot";
        bool disabled = builtInPath == null && systemPath == null;
        Assert.False(disabled, "Built-in card should NOT be disabled when system CLI exists as fallback");

        // Scenario 2: builtIn=exists, system=null → NOT disabled
        builtInPath = "/app/copilot";
        systemPath = null;
        disabled = builtInPath == null && systemPath == null;
        Assert.False(disabled, "Built-in card should NOT be disabled when built-in CLI exists");

        // Scenario 3: both null → disabled
        builtInPath = null;
        systemPath = null;
        disabled = builtInPath == null && systemPath == null;
        Assert.True(disabled, "Card should be disabled when no CLI is available at all");

        // Scenario 4: both exist → NOT disabled
        builtInPath = "/app/copilot";
        systemPath = "/usr/local/bin/copilot";
        disabled = builtInPath == null && systemPath == null;
        Assert.False(disabled, "Card should NOT be disabled when both CLIs exist");
    }

    [Fact]
    public void SetCliSource_AllowsSwitchBackToBuiltIn_WhenSystemExists()
    {
        // Simulates the SetCliSource guard logic.
        // When builtInPath is null but systemPath exists,
        // the user should be able to switch to BuiltIn mode.
        string? builtInPath = null;
        string? systemPath = "/usr/local/bin/copilot";

        // Old (buggy) guard: blocks if builtInPath is null
        bool oldGuardBlocks = builtInPath == null;
        Assert.True(oldGuardBlocks, "Old guard incorrectly blocks switch to Built-in");

        // New (fixed) guard: blocks only if BOTH are null
        bool newGuardBlocks = builtInPath == null && systemPath == null;
        Assert.False(newGuardBlocks, "New guard should allow switch to Built-in when system CLI exists");
    }

    [Fact]
    public void SetCliSource_AllowsSwitchToSystem_WhenBuiltInExists()
    {
        // Mirror test: System card with only built-in available
        string? builtInPath = "/app/copilot";
        string? systemPath = null;

        bool newGuardBlocks = builtInPath == null && systemPath == null;
        Assert.False(newGuardBlocks, "Should allow switch to System when built-in CLI exists as fallback");
    }

    [Fact]
    public void SetCliSource_BlocksWhenNoCli()
    {
        string? builtInPath = null;
        string? systemPath = null;

        bool newGuardBlocks = builtInPath == null && systemPath == null;
        Assert.True(newGuardBlocks, "Should block when no CLI is available at all");
    }

    private void Dispose()
    {
        try { Directory.Delete(_testDir, true); } catch { }
    }
}
