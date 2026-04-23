using System.Diagnostics;
using Microsoft.Maui.DevFlow.Driver;

namespace PolyPilot.IntegrationTests.Fixtures;

/// <summary>
/// xUnit collection fixture that manages the PolyPilot app lifecycle.
/// Builds, launches, connects DevFlow, and provides AgentClient for tests.
///
/// Based on the pattern from dotnet/maui-labs DevFlow integration tests.
/// Set DEVFLOW_TEST_PLATFORM to: maccatalyst, windows, linux (default: maccatalyst)
/// Set POLYPILOT_AGENT_PORT to connect to an already-running app instead of launching.
/// </summary>
public class AppFixture : IAsyncLifetime
{
    private Process? _appProcess;
    private Process? _xvfbProcess;

    public AgentClient Client { get; private set; } = null!;
    public HttpClient Http { get; private set; } = null!;
    public int AgentPort { get; private set; }
    public string AgentBaseUrl => $"http://localhost:{AgentPort}";
    public string Platform { get; private set; } = "";

    public async Task InitializeAsync()
    {
        Platform = Environment.GetEnvironmentVariable("DEVFLOW_TEST_PLATFORM")
            ?? (OperatingSystem.IsWindows() ? "windows"
                : OperatingSystem.IsLinux() ? "linux"
                : "maccatalyst");

        var existingPort = Environment.GetEnvironmentVariable("POLYPILOT_AGENT_PORT");
        if (!string.IsNullOrEmpty(existingPort) && int.TryParse(existingPort, out var port))
        {
            AgentPort = port;
            Console.WriteLine($"[Fixture] Connecting to existing app on port {AgentPort}");
        }
        else
        {
            await BuildAndLaunchAsync();
        }

        Http = new HttpClient { BaseAddress = new Uri(AgentBaseUrl) };
        Client = new AgentClient("localhost", AgentPort);

        await WaitForAgentReadyAsync(TimeSpan.FromSeconds(60));
        Console.WriteLine($"[Fixture] Agent ready on {AgentBaseUrl} (platform: {Platform})");
    }

    public async Task DisposeAsync()
    {
        Http?.Dispose();

        if (_appProcess is { HasExited: false })
        {
            try { _appProcess.Kill(entireProcessTree: true); } catch { }
            await _appProcess.WaitForExitAsync();
        }

        if (_xvfbProcess is { HasExited: false })
        {
            try { _xvfbProcess.Kill(); } catch { }
        }
    }

    private async Task BuildAndLaunchAsync()
    {
        var repoRoot = FindRepoRoot();
        AgentPort = FindFreePort();

        Console.WriteLine($"[Fixture] Building PolyPilot for {Platform}...");

        var (project, tfm, binary) = Platform switch
        {
            "maccatalyst" => ("PolyPilot/PolyPilot.csproj", "net10.0-maccatalyst", ""),
            "windows" => ("PolyPilot/PolyPilot.csproj", "net10.0-windows10.0.19041.0", ""),
            "linux" => ("PolyPilot.Gtk/PolyPilot.Gtk.csproj", "net10.0", ""),
            _ => throw new InvalidOperationException($"Unknown platform: {Platform}")
        };

        // Build
        var buildResult = await RunProcessAsync("dotnet", $"build {project} -f {tfm} -c Debug --nologo", repoRoot);
        if (buildResult.ExitCode != 0)
            throw new InvalidOperationException($"Build failed:\n{buildResult.Output}");

        // Find binary
        binary = Platform switch
        {
            "maccatalyst" => FindMacCatalystBinary(repoRoot, tfm),
            "windows" => FindWindowsBinary(repoRoot, tfm),
            "linux" => FindLinuxBinary(repoRoot),
            _ => throw new InvalidOperationException($"Unknown platform: {Platform}")
        };

        Console.WriteLine($"[Fixture] Launching: {binary}");

        // Start xvfb for Linux
        if (Platform == "linux")
        {
            _xvfbProcess = Process.Start(new ProcessStartInfo("Xvfb", ":99 -screen 0 1920x1080x24")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            });
            await Task.Delay(2000);
        }

        // Launch app
        var startInfo = new ProcessStartInfo
        {
            FileName = binary,
            WorkingDirectory = repoRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        startInfo.Environment["DEVFLOW_TEST_PORT"] = AgentPort.ToString();
        if (Platform == "linux")
            startInfo.Environment["DISPLAY"] = ":99";

        _appProcess = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start PolyPilot");
    }

    private async Task WaitForAgentReadyAsync(TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        while (!cts.IsCancellationRequested)
        {
            try
            {
                var response = await Http.GetAsync("/api/status", cts.Token);
                if (response.IsSuccessStatusCode)
                    return;
            }
            catch (Exception) when (!cts.IsCancellationRequested) { }

            await Task.Delay(1000, cts.Token);
        }
        throw new TimeoutException($"Agent did not become ready within {timeout.TotalSeconds}s on {AgentBaseUrl}");
    }

    private static string FindMacCatalystBinary(string repoRoot, string tfm)
    {
        var searchDir = Path.Combine(repoRoot, "PolyPilot", "bin", "Debug", tfm);
        var app = Directory.GetDirectories(searchDir, "*.app", SearchOption.AllDirectories).FirstOrDefault()
            ?? throw new FileNotFoundException($"No .app bundle found in {searchDir}");
        var binary = Path.Combine(app, "Contents", "MacOS", "PolyPilot");
        if (!File.Exists(binary))
            throw new FileNotFoundException($"Binary not found: {binary}");
        return binary;
    }

    private static string FindWindowsBinary(string repoRoot, string tfm)
    {
        var searchDir = Path.Combine(repoRoot, "PolyPilot", "bin", "Debug");
        var exe = Directory.GetFiles(searchDir, "PolyPilot.exe", SearchOption.AllDirectories).FirstOrDefault()
            ?? throw new FileNotFoundException($"No PolyPilot.exe found in {searchDir}");
        return exe;
    }

    private static string FindLinuxBinary(string repoRoot)
    {
        var searchDir = Path.Combine(repoRoot, "PolyPilot.Gtk", "bin", "Debug", "net10.0");
        var dll = Directory.GetFiles(searchDir, "PolyPilot.Gtk.dll", SearchOption.TopDirectoryOnly).FirstOrDefault();
        if (dll != null)
            return $"dotnet {dll}";
        var exe = Directory.GetFiles(searchDir, "PolyPilot.Gtk", SearchOption.TopDirectoryOnly)
            .FirstOrDefault(f => !f.EndsWith(".dll") && !f.EndsWith(".pdb"));
        return exe ?? throw new FileNotFoundException($"No PolyPilot binary found in {searchDir}");
    }

    private static int FindFreePort()
    {
        using var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static string FindRepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null && !File.Exists(Path.Combine(dir, "PolyPilot.slnx")))
            dir = Path.GetDirectoryName(dir);
        return dir ?? throw new InvalidOperationException("Could not find repo root (PolyPilot.slnx)");
    }

    private static async Task<(int ExitCode, string Output)> RunProcessAsync(
        string fileName, string arguments, string workingDirectory, int timeoutSeconds = 300)
    {
        var psi = new ProcessStartInfo(fileName, arguments)
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        using var proc = Process.Start(psi)!;
        var output = await proc.StandardOutput.ReadToEndAsync();
        var error = await proc.StandardError.ReadToEndAsync();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
        await proc.WaitForExitAsync(cts.Token);

        return (proc.ExitCode, output + error);
    }
}

[CollectionDefinition("PolyPilot")]
public class PolyPilotCollection : ICollectionFixture<AppFixture> { }
