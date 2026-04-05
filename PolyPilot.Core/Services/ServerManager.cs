using System.Diagnostics;
using System.Net.Sockets;
using GitHub.Copilot.SDK;
using PolyPilot.Models;

namespace PolyPilot.Services;

public class ServerManager : IServerManager
{
    private static string? _pidFilePath;
    private static string PidFilePath => _pidFilePath ??= Path.Combine(
        GetPolyPilotDir(), "server.pid");

    private static string GetPolyPilotDir()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrEmpty(home))
            home = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrEmpty(home))
            home = Path.GetTempPath();
        return Path.Combine(home, ".polypilot");
    }

    public bool IsServerRunning => CheckServerRunning();
    public int? ServerPid => ReadPidFile();
    public int ServerPort { get; private set; } = 4321;
    public string? LastError { get; private set; }

    public event Action? OnStatusChanged;

    /// <summary>
    /// Check if a copilot server is listening on the given port
    /// </summary>
    public bool CheckServerRunning(string host = "127.0.0.1", int? port = null)
    {
        port ??= ServerPort;
        try
        {
            using var client = new TcpClient();
            // Use Task.WaitAny with a timeout task instead of CancellationTokenSource.
            // CancellationTokenSource disposal while ConnectAsync is still running its
            // internal cleanup can produce unobserved ObjectDisposedException tasks.
            var connectTask = client.ConnectAsync(host, port.Value);
            int index = Task.WaitAny(new[] { connectTask }, TimeSpan.FromSeconds(1));
            if (index == -1)
            {
                // Timed out — observe any future exception (Faulted or Cancelled) to prevent
                // unobserved task exceptions. NotOnRanToCompletion covers both Faulted and
                // Cancelled states; OnlyOnFaulted would miss Cancelled (which can occur if the
                // TcpClient is disposed while the connect is still in-flight).
                _ = connectTask.ContinueWith(t => { _ = t.Exception; },
                    TaskContinuationOptions.NotOnRanToCompletion);
                return false;
            }
            connectTask.GetAwaiter().GetResult();
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Start copilot in headless server mode, detached from app lifecycle
    /// </summary>
    public async Task<bool> StartServerAsync(int port = 4321, string? githubToken = null)
    {
        ServerPort = port;
        LastError = null;

        if (CheckServerRunning("127.0.0.1", port))
        {
            Console.WriteLine($"[ServerManager] Server already running on port {port}");
            OnStatusChanged?.Invoke();
            return true;
        }

        try
        {
            // Use the native binary directly for better detachment
            var copilotPath = FindCopilotBinary();
            var psi = new ProcessStartInfo
            {
                FileName = copilotPath,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = false
            };

            // Forward the GitHub token via environment variable so the headless server
            // can authenticate even when the macOS Keychain is inaccessible (e.g., the
            // Keychain entry was created in a terminal session and the ACL dialog can't
            // be shown for a background process).
            if (!string.IsNullOrEmpty(githubToken))
            {
                psi.Environment["COPILOT_GITHUB_TOKEN"] = githubToken;
                Console.WriteLine("[ServerManager] Passing COPILOT_GITHUB_TOKEN to headless server");
            }

            // Use ArgumentList for proper escaping (especially MCP JSON)
            psi.ArgumentList.Add("--headless");
            psi.ArgumentList.Add("--no-auto-update");
            psi.ArgumentList.Add("--log-level");
            psi.ArgumentList.Add("info");
            psi.ArgumentList.Add("--port");
            psi.ArgumentList.Add(port.ToString());

            // Pass additional MCP server configs so tools are available
            foreach (var arg in CopilotService.GetMcpCliArgs())
                psi.ArgumentList.Add(arg);

            var process = Process.Start(psi);
            if (process == null)
            {
                LastError = "Failed to launch the copilot process (Process.Start returned null).";
                Console.WriteLine($"[ServerManager] {LastError}");
                return false;
            }

            SavePidFile(process.Id, port);
            Console.WriteLine($"[ServerManager] Started copilot server PID {process.Id} on port {port}");

            // Collect stderr lines for diagnostics while also draining the pipe to prevent deadlock.
            // stdout is drained silently; stderr is captured for error reporting.
            var stderrLines = new System.Collections.Concurrent.ConcurrentQueue<string>();
            var t1 = Task.Run(async () => { try { while (await process.StandardOutput.ReadLineAsync() != null) { } } catch { } });
            var t2 = Task.Run(async () =>
            {
                try
                {
                    string? line;
                    while ((line = await process.StandardError.ReadLineAsync()) != null)
                        stderrLines.Enqueue(line);
                }
                catch { }
            });
            _ = Task.WhenAll(t1, t2).ContinueWith(_ => process.Dispose());

            // Wait for server to become available
            for (int i = 0; i < 15; i++)
            {
                await Task.Delay(1000);
                if (CheckServerRunning("127.0.0.1", port))
                {
                    Console.WriteLine($"[ServerManager] Server is ready on port {port}");
                    OnStatusChanged?.Invoke();
                    return true;
                }
            }

            // Server didn't become ready — wait briefly for any final stderr output then collect diagnostics
            await Task.WhenAny(t2, Task.Delay(500));
            var stderr = string.Join("\n", stderrLines).Trim();
            LastError = string.IsNullOrEmpty(stderr)
                ? $"Server process started (PID {process.Id}) but did not respond on port {port} after 15 seconds."
                : $"Server process started (PID {process.Id}) but did not respond on port {port} after 15 seconds.\nProcess output:\n{stderr}";
            Console.WriteLine($"[ServerManager] {LastError}");
            OnStatusChanged?.Invoke();
            return false;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            Console.WriteLine($"[ServerManager] Error starting server: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Stop the persistent server
    /// </summary>
    public void StopServer()
    {
        var pid = ReadPidFile();
        if (pid != null)
        {
            try
            {
                var process = Process.GetProcessById(pid.Value);
                ProcessHelper.SafeKillAndDispose(process, entireProcessTree: false);
                Console.WriteLine($"[ServerManager] Killed server PID {pid}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ServerManager] Error stopping server: {ex.Message}");
            }
            DeletePidFile();
            OnStatusChanged?.Invoke();
        }
    }

    /// <summary>
    /// Check if a server from a previous app session is still alive
    /// </summary>
    public bool DetectExistingServer()
    {
        var info = ReadPidFileInfo();
        if (info == null) return false;

        ServerPort = info.Value.Port;
        if (CheckServerRunning("127.0.0.1", info.Value.Port))
        {
            Console.WriteLine($"[ServerManager] Found existing server PID {info.Value.Pid} on port {info.Value.Port}");
            return true;
        }

        // PID file exists but server is dead — clean up
        DeletePidFile();
        return false;
    }

    private void SavePidFile(int pid, int port)
    {
        try
        {
            var dir = Path.GetDirectoryName(PidFilePath)!;
            Directory.CreateDirectory(dir);
            File.WriteAllText(PidFilePath, $"{pid}\n{port}");
        }
        catch { }
    }

    private int? ReadPidFile()
    {
        return ReadPidFileInfo()?.Pid;
    }

    private (int Pid, int Port)? ReadPidFileInfo()
    {
        try
        {
            if (!File.Exists(PidFilePath)) return null;
            var lines = File.ReadAllLines(PidFilePath);
            if (lines.Length >= 2 && int.TryParse(lines[0], out var pid) && int.TryParse(lines[1], out var port))
                return (pid, port);
            if (lines.Length >= 1 && int.TryParse(lines[0], out pid))
                return (pid, 4321);
        }
        catch { }
        return null;
    }

    private void DeletePidFile()
    {
        try { File.Delete(PidFilePath); } catch { }
    }

    private static string FindCopilotBinary()
    {
        // Prefer the SDK-bundled binary — it's guaranteed to match the SDK's protocol version.
        // System-installed CLIs may have been updated independently and could have a mismatched protocol.
        var bundledPath = CopilotService.ResolveBundledCliPath();
        if (bundledPath != null)
            return bundledPath;

        Console.WriteLine($"[ServerManager] Bundled copilot binary not found. " +
            $"Assembly.Location='{typeof(CopilotClient).Assembly.Location}', " +
            $"AppContext.BaseDirectory='{AppContext.BaseDirectory}'");

        // Fall back to platform-specific native binaries (system-installed)
        var nativePaths = new List<string>();

        if (OperatingSystem.IsWindows())
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            nativePaths.AddRange(new[]
            {
                Path.Combine(appData, "npm", "node_modules", "@github", "copilot", "node_modules", "@github", "copilot-win-x64", "copilot.exe"),
                Path.Combine(localAppData, "npm", "node_modules", "@github", "copilot", "node_modules", "@github", "copilot-win-x64", "copilot.exe"),
                Path.Combine(appData, "npm", "copilot.cmd"),
            });
        }
        else
        {
            nativePaths.AddRange(new[]
            {
                "/opt/homebrew/lib/node_modules/@github/copilot/node_modules/@github/copilot-darwin-arm64/copilot",
                "/usr/local/lib/node_modules/@github/copilot/node_modules/@github/copilot-darwin-arm64/copilot",
            });
        }

        foreach (var path in nativePaths)
        {
            if (File.Exists(path))
            {
                Console.WriteLine($"[ServerManager] Using system copilot binary: {path}");
                return path;
            }
        }

        // Fallback to node wrapper (works if copilot is on PATH)
        Console.WriteLine("[ServerManager] WARNING: No copilot binary found at any known path, falling back to PATH lookup");
        return OperatingSystem.IsWindows() ? "copilot.cmd" : "copilot";
    }
}
