using System.Collections.Concurrent;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using PolyPilot.Models;

namespace PolyPilot.Services;

/// <summary>
/// Lightweight LAN discovery using multicast announcements.
/// Uses Bonjour-style service metadata, without external dependencies.
/// </summary>
public sealed class FiestaDiscoveryService : IDisposable
{
    private const string ServiceTag = "_polypilot-fiesta._tcp.local";
    private const string MulticastAddress = "239.255.80.80";
    private const int MulticastPort = 45454;
    private static readonly TimeSpan PeerTtl = TimeSpan.FromSeconds(12);
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private readonly ConcurrentDictionary<string, FiestaPeerInfo> _peers = new(StringComparer.Ordinal);
    private CancellationTokenSource? _cts;
    private Task? _announceTask;
    private Task? _receiveTask;
    private Task? _cleanupTask;
    private UdpClient? _announceClient;
    private UdpClient? _receiveClient;
    private FiestaPeerInfo? _localPeer;
    private bool _isBrowsing;

    public event Action? OnPeersChanged;

    public bool IsRunning => _cts is { IsCancellationRequested: false };
    public bool IsAdvertising { get; private set; }
    public bool IsBrowsing => _isBrowsing;
    public IReadOnlyList<FiestaPeerInfo> Peers => _peers.Values
        .OrderByDescending(p => p.LastSeenAt)
        .ThenBy(p => p.MachineName, StringComparer.OrdinalIgnoreCase)
        .ToList();

    public void Start(FiestaPeerInfo localPeer, bool advertise, bool browse)
    {
        _localPeer = localPeer;
        _isBrowsing = browse;
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
    }

    public void UpdateMode(bool advertise, bool browse)
    {
        IsAdvertising = advertise;
        _isBrowsing = browse;
        if (!browse)
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
                    var payload = new AnnouncementPayload
                    {
                        Tag = ServiceTag,
                        InstanceId = _localPeer.InstanceId,
                        MachineName = _localPeer.MachineName,
                        Host = _localPeer.Host,
                        Port = _localPeer.Port,
                        Platform = _localPeer.Platform,
                        SentAtUtc = DateTime.UtcNow
                    };
                    var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload, JsonOptions));
                    await _announceClient.SendAsync(bytes, bytes.Length, endpoint);
                }
            }
            catch { }

            try { await Task.Delay(TimeSpan.FromSeconds(3), ct); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (!_isBrowsing || _receiveClient == null)
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

                var peer = new FiestaPeerInfo
                {
                    InstanceId = payload.InstanceId,
                    MachineName = string.IsNullOrWhiteSpace(payload.MachineName) ? payload.InstanceId : payload.MachineName,
                    Host = host,
                    Port = payload.Port,
                    Platform = payload.Platform ?? "",
                    LastSeenAt = DateTime.UtcNow
                };

                _peers.AddOrUpdate(peer.InstanceId, peer, (_, existing) =>
                {
                    existing.MachineName = peer.MachineName;
                    existing.Host = peer.Host;
                    existing.Port = peer.Port;
                    existing.Platform = peer.Platform;
                    existing.LastSeenAt = peer.LastSeenAt;
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
                    if (kvp.Value.LastSeenAt < cutoff)
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
        public DateTime SentAtUtc { get; set; }
    }
}
