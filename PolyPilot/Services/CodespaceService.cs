using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace PolyPilot.Services;

/// <summary>
/// Discovers GitHub Codespaces and manages port-forwarding tunnels to copilot --headless servers.
/// Requires SSH (SSHD) in the codespace container — used for both starting copilot and establishing tunnels.
/// A <c>gh cs ports forward</c> fallback exists but is only useful if copilot is already running,
/// since there is no way to start copilot remotely without SSH.
/// </summary>
public partial class CodespaceService
{
    /// <summary>
    /// Holds a running tunnel process (SSH or port-forward) and the local port it forwards.
    /// </summary>
    public sealed class TunnelHandle : IAsyncDisposable
    {
        public int LocalPort { get; }
        public bool IsSshTunnel { get; }
        private readonly Process _process;

        internal TunnelHandle(int localPort, Process process, bool isSshTunnel = false)
        {
            LocalPort = localPort;
            _process = process;
            IsSshTunnel = isSshTunnel;
        }

        public bool IsAlive => !_process.HasExited;

        public async ValueTask DisposeAsync()
        {
            try
            {
                if (!_process.HasExited)
                    _process.Kill(entireProcessTree: true);
                await _process.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(3));
            }
            catch { }
            _process.Dispose();
        }
    }
    /// <summary>
    /// Opens a port-forwarding tunnel: a free local port → codespace:remotePort.
    /// Uses <c>gh cs ports forward</c> — works without an SSH server in the container.
    /// Waits up to <paramref name="connectTimeoutSeconds"/> for the tunnel to become reachable.
    /// When <paramref name="requireCopilot"/> is false, returns the handle even if copilot isn't
    /// detected — the caller can create the group in WaitingForCopilot state.
    /// </summary>
    public async Task<(TunnelHandle handle, bool copilotReady)> OpenTunnelAsync(
        string codespaceName, int remotePort = 4321, int connectTimeoutSeconds = 15, bool requireCopilot = true)
    {
        var localPort = FindFreePort();

        // `gh cs ports forward <remote>:<local>` uses GitHub's codespace networking
        // and does NOT require an SSH server to be installed in the container.
        var psi = new ProcessStartInfo
        {
            FileName = FindGhPath(),
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        psi.ArgumentList.Add("cs");
        psi.ArgumentList.Add("ports");
        psi.ArgumentList.Add("forward");
        psi.ArgumentList.Add($"{remotePort}:{localPort}");
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add(codespaceName);

        var process = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start gh cs ports forward");
        var handle = new TunnelHandle(localPort, process);

        // `gh cs ports forward` binds the local port immediately when the process starts.
        // We cannot use a bare TCP check because gh accepts the local connection regardless
        // of whether anything is listening at the remote end.
        // Instead: connect and try to read — copilot holds the connection open waiting for
        // the ACP handshake, while gh closes it immediately when remote:port isn't listening.
        var deadline = DateTime.UtcNow.AddSeconds(connectTimeoutSeconds);
        while (DateTime.UtcNow < deadline)
        {
            if (process.HasExited)
            {
                string errMsg = "";
                try { errMsg = await process.StandardError.ReadToEndAsync().WaitAsync(TimeSpan.FromSeconds(1)); } catch { }
                await handle.DisposeAsync();
                throw new InvalidOperationException($"Port forward exited (code {process.ExitCode}). {errMsg.Trim()}");
            }

            if (await IsCopilotListeningAsync(localPort))
                return (handle, copilotReady: true);

            await Task.Delay(500);
        }

        if (!requireCopilot && handle.IsAlive)
        {
            // Tunnel is alive but copilot isn't detected — return handle anyway
            // so the caller can create the group in WaitingForCopilot state.
            return (handle, copilotReady: false);
        }

        // Timed out — kill process and throw
        await handle.DisposeAsync();
        throw new TimeoutException($"Timed out waiting for copilot --headless in codespace '{codespaceName}' (port {remotePort}). Run 'copilot --headless --port {remotePort}' in the codespace terminal and retry.");
    }

    /// <summary>
    /// Opens an SSH tunnel: <c>gh cs ssh -c name -- -L local:localhost:remote</c>.
    /// The SSH session stays alive as the tunnel. After connecting, starts copilot --headless
    /// inside the codespace if it's not already running.
    /// </summary>
    /// <returns>
    /// A <see cref="TunnelHandle"/> with <see cref="TunnelHandle.IsSshTunnel"/> = true,
    /// or <c>null</c> if SSH is unavailable (no SSHD in the container).
    /// </returns>
    public async Task<TunnelHandle?> OpenSshTunnelAsync(
        string codespaceName, int remotePort = 4321, int connectTimeoutSeconds = 30)
    {
        var localPort = FindFreePort();

        var psi = new ProcessStartInfo
        {
            FileName = FindGhPath(),
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            CreateNoWindow = true
        };
        psi.ArgumentList.Add("cs");
        psi.ArgumentList.Add("ssh");
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add(codespaceName);
        psi.ArgumentList.Add("--");
        psi.ArgumentList.Add("-L");
        psi.ArgumentList.Add($"{localPort}:localhost:{remotePort}");
        psi.ArgumentList.Add("-o");
        psi.ArgumentList.Add("ExitOnForwardFailure=yes");
        // Keep the SSH session alive for tunneling
        psi.ArgumentList.Add("-o");
        psi.ArgumentList.Add("ServerAliveInterval=15");
        psi.ArgumentList.Add("-o");
        psi.ArgumentList.Add("ServerAliveCountMax=3");

        Process process;
        try
        {
            process = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start gh cs ssh");
        }
        catch
        {
            return null;
        }

        // Read stderr in background to detect "SSH server not available" quickly
        var sshFailed = 0; // 0=false, 1=true; int for Volatile.Read/Write (bool has no Volatile overload)
        var stderrTask = Task.Run(async () =>
        {
            try
            {
                // Read stderr continuously — gh cs ssh emits the error message then exits.
                // Check each line for the SSH server unavailability pattern.
                while (true)
                {
                    var errLine = await process.StandardError.ReadLineAsync()
                        .WaitAsync(TimeSpan.FromSeconds(connectTimeoutSeconds));
                    if (errLine == null) break; // stream closed
                    if (errLine.Contains("SSH server") || errLine.Contains("error getting ssh") || errLine.Contains("failed to start SSH"))
                    {
                        Volatile.Write(ref sshFailed, 1);
                        break;
                    }
                }
            }
            catch { }
        });

        // Wait for the SSH tunnel to become functional by probing the local port.
        // gh cs ssh takes time to fetch SSH details and connect. We poll until
        // the local forwarded port accepts connections and copilot is listening.
        var deadline = DateTime.UtcNow.AddSeconds(connectTimeoutSeconds);
        bool copilotListening = false;
        while (DateTime.UtcNow < deadline)
        {
            if (process.HasExited || Volatile.Read(ref sshFailed) != 0)
            {
                // SSH failed — likely no SSHD in the container
                try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
                process.Dispose();
                return null;
            }

            // Check if copilot is reachable through the tunnel
            if (await IsCopilotListeningAsync(localPort))
            {
                copilotListening = true;
                break;
            }

            await Task.Delay(1000);
        }

        if (!copilotListening && !process.HasExited)
        {
            // SSH connected but copilot isn't listening yet.
            // Try to start copilot via the SSH stdin.
            Console.WriteLine($"[CodespaceService] SSH tunnel open but copilot not listening on port {remotePort}. Starting copilot via SSH...");
            try
            {
                // Inject GitHub auth token so copilot can authenticate with GitHub's backend.
                // Codespaces don't have gh auth configured for SSH sessions by default.
                // Token is written via heredoc to gh auth login's stdin to avoid exposing it
                // in process arguments visible via /proc/<pid>/cmdline.
                var localToken = await GetLocalGhTokenAsync();
                var authCmd = "";
                if (!string.IsNullOrEmpty(localToken))
                {
                    authCmd = $"gh auth login --with-token <<< '{localToken.Replace("'", "'\\''")}' 2>/dev/null; ";
                    Console.WriteLine($"[CodespaceService] Injecting gh auth token into codespace SSH session");
                }

                var startCmd =
                    $"fuser -k {remotePort}/tcp 2>/dev/null; sleep 1; " +
                    authCmd +
                    $"nohup copilot --headless --port {remotePort} > /tmp/polypilot-copilot.log 2>&1 & disown $!; " +
                    $"sleep 3; echo COPILOT_START_DONE\n";
                await process.StandardInput.WriteAsync(startCmd);
                await process.StandardInput.FlushAsync();

                // Wait for copilot to actually start listening
                var startDeadline = DateTime.UtcNow.AddSeconds(15);
                while (DateTime.UtcNow < startDeadline)
                {
                    if (process.HasExited) break;
                    if (await IsCopilotListeningAsync(localPort))
                    {
                        copilotListening = true;
                        break;
                    }
                    await Task.Delay(1000);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[CodespaceService] Failed to start copilot via SSH: {ex.Message}");
            }
        }

        if (process.HasExited)
        {
            process.Dispose();
            return null;
        }

        var handle = new TunnelHandle(localPort, process, isSshTunnel: true);

        if (!copilotListening)
        {
            // SSH tunnel is alive but copilot still not listening — clean up
            await handle.DisposeAsync();
            throw new TimeoutException(
                $"SSH tunnel connected to codespace '{codespaceName}' but copilot is not running on port {remotePort}. " +
                $"Open a terminal and run: copilot --headless --port {remotePort}");
        }

        Console.WriteLine($"[CodespaceService] SSH tunnel established: localhost:{localPort} → {codespaceName}:{remotePort}");
        return handle;
    }

    /// <summary>
    /// Detects whether copilot --headless is actually listening at the remote end of a
    /// gh cs ports forward tunnel. The probe works by exploiting a behavioral difference:
    /// <list type="bullet">
    ///   <item>copilot is listening → TCP connects, connection stays open (copilot waits for ACP handshake)</item>
    ///   <item>nothing listening → TCP connects locally but gh immediately closes it (connection refused at remote)</item>
    /// </list>
    /// So: read with a short timeout. Timeout = connection held open = copilot is ready.
    /// IOException on read = connection closed immediately = not yet listening.
    /// </summary>
    private static async Task<bool> IsCopilotListeningAsync(int port)
    {
        try
        {
            using var tcp = new TcpClient();
            await tcp.ConnectAsync("127.0.0.1", port).WaitAsync(TimeSpan.FromMilliseconds(500));

            var stream = tcp.GetStream();
            var buf = new byte[1];
            try
            {
                using var readCts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
                var bytesRead = await stream.ReadAsync(buf, readCts.Token);
                // Got data — something is definitely running
                return bytesRead > 0;
            }
            catch (OperationCanceledException)
            {
                // Timed out waiting for data, but connection is still open.
                // This is exactly what copilot does: it waits for us to send the ACP handshake.
                return true;
            }
            catch (IOException)
            {
                // Connection was closed by the remote end.
                // gh cs ports forward closes immediately when remote:port isn't listening.
                return false;
            }
        }
        catch
        {
            return false;
        }
    }

    // TOCTOU: port may be taken between Stop() and the caller binding to it.
    // Callers handle this via retry (health check loop retries on tunnel failure).
    private static int FindFreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
