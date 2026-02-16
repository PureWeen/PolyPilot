using GitHub.Copilot.SDK;
using PolyPilot.Models;

namespace PolyPilot.Tests;

/// <summary>
/// Tests for initialization mode branching logic.
/// Validates that ConnectionSettings and CopilotClientOptions
/// behave correctly for each ConnectionMode path in CopilotService.InitializeAsync.
/// </summary>
public class InitializationModeTests
{
    // --- Helper: mirrors CreateClient option-building logic from CopilotService ---

    private static CopilotClientOptions BuildClientOptions(ConnectionSettings settings)
    {
        var options = new CopilotClientOptions();

        if (settings.Mode == ConnectionMode.Persistent)
        {
            options.CliPath = null;
            options.UseStdio = false;
            options.AutoStart = false;
            options.CliUrl = $"http://{settings.Host}:{settings.Port}";
        }

        return options;
    }

    [Fact]
    public void PersistentMode_RequiresHostAndPort()
    {
        var settings = new ConnectionSettings
        {
            Mode = ConnectionMode.Persistent,
            Host = "192.168.1.50",
            Port = 5555
        };

        var options = BuildClientOptions(settings);

        Assert.Equal("http://192.168.1.50:5555", options.CliUrl);
        Assert.Null(options.CliPath);
        Assert.False(options.UseStdio);
    }

    [Fact]
    public void RemoteMode_WithNoUrl_IsNotReady()
    {
        var settings = new ConnectionSettings
        {
            Mode = ConnectionMode.Remote,
            RemoteUrl = null
        };

        // CliUrl falls back to Host:Port which is not a valid remote endpoint
        var cliUrl = settings.CliUrl;

        Assert.Equal($"{settings.Host}:{settings.Port}", cliUrl);
        Assert.DoesNotContain("http", cliUrl);
    }

    [Fact]
    public void RemoteMode_WithUrl_CliUrl_IsRemoteUrl()
    {
        var settings = new ConnectionSettings
        {
            Mode = ConnectionMode.Remote,
            RemoteUrl = "https://my-tunnel.devtunnels.ms"
        };

        Assert.Equal("https://my-tunnel.devtunnels.ms", settings.CliUrl);
    }

    [Fact]
    public void DemoMode_NoNetworkRequired()
    {
        var settings = new ConnectionSettings
        {
            Mode = ConnectionMode.Demo,
            Host = "",
            Port = 0,
            RemoteUrl = null
        };

        // Demo mode should not need BuildClientOptions at all;
        // verify settings are valid without network config
        Assert.Equal(ConnectionMode.Demo, settings.Mode);
        Assert.Null(settings.RemoteUrl);
    }

    [Fact]
    public void PersistentMode_FallbackToEmbedded_CliPathValid()
    {
        // Simulate fallback: build Embedded options (no CliUrl, CliPath available)
        var settings = new ConnectionSettings { Mode = ConnectionMode.Embedded };

        var options = BuildClientOptions(settings);

        // Embedded mode must NOT set CliUrl (SDK uses CliPath + stdio instead)
        Assert.Null(options.CliUrl);
        // SDK auto-discovers CliPath or UseStdio
        bool hasAutoPath = !string.IsNullOrEmpty(options.CliPath);
        bool hasAutoStdio = options.UseStdio;
        Assert.True(hasAutoPath || hasAutoStdio,
            "Embedded fallback must have CliPath or UseStdio set by SDK defaults");
    }

    [Fact]
    public void EmbeddedMode_NoServerNeeded()
    {
        var settings = new ConnectionSettings
        {
            Mode = ConnectionMode.Embedded,
            Host = "unreachable-host",
            Port = 99999
        };

        var options = BuildClientOptions(settings);

        // Embedded mode ignores Host/Port — no CliUrl set
        Assert.Null(options.CliUrl);
        // SDK defaults AutoStart=true for Embedded (auto-spawn copilot process)
        Assert.True(options.AutoStart);
    }

    [Fact]
    public void AllModes_HaveDistinctEnumValues()
    {
        Assert.Equal(0, (int)ConnectionMode.Embedded);
        Assert.Equal(1, (int)ConnectionMode.Persistent);
        Assert.Equal(2, (int)ConnectionMode.Remote);
        Assert.Equal(3, (int)ConnectionMode.Demo);
    }

    [Fact]
    public void PersistentMode_CliUrl_Format()
    {
        var settings = new ConnectionSettings
        {
            Mode = ConnectionMode.Persistent,
            Host = "localhost",
            Port = 4321
        };

        var options = BuildClientOptions(settings);

        Assert.NotNull(options.CliUrl);
        Assert.StartsWith("http://", options.CliUrl);
        Assert.Contains("localhost", options.CliUrl);
        Assert.Contains("4321", options.CliUrl);
    }

    [Fact]
    public void ModeSwitch_PersistentToEmbedded_ChangesOptions()
    {
        var persistentSettings = new ConnectionSettings
        {
            Mode = ConnectionMode.Persistent,
            Host = "localhost",
            Port = 4321
        };
        var embeddedSettings = new ConnectionSettings
        {
            Mode = ConnectionMode.Embedded
        };

        var persistentOptions = BuildClientOptions(persistentSettings);
        var embeddedOptions = BuildClientOptions(embeddedSettings);

        // Persistent sets CliUrl; Embedded does not
        Assert.NotNull(persistentOptions.CliUrl);
        Assert.Null(embeddedOptions.CliUrl);

        // Persistent clears UseStdio; Embedded keeps SDK default
        Assert.False(persistentOptions.UseStdio);
    }

    [Fact]
    public void PersistentMode_DefaultPort()
    {
        var settings = new ConnectionSettings();

        Assert.Equal(4321, settings.Port);
        Assert.Equal("localhost", settings.Host);
    }
}
