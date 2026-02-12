using System.Diagnostics;
using System.Net.Http;
using System.Text.Json;

namespace PolyPilot.Services;

/// <summary>
/// Detects Tailscale status via the local API (Unix socket) or CLI fallback.
/// </summary>
public class TailscaleService
{
    private bool _checked;
    public bool IsRunning { get; private set; }
    public string? TailscaleIp { get; private set; }
    public string? MagicDnsName { get; private set; }

    public async Task DetectAsync()
    {
        if (_checked) return;
        _checked = true;

        try
        {
            // Try Unix socket API first (macOS/Linux)
            var socketPath = "/var/run/tailscale/tailscaled.sock";
            if (File.Exists(socketPath))
            {
                var handler = new SocketsHttpHandler
                {
                    ConnectCallback = async (ctx, ct) =>
                    {
                        var socket = new System.Net.Sockets.Socket(
                            System.Net.Sockets.AddressFamily.Unix,
                            System.Net.Sockets.SocketType.Stream,
                            System.Net.Sockets.ProtocolType.Unspecified);
                        await socket.ConnectAsync(new System.Net.Sockets.UnixDomainSocketEndPoint(socketPath), ct);
                        return new System.Net.Sockets.NetworkStream(socket, ownsSocket: true);
                    }
                };
                using var client = new HttpClient(handler) { BaseAddress = new Uri("http://local-tailscaled.sock") };
                client.Timeout = TimeSpan.FromSeconds(3);
                var json = await client.GetStringAsync("/localapi/v0/status");
                ParseStatus(json);
                return;
            }
        }
        catch { /* Fall through to CLI */ }

        try
        {
            // CLI fallback
            var psi = new ProcessStartInfo("tailscale", "status --json")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var proc = Process.Start(psi);
            if (proc != null)
            {
                var json = await proc.StandardOutput.ReadToEndAsync();
                await proc.WaitForExitAsync();
                if (proc.ExitCode == 0)
                    ParseStatus(json);
            }
        }
        catch { /* Tailscale not available */ }
    }

    private void ParseStatus(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (root.TryGetProperty("BackendState", out var state) &&
            state.GetString() == "Running")
        {
            IsRunning = true;
        }

        if (root.TryGetProperty("Self", out var self))
        {
            if (self.TryGetProperty("TailscaleIPs", out var ips) && ips.GetArrayLength() > 0)
                TailscaleIp = ips[0].GetString();
            if (self.TryGetProperty("DNSName", out var dns))
                MagicDnsName = dns.GetString()?.TrimEnd('.');
        }
    }
}
