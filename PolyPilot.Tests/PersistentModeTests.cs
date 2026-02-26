using GitHub.Copilot.SDK;
using PolyPilot.Models;

namespace PolyPilot.Tests;

/// <summary>
/// Tests for Persistent mode client configuration.
/// Validates that CopilotClientOptions are set correctly for each ConnectionMode,
/// respecting the SDK constraint: CliUrl is mutually exclusive with UseStdio and CliPath.
/// </summary>
public class PersistentModeTests
{
    // --- SDK default discovery tests ---

    [Fact]
    public void CopilotClientOptions_Defaults_DiscoverAutoSetFields()
    {
        // Discover what the SDK auto-sets so we know what to clear for Persistent mode.
        var options = new CopilotClientOptions();

        // The SDK auto-discovers a bundled CliPath and/or sets UseStdio.
        // At least one of these must be true for the CliUrl conflict to occur.
        bool hasAutoPath = !string.IsNullOrEmpty(options.CliPath);
        bool hasAutoStdio = options.UseStdio;

        // At least one is auto-set (this is why CliUrl alone throws)
        Assert.True(hasAutoPath || hasAutoStdio,
            $"Expected SDK to auto-set CliPath or UseStdio. CliPath='{options.CliPath}', UseStdio={options.UseStdio}");
    }

    // --- SDK constraint tests ---

    [Fact]
    public void CopilotClientOptions_CliUrl_WithClearedDefaults_DoesNotThrow()
    {
        // When connecting to an existing persistent server, we must:
        // 1. Clear CliPath (auto-discovered by SDK)
        // 2. Clear UseStdio (may default to true)
        // 3. Set AutoStart = false
        // 4. Set CliUrl
        var options = new CopilotClientOptions();
        options.CliPath = null;
        options.UseStdio = false;
        options.AutoStart = false;
        options.CliUrl = "http://localhost:4321";

        var client = new CopilotClient(options);
        Assert.NotNull(client);
    }

    [Fact]
    public void CopilotClientOptions_CliUrl_WithoutClearingDefaults_Accepted()
    {
        // SDK 0.1.26+ no longer throws when CliUrl is set on a fresh options object.
        var options = new CopilotClientOptions();
        options.CliUrl = "http://localhost:4321";

        var client = new CopilotClient(options);
        Assert.NotNull(client);
    }

    [Fact]
    public void CopilotClientOptions_CliUrl_WithCliPath_Throws()
    {
        var options = new CopilotClientOptions();
        options.CliPath = null;
        options.UseStdio = false;
        options.CliUrl = "http://localhost:4321";
        options.CliPath = "/some/path/to/copilot";

        Assert.Throws<ArgumentException>(() => new CopilotClient(options));
    }

    [Fact]
    public void CopilotClientOptions_CliUrl_WithUseStdio_Accepted()
    {
        // SDK 0.1.26+ no longer throws when CliUrl is set alongside UseStdio.
        var options = new CopilotClientOptions();
        options.CliPath = null;
        options.CliUrl = "http://localhost:4321";
        options.UseStdio = true;

        var client = new CopilotClient(options);
        Assert.NotNull(client);
    }

    // --- ConnectionSettings.CliUrl property tests ---

    [Fact]
    public void ConnectionSettings_PersistentMode_CliUrl_ReturnsHostPort()
    {
        var settings = new ConnectionSettings
        {
            Mode = ConnectionMode.Persistent,
            Host = "localhost",
            Port = 4321
        };

        Assert.Equal("localhost:4321", settings.CliUrl);
    }

    [Fact]
    public void ConnectionSettings_PersistentMode_CustomPort_CliUrl_ReturnsCorrectly()
    {
        var settings = new ConnectionSettings
        {
            Mode = ConnectionMode.Persistent,
            Host = "localhost",
            Port = 5555
        };

        Assert.Equal("localhost:5555", settings.CliUrl);
    }

    [Fact]
    public void ConnectionSettings_EmbeddedMode_CliUrl_ReturnsHostPort()
    {
        var settings = new ConnectionSettings
        {
            Mode = ConnectionMode.Embedded,
            Host = "localhost",
            Port = 4321
        };

        Assert.Equal("localhost:4321", settings.CliUrl);
    }

    // --- Option configuration logic tests ---

    [Fact]
    public void PersistentMode_ConfiguresOptionsForTcpConnection()
    {
        var settings = new ConnectionSettings
        {
            Mode = ConnectionMode.Persistent,
            Host = "localhost",
            Port = 4321
        };

        var options = BuildClientOptions(settings);

        Assert.Equal($"http://{settings.Host}:{settings.Port}", options.CliUrl);
        Assert.False(options.AutoStart);
        Assert.Null(options.CliPath);
        Assert.Null(options.CliArgs);
        Assert.False(options.UseStdio);
    }

    [Fact]
    public void PersistentMode_WithCustomPort_ConfiguresCorrectCliUrl()
    {
        var settings = new ConnectionSettings
        {
            Mode = ConnectionMode.Persistent,
            Host = "localhost",
            Port = 9999
        };

        var options = BuildClientOptions(settings);

        Assert.Equal("http://localhost:9999", options.CliUrl);
        Assert.False(options.AutoStart);
    }

    [Fact]
    public void PersistentMode_DoesNotSetCliPath()
    {
        var settings = new ConnectionSettings
        {
            Mode = ConnectionMode.Persistent,
            Host = "localhost",
            Port = 4321
        };

        var options = BuildClientOptions(settings);

        Assert.Null(options.CliPath);
    }

    [Fact]
    public void PersistentMode_DoesNotSetCliArgs()
    {
        var settings = new ConnectionSettings
        {
            Mode = ConnectionMode.Persistent,
            Host = "localhost",
            Port = 4321
        };

        var options = BuildClientOptions(settings);

        Assert.Null(options.CliArgs);
    }

    [Fact]
    public void PersistentMode_ClearsUseStdio()
    {
        var settings = new ConnectionSettings
        {
            Mode = ConnectionMode.Persistent,
            Host = "localhost",
            Port = 4321
        };

        var options = BuildClientOptions(settings);

        Assert.False(options.UseStdio);
    }

    [Fact]
    public void EmbeddedMode_DoesNotSetCliUrl()
    {
        var settings = new ConnectionSettings
        {
            Mode = ConnectionMode.Embedded,
            Host = "localhost",
            Port = 4321
        };

        var options = BuildClientOptions(settings);

        Assert.Null(options.CliUrl);
    }

    [Fact]
    public void PersistentMode_OptionsAreValidForCopilotClient()
    {
        // End-to-end: build options for Persistent mode and verify the SDK accepts them.
        var settings = new ConnectionSettings
        {
            Mode = ConnectionMode.Persistent,
            Host = "localhost",
            Port = 4321
        };

        var options = BuildClientOptions(settings);

        // This must NOT throw ArgumentException about mutual exclusivity.
        var client = new CopilotClient(options);
        Assert.NotNull(client);
    }

    [Fact]
    public void EmbeddedMode_OptionsDoNotConflict()
    {
        var settings = new ConnectionSettings
        {
            Mode = ConnectionMode.Embedded,
            Host = "localhost",
            Port = 4321
        };

        var options = BuildClientOptions(settings);

        Assert.Null(options.CliUrl);
    }

    [Fact]
    public void EmbeddedMode_OptionsAreValidForCopilotClient()
    {
        // Embedded path doesn't set CliUrl, so SDK defaults are fine.
        var settings = new ConnectionSettings
        {
            Mode = ConnectionMode.Embedded,
            Host = "localhost",
            Port = 4321
        };

        var options = BuildClientOptions(settings);

        // Must NOT throw — SDK auto-discovers CliPath for embedded mode.
        var client = new CopilotClient(options);
        Assert.NotNull(client);
    }

    [Fact]
    public void PersistentMode_HttpPrefixRequired()
    {
        var settings = new ConnectionSettings
        {
            Mode = ConnectionMode.Persistent,
            Host = "localhost",
            Port = 4321
        };

        var options = BuildClientOptions(settings);

        Assert.StartsWith("http://", options.CliUrl);
    }

    [Fact]
    public void PersistentMode_NonLocalhostHost()
    {
        var settings = new ConnectionSettings
        {
            Mode = ConnectionMode.Persistent,
            Host = "192.168.1.100",
            Port = 4321
        };

        var options = BuildClientOptions(settings);

        Assert.Equal("http://192.168.1.100:4321", options.CliUrl);
    }

    [Fact]
    public void EmbeddedMode_DoesNotClearCliPath()
    {
        // Verify Embedded mode preserves whatever CliPath the SDK auto-discovers,
        // unlike Persistent mode which explicitly nulls it out.
        var settings = new ConnectionSettings
        {
            Mode = ConnectionMode.Embedded,
            Host = "localhost",
            Port = 4321
        };

        var defaultOptions = new CopilotClientOptions();
        var builtOptions = BuildClientOptions(settings);

        // CliPath should be unchanged from SDK default (not cleared like Persistent mode does).
        Assert.Equal(defaultOptions.CliPath, builtOptions.CliPath);
    }

    [Fact]
    public void PersistentMode_SetsAutoStartFalse()
    {
        var settings = new ConnectionSettings
        {
            Mode = ConnectionMode.Persistent,
            Host = "localhost",
            Port = 4321
        };

        var options = BuildClientOptions(settings);

        Assert.False(options.AutoStart);
    }

    [Fact]
    public void DemoMode_TreatedAsEmbedded()
    {
        // Demo mode takes the else branch — no CliUrl set.
        var settings = new ConnectionSettings
        {
            Mode = ConnectionMode.Demo,
            Host = "localhost",
            Port = 4321
        };

        var options = BuildClientOptions(settings);

        Assert.Null(options.CliUrl);
    }

    [Fact]
    public void RemoteMode_NotHandledByCreateClient()
    {
        // Remote mode takes the else branch — handled separately by InitializeRemoteAsync.
        var settings = new ConnectionSettings
        {
            Mode = ConnectionMode.Remote,
            Host = "localhost",
            Port = 4321
        };

        var options = BuildClientOptions(settings);

        Assert.Null(options.CliUrl);
    }

    // --- StartAsync failure tests ---

    [Fact]
    public async Task StartAsync_WithUnreachableServer_Throws()
    {
        // Proves the premise of the InitializeAsync/ReconnectAsync try/catch fix:
        // CopilotClient.StartAsync() throws when the server is unreachable.
        var options = new CopilotClientOptions();
        options.CliPath = null;
        options.UseStdio = false;
        options.AutoStart = false;
        options.CliUrl = "http://localhost:19999"; // Nothing listening here

        var client = new CopilotClient(options);

        await Assert.ThrowsAnyAsync<Exception>(async () =>
            await client.StartAsync(CancellationToken.None));
    }

    // --- Helper: mirrors CreateClient option-building logic from CopilotService ---

    /// <summary>
    /// Extracted option-building logic from CopilotService.CreateClient().
    /// This must stay in sync with the actual implementation.
    /// Only Persistent mode sets CliUrl; all other modes (Embedded, Demo, Remote)
    /// take the else branch and leave SDK defaults intact.
    /// </summary>
    private static CopilotClientOptions BuildClientOptions(ConnectionSettings settings)
    {
        var options = new CopilotClientOptions();

        if (settings.Mode == ConnectionMode.Persistent)
        {
            // Connect to existing headless server via TCP.
            // Must clear auto-discovered CliPath and UseStdio first —
            // CliUrl is mutually exclusive with both.
            options.CliPath = null;
            options.UseStdio = false;
            options.AutoStart = false;
            options.CliUrl = $"http://{settings.Host}:{settings.Port}";
        }
        else
        {
            // Embedded, Demo, Remote: CliPath would be set here in the real code,
            // but we skip it in tests since binary may not exist.
            // The important thing is that CliUrl is NOT set.
            // Remote mode is handled separately by InitializeRemoteAsync.
        }

        return options;
    }
}

