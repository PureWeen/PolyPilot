using System.Collections.Concurrent;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using PolyPilot.Models;

namespace PolyPilot.Services;

/// <summary>
/// Lightweight peer discovery for Fiesta workers/organizers.
/// Combines LAN multicast discovery with optional Tailscale peer polling
/// and tailnet unicast announcement forwarding.
/// </summary>
public sealed class FiestaDiscoveryService : IDisposable
{
    private const string ServiceTag = "_polypilot-fiesta._tcp.local";
    private const string MulticastAddress = "239.255.80.80";
    private const int MulticastPort = 45454;
    private static readonly TimeSpan PeerTtl = TimeSpan.FromSeconds(12);
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private readonly ConcurrentDictionary<string, FiestaPeerInfo> _peers = new(StringComparer.Ordinal);
    private readonly TailscaleService _tailscale;

    private CancellationTokenSource? _cts;
    private Task? _announceTask;
    private Task? _receiveTask;
    private Task? _cleanupTask;
    private Task? _tailscaleBrowseTask;
    private Task? _tailnetAnnounceTask;
    private UdpClient? _announceClient;
    private UdpClient? _receiveClient;
    private FiestaPeerInfo? _localPeer;
    private bool _isBrowsing;
    private bool _tailscaleBrowsingEnabled;
    private bool _tailnetBroadcastEnabled;

    public event Action? OnPeersChanged;

    public bool IsRunning => _cts is { IsCancellationRequested: false };
    public bool IsAdvertising { get; private set; }
    public bool IsBrowsing => _isBrowsing;
    public bool IsTailscaleBrowsing => _tailscaleBrowsingEnabled;
    public bool IsTailnetBroadcastEnabled => _tailnetBroadcastEnabled;
    public IReadOnlyList<FiestaPeerInfo> Peers => _peers.Values
        .OrderByDescending(p => p.LastSeenAt)
        .ThenBy(p => p.MachineName, StringComparer.OrdinalIgnoreCase)
        .ToList();

    public FiestaDiscoveryService(TailscaleService tailscale)
    {
        _tailscale = tailscale;
    }

    public void Start(
        FiestaPeerInfo localPeer,
        bool advertise,
        bool browse,
        bool tailscaleBrowse = false,
        bool tailnetBroadcast = false)
    {
        _localPeer = localPeer;
        _isBrowsing = browse;
        _tailscaleBrowsingEnabled = tailscaleBrowse;
        _tailnetBroadcastEnabled = tailnetBroadcast;
        IsAdvertising = advertise;

        if (IsRunning) return;

        _cts = new CancellationTokenSource();
        _announceClient = new UdpClient();
        _announceClient.MulticastLoopback = false;
        _announceClient.EnableBroadcast = true;

        _receiveClient = new UdpClient(AddressFamily.InterNetwork);
        _receiveClient.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        _receiveClient.ExclusiveAddressUse = false;
        _receiveClient.Client.Bind(new IPEndPoint(IPAddress.Any, MulticastPort));
        _receiveClient.JoinMulticastGroup(IPAddress.Parse(MulticastAddress));

        _announceTask = Task.Run(() => AnnounceLoopAsync(_cts.Token));
        _receiveTask = Task.Run(() => ReceiveLoopAsync(_cts.Token));
        _cleanupTask = Task.Run(() => CleanupLoopAsync(_cts.Token));
        _tailscaleBrowseTask = Task.Run(() => TailscaleBrowseLoopAsync(_cts.Token));
        _tailnetAnnounceTask = Task.Run(() => TailnetAnnounceLoopAsync(_cts.Token));
    }

    public void UpdateMode(
        bool advertise,
        bool browse,
        bool tailscaleBrowse = false,
        bool tailnetBroadcast = false)
    {
        IsAdvertising = advertise;
        _isBrowsing = browse;
        _tailscaleBrowsingEnabled = tailscaleBrowse;
        _tailnetBroadcastEnabled = tailnetBroadcast;

        if (!browse && !tailscaleBrowse && !tailnetBroadcast)
        {
            _peers.Clear();
            OnPeersChanged?.Invoke();
        }
    }

    public void UpdateLocalPeer(FiestaPeerInfo peer)
    {
        _localPeer = peer;
    }

    public void Stop()
    {
        _cts?.Cancel();
        try { _announceClient?.Dispose(); } catch { }
        try { _receiveClient?.Close(); } catch { }
        try { _receiveClient?.Dispose(); } catch { }
        _announceClient = null;
        _receiveClient = null;
        _cts = null;
        _announceTask = null;
        _receiveTask = null;
        _cleanupTask = null;
        _tailscaleBrowseTask = null;
        _tailnetAnnounceTask = null;
        _peers.Clear();
        OnPeersChanged?.Invoke();
    }

    private async Task AnnounceLoopAsync(CancellationToken ct)
    {
        var endpoint = new IPEndPoint(IPAddress.Parse(MulticastAddress), MulticastPort);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (IsAdvertising && _localPeer != null && _announceClient != null)
                {
                    var bytes = BuildAnnouncementBytes(_localPeer, isTailnetAnnouncement: false);
                    await _announceClient.SendAsync(bytes, bytes.Length, endpoint);
                }
            }
            catch { }

            try { await Task.Delay(TimeSpan.FromSeconds(3), ct); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task TailnetAnnounceLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (IsAdvertising && _tailnetBroadcastEnabled && _localPeer != null && _announceClient != null)
                {
                    await _tailscale.DetectAsync();
                    if (_tailscale.IsRunning && _tailscale.Peers.Count > 0)
                    {
                        var bytes = BuildAnnouncementBytes(_localPeer, isTailnetAnnouncement: true);
                        foreach (var peer in _tailscale.Peers.Where(p => p.Online && !string.IsNullOrWhiteSpace(p.TailscaleIp)))
                        {
                            if (!string.IsNullOrWhiteSpace(_tailscale.TailscaleIp) &&
                                string.Equals(peer.TailscaleIp, _tailscale.TailscaleIp, StringComparison.OrdinalIgnoreCase))
                                continue;

                            try
                            {
                                await _announceClient.SendAsync(bytes, bytes.Length, new IPEndPoint(IPAddress.Parse(peer.TailscaleIp), MulticastPort));
                            }
                            catch { }
                        }
                    }
                }
            }
            catch { }

            try { await Task.Delay(TimeSpan.FromSeconds(5), ct); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task TailscaleBrowseLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (!_tailscaleBrowsingEnabled)
                {
                    await Task.Delay(2000, ct);
                    continue;
                }

                await _tailscale.DetectAsync();
                if (!_tailscale.IsRunning)
                {
                    await RemoveTailscalePolledPeersAsync();
                    await Task.Delay(4000, ct);
                    continue;
                }

                var now = DateTime.UtcNow;
                var seenIds = new HashSet<string>(StringComparer.Ordinal);
                foreach (var tsPeer in _tailscale.Peers.Where(p => p.Online && !string.IsNullOrWhiteSpace(p.TailscaleIp)))
                {
                    if (!string.IsNullOrWhiteSpace(_tailscale.TailscaleIp) &&
                        string.Equals(tsPeer.TailscaleIp, _tailscale.TailscaleIp, StringComparison.OrdinalIgnoreCase))
                        continue;

                    var id = $"ts:{(tsPeer.MagicDnsName ?? tsPeer.TailscaleIp)}";
                    seenIds.Add(id);

                    var peer = new FiestaPeerInfo
                    {
                        InstanceId = id,
                        MachineName = string.IsNullOrWhiteSpace(tsPeer.HostName) ? tsPeer.TailscaleIp : tsPeer.HostName,
                        Host = tsPeer.MagicDnsName ?? tsPeer.TailscaleIp,
                        Port = _localPeer?.Port ?? DevTunnelService.BridgePort,
                        Platform = tsPeer.OS,
                        LastSeenAt = now,
                        DiscoverySource = FiestaDiscoverySource.TailscalePoll,
                        IsWorkerAvailable = true,
                        IsTailscale = true,
                        TailnetHost = tsPeer.MagicDnsName ?? tsPeer.TailscaleIp,
                    };

                    _peers.AddOrUpdate(id, peer, (_, existing) =>
                    {
                        existing.MachineName = peer.MachineName;
                        existing.Host = peer.Host;
                        existing.Port = peer.Port;
                        existing.Platform = peer.Platform;
                        existing.LastSeenAt = peer.LastSeenAt;
                        existing.IsWorkerAvailable = true;
                        existing.IsTailscale = true;
                        existing.TailnetHost = peer.TailnetHost;
                        if (existing.DiscoverySource == FiestaDiscoverySource.TailscalePoll)
                            existing.DiscoverySource = FiestaDiscoverySource.TailscalePoll;
                        return existing;
                    });
                }

                var removed = false;
                foreach (var existing in _peers.Where(p => p.Value.DiscoverySource == FiestaDiscoverySource.TailscalePoll).ToList())
                {
                    if (!seenIds.Contains(existing.Key))
                    {
                        _peers.TryRemove(existing.Key, out _);
                        removed = true;
                    }
                }

                if (seenIds.Count > 0 || removed)
                    OnPeersChanged?.Invoke();
            }
            catch (OperationCanceledException) { break; }
            catch { }

            try { await Task.Delay(TimeSpan.FromSeconds(10), ct); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task RemoveTailscalePolledPeersAsync()
    {
        var removed = false;
        foreach (var existing in _peers.Where(p => p.Value.DiscoverySource == FiestaDiscoverySource.TailscalePoll).ToList())
        {
            _peers.TryRemove(existing.Key, out _);
            removed = true;
        }

        if (removed)
            await Task.Run(() => OnPeersChanged?.Invoke());
    }

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if ((!_isBrowsing && !_tailscaleBrowsingEnabled && !_tailnetBroadcastEnabled) || _receiveClient == null)
                {
                    await Task.Delay(400, ct);
                    continue;
                }

                var result = await _receiveClient.ReceiveAsync(ct);
                var json = Encoding.UTF8.GetString(result.Buffer);
                var payload = JsonSerializer.Deserialize<AnnouncementPayload>(json, JsonOptions);
                if (payload == null || payload.Tag != ServiceTag) continue;
                if (string.IsNullOrWhiteSpace(payload.InstanceId) || payload.Port <= 0) continue;
                if (_localPeer != null && payload.InstanceId == _localPeer.InstanceId) continue;

                var host = payload.Host;
                if (string.IsNullOrWhiteSpace(host))
                    host = result.RemoteEndPoint.Address.ToString();

                var source = payload.IsTailnetAnnouncement || IsTailscaleIp(result.RemoteEndPoint.Address)
                    ? FiestaDiscoverySource.TailnetAnnouncement
                    : FiestaDiscoverySource.LanMulticast;

                var peer = new FiestaPeerInfo
                {
                    InstanceId = payload.InstanceId,
                    MachineName = string.IsNullOrWhiteSpace(payload.MachineName) ? payload.InstanceId : payload.MachineName,
                    Host = host,
                    Port = payload.Port,
                    Platform = payload.Platform ?? "",
                    LastSeenAt = DateTime.UtcNow,
                    DiscoverySource = source,
                    IsWorkerAvailable = payload.IsWorkerAvailable,
                    IsTailscale = source != FiestaDiscoverySource.LanMulticast,
                    TailnetHost = payload.TailnetHost,
                    AdvertisedJoinCode = payload.JoinCode
                };

                _peers.AddOrUpdate(peer.InstanceId, peer, (_, existing) =>
                {
                    existing.MachineName = peer.MachineName;
                    existing.Host = peer.Host;
                    existing.Port = peer.Port;
                    existing.Platform = peer.Platform;
                    existing.LastSeenAt = peer.LastSeenAt;
                    existing.IsWorkerAvailable = peer.IsWorkerAvailable;
                    existing.IsTailscale = existing.IsTailscale || peer.IsTailscale;
                    existing.TailnetHost = string.IsNullOrWhiteSpace(peer.TailnetHost) ? existing.TailnetHost : peer.TailnetHost;
                    existing.AdvertisedJoinCode = string.IsNullOrWhiteSpace(peer.AdvertisedJoinCode) ? existing.AdvertisedJoinCode : peer.AdvertisedJoinCode;
                    existing.DiscoverySource = peer.DiscoverySource;
                    return existing;
                });
                OnPeersChanged?.Invoke();
            }
            catch (OperationCanceledException) { break; }
            catch { }
        }
    }

    private async Task CleanupLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var cutoff = DateTime.UtcNow - PeerTtl;
                var removed = false;
                foreach (var kvp in _peers)
                {
                    if (kvp.Value.LastSeenAt < cutoff &&
                        kvp.Value.DiscoverySource != FiestaDiscoverySource.TailscalePoll)
                    {
                        _peers.TryRemove(kvp.Key, out _);
                        removed = true;
                    }
                }
                if (removed) OnPeersChanged?.Invoke();
            }
            catch { }

            try { await Task.Delay(TimeSpan.FromSeconds(2), ct); }
            catch (OperationCanceledException) { break; }
        }
    }

    private static bool IsTailscaleIp(IPAddress address)
    {
        if (address.AddressFamily != AddressFamily.InterNetwork)
            return false;

        var bytes = address.GetAddressBytes();
        return bytes[0] == 100 && bytes[1] is >= 64 and <= 127;
    }

    private static byte[] BuildAnnouncementBytes(FiestaPeerInfo peer, bool isTailnetAnnouncement)
    {
        var payload = new AnnouncementPayload
        {
            Tag = ServiceTag,
            InstanceId = peer.InstanceId,
            MachineName = peer.MachineName,
            Host = peer.Host,
            Port = peer.Port,
            Platform = peer.Platform,
            IsWorkerAvailable = peer.IsWorkerAvailable,
            JoinCode = peer.AdvertisedJoinCode,
            TailnetHost = peer.TailnetHost,
            IsTailnetAnnouncement = isTailnetAnnouncement,
            SentAtUtc = DateTime.UtcNow
        };
        return Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload, JsonOptions));
    }

    public static string ResolveBestLanAddress()
    {
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up) continue;
                if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                foreach (var addr in ni.GetIPProperties().UnicastAddresses)
                {
                    if (addr.Address.AddressFamily == AddressFamily.InterNetwork)
                        return addr.Address.ToString();
                }
            }
        }
        catch { }

        return "127.0.0.1";
    }

    public void Dispose()
    {
        Stop();
        GC.SuppressFinalize(this);
    }

    private sealed class AnnouncementPayload
    {
        public string Tag { get; set; } = "";
        public string InstanceId { get; set; } = "";
        public string MachineName { get; set; } = "";
        public string Host { get; set; } = "";
        public int Port { get; set; }
        public string? Platform { get; set; }
        public bool IsWorkerAvailable { get; set; }
        public string? JoinCode { get; set; }
        public string? TailnetHost { get; set; }
        public bool IsTailnetAnnouncement { get; set; }
        public DateTime SentAtUtc { get; set; }
    }
}
