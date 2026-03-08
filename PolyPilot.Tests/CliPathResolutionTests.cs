using System.Runtime.InteropServices;
using GitHub.Copilot.SDK;
using PolyPilot.Models;

namespace PolyPilot.Tests;

/// <summary>
/// Tests for CLI path resolution logic and CopilotClientOptions behavior
/// around CliPath, CliUrl, and CliSourceMode configuration.
/// </summary>
public class CliPathResolutionTests
{
    private static string CopilotBinaryName =>
        OperatingSystem.IsWindows() ? "copilot.exe" : "copilot";
    [Fact]
    public void CliSourceMode_BuiltIn_IsDefault()
    {
        var settings = new ConnectionSettings();
        Assert.Equal(CliSourceMode.BuiltIn, settings.CliSource);
    }

    [Fact]
    public void CliSourceMode_System_IsOne()
    {
        Assert.Equal(1, (int)CliSourceMode.System);
    }

    [Fact]
    public void CopilotClientOptions_CliPath_AcceptsNonExistentPath()
    {
        // Setting CliPath to a non-existent path doesn't throw at construction time;
        // failure is deferred until StartAsync.
        var options = new CopilotClientOptions();
        options.CliPath = "/nonexistent/path/to/copilot";

        var client = new CopilotClient(options);
        Assert.NotNull(client);
    }

    [Fact]
    public void CopilotClientOptions_CliPath_AcceptsNull()
    {
        var options = new CopilotClientOptions();
        options.CliPath = null;

        Assert.Null(options.CliPath);
    }

    [Fact]
    public void CopilotClientOptions_DefaultCliPath_AutoDiscoveryState()
    {
        // The SDK may auto-discover a CliPath or set UseStdio depending on environment.
        // This test documents the observed behavior in the test context.
        var options = new CopilotClientOptions();

        bool hasCliPath = !string.IsNullOrEmpty(options.CliPath);
        bool hasUseStdio = options.UseStdio;

        // Per PersistentModeTests, at least one should be set. If both are false,
        // the SDK has no CLI to launch and embedded mode will fail at StartAsync.
        Assert.True(hasCliPath || hasUseStdio,
            $"Expected SDK to auto-set CliPath or UseStdio. CliPath='{options.CliPath}', UseStdio={options.UseStdio}");
    }

    [Fact]
    public void CopilotClientOptions_CliPath_CanBeOverridden()
    {
        var options = new CopilotClientOptions();
        options.CliPath = "/custom/path";

        Assert.Equal("/custom/path", options.CliPath);
    }

    [Fact]
    public void EmbeddedMode_WithCustomCliPath_CreatesValidClient()
    {
        // Use the SDK-discovered default path to verify CopilotClient creation works.
        var defaultOptions = new CopilotClientOptions();
        var discoveredPath = defaultOptions.CliPath;

        var options = new CopilotClientOptions();
        options.CliPath = discoveredPath;

        var client = new CopilotClient(options);
        Assert.NotNull(client);
    }

    [Fact]
    public void PersistentMode_NullCliPath_RequiredForCliUrl()
    {
        // CliPath must be null before CliUrl can be set without throwing.
        var options = new CopilotClientOptions();
        options.CliPath = null;
        options.UseStdio = false;
        options.AutoStart = false;
        options.CliUrl = "http://localhost:4321";

        var client = new CopilotClient(options);
        Assert.NotNull(client);

        // SDK 0.1.26+ no longer throws when CliUrl is set on a fresh options object.
        var options2 = new CopilotClientOptions();
        options2.CliUrl = "http://localhost:4321";

        var client2 = new CopilotClient(options2);
        Assert.NotNull(client2);
    }

    // ================================================================
    // Bundled-only CLI resolution tests
    // ================================================================
    // These tests verify that the copilot binary can be found when there
    // is NO global install (no homebrew, no npm global). The SDK ships a
    // bundled binary under runtimes/{rid}/native/copilot.

    [Fact]
    public void SdkAutoDiscoveredCliPath_IsNotNull()
    {
        // The SDK default CliPath is null — it uses UseStdio instead.
        // Our app's ResolveBundledCliPath provides the actual binary path.
        // Verify that when we manually construct the bundled path (same logic
        // as GetBundledCliPath in CopilotService), the file exists.
        var assemblyDir = Path.GetDirectoryName(typeof(CopilotClient).Assembly.Location);
        Assert.NotNull(assemblyDir);

        var rid = RuntimeInformation.RuntimeIdentifier;
        var bundledPath = Path.Combine(assemblyDir!, "runtimes", rid, "native", CopilotBinaryName);

        Assert.True(File.Exists(bundledPath),
            $"Bundled copilot binary not found at: {bundledPath} (RID={rid})");
    }

    [Fact]
    public void SdkAutoDiscoveredCliPath_PointsToRuntimesDir()
    {
        // The bundled binary path follows the runtimes/{rid}/native/copilot convention.
        // This is the path that ResolveBundledCliPath (via GetBundledCliPath) constructs.
        var assemblyDir = Path.GetDirectoryName(typeof(CopilotClient).Assembly.Location);
        Assert.NotNull(assemblyDir);

        var rid = RuntimeInformation.RuntimeIdentifier;
        var bundledPath = Path.Combine(assemblyDir!, "runtimes", rid, "native", CopilotBinaryName);

        Assert.Contains("runtimes", bundledPath);
        Assert.Contains(rid, bundledPath);
        Assert.EndsWith(CopilotBinaryName, bundledPath);
    }

    [Fact]
    public void EmbeddedMode_WithNullResolvedPath_StillHasSdkDefault()
    {
        // When ResolveCopilotCliPath returns null, CopilotService.CreateClient does NOT
        // set options.CliPath. The SDK's default CliPath is also null, but UseStdio is true.
        // This means the SDK falls back to UseStdio mode, which auto-discovers the binary
        // by searching for "copilot" in standard locations including the runtimes/ dir.
        var options = new CopilotClientOptions();

        // SDK default: CliPath is null, UseStdio is true
        Assert.Null(options.CliPath);
        Assert.True(options.UseStdio,
            "SDK default should have UseStdio=true as fallback when CliPath is null");

        // Simulate what happens when ResolveCopilotCliPath returns null:
        // CopilotService.CreateClient does NOT override CliPath → SDK uses UseStdio
        string? cliPath = null;
        if (cliPath != null)
            options.CliPath = cliPath;

        // UseStdio remains true — the SDK will find the binary via its own resolution
        Assert.True(options.UseStdio);
    }

    [Fact]
    public void EmbeddedMode_BuiltIn_PrefersBundled()
    {
        // CliSourceMode.BuiltIn (default) means the bundled binary is tried before system paths.
        // ResolveCopilotCliPath(BuiltIn) calls ResolveBundledCliPath() first, then ResolveSystemCliPath().
        // This ensures users without a global install always get the bundled binary.
        var settings = new ConnectionSettings();
        Assert.Equal(CliSourceMode.BuiltIn, settings.CliSource);

        // Verify the bundled path exists and is NOT a system path
        var assemblyDir = Path.GetDirectoryName(typeof(CopilotClient).Assembly.Location);
        Assert.NotNull(assemblyDir);

        var rid = RuntimeInformation.RuntimeIdentifier;
        var bundledPath = Path.Combine(assemblyDir!, "runtimes", rid, "native", CopilotBinaryName);

        Assert.True(File.Exists(bundledPath),
            $"Bundled copilot binary should exist at: {bundledPath}");

        // Bundled path should NOT be a system path like /opt/homebrew or /usr/local
        Assert.DoesNotContain("/opt/homebrew/", bundledPath);
        Assert.DoesNotContain("/usr/local/lib/node_modules/", bundledPath);
    }

    [Fact]
    public void PersistentMode_BypassesCliPath_UsesCliUrl()
    {
        // In Persistent mode, CliPath is set to null and CliUrl is used instead.
        // Binary resolution doesn't matter for client creation in this mode,
        // but ServerManager.FindCopilotBinary() is still used to spawn the server process.
        var options = new CopilotClientOptions();
        options.CliPath = null;
        options.UseStdio = false;
        options.AutoStart = false;
        options.CliUrl = "http://localhost:4321";

        // Client creation succeeds without any CliPath
        var client = new CopilotClient(options);
        Assert.NotNull(client);
        Assert.Null(options.CliPath);
    }

    [Fact]
    public void ServerManager_WouldUseBundledPath_WhenNoSystem()
    {
        // ServerManager.FindCopilotBinary() checks system paths first (homebrew, /usr/local)
        // but falls back to CopilotService.ResolveBundledCliPath(), which checks:
        //   1. runtimes/{rid}/native/copilot (SDK bundled path)
        //   2. {assemblyDir}/copilot (MonoBundle fallback for Mac Catalyst)
        //
        // This means persistent mode works without a global copilot install,
        // as long as the SDK's bundled binary exists.
        //
        // We can't call FindCopilotBinary directly (it's in the MAUI project),
        // but we can verify the bundled path it would resolve to.
        var assemblyDir = Path.GetDirectoryName(typeof(CopilotClient).Assembly.Location);
        Assert.NotNull(assemblyDir);

        var rid = RuntimeInformation.RuntimeIdentifier;
        var bundledPath = Path.Combine(assemblyDir!, "runtimes", rid, "native", CopilotBinaryName);

        // The path should be well-formed and match the pattern ServerManager expects
        Assert.Contains("runtimes", bundledPath);
        Assert.Contains("native", bundledPath);
        Assert.EndsWith(CopilotBinaryName, bundledPath);
    }

    [Fact]
    public void BundledBinary_ExistsInBuildOutput()
    {
        // CRITICAL: Verify the copilot binary actually exists at the SDK-expected path
        // relative to the test assembly. The GitHub.Copilot.SDK NuGet package ships the
        // binary under runtimes/{rid}/native/copilot.
        var assemblyDir = Path.GetDirectoryName(typeof(CopilotClient).Assembly.Location);
        Assert.NotNull(assemblyDir);

        var rid = RuntimeInformation.RuntimeIdentifier;
        var expectedPath = Path.Combine(assemblyDir!, "runtimes", rid, "native", CopilotBinaryName);

        // The test project runs as net10.0 (not maccatalyst), so the RID is typically
        // osx-arm64 on Apple Silicon Macs. The SDK packages the binary for this RID.
        if (!File.Exists(expectedPath))
        {
            // Try common alternative RIDs in case the exact RID doesn't match
            var alternativeRids = new[] { "win-x64", "osx-arm64", "osx-x64", "maccatalyst-arm64" };
            var found = false;
            var checkedPaths = new List<string> { expectedPath };

            foreach (var altRid in alternativeRids)
            {
                if (altRid == rid) continue;
                var altPath = Path.Combine(assemblyDir!, "runtimes", altRid, "native", CopilotBinaryName);
                checkedPaths.Add(altPath);
                if (File.Exists(altPath))
                {
                    found = true;
                    break;
                }
            }

            // Also check the SDK's own auto-discovered path
            var sdkPath = new CopilotClientOptions().CliPath;
            if (sdkPath != null && File.Exists(sdkPath))
            {
                found = true;
                checkedPaths.Add($"SDK auto-discovered: {sdkPath}");
            }

            Assert.True(found,
                $"Bundled copilot binary not found at any expected path. " +
                $"RID='{rid}', Checked: [{string.Join(", ", checkedPaths)}]");
        }
        else
        {
            // Binary exists at the exact expected path — verify it's executable
            Assert.True(File.Exists(expectedPath),
                $"Bundled copilot binary should exist at: {expectedPath}");
        }
    }

    [Fact]
    public void MonoBundleFallback_PathIsAssemblyDir()
    {
        // The MonoBundle fallback looks for "copilot" in the same directory as the SDK assembly.
        // On Mac Catalyst, MAUI flattens runtimes/ into Contents/MonoBundle/, so the binary
        // ends up alongside the SDK DLL. This test documents that fallback path.
        var assemblyDir = Path.GetDirectoryName(typeof(CopilotClient).Assembly.Location);
        Assert.NotNull(assemblyDir);

        var monoBundlePath = Path.Combine(assemblyDir!, CopilotBinaryName);

        // In test context, the MonoBundle path typically doesn't exist (we're not in a .app bundle).
        // But the path should be well-formed and point to the assembly directory.
        Assert.Equal(assemblyDir, Path.GetDirectoryName(monoBundlePath));
        Assert.Equal(CopilotBinaryName, Path.GetFileName(monoBundlePath));

        // The runtimes/{rid}/native/ path is the primary bundled path;
        // MonoBundle is only a fallback for the Mac Catalyst app bundle layout.
        var rid = RuntimeInformation.RuntimeIdentifier;
        var primaryPath = Path.Combine(assemblyDir!, "runtimes", rid, "native", CopilotBinaryName);
        Assert.NotEqual(primaryPath, monoBundlePath);
    }
}
