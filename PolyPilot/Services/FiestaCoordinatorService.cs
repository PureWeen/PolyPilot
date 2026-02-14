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
    private readonly TailscaleService _tailscaleService;

    private readonly ConcurrentDictionary<string, FiestaRoom> _rooms = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, FiestaRegisteredWorker> _registeredWorkers = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, WorkerConnection> _workerConnections = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, byte>> _roomAssignments = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, FiestaJoinRequest> _pendingJoinRequests = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _trustedOrganizers = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _stateLock = new(1, 1);
    private readonly JsonSerializerOptions _jsonOptions = new() { WriteIndented = true };

    private ConnectionSettings _settings = new();
    private FiestaOrganizationState _organization = new();
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
    public bool IsWorkerModeEnabled => _settings.FiestaOfferAsWorker;

    public IReadOnlyList<FiestaRoom> Rooms => _rooms.Values
        .Select(CloneRoom)
        .OrderByDescending(r => r.LastActivityAt)
        .ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
        .ToList();

    public IReadOnlyList<FiestaRegisteredWorker> RegisteredWorkers => _registeredWorkers.Values
        .Select(CloneWorker)
        .OrderByDescending(w => w.IsConnected)
        .ThenBy(w => w.MachineName, StringComparer.OrdinalIgnoreCase)
        .ToList();

    public IReadOnlyList<string> TrustedOrganizers => _trustedOrganizers.Keys
        .OrderBy(v => v, StringComparer.OrdinalIgnoreCase)
        .ToList();

    public IReadOnlyList<FiestaJoinRequest> PendingJoinRequests => _pendingJoinRequests.Values
        .Where(r => r.Status == FiestaJoinState.Pending)
        .OrderByDescending(r => r.RequestedAt)
        .ToList();

    public IReadOnlyList<FiestaPeerInfo> DiscoveredPeers => _discoveryService.Peers;

    public IReadOnlyList<FiestaGroup> Groups
    {
        get
        {
            lock (_organization)
            {
                return _organization.Groups
                    .OrderBy(g => g.SortOrder)
                    .Select(g => new FiestaGroup
                    {
                        Id = g.Id,
                        Name = g.Name,
                        SortOrder = g.SortOrder,
                        IsCollapsed = g.IsCollapsed
                    })
                    .ToList();
            }
        }
    }

    public bool HasMultipleGroups
    {
        get
        {
            lock (_organization)
                return _organization.Groups.Count > 1;
        }
    }

    public FiestaCoordinatorService(
        WsBridgeServer bridgeServer,
        CopilotService copilotService,
        FiestaDiscoveryService discoveryService,
        TailscaleService tailscaleService)
    {
        _bridgeServer = bridgeServer;
        _copilotService = copilotService;
        _discoveryService = discoveryService;
        _tailscaleService = tailscaleService;
        _discoveryService.OnPeersChanged += () => OnStateChanged?.Invoke();
    }

    public async Task InitializeAsync()
    {
        if (_initialized) return;

        _settings = ConnectionSettings.Load();
        await DetectAndApplyTailscaleDefaultsAsync();
        LoadState();
        ReconcileOrganization();

        await EnsureWorkerModeHostingAsync();
        ApplyDiscoveryMode();

        _bridgeServer.SetFiestaCoordinator(this);
        _initialized = true;
        OnStateChanged?.Invoke();
    }

    public async Task ApplySettingsAsync(ConnectionSettings settings)
    {
        _settings = settings;
        if (string.IsNullOrWhiteSpace(_settings.MachineName))
            _settings.MachineName = ConnectionSettings.DefaultMachineName;
        if (string.IsNullOrWhiteSpace(_settings.InstanceId))
            _settings.InstanceId = Guid.NewGuid().ToString("N");

        await DetectAndApplyTailscaleDefaultsAsync();
        await EnsureWorkerModeHostingAsync();
        ApplyDiscoveryMode();
        _settings.Save();

        SaveState();
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
            LastActivityAt = DateTime.UtcNow,
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
        _roomAssignments.GetOrAdd(room.Id, _ => new ConcurrentDictionary<string, byte>(StringComparer.Ordinal));
        EnsureRoomMeta(room.Id);
        SaveState();
        OnStatusMessage?.Invoke($"Fiesta '{room.Name}' created.");
        OnStateChanged?.Invoke();
        return CloneRoom(room);
    }

    public void RemoveRoom(string roomId)
    {
        if (!_rooms.TryRemove(roomId, out var room)) return;

        _roomAssignments.TryRemove(roomId, out _);
        lock (_organization)
        {
            _organization.Rooms.RemoveAll(m => m.RoomId == roomId);
        }

        SaveState();
        OnStatusMessage?.Invoke($"Fiesta '{room.Name}' closed.");
        OnStateChanged?.Invoke();
    }

    public void RenameRoom(string roomId, string name)
    {
        if (!_rooms.TryGetValue(roomId, out var room) || string.IsNullOrWhiteSpace(name))
            return;

        room.Name = name.Trim();
        room.LastActivityAt = DateTime.UtcNow;
        SaveState();
        OnStateChanged?.Invoke();
    }

    public FiestaOrganizationState GetOrganizationState()
    {
        lock (_organization)
        {
            return new FiestaOrganizationState
            {
                SortMode = _organization.SortMode,
                Groups = _organization.Groups.Select(g => new FiestaGroup
                {
                    Id = g.Id,
                    Name = g.Name,
                    SortOrder = g.SortOrder,
                    IsCollapsed = g.IsCollapsed
                }).ToList(),
                Rooms = _organization.Rooms.Select(m => new FiestaRoomMeta
                {
                    RoomId = m.RoomId,
                    GroupId = m.GroupId,
                    IsPinned = m.IsPinned,
                    ManualOrder = m.ManualOrder
                }).ToList()
            };
        }
    }

    public IEnumerable<(FiestaGroup Group, List<FiestaRoom> Rooms)> GetOrganizedRooms()
    {
        var roomsSnapshot = _rooms.Values.Select(CloneRoom).ToList();
        var results = new List<(FiestaGroup Group, List<FiestaRoom> Rooms)>();
        lock (_organization)
        {
            var roomMetaMap = _organization.Rooms.ToDictionary(m => m.RoomId, StringComparer.Ordinal);
            foreach (var group in _organization.Groups.OrderBy(g => g.SortOrder))
            {
                var grouped = roomsSnapshot
                    .Where(r => roomMetaMap.TryGetValue(r.Id, out var meta) && meta.GroupId == group.Id)
                    .OrderByDescending(r => roomMetaMap.TryGetValue(r.Id, out var meta) && meta.IsPinned)
                    .ThenBy(r => ApplyRoomSort(r, roomMetaMap))
                    .ToList();

                results.Add((new FiestaGroup
                {
                    Id = group.Id,
                    Name = group.Name,
                    SortOrder = group.SortOrder,
                    IsCollapsed = group.IsCollapsed
                }, grouped));
            }
        }
        return results;
    }

    public FiestaRoomMeta? GetRoomMeta(string roomId)
    {
        lock (_organization)
        {
            var meta = _organization.Rooms.FirstOrDefault(m => m.RoomId == roomId);
            if (meta == null) return null;
            return new FiestaRoomMeta
            {
                RoomId = meta.RoomId,
                GroupId = meta.GroupId,
                IsPinned = meta.IsPinned,
                ManualOrder = meta.ManualOrder
            };
        }
    }

    public FiestaGroup CreateGroup(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Group name is required.", nameof(name));

        FiestaGroup group;
        lock (_organization)
        {
            var nextOrder = _organization.Groups.Count == 0 ? 0 : _organization.Groups.Max(g => g.SortOrder) + 1;
            group = new FiestaGroup { Id = Guid.NewGuid().ToString("N"), Name = name.Trim(), SortOrder = nextOrder };
            _organization.Groups.Add(group);
        }
        SaveState();
        OnStateChanged?.Invoke();
        return group;
    }

    public void RenameGroup(string groupId, string name)
    {
        if (string.IsNullOrWhiteSpace(groupId) || string.IsNullOrWhiteSpace(name)) return;
        lock (_organization)
        {
            var group = _organization.Groups.FirstOrDefault(g => g.Id == groupId);
            if (group != null) group.Name = name.Trim();
        }
        SaveState();
        OnStateChanged?.Invoke();
    }

    public void DeleteGroup(string groupId)
    {
        if (groupId == FiestaGroup.DefaultId) return;
        lock (_organization)
        {
            foreach (var room in _organization.Rooms.Where(r => r.GroupId == groupId))
                room.GroupId = FiestaGroup.DefaultId;
            _organization.Groups.RemoveAll(g => g.Id == groupId);
        }
        SaveState();
        OnStateChanged?.Invoke();
    }

    public void ToggleGroupCollapsed(string groupId)
    {
        lock (_organization)
        {
            var group = _organization.Groups.FirstOrDefault(g => g.Id == groupId);
            if (group != null) group.IsCollapsed = !group.IsCollapsed;
        }
        SaveState();
        OnStateChanged?.Invoke();
    }

    public void SetSortMode(FiestaSortMode mode)
    {
        lock (_organization)
            _organization.SortMode = mode;
        SaveState();
        OnStateChanged?.Invoke();
    }

    public void PinRoom(string roomId, bool pinned)
    {
        lock (_organization)
        {
            EnsureRoomMeta(roomId);
            var meta = _organization.Rooms.FirstOrDefault(r => r.RoomId == roomId);
            if (meta != null) meta.IsPinned = pinned;
        }
        SaveState();
        OnStateChanged?.Invoke();
    }

    public void MoveRoom(string roomId, string groupId)
    {
        lock (_organization)
        {
            EnsureRoomMeta(roomId);
            var meta = _organization.Rooms.FirstOrDefault(r => r.RoomId == roomId);
            if (meta != null && _organization.Groups.Any(g => g.Id == groupId))
                meta.GroupId = groupId;
        }
        SaveState();
        OnStateChanged?.Invoke();
    }

    public void SetRoomManualOrder(string roomId, int order)
    {
        lock (_organization)
        {
            EnsureRoomMeta(roomId);
            var meta = _organization.Rooms.FirstOrDefault(r => r.RoomId == roomId);
            if (meta != null) meta.ManualOrder = order;
        }
        SaveState();
    }

    public void SetRoomSessionName(string roomId, string sessionName)
    {
        if (!_rooms.TryGetValue(roomId, out var room) || string.IsNullOrWhiteSpace(sessionName))
            return;

        room.SessionName = sessionName.Trim();
        room.LastActivityAt = DateTime.UtcNow;
        SaveState();
        OnStateChanged?.Invoke();
    }

    public bool ValidateJoinCode(string? code)
    {
        if (string.IsNullOrWhiteSpace(code)) return false;
        return string.Equals(code.Trim(), CurrentJoinCode, StringComparison.Ordinal);
    }

    public FiestaJoinRequest RegisterIncomingJoinRequest(FiestaJoinRequestPayload payload, string? remoteHost)
    {
        var organizerInstanceId = payload.OrganizerInstanceId?.Trim() ?? "";
        var organizerMachineName = string.IsNullOrWhiteSpace(payload.OrganizerMachineName)
            ? organizerInstanceId
            : payload.OrganizerMachineName.Trim();

        var request = new FiestaJoinRequest
        {
            RequestId = payload.RequestId,
            FiestaId = payload.FiestaId,
            OrganizerInstanceId = organizerInstanceId,
            OrganizerMachineName = organizerMachineName,
            OrganizerTrustToken = payload.OrganizerTrustToken,
            JoinCode = payload.JoinCode,
            RemoteHost = remoteHost,
            RequestedAt = DateTime.UtcNow,
            Status = FiestaJoinState.Pending
        };

        if (string.IsNullOrWhiteSpace(organizerInstanceId))
        {
            request.Status = FiestaJoinState.Rejected;
            request.Reason = "Missing organizer identity";
            OnJoinRequestResolved?.Invoke(request);
            return request;
        }

        if (IsTrustedOrganizer(organizerInstanceId, payload.OrganizerTrustToken))
        {
            request.Status = FiestaJoinState.Approved;
            request.AutoApproved = true;
            request.Reason = "Trusted organizer";
            OnJoinRequestResolved?.Invoke(request);
            OnStatusMessage?.Invoke($"Auto-approved trusted organizer {request.OrganizerMachineName}.");
            return request;
        }

        if (_pendingJoinRequests.Count >= 25)
        {
            request.Status = FiestaJoinState.Rejected;
            request.Reason = "Too many pending pairing requests";
            OnJoinRequestResolved?.Invoke(request);
            OnStatusMessage?.Invoke("Rejected pairing request because too many are pending.");
            return request;
        }

        _pendingJoinRequests[request.RequestId] = request;
        OnStatusMessage?.Invoke($"Join request from {request.OrganizerMachineName} for fiesta {request.FiestaId}.");
        OnStateChanged?.Invoke();
        return request;
    }

    public bool IsTrustedOrganizer(string? organizerInstanceId, string? organizerTrustToken = null)
    {
        if (string.IsNullOrWhiteSpace(organizerInstanceId))
            return false;
        if (!_trustedOrganizers.TryGetValue(organizerInstanceId, out var trustedToken))
            return false;
        if (string.IsNullOrWhiteSpace(trustedToken))
            return false;
        return string.Equals(trustedToken, organizerTrustToken ?? "", StringComparison.Ordinal);
    }

    public void ResolveJoinRequest(string requestId, bool approved, string? reason = null)
    {
        if (!_pendingJoinRequests.TryRemove(requestId, out var request)) return;

        request.Status = approved ? FiestaJoinState.Approved : FiestaJoinState.Rejected;
        request.Reason = reason;
        request.AutoApproved = false;
        if (approved &&
            !string.IsNullOrWhiteSpace(request.OrganizerInstanceId) &&
            !string.IsNullOrWhiteSpace(request.OrganizerTrustToken))
            _trustedOrganizers[request.OrganizerInstanceId] = request.OrganizerTrustToken;

        SaveState();
        OnJoinRequestResolved?.Invoke(request);
        OnStateChanged?.Invoke();
    }

    public void UntrustOrganizer(string organizerInstanceId)
    {
        if (string.IsNullOrWhiteSpace(organizerInstanceId)) return;
        _trustedOrganizers.TryRemove(organizerInstanceId, out _);
        SaveState();
        OnStateChanged?.Invoke();
    }

    public async Task<FiestaRegisteredWorker> RegisterWorkerAsync(string peerInstanceId, string? joinCode = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(peerInstanceId))
            throw new InvalidOperationException("Peer ID is required.");

        var peer = _discoveryService.Peers.FirstOrDefault(p => p.InstanceId == peerInstanceId);
        FiestaRegisteredWorker? existingWorker = null;
        if (peer == null && !_registeredWorkers.TryGetValue(peerInstanceId, out existingWorker))
            throw new InvalidOperationException("Peer not found. Refresh discovery and try again.");

        var worker = peer == null ? CloneWorker(existingWorker!) : new FiestaRegisteredWorker
        {
            InstanceId = peer.InstanceId,
            MachineName = peer.MachineName,
            Host = peer.Host,
            Port = peer.Port,
            Platform = peer.Platform,
            TailnetHost = peer.TailnetHost,
            JoinCode = joinCode ?? peer.AdvertisedJoinCode ?? "",
            LastUpdatedAt = DateTime.UtcNow
        };

        if (_registeredWorkers.TryGetValue(worker.InstanceId, out var persisted))
        {
            if (string.IsNullOrWhiteSpace(worker.JoinCode))
                worker.JoinCode = persisted.JoinCode;
            if (string.IsNullOrWhiteSpace(worker.TailnetHost))
                worker.TailnetHost = persisted.TailnetHost;
            if (string.IsNullOrWhiteSpace(worker.PairingToken))
                worker.PairingToken = persisted.PairingToken;
        }
        if (string.IsNullOrWhiteSpace(worker.PairingToken))
            worker.PairingToken = GeneratePairingToken();

        var hadExisting = _registeredWorkers.TryGetValue(worker.InstanceId, out var previousWorker);
        var previousSnapshot = hadExisting && previousWorker != null ? CloneWorker(previousWorker) : null;

        _registeredWorkers[worker.InstanceId] = worker;
        try
        {
            await EnsureWorkerConnectedAsync(worker, cancellationToken);
            SaveState();
            OnStatusMessage?.Invoke($"Registered worker {worker.MachineName}.");
            OnStateChanged?.Invoke();
            return CloneWorker(worker);
        }
        catch
        {
            if (previousSnapshot != null)
                _registeredWorkers[worker.InstanceId] = previousSnapshot;
            else
                _registeredWorkers.TryRemove(worker.InstanceId, out _);
            throw;
        }
    }

    public async Task<FiestaRegisteredWorker> RegisterManualWorkerAsync(string name, string host, int port, string joinCode, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(host) || port <= 0 || string.IsNullOrWhiteSpace(joinCode))
            throw new InvalidOperationException("Host, port, and join code are required.");

        var existing = _registeredWorkers.Values.FirstOrDefault(w =>
            string.Equals(w.Host, host, StringComparison.OrdinalIgnoreCase) && w.Port == port);

        var worker = existing ?? new FiestaRegisteredWorker
        {
            InstanceId = $"manual:{host}:{port}",
            MachineName = string.IsNullOrWhiteSpace(name) ? host : name.Trim(),
            Host = host.Trim(),
            Port = port,
            Platform = "unknown",
        };

        worker.MachineName = string.IsNullOrWhiteSpace(name) ? worker.MachineName : name.Trim();
        worker.Host = host.Trim();
        worker.Port = port;
        worker.JoinCode = joinCode.Trim();
        if (string.IsNullOrWhiteSpace(worker.PairingToken))
            worker.PairingToken = GeneratePairingToken();
        worker.LastUpdatedAt = DateTime.UtcNow;

        _registeredWorkers[worker.InstanceId] = worker;
        await EnsureWorkerConnectedAsync(worker, cancellationToken);
        SaveState();
        OnStateChanged?.Invoke();
        return CloneWorker(worker);
    }

    public void RemoveRegisteredWorker(string workerInstanceId)
    {
        if (!_registeredWorkers.TryRemove(workerInstanceId, out var worker)) return;

        if (_workerConnections.TryRemove(workerInstanceId, out var connection))
            connection.Dispose();

        foreach (var assignment in _roomAssignments.Values)
            assignment.TryRemove(workerInstanceId, out _);

        foreach (var room in _rooms.Values)
        {
            lock (room.Members)
            {
                room.Members.RemoveAll(m => m.InstanceId == workerInstanceId && m.Role == FiestaMemberRole.Worker);
            }
        }

        SaveState();
        OnStatusMessage?.Invoke($"Removed worker {worker.MachineName}.");
        OnStateChanged?.Invoke();
    }

    public async Task<bool> ReconnectWorkerAsync(string workerInstanceId, CancellationToken cancellationToken = default)
    {
        if (!_registeredWorkers.TryGetValue(workerInstanceId, out var worker))
            return false;

        try
        {
            await EnsureWorkerConnectedAsync(worker, cancellationToken);
            SaveState();
            OnStateChanged?.Invoke();
            return true;
        }
        catch
        {
            worker.IsConnected = false;
            worker.LastUpdatedAt = DateTime.UtcNow;
            SaveState();
            OnStateChanged?.Invoke();
            return false;
        }
    }

    public async Task<FiestaJoinStatusPayload> AddPeerToRoomAsync(string roomId, string peerInstanceId, string joinCode, CancellationToken cancellationToken = default)
    {
        if (!_rooms.TryGetValue(roomId, out var room))
            throw new InvalidOperationException($"Fiesta room '{roomId}' not found.");

        var existingWorker = _registeredWorkers.TryGetValue(peerInstanceId, out var known) ? known : null;
        var resolvedCode = string.IsNullOrWhiteSpace(joinCode)
            ? existingWorker?.JoinCode
            : joinCode.Trim();

        if (string.IsNullOrWhiteSpace(resolvedCode))
        {
            var discovered = _discoveryService.Peers.FirstOrDefault(p => p.InstanceId == peerInstanceId);
            resolvedCode = discovered?.AdvertisedJoinCode;
        }

        var worker = await RegisterWorkerAsync(peerInstanceId, resolvedCode, cancellationToken);
        var status = await AssignWorkerToRoomInternalAsync(room, worker.InstanceId, cancellationToken);
        return status;
    }

    public async Task AssignWorkerToRoomAsync(string roomId, string workerInstanceId, CancellationToken cancellationToken = default)
    {
        if (!_rooms.TryGetValue(roomId, out var room))
            throw new InvalidOperationException("Fiesta room not found.");
        if (!_registeredWorkers.ContainsKey(workerInstanceId))
            throw new InvalidOperationException("Worker is not registered.");

        await AssignWorkerToRoomInternalAsync(room, workerInstanceId, cancellationToken);
    }

    public void UnassignWorkerFromRoom(string roomId, string workerInstanceId)
    {
        if (!_rooms.TryGetValue(roomId, out var room)) return;
        if (_roomAssignments.TryGetValue(roomId, out var assigned))
            assigned.TryRemove(workerInstanceId, out _);

        lock (room.Members)
            room.Members.RemoveAll(m => m.InstanceId == workerInstanceId && m.Role == FiestaMemberRole.Worker);

        SaveState();
        OnStateChanged?.Invoke();
    }

    public IReadOnlyList<FiestaRegisteredWorker> GetAssignedWorkers(string roomId)
    {
        if (!_roomAssignments.TryGetValue(roomId, out var assigned))
            return Array.Empty<FiestaRegisteredWorker>();

        var workers = new List<FiestaRegisteredWorker>();
        foreach (var id in assigned.Keys)
        {
            if (_registeredWorkers.TryGetValue(id, out var worker))
                workers.Add(CloneWorker(worker));
        }
        return workers
            .OrderByDescending(w => w.IsConnected)
            .ThenBy(w => w.MachineName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public async Task BroadcastPromptAsync(string roomId, string sessionName, string prompt, string? model = null, string? workingDirectory = null, CancellationToken cancellationToken = default)
    {
        if (!_rooms.TryGetValue(roomId, out var room))
            throw new InvalidOperationException("Fiesta room not found.");
        if (string.IsNullOrWhiteSpace(prompt))
            throw new ArgumentException("Prompt is required.", nameof(prompt));

        var effectiveSession = string.IsNullOrWhiteSpace(sessionName) ? room.SessionName : sessionName.Trim();
        room.SessionName = effectiveSession;

        var connections = GetRoomWorkerConnections(roomId);
        if (connections.Count == 0)
            throw new InvalidOperationException("No connected workers in this fiesta.");

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(45));

        var tasks = new List<Task>();
        foreach (var worker in connections)
        {
            var request = new FiestaDispatchPromptPayload
            {
                RequestId = Guid.NewGuid().ToString("N"),
                FiestaId = roomId,
                SessionName = effectiveSession,
                Message = prompt,
                Model = model,
                WorkingDirectory = workingDirectory,
                CreateSessionIfMissing = true
            };
            tasks.Add(worker.Client.SendFiestaDispatchPromptAsync(request, roomId, timeoutCts.Token));
        }

        await Task.WhenAll(tasks);
        TouchRoomActivity(roomId, $"Broadcasted prompt to {tasks.Count} worker(s).");
    }

    public async Task SendSessionCommandAsync(string roomId, string command, string sessionName, string? model = null, string? workingDirectory = null, CancellationToken cancellationToken = default)
    {
        if (!_rooms.TryGetValue(roomId, out var room))
            throw new InvalidOperationException("Fiesta room not found.");
        if (string.IsNullOrWhiteSpace(command))
            throw new ArgumentException("Command is required.", nameof(command));

        var effectiveSession = string.IsNullOrWhiteSpace(sessionName) ? room.SessionName : sessionName.Trim();
        room.SessionName = effectiveSession;

        var connections = GetRoomWorkerConnections(roomId);
        if (connections.Count == 0)
            throw new InvalidOperationException("No connected workers in this fiesta.");

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(45));

        var tasks = new List<Task>();
        foreach (var worker in connections)
        {
            var request = new FiestaSessionCommandPayload
            {
                RequestId = Guid.NewGuid().ToString("N"),
                FiestaId = roomId,
                Command = command,
                SessionName = effectiveSession,
                Model = model,
                WorkingDirectory = workingDirectory
            };
            tasks.Add(worker.Client.SendFiestaSessionCommandAsync(request, roomId, timeoutCts.Token));
        }

        await Task.WhenAll(tasks);
        TouchRoomActivity(roomId, $"Sent '{command}' command to {tasks.Count} worker(s).");
    }

    public async Task StartHostingAsync()
    {
        if (string.IsNullOrWhiteSpace(_settings.FiestaJoinCode))
            _settings.FiestaJoinCode = GenerateJoinCode();
        if (string.IsNullOrWhiteSpace(_settings.ServerPassword))
            _settings.ServerPassword = GenerateServerSecret();

        _settings.DirectSharingEnabled = true;
        _settings.Save();

        _bridgeServer.ServerPassword = _settings.ServerPassword;
        _bridgeServer.SetCopilotService(_copilotService);
        _bridgeServer.SetFiestaCoordinator(this);
        _bridgeServer.Start(DevTunnelService.BridgePort, _settings.Port);

        ApplyDiscoveryMode();
        OnStatusMessage?.Invoke($"Fiesta hosting started on {DevTunnelService.BridgePort}.");
        OnStateChanged?.Invoke();
        await Task.CompletedTask;
    }

    public void StopHosting()
    {
        _bridgeServer.Stop();
        _settings.DirectSharingEnabled = false;
        _settings.Save();
        ApplyDiscoveryMode();

        OnStatusMessage?.Invoke("Fiesta hosting stopped.");
        OnStateChanged?.Invoke();
    }

    private async Task EnsureWorkerModeHostingAsync()
    {
        if (!_settings.FiestaOfferAsWorker || !_settings.FiestaAutoStartWorkerHosting)
            return;

        if (string.IsNullOrWhiteSpace(_settings.FiestaJoinCode))
            _settings.FiestaJoinCode = GenerateJoinCode();
        if (string.IsNullOrWhiteSpace(_settings.ServerPassword))
            _settings.ServerPassword = GenerateServerSecret();

        if (_bridgeServer.IsRunning)
            return;

        _bridgeServer.ServerPassword = _settings.ServerPassword;
        _bridgeServer.SetCopilotService(_copilotService);
        _bridgeServer.SetFiestaCoordinator(this);
        _bridgeServer.Start(DevTunnelService.BridgePort, _settings.Port);
        _settings.Save();
        OnStatusMessage?.Invoke("Worker mode hosting auto-started.");
        await Task.CompletedTask;
    }

    private async Task DetectAndApplyTailscaleDefaultsAsync()
    {
        await _tailscaleService.DetectAsync();
        if (_tailscaleService.IsRunning && !_settings.FiestaTailscaleDiscoveryConfigured)
        {
            _settings.FiestaTailscaleDiscoveryEnabled = true;
            _settings.FiestaTailscaleDiscoveryConfigured = true;
            _settings.Save();
        }
    }

    private void ApplyDiscoveryMode()
    {
        var localPeer = BuildLocalPeer(DevTunnelService.BridgePort);
        var tailscaleBrowse = PlatformHelper.IsDesktop && _settings.FiestaTailscaleDiscoveryEnabled;
        var tailnetBroadcast = PlatformHelper.IsDesktop && _settings.FiestaTailnetBroadcastEnabled && _settings.FiestaOfferAsWorker;

        if (_discoveryService.IsRunning)
        {
            _discoveryService.UpdateLocalPeer(localPeer);
            _discoveryService.UpdateMode(advertise: _bridgeServer.IsRunning, browse: _settings.FiestaDiscoveryEnabled, tailscaleBrowse, tailnetBroadcast);
        }
        else
        {
            _discoveryService.Start(localPeer, advertise: _bridgeServer.IsRunning, browse: _settings.FiestaDiscoveryEnabled, tailscaleBrowse, tailnetBroadcast);
        }
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
            LastSeenAt = DateTime.UtcNow,
            DiscoverySource = FiestaDiscoverySource.LanMulticast,
            IsWorkerAvailable = _settings.FiestaOfferAsWorker,
            IsTailscale = _tailscaleService.IsRunning,
            TailnetHost = _tailscaleService.MagicDnsName ?? _tailscaleService.TailscaleIp,
            AdvertisedJoinCode = null
        };
    }

    private async Task<FiestaJoinStatusPayload> AssignWorkerToRoomInternalAsync(FiestaRoom room, string workerInstanceId, CancellationToken cancellationToken)
    {
        if (!_registeredWorkers.TryGetValue(workerInstanceId, out var worker))
            throw new InvalidOperationException("Worker is not registered.");
        if (string.IsNullOrWhiteSpace(worker.PairingToken))
            worker.PairingToken = GeneratePairingToken();

        await EnsureWorkerConnectedAsync(worker, cancellationToken);
        if (!_workerConnections.TryGetValue(worker.InstanceId, out var connection))
            throw new InvalidOperationException("Worker is not connected.");

        if (connection.AuthorizedFiestas.ContainsKey(room.Id))
        {
            AssignWorkerMembership(room, worker);
            return new FiestaJoinStatusPayload
            {
                RequestId = Guid.NewGuid().ToString("N"),
                FiestaId = room.Id,
                Status = FiestaJoinState.Approved,
                WorkerInstanceId = worker.InstanceId,
                WorkerMachineName = worker.MachineName
            };
        }

        var requestId = Guid.NewGuid().ToString("N");
        var joinResult = new TaskCompletionSource<FiestaJoinStatusPayload>(TaskCreationOptions.RunContinuationsAsynchronously);

        void HandleJoinStatus(FiestaJoinStatusPayload payload)
        {
            if (!string.Equals(payload.RequestId, requestId, StringComparison.Ordinal))
                return;
            if (payload.Status == FiestaJoinState.Pending)
            {
                OnStatusMessage?.Invoke($"Join request pending approval on {worker.MachineName}...");
                return;
            }
            joinResult.TrySetResult(payload);
        }

        connection.Client.OnFiestaJoinStatus += HandleJoinStatus;
        try
        {
            await connection.Client.SendFiestaJoinRequestAsync(new FiestaJoinRequestPayload
            {
                RequestId = requestId,
                FiestaId = room.Id,
                OrganizerInstanceId = InstanceId,
                OrganizerMachineName = MachineName,
                OrganizerTrustToken = worker.PairingToken,
                JoinCode = worker.JoinCode,
                RequestedAt = DateTime.UtcNow
            }, room.Id, cancellationToken);

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
                    throw new TimeoutException($"Timed out waiting for {worker.MachineName} to approve fiesta join.");
                }

                if (status.Status == FiestaJoinState.Approved)
                {
                    var canonicalId = string.IsNullOrWhiteSpace(status.WorkerInstanceId) ? worker.InstanceId : status.WorkerInstanceId;
                    if (!string.Equals(canonicalId, worker.InstanceId, StringComparison.Ordinal))
                        CanonicalizeWorkerIdentity(worker.InstanceId, canonicalId, status.WorkerMachineName);

                    connection.AuthorizedFiestas[room.Id] = 0;
                    if (_registeredWorkers.TryGetValue(canonicalId, out var canonicalWorker))
                        AssignWorkerMembership(room, canonicalWorker);
                    else
                        AssignWorkerMembership(room, worker);

                    TouchRoomActivity(room.Id, $"{status.WorkerMachineName} joined this fiesta.");
                    SaveState();
                }

                return status;
            }
        }
        finally
        {
            connection.Client.OnFiestaJoinStatus -= HandleJoinStatus;
        }
    }

    private void CanonicalizeWorkerIdentity(string fromInstanceId, string toInstanceId, string? machineNameHint)
    {
        if (string.Equals(fromInstanceId, toInstanceId, StringComparison.Ordinal))
            return;

        if (_registeredWorkers.TryRemove(fromInstanceId, out var worker))
        {
            worker.InstanceId = toInstanceId;
            if (!string.IsNullOrWhiteSpace(machineNameHint))
                worker.MachineName = machineNameHint;
            _registeredWorkers[toInstanceId] = worker;
        }

        if (_workerConnections.TryRemove(fromInstanceId, out var connection))
            _workerConnections[toInstanceId] = connection;

        foreach (var assignment in _roomAssignments.Values)
        {
            if (assignment.TryRemove(fromInstanceId, out _))
                assignment[toInstanceId] = 0;
        }

        foreach (var room in _rooms.Values)
        {
            lock (room.Members)
            {
                var member = room.Members.FirstOrDefault(m => m.InstanceId == fromInstanceId && m.Role == FiestaMemberRole.Worker);
                if (member != null)
                {
                    member.InstanceId = toInstanceId;
                    if (!string.IsNullOrWhiteSpace(machineNameHint))
                        member.MachineName = machineNameHint;
                    member.LastUpdatedAt = DateTime.UtcNow;
                }
            }
        }
    }

    private void AssignWorkerMembership(FiestaRoom room, FiestaRegisteredWorker worker)
    {
        var assigned = _roomAssignments.GetOrAdd(room.Id, _ => new ConcurrentDictionary<string, byte>(StringComparer.Ordinal));
        assigned[worker.InstanceId] = 0;

        lock (room.Members)
        {
            var member = room.Members.FirstOrDefault(m => m.InstanceId == worker.InstanceId && m.Role == FiestaMemberRole.Worker);
            if (member == null)
            {
                member = new FiestaMember
                {
                    InstanceId = worker.InstanceId,
                    MachineName = worker.MachineName,
                    Host = worker.Host,
                    Port = worker.Port,
                    Role = FiestaMemberRole.Worker
                };
                room.Members.Add(member);
            }

            member.IsConnected = worker.IsConnected;
            member.LastUpdatedAt = DateTime.UtcNow;
            member.Host = worker.Host;
            member.Port = worker.Port;
        }

        SaveState();
        OnStateChanged?.Invoke();
    }

    private async Task EnsureWorkerConnectedAsync(FiestaRegisteredWorker worker, CancellationToken cancellationToken)
    {
        if (_workerConnections.TryGetValue(worker.InstanceId, out var existing))
        {
            if (existing.Client.IsConnected)
            {
                worker.IsConnected = true;
                worker.LastConnectedAt = DateTime.UtcNow;
                worker.LastUpdatedAt = DateTime.UtcNow;
                return;
            }

            existing.Dispose();
            _workerConnections.TryRemove(worker.InstanceId, out _);
        }

        var client = new WsBridgeClient();
        client.OnFiestaDispatchResult += HandleWorkerDispatchResult;
        client.OnFiestaSessionCommandResult += HandleWorkerSessionCommandResult;

        try
        {
            var authToken = string.IsNullOrWhiteSpace(worker.JoinCode) ? null : worker.JoinCode.Trim();
            await client.ConnectAsync(BuildWsUrl(worker), authToken, cancellationToken);
            worker.IsConnected = true;
            worker.LastConnectedAt = DateTime.UtcNow;
            worker.LastUpdatedAt = DateTime.UtcNow;
            _workerConnections[worker.InstanceId] = new WorkerConnection(worker, client);
        }
        catch
        {
            worker.IsConnected = false;
            worker.LastUpdatedAt = DateTime.UtcNow;
            client.Dispose();
            throw;
        }
    }

    private List<WorkerConnection> GetRoomWorkerConnections(string roomId)
    {
        if (!_roomAssignments.TryGetValue(roomId, out var assigned))
            return new List<WorkerConnection>();

        var workers = new List<WorkerConnection>();
        foreach (var workerId in assigned.Keys.ToList())
        {
            if (_workerConnections.TryGetValue(workerId, out var connection) && connection.Client.IsConnected)
            {
                workers.Add(connection);
                if (_registeredWorkers.TryGetValue(workerId, out var worker))
                {
                    worker.IsConnected = true;
                    worker.LastUpdatedAt = DateTime.UtcNow;
                }
                continue;
            }

            if (_registeredWorkers.TryGetValue(workerId, out var staleWorker))
            {
                staleWorker.IsConnected = false;
                staleWorker.LastUpdatedAt = DateTime.UtcNow;
            }

            if (_rooms.TryGetValue(roomId, out var room))
            {
                lock (room.Members)
                {
                    var member = room.Members.FirstOrDefault(m => m.InstanceId == workerId && m.Role == FiestaMemberRole.Worker);
                    if (member != null)
                    {
                        member.IsConnected = false;
                        member.LastUpdatedAt = DateTime.UtcNow;
                    }
                }
            }
        }

        return workers;
    }

    private void HandleWorkerDispatchResult(FiestaDispatchResultPayload payload)
    {
        var summary = payload.Summary;
        if (string.IsNullOrWhiteSpace(summary))
            summary = payload.Success ? "Prompt completed." : payload.Error ?? "Prompt failed.";

        TouchRoomActivity(payload.FiestaId, $"[{payload.WorkerMachineName}] {summary}");
        OnStatusMessage?.Invoke($"[{payload.WorkerMachineName}] {summary}");
    }

    private void HandleWorkerSessionCommandResult(FiestaSessionCommandResultPayload payload)
    {
        var summary = payload.Success
            ? $"[{payload.WorkerMachineName}] {payload.Command} ok"
            : $"[{payload.WorkerMachineName}] {payload.Command} failed: {payload.Error}";
        TouchRoomActivity(payload.FiestaId, summary);
        OnStatusMessage?.Invoke(summary);
    }

    private void TouchRoomActivity(string roomId, string summary)
    {
        if (_rooms.TryGetValue(roomId, out var room))
        {
            room.LastActivityAt = DateTime.UtcNow;
            room.LastSummary = summary;
        }
        SaveState();
        OnStateChanged?.Invoke();
    }

    private object ApplyRoomSort(FiestaRoom room, Dictionary<string, FiestaRoomMeta> roomMetaMap)
    {
        lock (_organization)
        {
            return _organization.SortMode switch
            {
                FiestaSortMode.LastActivity => DateTime.MaxValue - room.LastActivityAt,
                FiestaSortMode.CreatedAt => DateTime.MaxValue - room.CreatedAt,
                FiestaSortMode.Alphabetical => room.Name,
                FiestaSortMode.Manual => (object)(roomMetaMap.TryGetValue(room.Id, out var m) ? m.ManualOrder : int.MaxValue),
                _ => DateTime.MaxValue - room.LastActivityAt
            };
        }
    }

    private void EnsureRoomMeta(string roomId)
    {
        lock (_organization)
        {
            if (_organization.Rooms.Any(r => r.RoomId == roomId))
                return;

            _organization.Rooms.Add(new FiestaRoomMeta { RoomId = roomId, GroupId = FiestaGroup.DefaultId });
        }
    }

    private void ReconcileOrganization()
    {
        lock (_organization)
        {
            if (!_organization.Groups.Any(g => g.Id == FiestaGroup.DefaultId))
            {
                _organization.Groups.Insert(0, new FiestaGroup
                {
                    Id = FiestaGroup.DefaultId,
                    Name = FiestaGroup.DefaultName,
                    SortOrder = 0
                });
            }

            foreach (var roomId in _rooms.Keys)
            {
                if (!_organization.Rooms.Any(r => r.RoomId == roomId))
                    _organization.Rooms.Add(new FiestaRoomMeta { RoomId = roomId, GroupId = FiestaGroup.DefaultId });
            }

            var roomIds = _rooms.Keys.ToHashSet(StringComparer.Ordinal);
            _organization.Rooms.RemoveAll(r => !roomIds.Contains(r.RoomId));
        }
    }

    private void LoadState()
    {
        try
        {
            if (!File.Exists(StateFilePath)) return;
            var json = File.ReadAllText(StateFilePath);
            var state = JsonSerializer.Deserialize<FiestaStateStore>(json);
            if (state == null) return;

            if (state.Rooms != null)
            {
                foreach (var room in state.Rooms)
                    _rooms[room.Id] = room;
            }

            if (state.RegisteredWorkers != null)
            {
                foreach (var worker in state.RegisteredWorkers)
                {
                    if (string.IsNullOrWhiteSpace(worker.PairingToken))
                        worker.PairingToken = GeneratePairingToken();
                    worker.IsConnected = false;
                    _registeredWorkers[worker.InstanceId] = worker;
                }
            }

            if (state.TrustedOrganizers != null)
            {
                foreach (var organizer in state.TrustedOrganizers.Where(v => !string.IsNullOrWhiteSpace(v)))
                    _trustedOrganizers[organizer] = "";
            }

            if (state.TrustedOrganizerRecords != null)
            {
                foreach (var organizer in state.TrustedOrganizerRecords.Where(v => !string.IsNullOrWhiteSpace(v.OrganizerInstanceId)))
                    _trustedOrganizers[organizer.OrganizerInstanceId] = organizer.TrustToken ?? "";
            }

            _organization = state.Organization ?? new FiestaOrganizationState();

            foreach (var room in _rooms.Values)
            {
                var assigned = _roomAssignments.GetOrAdd(room.Id, _ => new ConcurrentDictionary<string, byte>(StringComparer.Ordinal));
                lock (room.Members)
                {
                    foreach (var member in room.Members.Where(m => m.Role == FiestaMemberRole.Worker))
                    {
                        assigned[member.InstanceId] = 0;
                        if (!_registeredWorkers.ContainsKey(member.InstanceId))
                        {
                            _registeredWorkers[member.InstanceId] = new FiestaRegisteredWorker
                            {
                                InstanceId = member.InstanceId,
                                MachineName = member.MachineName,
                                Host = member.Host,
                                Port = member.Port,
                                JoinCode = "",
                                PairingToken = GeneratePairingToken(),
                                Platform = "",
                                IsConnected = member.IsConnected,
                                LastUpdatedAt = member.LastUpdatedAt
                            };
                        }
                    }
                }
            }
        }
        catch { }
    }

    private void SaveState()
    {
        _ = SaveStateAsync();
    }

    private async Task SaveStateAsync()
    {
        await _stateLock.WaitAsync();
        try
        {
            var dir = Path.GetDirectoryName(StateFilePath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            var workersSnapshot = _registeredWorkers.Values.Select(w =>
            {
                var clone = CloneWorker(w);
                clone.IsConnected = _workerConnections.TryGetValue(w.InstanceId, out var c) && c.Client.IsConnected;
                return clone;
            }).ToList();

            FiestaOrganizationState organizationSnapshot;
            lock (_organization)
            {
                organizationSnapshot = new FiestaOrganizationState
                {
                    SortMode = _organization.SortMode,
                    Groups = _organization.Groups.Select(g => new FiestaGroup
                    {
                        Id = g.Id,
                        Name = g.Name,
                        SortOrder = g.SortOrder,
                        IsCollapsed = g.IsCollapsed
                    }).ToList(),
                    Rooms = _organization.Rooms.Select(m => new FiestaRoomMeta
                    {
                        RoomId = m.RoomId,
                        GroupId = m.GroupId,
                        IsPinned = m.IsPinned,
                        ManualOrder = m.ManualOrder
                    }).ToList()
                };
            }

            var state = new FiestaStateStore
            {
                Rooms = _rooms.Values.Select(CloneRoom).ToList(),
                RegisteredWorkers = workersSnapshot,
                TrustedOrganizers = _trustedOrganizers.Keys.ToList(),
                TrustedOrganizerRecords = _trustedOrganizers.Select(kvp => new FiestaTrustedOrganizer
                {
                    OrganizerInstanceId = kvp.Key,
                    TrustToken = kvp.Value
                }).ToList(),
                Organization = organizationSnapshot
            };
            File.WriteAllText(StateFilePath, JsonSerializer.Serialize(state, _jsonOptions));
        }
        catch { }
        finally
        {
            _stateLock.Release();
        }
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
            Members = members,
            SessionName = room.SessionName,
            LastActivityAt = room.LastActivityAt,
            LastSummary = room.LastSummary
        };
    }

    private static FiestaRegisteredWorker CloneWorker(FiestaRegisteredWorker worker)
    {
        return new FiestaRegisteredWorker
        {
            InstanceId = worker.InstanceId,
            MachineName = worker.MachineName,
            Host = worker.Host,
            Port = worker.Port,
            Platform = worker.Platform,
            TailnetHost = worker.TailnetHost,
            JoinCode = worker.JoinCode,
            PairingToken = worker.PairingToken,
            IsConnected = worker.IsConnected,
            LastConnectedAt = worker.LastConnectedAt,
            LastUpdatedAt = worker.LastUpdatedAt
        };
    }

    private static string BuildWsUrl(FiestaRegisteredWorker worker)
    {
        var preferredHost = !string.IsNullOrWhiteSpace(worker.TailnetHost)
            ? worker.TailnetHost!
            : worker.Host;

        if (preferredHost.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
            return preferredHost.Replace("http://", "ws://", StringComparison.OrdinalIgnoreCase).TrimEnd('/');
        if (preferredHost.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return preferredHost.Replace("https://", "wss://", StringComparison.OrdinalIgnoreCase).TrimEnd('/');
        if (preferredHost.StartsWith("ws://", StringComparison.OrdinalIgnoreCase) ||
            preferredHost.StartsWith("wss://", StringComparison.OrdinalIgnoreCase))
            return preferredHost.TrimEnd('/');
        return $"ws://{preferredHost}:{worker.Port}/";
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

    private static string GeneratePairingToken()
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
        foreach (var worker in _workerConnections.Values)
            worker.Dispose();

        _workerConnections.Clear();
        _roomAssignments.Clear();
        _pendingJoinRequests.Clear();
        _stateLock.Dispose();
        GC.SuppressFinalize(this);
    }

    private sealed class WorkerConnection : IDisposable
    {
        public FiestaRegisteredWorker Worker { get; }
        public WsBridgeClient Client { get; }
        public ConcurrentDictionary<string, byte> AuthorizedFiestas { get; } = new(StringComparer.Ordinal);

        public WorkerConnection(FiestaRegisteredWorker worker, WsBridgeClient client)
        {
            Worker = worker;
            Client = client;
        }

        public void Dispose()
        {
            Client.Dispose();
        }
    }
}
