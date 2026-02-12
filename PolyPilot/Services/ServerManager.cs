using System.Diagnostics;
using System.IO.Compression;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using PolyPilot.Models;

namespace PolyPilot.Services;

public class ServerManager
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

    private static string? _embeddedBinDir;
    private static string EmbeddedBinDir => _embeddedBinDir ??= Path.Combine(GetPolyPilotDir(), "bin");

    private const string GitHubReleaseUrlTemplate =
        "https://github.com/github/copilot-cli/releases/download/v{0}/copilot-{1}.tar.gz";

    /// <summary>
    /// Returns the platform-specific asset name for GitHub releases (e.g. "darwin-arm64", "linux-x64").
    /// </summary>
    private static string? GetReleaseAssetRid()
    {
        if (OperatingSystem.IsMacOS() || OperatingSystem.IsMacCatalyst())
            return RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "darwin-arm64" : "darwin-x64";
        if (OperatingSystem.IsLinux())
            return RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "linux-arm64" : "linux-x64";
        if (OperatingSystem.IsWindows())
            return "win-x64";
        return null;
    }

    /// <summary>
    /// Path where the downloaded copilot binary is stored.
    /// </summary>
    public static string EmbeddedBinaryPath
    {
        get
        {
            var name = OperatingSystem.IsWindows() ? "copilot.exe" : "copilot";
            return Path.Combine(EmbeddedBinDir, name);
        }
    }

    /// <summary>
    /// True when an embedded (downloaded) binary exists at ~/.polypilot/bin/copilot.
    /// </summary>
    public bool HasEmbeddedBinary => File.Exists(EmbeddedBinaryPath);

    /// <summary>
    /// True when any copilot binary can be found (embedded, system-installed, or on PATH).
    /// </summary>
    public bool HasAnyCopilotBinary
    {
        get
        {
            var path = FindCopilotBinary();
            if (path == "copilot" || path == "copilot.cmd")
            {
                // PATH fallback — check if it actually resolves
                try
                {
                    var psi = new ProcessStartInfo(path, "--version")
                    { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
                    using var p = Process.Start(psi);
                    p?.WaitForExit(3000);
                    return p?.ExitCode == 0;
                }
                catch { return false; }
            }
            return true;
        }
    }

    /// <summary>
    /// Returns the version of the embedded binary, or null.
    /// </summary>
    public string? GetEmbeddedVersion()
    {
        if (!HasEmbeddedBinary) return null;
        try
        {
            var psi = new ProcessStartInfo(EmbeddedBinaryPath, "--version")
            { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
            using var p = Process.Start(psi);
            var output = p?.StandardOutput.ReadToEnd()?.Trim();
            p?.WaitForExit(3000);
            return output;
        }
        catch { return null; }
    }

    public event Action<string>? OnDownloadProgress;

    /// <summary>
    /// Downloads the Copilot CLI binary from GitHub releases and stores it at ~/.polypilot/bin/copilot.
    /// </summary>
    public async Task<bool> DownloadCopilotBinaryAsync(string version, CancellationToken ct = default)
    {
        var rid = GetReleaseAssetRid();
        if (rid == null)
        {
            OnDownloadProgress?.Invoke("Unsupported platform");
            return false;
        }

        var url = string.Format(GitHubReleaseUrlTemplate, version, rid);
        Console.WriteLine($"[ServerManager] Downloading Copilot CLI from {url}");
        OnDownloadProgress?.Invoke("Downloading...");

        try
        {
            Directory.CreateDirectory(EmbeddedBinDir);
            using var http = new HttpClient();
            http.DefaultRequestHeaders.UserAgent.ParseAdd("PolyPilot/1.0");

            using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();

            var totalBytes = response.Content.Headers.ContentLength;
            var tarGzPath = Path.Combine(EmbeddedBinDir, $"copilot-{rid}.tar.gz");

            await using (var contentStream = await response.Content.ReadAsStreamAsync(ct))
            await using (var fileStream = File.Create(tarGzPath))
            {
                var buffer = new byte[81920];
                long downloaded = 0;
                int bytesRead;
                while ((bytesRead = await contentStream.ReadAsync(buffer, ct)) > 0)
                {
                    await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), ct);
                    downloaded += bytesRead;
                    if (totalBytes > 0)
                        OnDownloadProgress?.Invoke($"Downloading... {downloaded * 100 / totalBytes}%");
                }
            }

            OnDownloadProgress?.Invoke("Extracting...");

            // Extract .tar.gz — use system tar on Unix, manual GZip+tar on Windows
            if (!OperatingSystem.IsWindows())
            {
                var psi = new ProcessStartInfo("tar", $"xzf \"{tarGzPath}\" -C \"{EmbeddedBinDir}\"")
                { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
                using var proc = Process.Start(psi);
                await proc!.WaitForExitAsync(ct);
                if (proc.ExitCode != 0)
                {
                    var err = await proc.StandardError.ReadToEndAsync(ct);
                    Console.WriteLine($"[ServerManager] tar failed: {err}");
                    OnDownloadProgress?.Invoke($"Extract failed: {err}");
                    return false;
                }
            }
            else
            {
                // Windows: use tar.exe (available since Win10 1803)
                var psi = new ProcessStartInfo("tar", $"xzf \"{tarGzPath}\" -C \"{EmbeddedBinDir}\"")
                { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
                using var proc = Process.Start(psi);
                await proc!.WaitForExitAsync(ct);
            }

            // Clean up archive
            try { File.Delete(tarGzPath); } catch { }

            // Make executable on Unix
            if (!OperatingSystem.IsWindows() && File.Exists(EmbeddedBinaryPath))
            {
                var chmod = Process.Start("chmod", $"+x \"{EmbeddedBinaryPath}\"");
                chmod?.WaitForExit(3000);
            }

            if (File.Exists(EmbeddedBinaryPath))
            {
                OnDownloadProgress?.Invoke("Done!");
                Console.WriteLine($"[ServerManager] Copilot CLI installed to {EmbeddedBinaryPath}");
                return true;
            }

            OnDownloadProgress?.Invoke("Binary not found after extraction");
            return false;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ServerManager] Download failed: {ex.Message}");
            OnDownloadProgress?.Invoke($"Failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Fetches the latest release version tag from GitHub.
    /// </summary>
    public static async Task<string?> GetLatestReleaseVersionAsync(CancellationToken ct = default)
    {
        try
        {
            using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });
            http.DefaultRequestHeaders.UserAgent.ParseAdd("PolyPilot/1.0");
            var response = await http.GetAsync(
                "https://github.com/github/copilot-cli/releases/latest", ct);
            // GitHub redirects to /releases/tag/vX.Y.Z
            if (response.Headers.Location?.AbsolutePath is string loc)
            {
                var tag = loc.Split('/').Last();
                return tag.StartsWith('v') ? tag[1..] : tag;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ServerManager] Failed to fetch latest version: {ex.Message}");
        }
        return null;
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
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = false
            };

            // Use ArgumentList for proper escaping (especially MCP JSON)
            psi.ArgumentList.Add("--headless");
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

    public static string FindCopilotBinary()
    {
        // Try embedded (downloaded) binary first
        if (File.Exists(EmbeddedBinaryPath))
            return EmbeddedBinaryPath;

        // Try platform-specific native binaries (system-installed)
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
                "/opt/homebrew/bin/copilot",
                "/opt/homebrew/lib/node_modules/@github/copilot/node_modules/@github/copilot-darwin-arm64/copilot",
                "/usr/local/lib/node_modules/@github/copilot/node_modules/@github/copilot-darwin-arm64/copilot",
                "/usr/local/bin/copilot",
            });
        }

        foreach (var path in nativePaths)
        {
            if (File.Exists(path)) return path;
        }

        // Fallback to node wrapper (works if copilot is on PATH)
        return OperatingSystem.IsWindows() ? "copilot.cmd" : "copilot";
    }
}
