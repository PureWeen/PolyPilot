using System.Diagnostics;
using System.Net.Sockets;
using AutoPilot.App.Models;

namespace AutoPilot.App.Services;

public class ServerManager
{
    private static readonly string PidFilePath = Path.Combine(
        GetCopilotDir(), "autopilot-server.pid");

    private static string GetCopilotDir()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrEmpty(home))
            home = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrEmpty(home))
            home = Path.GetTempPath();
        return Path.Combine(home, ".copilot");
    }

    public bool IsServerRunning => CheckServerRunning();
    public int? ServerPid => ReadPidFile();
    public int ServerPort { get; private set; } = 4321;

    public event Action? OnStatusChanged;

    /// <summary>
    /// Check if a copilot server is listening on the given port
    /// </summary>
    public bool CheckServerRunning(string host = "localhost", int? port = null)
    {
        port ??= ServerPort;
        try
        {
            using var client = new TcpClient();
            var result = client.BeginConnect(host, port.Value, null, null);
            var success = result.AsyncWaitHandle.WaitOne(TimeSpan.FromSeconds(1));
            if (success && client.Connected)
            {
                client.EndConnect(result);
                return true;
            }
            return false;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Start copilot in headless server mode, detached from app lifecycle
    /// </summary>
    public async Task<bool> StartServerAsync(int port = 4321)
    {
        ServerPort = port;

        if (CheckServerRunning("localhost", port))
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
                Arguments = $"--headless --log-level info --port {port}",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = false
            };

            var process = Process.Start(psi);
            if (process == null)
            {
                Console.WriteLine("[ServerManager] Failed to start copilot process");
                return false;
            }

            SavePidFile(process.Id, port);
            Console.WriteLine($"[ServerManager] Started copilot server PID {process.Id} on port {port}");

            // Detach stdout/stderr readers so they don't hold the process
            _ = Task.Run(async () =>
            {
                try { while (await process.StandardOutput.ReadLineAsync() != null) { } } catch { }
            });
            _ = Task.Run(async () =>
            {
                try { while (await process.StandardError.ReadLineAsync() != null) { } } catch { }
            });

            // Wait for server to become available
            for (int i = 0; i < 15; i++)
            {
                await Task.Delay(1000);
                if (CheckServerRunning("localhost", port))
                {
                    Console.WriteLine($"[ServerManager] Server is ready on port {port}");
                    OnStatusChanged?.Invoke();
                    return true;
                }
            }

            Console.WriteLine("[ServerManager] Server started but not responding on port");
            OnStatusChanged?.Invoke();
            return false;
        }
        catch (Exception ex)
        {
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
                process.Kill();
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
        if (CheckServerRunning("localhost", info.Value.Port))
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
        // Try the native binary first (faster startup, better detachment)
        var nativePaths = new[]
        {
            "/opt/homebrew/lib/node_modules/@github/copilot/node_modules/@github/copilot-darwin-arm64/copilot",
            "/usr/local/lib/node_modules/@github/copilot/node_modules/@github/copilot-darwin-arm64/copilot",
        };

        foreach (var path in nativePaths)
        {
            if (File.Exists(path)) return path;
        }

        // Fallback to node wrapper
        return "copilot";
    }
}
