using System.Net.Sockets;
using System.Text.Json;

namespace PolyPilot.Services;

/// <summary>
/// Detects Tailscale status and network info on desktop platforms.
/// Uses the Tailscale local API via Unix socket when available,
/// falling back to the CLI.
/// </summary>
public class TailscaleService
{
    private const string SocketPath = "/var/run/tailscale/tailscaled.sock";

    public string? TailscaleIp { get; private set; }
    public string? MagicDnsName { get; private set; }
    public bool IsRunning { get; private set; }

    /// <summary>
    /// Refresh Tailscale status. Safe to call frequently — returns quickly if not available.
    /// </summary>
    public async Task RefreshAsync()
    {
        TailscaleIp = null;
        MagicDnsName = null;
        IsRunning = false;

#if IOS || ANDROID
        return;
#else
        try
        {
            // Try local API via Unix socket first (works regardless of CLI install path)
            if (File.Exists(SocketPath))
            {
                var json = await QueryLocalApiAsync();
                if (json != null && ParseStatus(json))
                {
                    IsRunning = true;
                    return;
                }
            }

            // Fallback: CLI
            await TryCliAsync();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Tailscale] Detection failed: {ex.Message}");
        }
#endif
    }

    private async Task<string?> QueryLocalApiAsync()
    {
        try
        {
            var handler = new SocketsHttpHandler
            {
                ConnectCallback = async (ctx, ct) =>
                {
                    var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                    await socket.ConnectAsync(new UnixDomainSocketEndPoint(SocketPath), ct);
                    return new NetworkStream(socket, ownsSocket: true);
                }
            };
            using var client = new HttpClient(handler) { BaseAddress = new Uri("http://local-tailscaled.sock/") };
            client.Timeout = TimeSpan.FromSeconds(3);
            return await client.GetStringAsync("localapi/v0/status");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Tailscale] Unix socket query failed: {ex.Message}");
            return null;
        }
    }

    private bool ParseStatus(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.TryGetProperty("Self", out var self))
            {
                if (self.TryGetProperty("TailscaleIPs", out var ips) && ips.GetArrayLength() > 0)
                    TailscaleIp = ips[0].GetString();

                if (self.TryGetProperty("DNSName", out var dns))
                {
                    var dnsName = dns.GetString()?.TrimEnd('.');
                    if (!string.IsNullOrEmpty(dnsName))
                        MagicDnsName = dnsName;
                }
            }

            return TailscaleIp != null;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Tailscale] Failed to parse status: {ex.Message}");
            return false;
        }
    }

    private async Task TryCliAsync()
    {
#if !IOS && !ANDROID
        try
        {
            using var proc = new System.Diagnostics.Process();
            proc.StartInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "tailscale",
                Arguments = "status --json",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            proc.Start();
            var output = await proc.StandardOutput.ReadToEndAsync();
            await proc.WaitForExitAsync();

            if (proc.ExitCode == 0 && !string.IsNullOrEmpty(output))
            {
                if (ParseStatus(output))
                    IsRunning = true;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Tailscale] CLI fallback failed: {ex.Message}");
        }
#endif
    }
}
