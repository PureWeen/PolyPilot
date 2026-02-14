using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using PolyPilot.Models;

namespace PolyPilot.Services;

public sealed class FiestaCoordinatorService : IDisposable
{
    private readonly WsBridgeServer _bridgeServer;
    private readonly CopilotService _copilotService;
    private readonly FiestaDiscoveryService _discoveryService;

    private readonly ConcurrentDictionary<string, FiestaRoom> _rooms = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, FiestaJoinRequest> _pendingJoinRequests = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, WorkerConnection>> _workerConnections = new(StringComparer.Ordinal);

    private readonly JsonSerializerOptions _jsonOptions = new() { WriteIndented = true };
    private ConnectionSettings _settings = new();
    private bool _initialized;

    private static string? _stateFilePath;
    private static string StateFilePath => _stateFilePath ??= GetStateFilePath();

    public event Action? OnStateChanged;
    public event Action<string>? OnStatusMessage;
    public event Action<FiestaJoinRequest>? OnJoinRequestResolved;

    public string MachineName => _settings.MachineName ?? ConnectionSettings.DefaultMachineName;
    public string InstanceId => _settings.InstanceId;
    public string CurrentJoinCode => _settings.FiestaJoinCode ?? "";
    public bool IsHosting => _bridgeServer.IsRunning;
    public bool IsInitialized => _initialized;

    public IReadOnlyList<FiestaRoom> Rooms => _rooms.Values
        .Select(CloneRoom)
        .OrderByDescending(r => r.CreatedAt)
        .ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
        .ToList();

    public IReadOnlyList<FiestaJoinRequest> PendingJoinRequests => _pendingJoinRequests.Values
        .Where(r => r.Status == FiestaJoinState.Pending)
        .OrderByDescending(r => r.RequestedAt)
        .ToList();

    public IReadOnlyList<FiestaPeerInfo> DiscoveredPeers => _discoveryService.Peers;

    public FiestaCoordinatorService(WsBridgeServer bridgeServer, CopilotService copilotService, FiestaDiscoveryService discoveryService)
    {
        _bridgeServer = bridgeServer;
        _copilotService = copilotService;
        _discoveryService = discoveryService;
        _discoveryService.OnPeersChanged += () => OnStateChanged?.Invoke();
    }

    public async Task InitializeAsync()
    {
        if (_initialized) return;

        _settings = ConnectionSettings.Load();
        LoadState();

        var localPeer = BuildLocalPeer(DevTunnelService.BridgePort);
        _discoveryService.Start(localPeer, advertise: _bridgeServer.IsRunning, browse: _settings.FiestaDiscoveryEnabled);

        _bridgeServer.SetFiestaCoordinator(this);

        _initialized = true;
        await Task.CompletedTask;
        OnStateChanged?.Invoke();
    }

    public async Task ApplySettingsAsync(ConnectionSettings settings)
    {
        _settings = settings;
        if (string.IsNullOrWhiteSpace(_settings.MachineName))
            _settings.MachineName = ConnectionSettings.DefaultMachineName;
        if (string.IsNullOrWhiteSpace(_settings.InstanceId))
            _settings.InstanceId = Guid.NewGuid().ToString("N");

        var localPeer = BuildLocalPeer(DevTunnelService.BridgePort);
        if (_discoveryService.IsRunning)
        {
            _discoveryService.UpdateLocalPeer(localPeer);
            _discoveryService.UpdateMode(advertise: _bridgeServer.IsRunning, browse: _settings.FiestaDiscoveryEnabled);
        }
        else
        {
            _discoveryService.Start(localPeer, advertise: _bridgeServer.IsRunning, browse: _settings.FiestaDiscoveryEnabled);
        }

        await Task.CompletedTask;
        OnStateChanged?.Invoke();
    }

    public FiestaRoom CreateRoom(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Room name is required.", nameof(name));

        var room = new FiestaRoom
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = name.Trim(),
            CreatedAt = DateTime.UtcNow,
            OrganizerInstanceId = InstanceId,
            OrganizerMachineName = MachineName,
            Members = new List<FiestaMember>
            {
                new()
                {
                    InstanceId = InstanceId,
                    MachineName = MachineName,
                    Host = FiestaDiscoveryService.ResolveBestLanAddress(),
                    Port = DevTunnelService.BridgePort,
                    Role = FiestaMemberRole.Organizer,
                    IsConnected = true,
                    LastUpdatedAt = DateTime.UtcNow
                }
            }
        };

        _rooms[room.Id] = room;
        SaveState();
        OnStatusMessage?.Invoke($"Fiesta '{room.Name}' created.");
        OnStateChanged?.Invoke();
        return CloneRoom(room);
    }

    public void RemoveRoom(string roomId)
    {
        if (!_rooms.TryRemove(roomId, out var room)) return;

        if (_workerConnections.TryRemove(roomId, out var roomWorkers))
        {
            foreach (var worker in roomWorkers.Values)
                worker.Dispose();
        }

        SaveState();
        OnStatusMessage?.Invoke($"Fiesta '{room.Name}' closed.");
        OnStateChanged?.Invoke();
    }

    public bool ValidateJoinCode(string? code)
    {
        if (string.IsNullOrWhiteSpace(code)) return false;
        return string.Equals(code.Trim(), CurrentJoinCode, StringComparison.Ordinal);
    }

    public void RegisterIncomingJoinRequest(FiestaJoinRequestPayload payload, string? remoteHost)
    {
        var request = new FiestaJoinRequest
        {
            RequestId = payload.RequestId,
            FiestaId = payload.FiestaId,
            OrganizerInstanceId = payload.OrganizerInstanceId,
            OrganizerMachineName = payload.OrganizerMachineName,
            JoinCode = payload.JoinCode,
            RemoteHost = remoteHost,
            RequestedAt = DateTime.UtcNow,
            Status = FiestaJoinState.Pending
        };

        _pendingJoinRequests[request.RequestId] = request;
        OnStatusMessage?.Invoke($"Join request from {request.OrganizerMachineName} for fiesta {request.FiestaId}.");
        OnStateChanged?.Invoke();
    }

    public void ResolveJoinRequest(string requestId, bool approved, string? reason = null)
    {
        if (!_pendingJoinRequests.TryGetValue(requestId, out var request)) return;

        request.Status = approved ? FiestaJoinState.Approved : FiestaJoinState.Rejected;
        request.Reason = reason;
        OnJoinRequestResolved?.Invoke(request);

        _pendingJoinRequests.TryRemove(requestId, out _);
        OnStateChanged?.Invoke();
    }

    public async Task StartHostingAsync()
    {
        _settings.FiestaJoinCode = GenerateJoinCode();
        if (string.IsNullOrWhiteSpace(_settings.ServerPassword))
            _settings.ServerPassword = GenerateServerSecret();

        _settings.DirectSharingEnabled = true;
        _settings.Save();

        _bridgeServer.ServerPassword = _settings.ServerPassword;
        _bridgeServer.SetCopilotService(_copilotService);
        _bridgeServer.SetFiestaCoordinator(this);
        _bridgeServer.Start(DevTunnelService.BridgePort, _settings.Port);

        var localPeer = BuildLocalPeer(DevTunnelService.BridgePort);
        if (_discoveryService.IsRunning)
        {
            _discoveryService.UpdateLocalPeer(localPeer);
            _discoveryService.UpdateMode(advertise: true, browse: _settings.FiestaDiscoveryEnabled);
        }
        else
        {
            _discoveryService.Start(localPeer, advertise: true, browse: _settings.FiestaDiscoveryEnabled);
        }

        OnStatusMessage?.Invoke($"Fiesta hosting started on {DevTunnelService.BridgePort}.");
        OnStateChanged?.Invoke();
        await Task.CompletedTask;
    }

    public void StopHosting()
    {
        _bridgeServer.Stop();
        _settings.DirectSharingEnabled = false;
        _settings.Save();

        if (_discoveryService.IsRunning)
            _discoveryService.UpdateMode(advertise: false, browse: _settings.FiestaDiscoveryEnabled);

        OnStatusMessage?.Invoke("Fiesta hosting stopped.");
        OnStateChanged?.Invoke();
    }

    public async Task<FiestaJoinStatusPayload> AddPeerToRoomAsync(string roomId, string peerInstanceId, string joinCode, CancellationToken cancellationToken = default)
    {
        if (!_rooms.TryGetValue(roomId, out var room))
            throw new InvalidOperationException($"Fiesta room '{roomId}' not found.");

        var peer = _discoveryService.Peers.FirstOrDefault(p => p.InstanceId == peerInstanceId)
            ?? throw new InvalidOperationException("Peer not found. Refresh discovery and try again.");

        if (string.IsNullOrWhiteSpace(joinCode))
            throw new InvalidOperationException("Join code is required.");

        var roomWorkers = _workerConnections.GetOrAdd(roomId, _ => new ConcurrentDictionary<string, WorkerConnection>(StringComparer.Ordinal));
        if (roomWorkers.TryGetValue(peer.InstanceId, out var existing))
        {
            if (existing.Client.IsConnected)
            {
                return new FiestaJoinStatusPayload
                {
                    RequestId = Guid.NewGuid().ToString("N"),
                    FiestaId = roomId,
                    Status = FiestaJoinState.Approved,
                    WorkerInstanceId = peer.InstanceId,
                    WorkerMachineName = peer.MachineName
                };
            }
            // Dispose stale connection
            existing.Dispose();
            roomWorkers.TryRemove(peer.InstanceId, out _);
        }

        var client = new WsBridgeClient();
        try
        {
            var requestId = Guid.NewGuid().ToString("N");
            var joinResult = new TaskCompletionSource<FiestaJoinStatusPayload>(TaskCreationOptions.RunContinuationsAsynchronously);

            void HandleJoinStatus(FiestaJoinStatusPayload payload)
            {
                if (!string.Equals(payload.RequestId, requestId, StringComparison.Ordinal)) return;
                if (payload.Status == FiestaJoinState.Pending)
                {
                    OnStatusMessage?.Invoke($"Join request pending approval on {peer.MachineName}...");
                    return;
                }

                joinResult.TrySetResult(payload);
            }

            client.OnFiestaJoinStatus += HandleJoinStatus;
            client.OnFiestaDispatchResult += payload => OnStatusMessage?.Invoke($"[{payload.WorkerMachineName}] {payload.Summary ?? (payload.Success ? "Prompt completed." : payload.Error ?? "Prompt failed.")}");
            client.OnFiestaSessionCommandResult += payload => OnStatusMessage?.Invoke($"[{payload.WorkerMachineName}] {payload.Command} {(payload.Success ? "ok" : $"failed: {payload.Error}")}");

            var wsUrl = BuildWsUrl(peer);
            await client.ConnectAsync(wsUrl, joinCode.Trim(), cancellationToken);
            await client.SendFiestaJoinRequestAsync(new FiestaJoinRequestPayload
            {
                RequestId = requestId,
                FiestaId = roomId,
                OrganizerInstanceId = InstanceId,
                OrganizerMachineName = MachineName,
                JoinCode = joinCode.Trim(),
                RequestedAt = DateTime.UtcNow
            }, roomId, cancellationToken);

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(90));
            using (timeoutCts.Token.Register(() => joinResult.TrySetCanceled()))
            {
                FiestaJoinStatusPayload status;
                try
                {
                    status = await joinResult.Task;
                }
                catch
                {
                    client.OnFiestaJoinStatus -= HandleJoinStatus;
                    throw new TimeoutException($"Timed out waiting for {peer.MachineName} to approve fiesta join.");
                }

                if (status.Status != FiestaJoinState.Approved)
                {
                    client.OnFiestaJoinStatus -= HandleJoinStatus;
                    // We don't dispose here because the caller might want to know the reason?
                    // But we return the status, so the client is useless now as it's not approved.
                    client.Dispose();
                    return status;
                }

                lock (room.Members)
                {
                    var member = room.Members.FirstOrDefault(m => m.InstanceId == peer.InstanceId);
                    if (member == null)
                    {
                        member = new FiestaMember
                        {
                            InstanceId = peer.InstanceId,
                            MachineName = peer.MachineName,
                            Host = peer.Host,
                            Port = peer.Port,
                            Role = FiestaMemberRole.Worker,
                            IsConnected = true,
                            LastUpdatedAt = DateTime.UtcNow
                        };
                        room.Members.Add(member);
                    }
                    else
                    {
                        member.IsConnected = true;
                        member.LastUpdatedAt = DateTime.UtcNow;
                        member.Host = peer.Host;
                        member.Port = peer.Port;
                    }
                }

                roomWorkers[peer.InstanceId] = new WorkerConnection(peer, client);
                SaveState();
                OnStatusMessage?.Invoke($"{peer.MachineName} joined fiesta '{room.Name}'.");
                OnStateChanged?.Invoke();
                return status;
            }
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    public async Task BroadcastPromptAsync(string roomId, string sessionName, string prompt, string? model = null, string? workingDirectory = null, CancellationToken cancellationToken = default)
    {
        if (!_rooms.TryGetValue(roomId, out var room))
            throw new InvalidOperationException("Fiesta room not found.");
        if (string.IsNullOrWhiteSpace(prompt))
            throw new ArgumentException("Prompt is required.", nameof(prompt));
        if (string.IsNullOrWhiteSpace(sessionName))
            throw new ArgumentException("Session name is required.", nameof(sessionName));

        if (!_workerConnections.TryGetValue(roomId, out var workers) || workers.Count == 0)
            throw new InvalidOperationException("No connected workers in this fiesta.");

        // Clean up disconnected workers before broadcasting
        var disconnectedIds = workers.Where(w => !w.Value.Client.IsConnected).Select(w => w.Key).ToList();
        foreach (var id in disconnectedIds)
        {
            if (workers.TryRemove(id, out var worker))
                worker.Dispose();
            
            lock (room.Members)
            {
                var member = room.Members.FirstOrDefault(m => m.InstanceId == id);
                if (member != null) member.IsConnected = false;
            }
        }
        
        if (workers.Count == 0)
        {
             OnStateChanged?.Invoke();
             throw new InvalidOperationException("No connected workers in this fiesta.");
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(45));

        var tasks = new List<Task>();
        foreach (var worker in workers.Values)
        {
            var req = new FiestaDispatchPromptPayload
            {
                RequestId = Guid.NewGuid().ToString("N"),
                FiestaId = roomId,
                SessionName = sessionName,
                Message = prompt,
                Model = model,
                WorkingDirectory = workingDirectory,
                CreateSessionIfMissing = true
            };
            tasks.Add(worker.Client.SendFiestaDispatchPromptAsync(req, roomId, timeoutCts.Token));
        }

        await Task.WhenAll(tasks);
        OnStatusMessage?.Invoke($"Broadcast sent to {tasks.Count} worker(s).");
    }

    public async Task SendSessionCommandAsync(string roomId, string command, string sessionName, string? model = null, string? workingDirectory = null, CancellationToken cancellationToken = default)
    {
        if (!_rooms.TryGetValue(roomId, out var room))
            throw new InvalidOperationException("Fiesta room not found.");
        if (!_workerConnections.TryGetValue(roomId, out var workers) || workers.Count == 0)
            throw new InvalidOperationException("No connected workers in this fiesta.");
        if (string.IsNullOrWhiteSpace(command))
            throw new ArgumentException("Command is required.", nameof(command));
        if (string.IsNullOrWhiteSpace(sessionName))
            throw new ArgumentException("Session name is required.", nameof(sessionName));

        // Clean up disconnected workers
        var disconnectedIds = workers.Where(w => !w.Value.Client.IsConnected).Select(w => w.Key).ToList();
        foreach (var id in disconnectedIds)
        {
            if (workers.TryRemove(id, out var worker))
                worker.Dispose();
            
            lock (room.Members)
            {
                var member = room.Members.FirstOrDefault(m => m.InstanceId == id);
                if (member != null) member.IsConnected = false;
            }
        }

        if (workers.Count == 0)
        {
             OnStateChanged?.Invoke();
             throw new InvalidOperationException("No connected workers in this fiesta.");
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(45));

        var tasks = new List<Task>();
        foreach (var worker in workers.Values)
        {
            var req = new FiestaSessionCommandPayload
            {
                RequestId = Guid.NewGuid().ToString("N"),
                FiestaId = roomId,
                Command = command,
                SessionName = sessionName,
                Model = model,
                WorkingDirectory = workingDirectory
            };
            tasks.Add(worker.Client.SendFiestaSessionCommandAsync(req, roomId, timeoutCts.Token));
        }

        await Task.WhenAll(tasks);
        OnStatusMessage?.Invoke($"Session command '{command}' sent to {tasks.Count} worker(s).");
    }

    private void LoadState()
    {
        try
        {
            if (!File.Exists(StateFilePath)) return;
            var json = File.ReadAllText(StateFilePath);
            var state = JsonSerializer.Deserialize<FiestaStateStore>(json);
            if (state?.Rooms == null) return;
            foreach (var room in state.Rooms)
                _rooms[room.Id] = room;
        }
        catch { }
    }

    private void SaveState()
    {
        try
        {
            var dir = Path.GetDirectoryName(StateFilePath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            var state = new FiestaStateStore { Rooms = _rooms.Values.Select(CloneRoom).ToList() };
            File.WriteAllText(StateFilePath, JsonSerializer.Serialize(state, _jsonOptions));
        }
        catch { }
    }

    private FiestaPeerInfo BuildLocalPeer(int port)
    {
        return new FiestaPeerInfo
        {
            InstanceId = _settings.InstanceId,
            MachineName = MachineName,
            Host = FiestaDiscoveryService.ResolveBestLanAddress(),
            Port = port,
            Platform = GetPlatformLabel(),
            LastSeenAt = DateTime.UtcNow
        };
    }

    private static FiestaRoom CloneRoom(FiestaRoom room)
    {
        List<FiestaMember> members;
        lock (room.Members)
        {
            members = room.Members.Select(m => new FiestaMember
            {
                InstanceId = m.InstanceId,
                MachineName = m.MachineName,
                Host = m.Host,
                Port = m.Port,
                Role = m.Role,
                IsConnected = m.IsConnected,
                LastUpdatedAt = m.LastUpdatedAt
            }).ToList();
        }

        return new FiestaRoom
        {
            Id = room.Id,
            Name = room.Name,
            CreatedAt = room.CreatedAt,
            OrganizerInstanceId = room.OrganizerInstanceId,
            OrganizerMachineName = room.OrganizerMachineName,
            Members = members
        };
    }

    private static string BuildWsUrl(FiestaPeerInfo peer)
    {
        if (peer.Host.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
            return peer.Host.Replace("http://", "ws://", StringComparison.OrdinalIgnoreCase).TrimEnd('/');
        if (peer.Host.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return peer.Host.Replace("https://", "wss://", StringComparison.OrdinalIgnoreCase).TrimEnd('/');
        if (peer.Host.StartsWith("ws://", StringComparison.OrdinalIgnoreCase) ||
            peer.Host.StartsWith("wss://", StringComparison.OrdinalIgnoreCase))
            return peer.Host.TrimEnd('/');
        return $"ws://{peer.Host}:{peer.Port}/";
    }

    private static string GenerateJoinCode()
    {
        Span<byte> bytes = stackalloc byte[6];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToHexString(bytes);
    }

    private static string GenerateServerSecret()
    {
        Span<byte> bytes = stackalloc byte[16];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToHexString(bytes);
    }

    private static string GetPlatformLabel()
    {
        if (OperatingSystem.IsWindows()) return "windows";
        if (OperatingSystem.IsMacCatalyst()) return "maccatalyst";
        if (OperatingSystem.IsMacOS()) return "macos";
        if (OperatingSystem.IsIOS()) return "ios";
        if (OperatingSystem.IsAndroid()) return "android";
        if (OperatingSystem.IsLinux()) return "linux";
        return "unknown";
    }

    private static string GetStateFilePath()
    {
        try
        {
#if IOS || ANDROID
            return Path.Combine(FileSystem.AppDataDirectory, ".polypilot", "fiestas.json");
#else
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (string.IsNullOrEmpty(home))
                home = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return Path.Combine(home, ".polypilot", "fiestas.json");
#endif
        }
        catch
        {
            return Path.Combine(Path.GetTempPath(), ".polypilot", "fiestas.json");
        }
    }

    public void Dispose()
    {
        foreach (var roomWorkers in _workerConnections.Values)
        {
            foreach (var worker in roomWorkers.Values)
                worker.Dispose();
        }
        _workerConnections.Clear();
        GC.SuppressFinalize(this);
    }

    private sealed class WorkerConnection : IDisposable
    {
        public FiestaPeerInfo Peer { get; }
        public WsBridgeClient Client { get; }

        public WorkerConnection(FiestaPeerInfo peer, WsBridgeClient client)
        {
            Peer = peer;
            Client = client;
        }

        public void Dispose()
        {
            Client.Dispose();
        }
    }
}
