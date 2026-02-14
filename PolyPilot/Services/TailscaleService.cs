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
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    public bool IsInstalled { get; private set; }
    public bool IsRunning { get; private set; }
    public string? TailscaleIp { get; private set; }
    public string? MagicDnsName { get; private set; }
    public IReadOnlyList<TailscalePeer> Peers { get; private set; } = Array.Empty<TailscalePeer>();

    public async Task DetectAsync()
    {
        if (!_checked)
            _checked = true;

        await RefreshStatusAsync();
    }

    public async Task RefreshStatusAsync()
    {
        await _refreshLock.WaitAsync();
        try
        {
            var parsed = false;
            try
            {
                // Try Unix socket API first (macOS/Linux)
                var socketPath = "/var/run/tailscale/tailscaled.sock";
                if (!OperatingSystem.IsWindows() && File.Exists(socketPath))
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
                    parsed = ParseStatus(json);
                    if (parsed)
                    {
                        IsInstalled = true;
                        return;
                    }
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
                    {
                        parsed = ParseStatus(json);
                        if (parsed)
                        {
                            IsInstalled = true;
                            return;
                        }
                    }
                }
            }
            catch { /* Tailscale not available */ }

            if (!parsed)
            {
                IsRunning = false;
                TailscaleIp = null;
                MagicDnsName = null;
                Peers = Array.Empty<TailscalePeer>();
            }
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    private bool ParseStatus(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (root.TryGetProperty("BackendState", out var state) &&
            state.GetString() == "Running")
        {
            IsRunning = true;
        }
        else if (root.TryGetProperty("BackendState", out _))
        {
            IsRunning = false;
        }

        if (root.TryGetProperty("Self", out var self))
        {
            if (self.TryGetProperty("TailscaleIPs", out var ips) && ips.GetArrayLength() > 0)
                TailscaleIp = ips[0].GetString();
            if (self.TryGetProperty("DNSName", out var dns))
                MagicDnsName = dns.GetString()?.TrimEnd('.');
        }

        var peers = new List<TailscalePeer>();
        if (root.TryGetProperty("Peer", out var peerMap))
        {
            foreach (var prop in peerMap.EnumerateObject())
            {
                var node = prop.Value;
                var peer = new TailscalePeer();

                if (node.TryGetProperty("HostName", out var hn))
                    peer.HostName = hn.GetString() ?? "";

                if (node.TryGetProperty("DNSName", out var dn))
                    peer.MagicDnsName = dn.GetString()?.TrimEnd('.');

                if (node.TryGetProperty("TailscaleIPs", out var pIps) && pIps.GetArrayLength() > 0)
                    peer.TailscaleIp = pIps[0].GetString() ?? "";

                if (node.TryGetProperty("Online", out var online))
                    peer.Online = online.GetBoolean();

                if (node.TryGetProperty("OS", out var os))
                    peer.OS = os.GetString() ?? "";

                if (!string.IsNullOrWhiteSpace(peer.TailscaleIp))
                    peers.Add(peer);
            }
        }
        Peers = peers;

        return true;
    }
}

public class TailscalePeer
{
    public string HostName { get; set; } = "";
    public string? MagicDnsName { get; set; }
    public string TailscaleIp { get; set; } = "";
    public bool Online { get; set; }
    public string OS { get; set; } = "";
}
