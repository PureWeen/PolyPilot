using System.Collections.Concurrent;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using PolyPilot.Models;

namespace PolyPilot.Services;

public class FiestaService : IDisposable
{
    private const int DiscoveryPort = 43223;
    private static readonly TimeSpan DiscoveryInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan DiscoveryStaleAfter = TimeSpan.FromSeconds(20);
    private static readonly Regex MentionRegex = new(@"(?<!\S)@(?<name>[A-Za-z0-9._-]+)", RegexOptions.Compiled);

    private readonly CopilotService _copilot;
    private readonly WsBridgeServer _bridgeServer;
    private readonly ConcurrentDictionary<string, FiestaDiscoveredWorker> _discoveredWorkers = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, FiestaSessionState> _activeFiestas = new(StringComparer.Ordinal);
    private readonly object _stateLock = new();
    private readonly string _instanceId = Guid.NewGuid().ToString("N");
    private readonly List<FiestaLinkedWorker> _linkedWorkers = new();
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    private CancellationTokenSource? _discoveryCts;
    private Task? _broadcastTask;
    private Task? _listenTask;
    private static string? _stateFilePath;

    public event Action? OnStateChanged;
    public event Action<string, FiestaTaskUpdate>? OnHostTaskUpdate;

    public FiestaService(CopilotService copilot, WsBridgeServer bridgeServer)
    {
        _copilot = copilot;
        _bridgeServer = bridgeServer;
        _bridgeServer.SetFiestaService(this);
        LoadState();
        if (PlatformHelper.IsDesktop)
            StartDiscovery();
    }

    private static string StateFilePath => _stateFilePath ??= Path.Combine(GetPolyPilotBaseDir(), "fiesta.json");

    private static string GetPolyPilotBaseDir()
    {
        try
        {
#if IOS || ANDROID
            return Path.Combine(FileSystem.AppDataDirectory, ".polypilot");
#else
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (string.IsNullOrEmpty(home))
                home = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return Path.Combine(home, ".polypilot");
#endif
        }
        catch
        {
            return Path.Combine(Path.GetTempPath(), ".polypilot");
        }
    }

    public IReadOnlyList<FiestaDiscoveredWorker> DiscoveredWorkers =>
        _discoveredWorkers.Values
            .OrderByDescending(w => w.LastSeenAt)
            .Select(CloneDiscoveredWorker)
            .ToList();

    public IReadOnlyList<FiestaLinkedWorker> LinkedWorkers
    {
        get
        {
            lock (_stateLock)
            {
                return _linkedWorkers.Select(CloneLinkedWorker).ToList();
            }
        }
    }

    public bool IsFiestaActive(string sessionName)
    {
        lock (_stateLock)
        {
            return _activeFiestas.ContainsKey(sessionName);
        }
    }

    public FiestaSessionState? GetFiestaState(string sessionName)
    {
        lock (_stateLock)
        {
            if (!_activeFiestas.TryGetValue(sessionName, out var state))
                return null;
            return new FiestaSessionState
            {
                SessionName = state.SessionName,
                FiestaName = state.FiestaName,
                WorkerIds = state.WorkerIds.ToList()
            };
        }
    }

    public void LinkWorker(string name, string hostname, string bridgeUrl, string token)
    {
        var normalizedUrl = NormalizeBridgeUrl(bridgeUrl);
        if (string.IsNullOrWhiteSpace(normalizedUrl) || string.IsNullOrWhiteSpace(token))
            return;

        var workerName = string.IsNullOrWhiteSpace(name)
            ? (!string.IsNullOrWhiteSpace(hostname) ? hostname.Trim() : normalizedUrl)
            : name.Trim();
        var workerHostname = string.IsNullOrWhiteSpace(hostname) ? workerName : hostname.Trim();

        lock (_stateLock)
        {
            var existing = _linkedWorkers.FirstOrDefault(w =>
                string.Equals(w.BridgeUrl, normalizedUrl, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(w.Hostname, workerHostname, StringComparison.OrdinalIgnoreCase));

            if (existing != null)
            {
                existing.Name = workerName;
                existing.Hostname = workerHostname;
                existing.BridgeUrl = normalizedUrl;
                existing.Token = token.Trim();
                existing.LinkedAt = DateTime.UtcNow;
            }
            else
            {
                _linkedWorkers.Add(new FiestaLinkedWorker
                {
                    Name = workerName,
                    Hostname = workerHostname,
                    BridgeUrl = normalizedUrl,
                    Token = token.Trim(),
                    LinkedAt = DateTime.UtcNow
                });
            }
        }

        SaveState();
        UpdateLinkedWorkerPresence();
        OnStateChanged?.Invoke();
    }

    public void RemoveLinkedWorker(string workerId)
    {
        lock (_stateLock)
        {
            _linkedWorkers.RemoveAll(w => string.Equals(w.Id, workerId, StringComparison.Ordinal));
            foreach (var state in _activeFiestas.Values)
                state.WorkerIds.RemoveAll(id => string.Equals(id, workerId, StringComparison.Ordinal));
        }
        SaveState();
        OnStateChanged?.Invoke();
    }

    public bool StartFiesta(string sessionName, string fiestaName, IReadOnlyCollection<string>? workerIds)
    {
        if (string.IsNullOrWhiteSpace(sessionName) || workerIds == null || workerIds.Count == 0)
            return false;

        var sanitizedName = SanitizeFiestaName(fiestaName);
        lock (_stateLock)
        {
            _activeFiestas[sessionName] = new FiestaSessionState
            {
                SessionName = sessionName,
                FiestaName = sanitizedName,
                WorkerIds = workerIds.Distinct(StringComparer.Ordinal).ToList()
            };
        }

        UpdateLinkedWorkerPresence();
        OnStateChanged?.Invoke();
        return true;
    }

    public void StopFiesta(string sessionName)
    {
        lock (_stateLock)
        {
            _activeFiestas.Remove(sessionName);
        }
        OnStateChanged?.Invoke();
    }

    public async Task<FiestaDispatchResult> DispatchMentionedWorkAsync(string hostSessionName, string prompt, CancellationToken cancellationToken = default)
    {
        var result = new FiestaDispatchResult();
        if (string.IsNullOrWhiteSpace(prompt))
            return result;

        FiestaSessionState? state;
        List<FiestaLinkedWorker> selectedWorkers;
        lock (_stateLock)
        {
            if (!_activeFiestas.TryGetValue(hostSessionName, out var active))
                return result;
            state = new FiestaSessionState
            {
                SessionName = active.SessionName,
                FiestaName = active.FiestaName,
                WorkerIds = active.WorkerIds.ToList()
            };
            selectedWorkers = _linkedWorkers
                .Where(w => state.WorkerIds.Contains(w.Id, StringComparer.Ordinal))
                .Select(CloneLinkedWorker)
                .ToList();
        }

        var mentions = MentionRegex.Matches(prompt)
            .Select(m => m.Groups["name"].Value)
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (mentions.Count == 0)
            return result;

        result.MentionsFound = true;

        var dispatchPrompt = MentionRegex.Replace(prompt, "").Trim();
        if (string.IsNullOrWhiteSpace(dispatchPrompt))
            dispatchPrompt = prompt.Trim();

        var targets = new Dictionary<string, FiestaLinkedWorker>(StringComparer.Ordinal);
        foreach (var mention in mentions)
        {
            if (string.Equals(mention, "all", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var worker in selectedWorkers)
                    targets[worker.Id] = worker;
                continue;
            }

            var match = selectedWorkers.FirstOrDefault(w =>
                string.Equals(w.Name, mention, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(w.Hostname, mention, StringComparison.OrdinalIgnoreCase));
            if (match == null)
            {
                result.UnresolvedMentions.Add(mention);
                continue;
            }

            targets[match.Id] = match;
        }

        foreach (var unresolved in result.UnresolvedMentions)
        {
            OnHostTaskUpdate?.Invoke(hostSessionName, new FiestaTaskUpdate
            {
                TaskId = Guid.NewGuid().ToString("N"),
                WorkerName = unresolved,
                Kind = FiestaTaskUpdateKind.Error,
                Content = $"Worker '@{unresolved}' is not linked/selected for this Fiesta."
            });
        }

        foreach (var worker in targets.Values)
        {
            var taskId = Guid.NewGuid().ToString("N");
            result.DispatchCount++;
            _ = Task.Run(() =>
                RunWorkerTaskAsync(worker, hostSessionName, state.FiestaName, dispatchPrompt, taskId, cancellationToken),
                cancellationToken);
        }

        await Task.Yield();
        return result;
    }

    public async Task<bool> HandleBridgeMessageAsync(string clientId, WebSocket ws, BridgeMessage msg, CancellationToken ct)
    {
        if (msg == null) return false;

        if (msg.Type == BridgeMessageTypes.FiestaPing)
        {
            await SendAsync(ws, BridgeMessage.Create(BridgeMessageTypes.FiestaPong, new FiestaPongPayload
            {
                Sender = Environment.MachineName
            }), ct);
            return true;
        }

        if (msg.Type != BridgeMessageTypes.FiestaAssign)
            return false;

        var assign = msg.GetPayload<FiestaAssignPayload>();
        if (assign == null || string.IsNullOrWhiteSpace(assign.TaskId) || string.IsNullOrWhiteSpace(assign.Prompt))
            return true;

        await HandleFiestaAssignAsync(clientId, ws, assign, ct);
        return true;
    }

    private async Task HandleFiestaAssignAsync(string clientId, WebSocket ws, FiestaAssignPayload assign, CancellationToken ct)
    {
        var workerName = Environment.MachineName;
        var sessionName = $"Fiesta: {SanitizeFiestaName(assign.FiestaName)}";

        async Task SendSafeAsync(BridgeMessage message, CancellationToken token)
        {
            try
            {
                await _bridgeServer.SendBridgeMessageAsync(clientId, ws, message, token);
            }
            catch
            {
                // Best-effort stream updates back to host.
            }
        }

        await SendSafeAsync(BridgeMessage.Create(BridgeMessageTypes.FiestaTaskStarted, new FiestaTaskStartedPayload
        {
            TaskId = assign.TaskId,
            WorkerName = workerName,
            Prompt = assign.Prompt
        }), ct);

        string workspacePath;
        try
        {
            workspacePath = GetFiestaWorkspaceDirectory(assign.FiestaName);
            Directory.CreateDirectory(workspacePath);
        }
        catch (Exception ex)
        {
            await SendSafeAsync(BridgeMessage.Create(BridgeMessageTypes.FiestaTaskError, new FiestaTaskErrorPayload
            {
                TaskId = assign.TaskId,
                WorkerName = workerName,
                Error = $"Failed to initialize workspace: {ex.Message}"
            }), ct);
            return;
        }

        Action<string, string>? onContent = null;
        Action<string, string>? onError = null;

        try
        {
            if (_copilot.GetSession(sessionName) == null)
            {
                await _copilot.CreateSessionAsync(sessionName, workingDirectory: workspacePath, cancellationToken: ct);
            }

            onContent = (session, delta) =>
            {
                if (!string.Equals(session, sessionName, StringComparison.Ordinal) || string.IsNullOrEmpty(delta))
                    return;
                _ = SendSafeAsync(BridgeMessage.Create(BridgeMessageTypes.FiestaTaskDelta, new FiestaTaskDeltaPayload
                {
                    TaskId = assign.TaskId,
                    WorkerName = workerName,
                    Delta = delta
                }), CancellationToken.None);
            };

            onError = (session, error) =>
            {
                if (!string.Equals(session, sessionName, StringComparison.Ordinal) || string.IsNullOrEmpty(error))
                    return;
                _ = SendSafeAsync(BridgeMessage.Create(BridgeMessageTypes.FiestaTaskError, new FiestaTaskErrorPayload
                {
                    TaskId = assign.TaskId,
                    WorkerName = workerName,
                    Error = error
                }), CancellationToken.None);
            };

            _copilot.OnContentReceived += onContent;
            _copilot.OnError += onError;

            var summary = await _copilot.SendPromptAsync(sessionName, assign.Prompt, cancellationToken: ct);
            await SendSafeAsync(BridgeMessage.Create(BridgeMessageTypes.FiestaTaskComplete, new FiestaTaskCompletePayload
            {
                TaskId = assign.TaskId,
                WorkerName = workerName,
                Success = true,
                Summary = summary ?? ""
            }), ct);
        }
        catch (Exception ex)
        {
            await SendSafeAsync(BridgeMessage.Create(BridgeMessageTypes.FiestaTaskError, new FiestaTaskErrorPayload
            {
                TaskId = assign.TaskId,
                WorkerName = workerName,
                Error = ex.Message
            }), ct);
        }
        finally
        {
            if (onContent != null) _copilot.OnContentReceived -= onContent;
            if (onError != null) _copilot.OnError -= onError;
        }
    }

    private async Task RunWorkerTaskAsync(FiestaLinkedWorker worker, string hostSessionName, string fiestaName, string prompt, string taskId, CancellationToken cancellationToken)
    {
        OnHostTaskUpdate?.Invoke(hostSessionName, new FiestaTaskUpdate
        {
            TaskId = taskId,
            WorkerName = worker.Name,
            Kind = FiestaTaskUpdateKind.Started,
            Content = $"Dispatching to @{worker.Name}..."
        });

        try
        {
            using var ws = new ClientWebSocket();
            if (!string.IsNullOrWhiteSpace(worker.Token))
                ws.Options.SetRequestHeader("X-Tunnel-Authorization", $"tunnel {worker.Token}");

            var wsUri = ToWebSocketUri(worker.BridgeUrl);
            using var connectTimeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            connectTimeoutCts.CancelAfter(TimeSpan.FromSeconds(30));

            await ws.ConnectAsync(new Uri(wsUri), connectTimeoutCts.Token);
            await SendAsync(ws, BridgeMessage.Create(BridgeMessageTypes.FiestaAssign, new FiestaAssignPayload
            {
                TaskId = taskId,
                HostSessionName = hostSessionName,
                FiestaName = fiestaName,
                Prompt = prompt
            }), cancellationToken);

            await ReadTaskUpdatesAsync(ws, hostSessionName, worker.Name, taskId, cancellationToken);
        }
        catch (Exception ex)
        {
            OnHostTaskUpdate?.Invoke(hostSessionName, new FiestaTaskUpdate
            {
                TaskId = taskId,
                WorkerName = worker.Name,
                Kind = FiestaTaskUpdateKind.Error,
                Content = ex.Message
            });
        }
    }

    private async Task ReadTaskUpdatesAsync(ClientWebSocket ws, string hostSessionName, string workerName, string taskId, CancellationToken ct)
    {
        var buffer = new byte[65536];
        var messageBuffer = new StringBuilder();
        var completed = false;

        while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
        {
            var result = await ws.ReceiveAsync(buffer, ct);
            if (result.MessageType == WebSocketMessageType.Close)
                break;

            messageBuffer.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
            if (!result.EndOfMessage)
                continue;

            var json = messageBuffer.ToString();
            messageBuffer.Clear();
            var msg = BridgeMessage.Deserialize(json);
            if (msg == null)
                continue;

            switch (msg.Type)
            {
                case BridgeMessageTypes.FiestaTaskStarted:
                    var started = msg.GetPayload<FiestaTaskStartedPayload>();
                    OnHostTaskUpdate?.Invoke(hostSessionName, new FiestaTaskUpdate
                    {
                        TaskId = taskId,
                        WorkerName = started?.WorkerName ?? workerName,
                        Kind = FiestaTaskUpdateKind.Started,
                        Content = started?.Prompt ?? ""
                    });
                    break;

                case BridgeMessageTypes.FiestaTaskDelta:
                    var delta = msg.GetPayload<FiestaTaskDeltaPayload>();
                    if (delta != null)
                    {
                        OnHostTaskUpdate?.Invoke(hostSessionName, new FiestaTaskUpdate
                        {
                            TaskId = taskId,
                            WorkerName = delta.WorkerName,
                            Kind = FiestaTaskUpdateKind.Delta,
                            Content = delta.Delta
                        });
                    }
                    break;

                case BridgeMessageTypes.FiestaTaskComplete:
                    var complete = msg.GetPayload<FiestaTaskCompletePayload>();
                    OnHostTaskUpdate?.Invoke(hostSessionName, new FiestaTaskUpdate
                    {
                        TaskId = taskId,
                        WorkerName = complete?.WorkerName ?? workerName,
                        Kind = FiestaTaskUpdateKind.Completed,
                        Success = complete?.Success ?? false,
                        Content = complete?.Summary ?? ""
                    });
                    completed = true;
                    break;

                case BridgeMessageTypes.FiestaTaskError:
                    var err = msg.GetPayload<FiestaTaskErrorPayload>();
                    OnHostTaskUpdate?.Invoke(hostSessionName, new FiestaTaskUpdate
                    {
                        TaskId = taskId,
                        WorkerName = err?.WorkerName ?? workerName,
                        Kind = FiestaTaskUpdateKind.Error,
                        Content = err?.Error ?? "Unknown Fiesta worker error."
                    });
                    completed = true;
                    break;
            }

            if (completed)
                break;
        }

        if (!completed)
        {
            OnHostTaskUpdate?.Invoke(hostSessionName, new FiestaTaskUpdate
            {
                TaskId = taskId,
                WorkerName = workerName,
                Kind = FiestaTaskUpdateKind.Error,
                Content = "Worker connection closed before completion."
            });
        }
    }

    private static async Task SendAsync(WebSocket ws, BridgeMessage message, CancellationToken ct)
    {
        if (ws.State != WebSocketState.Open) return;
        var json = message.Serialize();
        var bytes = Encoding.UTF8.GetBytes(json);
        await ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, ct);
    }

    private static string ToWebSocketUri(string bridgeUrl)
    {
        var normalized = NormalizeBridgeUrl(bridgeUrl);
        if (normalized.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return "wss://" + normalized["https://".Length..];
        if (normalized.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
            return "ws://" + normalized["http://".Length..];
        return normalized;
    }

    private static string NormalizeBridgeUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return "";
        var normalized = url.Trim();

        if (normalized.StartsWith("ws://", StringComparison.OrdinalIgnoreCase))
            normalized = "http://" + normalized["ws://".Length..];
        else if (normalized.StartsWith("wss://", StringComparison.OrdinalIgnoreCase))
            normalized = "https://" + normalized["wss://".Length..];

        if (!normalized.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !normalized.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            normalized = "http://" + normalized;
        }

        return normalized.TrimEnd('/');
    }

    public static string GetFiestaWorkspaceDirectory(string fiestaName)
    {
        var safeName = SanitizeFiestaName(fiestaName);
        return Path.Combine(GetPolyPilotBaseDir(), "workspace", safeName);
    }

    private static string SanitizeFiestaName(string fiestaName)
    {
        if (string.IsNullOrWhiteSpace(fiestaName)) return "Fiesta";
        var sb = new StringBuilder(fiestaName.Length);
        foreach (var ch in fiestaName.Trim())
        {
            if (char.IsLetterOrDigit(ch) || ch == '-' || ch == '_' || ch == ' ')
                sb.Append(ch);
            else
                sb.Append('_');
        }

        var safe = sb.ToString().Trim().Replace(' ', '-');
        while (safe.Contains("--", StringComparison.Ordinal))
            safe = safe.Replace("--", "-", StringComparison.Ordinal);

        if (safe.Length > 64) safe = safe[..64];
        return string.IsNullOrWhiteSpace(safe) ? "Fiesta" : safe;
    }

    private void LoadState()
    {
        try
        {
            if (!File.Exists(StateFilePath))
                return;

            var json = File.ReadAllText(StateFilePath);
            var state = JsonSerializer.Deserialize<FiestaState>(json, _jsonOptions);
            if (state?.LinkedWorkers == null) return;

            lock (_stateLock)
            {
                _linkedWorkers.Clear();
                _linkedWorkers.AddRange(state.LinkedWorkers.Select(CloneLinkedWorker));
            }
        }
        catch
        {
            // Ignore corrupt Fiesta state and continue with empty state.
        }
    }

    private void SaveState()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(StateFilePath)!);
            FiestaState state;
            lock (_stateLock)
            {
                state = new FiestaState
                {
                    LinkedWorkers = _linkedWorkers.Select(CloneLinkedWorker).ToList()
                };
            }
            File.WriteAllText(StateFilePath, JsonSerializer.Serialize(state, _jsonOptions));
        }
        catch
        {
            // Best effort persistence.
        }
    }

    private void StartDiscovery()
    {
        _discoveryCts = new CancellationTokenSource();
        _broadcastTask = Task.Run(() => BroadcastPresenceLoopAsync(_discoveryCts.Token));
        _listenTask = Task.Run(() => ListenForWorkersLoopAsync(_discoveryCts.Token));
    }

    private async Task BroadcastPresenceLoopAsync(CancellationToken ct)
    {
        using var sender = new UdpClient { EnableBroadcast = true };
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (_bridgeServer.IsRunning && _bridgeServer.BridgePort > 0)
                {
                    var localIp = GetPrimaryLocalIpAddress();
                    if (!string.IsNullOrEmpty(localIp))
                    {
                        var announcement = new FiestaDiscoveryAnnouncement
                        {
                            InstanceId = _instanceId,
                            Hostname = Environment.MachineName,
                            BridgeUrl = $"http://{localIp}:{_bridgeServer.BridgePort}",
                            TimestampUtc = DateTime.UtcNow
                        };

                        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(announcement, _jsonOptions));
                        await sender.SendAsync(bytes, bytes.Length, new IPEndPoint(IPAddress.Broadcast, DiscoveryPort));
                    }
                }
            }
            catch
            {
                // Discovery is best effort.
            }

            try { await Task.Delay(DiscoveryInterval, ct); } catch { }
        }
    }

    private async Task ListenForWorkersLoopAsync(CancellationToken ct)
    {
        using var listener = new UdpClient(DiscoveryPort) { EnableBroadcast = true };
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var result = await listener.ReceiveAsync(ct);
                var json = Encoding.UTF8.GetString(result.Buffer);
                var announcement = JsonSerializer.Deserialize<FiestaDiscoveryAnnouncement>(json, _jsonOptions);
                if (announcement == null || string.IsNullOrWhiteSpace(announcement.InstanceId))
                    continue;
                if (string.Equals(announcement.InstanceId, _instanceId, StringComparison.OrdinalIgnoreCase))
                    continue;

                _discoveredWorkers.AddOrUpdate(
                    announcement.InstanceId,
                    _ => new FiestaDiscoveredWorker
                    {
                        InstanceId = announcement.InstanceId,
                        Hostname = announcement.Hostname ?? "Unknown",
                        BridgeUrl = NormalizeBridgeUrl(announcement.BridgeUrl ?? ""),
                        LastSeenAt = DateTime.UtcNow
                    },
                    (_, existing) =>
                    {
                        existing.Hostname = string.IsNullOrWhiteSpace(announcement.Hostname) ? existing.Hostname : announcement.Hostname;
                        existing.BridgeUrl = string.IsNullOrWhiteSpace(announcement.BridgeUrl) ? existing.BridgeUrl : NormalizeBridgeUrl(announcement.BridgeUrl);
                        existing.LastSeenAt = DateTime.UtcNow;
                        return existing;
                    });

                PruneStaleDiscoveredWorkers();
                UpdateLinkedWorkerPresence();
                OnStateChanged?.Invoke();
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
                // Ignore malformed discovery packets.
            }
        }
    }

    private void PruneStaleDiscoveredWorkers()
    {
        var cutoff = DateTime.UtcNow - DiscoveryStaleAfter;
        foreach (var worker in _discoveredWorkers.Values.Where(w => w.LastSeenAt < cutoff).ToList())
            _discoveredWorkers.TryRemove(worker.InstanceId, out _);
    }

    private void UpdateLinkedWorkerPresence()
    {
        var discovered = _discoveredWorkers.Values.ToList();
        lock (_stateLock)
        {
            foreach (var linked in _linkedWorkers)
            {
                var found = discovered.FirstOrDefault(d =>
                    string.Equals(d.Hostname, linked.Hostname, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(NormalizeBridgeUrl(d.BridgeUrl), NormalizeBridgeUrl(linked.BridgeUrl), StringComparison.OrdinalIgnoreCase));
                linked.IsOnline = found != null && (DateTime.UtcNow - found.LastSeenAt) <= DiscoveryStaleAfter;
                if (found != null) linked.LastSeenAt = found.LastSeenAt;
            }
        }
    }

    private static string? GetPrimaryLocalIpAddress()
    {
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up) continue;
                if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;

                var ip = ni.GetIPProperties().UnicastAddresses
                    .FirstOrDefault(a => a.Address.AddressFamily == AddressFamily.InterNetwork);
                if (ip != null)
                    return ip.Address.ToString();
            }
        }
        catch
        {
            // Ignore and return null.
        }
        return null;
    }

    private static FiestaDiscoveredWorker CloneDiscoveredWorker(FiestaDiscoveredWorker worker) =>
        new()
        {
            InstanceId = worker.InstanceId,
            Hostname = worker.Hostname,
            BridgeUrl = worker.BridgeUrl,
            LastSeenAt = worker.LastSeenAt
        };

    private static FiestaLinkedWorker CloneLinkedWorker(FiestaLinkedWorker worker) =>
        new()
        {
            Id = worker.Id,
            Name = worker.Name,
            Hostname = worker.Hostname,
            BridgeUrl = worker.BridgeUrl,
            Token = worker.Token,
            LinkedAt = worker.LinkedAt,
            LastSeenAt = worker.LastSeenAt,
            IsOnline = worker.IsOnline
        };

    public void Dispose()
    {
        _discoveryCts?.Cancel();
        _discoveryCts?.Dispose();
    }

    private sealed class FiestaDiscoveryAnnouncement
    {
        public string InstanceId { get; set; } = "";
        public string? Hostname { get; set; }
        public string? BridgeUrl { get; set; }
        public DateTime TimestampUtc { get; set; }
    }
}
