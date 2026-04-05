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
    private const int MaxPendingPairRequests = 5;
    private const int MaxPendingPairRequestsPerIp = 2;
    private static readonly TimeSpan DiscoveryInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan DiscoveryStaleAfter = TimeSpan.FromSeconds(20);
    private static readonly Regex MentionRegex = new(@"(?<!\S)@(?<name>[A-Za-z0-9._-]+)", RegexOptions.Compiled);

    private readonly CopilotService _copilot;
    private readonly WsBridgeServer _bridgeServer;
    private readonly TailscaleService? _tailscale;
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
    private readonly Dictionary<string, PendingPairRequest> _pendingPairRequests = new(StringComparer.Ordinal);

    internal static void SetStateFilePathForTesting(string path) => _stateFilePath = path;

    public event Action? OnStateChanged;
    public event Action<string, FiestaTaskUpdate>? OnHostTaskUpdate;
    /// <summary>Fires on the worker side when a remote host requests pairing. Args: requestId, hostName, remoteIp.</summary>
    public event Action<string, string, string>? OnPairRequested;
    /// <summary>
    /// Fires when ApprovePairRequestAsync succeeds in claiming the TCS but the send fails.
    /// The pairing cannot be completed for this request — the host will time out and show "Unreachable".
    /// UI should prompt the user to retry pairing from the host side.
    /// Args: requestId, errorMessage.
    /// </summary>
    public event Action<string, string>? OnPairApprovalSendFailed;

    public FiestaService(CopilotService copilot, WsBridgeServer bridgeServer, TailscaleService tailscale)
    {
        _copilot = copilot;
        _bridgeServer = bridgeServer;
        _tailscale = tailscale;
        _bridgeServer.SetFiestaService(this);
        LoadState();
        if (PlatformHelper.IsDesktop)
            StartDiscovery();
    }

    private static string StateFilePath => _stateFilePath ??= Path.Combine(CopilotService.BaseDir, "fiesta.json");

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
        => LinkWorkerAndReturn(name, hostname, bridgeUrl, token);

    private FiestaLinkedWorker? LinkWorkerAndReturn(string name, string hostname, string bridgeUrl, string token)
    {
        var normalizedUrl = NormalizeBridgeUrl(bridgeUrl);
        if (string.IsNullOrWhiteSpace(normalizedUrl) || string.IsNullOrWhiteSpace(token))
            return null;

        var workerName = string.IsNullOrWhiteSpace(name)
            ? (!string.IsNullOrWhiteSpace(hostname) ? hostname.Trim() : normalizedUrl)
            : name.Trim();
        var workerHostname = string.IsNullOrWhiteSpace(hostname) ? workerName : hostname.Trim();

        FiestaLinkedWorker result;
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
                result = existing;
            }
            else
            {
                var added = new FiestaLinkedWorker
                {
                    Name = workerName,
                    Hostname = workerHostname,
                    BridgeUrl = normalizedUrl,
                    Token = token.Trim(),
                    LinkedAt = DateTime.UtcNow
                };
                _linkedWorkers.Add(added);
                result = added;
            }
        }

        SaveState();
        UpdateLinkedWorkerPresence();
        OnStateChanged?.Invoke();
        return result;
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
            var mentionToken = NormalizeMentionToken(mention);
            if (string.Equals(mention, "all", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var worker in selectedWorkers)
                    targets[worker.Id] = worker;
                continue;
            }

            var match = selectedWorkers.FirstOrDefault(w =>
                string.Equals(w.Name, mention, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(w.Hostname, mention, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(NormalizeMentionToken(w.Name), mentionToken, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(NormalizeMentionToken(w.Hostname), mentionToken, StringComparison.OrdinalIgnoreCase));
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

    // ---- Pairing string (Feature B) ----

    public IReadOnlyList<PendingPairRequestInfo> PendingPairRequests
    {
        get
        {
            lock (_stateLock)
                return _pendingPairRequests.Values
                    .Where(r => r.ExpiresAt > DateTime.UtcNow)
                    .Select(r => new PendingPairRequestInfo
                    {
                        RequestId = r.RequestId,
                        HostName = r.HostName,
                        RemoteIp = r.RemoteIp,
                        ExpiresAt = r.ExpiresAt
                    })
                    .ToList();
        }
    }

    public string GeneratePairingString(string? preferredHost = null)
    {
        if (!_bridgeServer.IsRunning)
            throw new InvalidOperationException("Bridge server is not running. Enable Direct Sharing first.");

        var token = EnsureServerPassword();

        // If no explicit host supplied, prefer Tailscale IP/MagicDNS when running —
        // it works across different networks, not just the local LAN.
        if (preferredHost == null && _tailscale?.IsRunning == true)
            preferredHost = _tailscale.MagicDnsName ?? _tailscale.TailscaleIp;

        var localIp = preferredHost ?? GetPrimaryLocalIpAddress() ?? "localhost";
        var url = $"http://{localIp}:{_bridgeServer.BridgePort}";

        var payload = new FiestaPairingPayload
        {
            Url = url,
            Token = token,
            Hostname = Environment.MachineName
        };
        var json = JsonSerializer.Serialize(payload, _jsonOptions);
        var b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(json))
                         .TrimEnd('=')
                         .Replace('+', '-')
                         .Replace('/', '_');
        return $"pp+{b64}";
    }

    public FiestaLinkedWorker ParseAndLinkPairingString(string pairingString)
    {
        if (string.IsNullOrWhiteSpace(pairingString) || !pairingString.StartsWith("pp+", StringComparison.Ordinal))
            throw new FormatException("Not a valid PolyPilot pairing string (must start with 'pp+').");
        if (pairingString.Length > 4096)
            throw new FormatException("Pairing string is too large.");

        var b64 = pairingString[3..].Replace('-', '+').Replace('_', '/');
        // Restore standard base64 padding
        int remainder = b64.Length % 4;
        var padded = remainder == 2 ? b64 + "=="
                   : remainder == 3 ? b64 + "="
                   : b64;

        byte[] bytes;
        try { bytes = Convert.FromBase64String(padded); }
        catch (FormatException) { throw new FormatException("Pairing string is corrupted (invalid base64)."); }

        var json = Encoding.UTF8.GetString(bytes);
        var parsed = JsonSerializer.Deserialize<FiestaPairingPayload>(json, _jsonOptions)
            ?? throw new FormatException("Pairing string payload is empty.");

        if (string.IsNullOrWhiteSpace(parsed.Url))
            throw new FormatException("Pairing string is missing a URL.");
        if (string.IsNullOrWhiteSpace(parsed.Token))
            throw new FormatException("Pairing string is missing a token.");

        var name = !string.IsNullOrWhiteSpace(parsed.Hostname) ? parsed.Hostname : "Unknown";
        var linked = LinkWorkerAndReturn(name, name, parsed.Url, parsed.Token)
            ?? throw new InvalidOperationException("Failed to link worker (invalid URL or token).");
        return CloneLinkedWorker(linked);
    }

    // ---- Push-to-pair — Worker (incoming) side (Feature C) ----

    public async Task HandleIncomingPairHandshakeAsync(WebSocket ws, string remoteIp, CancellationToken ct)
    {
        // Read the initial pair request with a short timeout
        using var readCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        readCts.CancelAfter(TimeSpan.FromSeconds(10));

        BridgeMessage? msg;
        try { msg = await ReadSingleMessageAsync(ws, readCts.Token); }
        catch (OperationCanceledException) { return; }

        if (msg?.Type != BridgeMessageTypes.FiestaPairRequest) return;

        var req = msg.GetPayload<FiestaPairRequestPayload>();
        if (req == null || string.IsNullOrWhiteSpace(req.RequestId)) return;

        var pending = new PendingPairRequest
        {
            RequestId = req.RequestId,
            HostInstanceId = req.HostInstanceId,
            HostName = req.HostName,
            RemoteIp = remoteIp,
            Socket = ws,
            ExpiresAt = DateTime.UtcNow.AddSeconds(60)
        };

        // Capture the TCS before releasing the lock
        TaskCompletionSource<bool> tcs;
        bool isDuplicate;
        lock (_stateLock)
        {
            var requestsFromIp = _pendingPairRequests.Values.Count(r => r.RemoteIp == remoteIp);
            isDuplicate = _pendingPairRequests.Count >= MaxPendingPairRequests
                       || requestsFromIp >= MaxPendingPairRequestsPerIp;
            if (!isDuplicate)
            {
                _pendingPairRequests[req.RequestId] = pending;
                tcs = pending.CompletionSource;
            }
            else
            {
                tcs = null!; // won't be used
            }
        }

        if (isDuplicate)
        {
            // Already handling a pair request — deny inline so the send completes
            // before this method returns and the caller closes the socket.
            try
            {
                await SendAsync(ws, BridgeMessage.Create(BridgeMessageTypes.FiestaPairResponse,
                    new FiestaPairResponsePayload { RequestId = req.RequestId, Approved = false }), ct);
            }
            catch { }
            return;
        }

        OnPairRequested?.Invoke(req.RequestId, req.HostName, remoteIp);
        OnStateChanged?.Invoke();

        // Wait for user approval/denial (up to 60s)
        using var expiryCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        expiryCts.CancelAfter(TimeSpan.FromSeconds(60));
        try
        {
            await tcs.Task.WaitAsync(expiryCts.Token);
            // Winner's send is in-flight — wait for it to complete before returning so the
            // caller's finally (socket close) doesn't race the outgoing message.
            try { await pending.SendComplete.Task.WaitAsync(TimeSpan.FromSeconds(5)); } catch (TimeoutException) { } catch (OperationCanceledException) { }
        }
        catch (OperationCanceledException)
        {
            // Timed out — auto-deny. Claim via TrySetResult first so we don't race with
            // ApprovePairRequestAsync (only the winner of TrySetResult sends).
            if (tcs.TrySetResult(false))
            {
                try
                {
                    await SendAsync(ws, BridgeMessage.Create(BridgeMessageTypes.FiestaPairResponse,
                        new FiestaPairResponsePayload { RequestId = req.RequestId, Approved = false }), CancellationToken.None);
                }
                catch { }
                finally
                {
                    pending.SendComplete.TrySetResult();
                }
            }
            else
            {
                // Approve already won — wait for its send to finish before closing socket
                try { await pending.SendComplete.Task.WaitAsync(TimeSpan.FromSeconds(5)); } catch (TimeoutException) { } catch (OperationCanceledException) { }
            }
        }
        finally
        {
            lock (_stateLock) _pendingPairRequests.Remove(req.RequestId);
            OnStateChanged?.Invoke();
        }
    }

    public async Task<bool> ApprovePairRequestAsync(string requestId)
    {
        PendingPairRequest? pending;
        TaskCompletionSource<bool>? tcs;
        lock (_stateLock)
        {
            if (!_pendingPairRequests.TryGetValue(requestId, out pending)) return false;
            tcs = pending.CompletionSource;
        }

        var token = EnsureServerPassword();
        var localIp = (_tailscale?.IsRunning == true ? (_tailscale.MagicDnsName ?? _tailscale.TailscaleIp) : null)
                      ?? GetPrimaryLocalIpAddress() ?? "localhost";
        var bridgeUrl = $"http://{localIp}:{_bridgeServer.BridgePort}";

        // Atomically claim ownership. If the timeout already fired (TrySetResult(false) won),
        // skip sending — the WebSocket may already be closed.
        if (!tcs.TrySetResult(true))
            return false; // timeout already won, don't attempt a concurrent send

        try
        {
            await SendAsync(pending.Socket, BridgeMessage.Create(
                BridgeMessageTypes.FiestaPairResponse,
                new FiestaPairResponsePayload
                {
                    RequestId = requestId,
                    Approved = true,
                    BridgeUrl = bridgeUrl,
                    Token = token,
                    WorkerName = Environment.MachineName
                }), CancellationToken.None);
            return true;
        }
        catch (Exception ex)
        {
            // TCS already resolved to true so this request cannot be retried or denied.
            // Log clearly and fire event so the UI can prompt the user to retry from the host side.
            var msg = ex.Message;
            Console.WriteLine($"[Fiesta] Approval send failed (request={requestId}, irrecoverable): {msg}");
            OnPairApprovalSendFailed?.Invoke(requestId, msg);
            return false;
        }
        finally
        {
            // Signal that our send is complete so HandleIncomingPairHandshakeAsync
            // can safely return (allowing the caller to close the socket).
            pending.SendComplete.TrySetResult();
        }
    }

    public async Task DenyPairRequestAsync(string requestId)
    {
        PendingPairRequest? pending;
        TaskCompletionSource<bool>? tcs;
        lock (_stateLock)
        {
            if (!_pendingPairRequests.TryGetValue(requestId, out pending)) return;
            tcs = pending.CompletionSource;
        }

        // Atomically claim ownership — if approve already won, skip sending.
        if (!tcs.TrySetResult(false))
            return; // approve already won, don't race on the socket

        try
        {
            await SendAsync(pending.Socket, BridgeMessage.Create(
                BridgeMessageTypes.FiestaPairResponse,
                new FiestaPairResponsePayload { RequestId = requestId, Approved = false }),
                CancellationToken.None);
        }
        catch { }
        finally
        {
            // Signal send complete so HandleIncomingPairHandshakeAsync can safely return.
            pending.SendComplete.TrySetResult();
        }
    }

    // Keep a synchronous shim for callers that can't await (e.g., Blazor @onclick non-async)
    public void DenyPairRequest(string requestId) =>
        _ = DenyPairRequestAsync(requestId);

    // ---- Push-to-pair — Host (outgoing) side (Feature C) ----

    public async Task<PairRequestResult> RequestPairAsync(FiestaDiscoveredWorker worker, CancellationToken ct = default)
    {
        var wsUri = ToWebSocketUri(worker.BridgeUrl);
        // Append /pair path
        wsUri = wsUri.TrimEnd('/') + "/pair";
        var requestId = Guid.NewGuid().ToString("N");

        try
        {
            using var ws = new ClientWebSocket();
            // No auth header — /pair is intentionally unauthenticated
            using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            connectCts.CancelAfter(TimeSpan.FromSeconds(10));

            await ws.ConnectAsync(new Uri(wsUri), connectCts.Token);

            await SendAsync(ws, BridgeMessage.Create(
                BridgeMessageTypes.FiestaPairRequest,
                new FiestaPairRequestPayload
                {
                    RequestId = requestId,
                    HostInstanceId = _instanceId,
                    HostName = Environment.MachineName
                }), ct);

            // Wait up to 65s for the worker to approve or deny
            using var responseCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            responseCts.CancelAfter(TimeSpan.FromSeconds(65));
            var msg = await ReadSingleMessageAsync(ws, responseCts.Token);

            if (msg?.Type != BridgeMessageTypes.FiestaPairResponse)
                return PairRequestResult.Unreachable;

            var resp = msg.GetPayload<FiestaPairResponsePayload>();
            if (resp == null || !resp.Approved)
                return PairRequestResult.Denied;

            // Guard: an approval without connection details is a malformed response
            if (string.IsNullOrWhiteSpace(resp.BridgeUrl) || string.IsNullOrWhiteSpace(resp.Token))
                return PairRequestResult.Unreachable;

            var workerName = !string.IsNullOrWhiteSpace(resp.WorkerName) ? resp.WorkerName : worker.Hostname;
            LinkWorker(workerName, worker.Hostname, resp.BridgeUrl, resp.Token);
            return PairRequestResult.Approved;
        }
        catch (WebSocketException) { return PairRequestResult.Unreachable; }
        catch (OperationCanceledException) { return PairRequestResult.Timeout; }
    }

    // ---- Shared helper: read a single framed WebSocket message ----

    private static async Task<BridgeMessage?> ReadSingleMessageAsync(WebSocket ws, CancellationToken ct)
    {
        var buffer = new byte[65536];
        var sb = new StringBuilder();
        while (ws.State == WebSocketState.Open)
        {
            var result = await ws.ReceiveAsync(buffer, ct);
            if (result.MessageType == WebSocketMessageType.Close) return null;
            sb.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
            if (sb.Length > 256 * 1024) return null; // guard against unbounded frames on unauthenticated /pair path
            if (result.EndOfMessage) break;
        }
        return BridgeMessage.Deserialize(sb.ToString());
    }

    // ---- Settings integration ----

    private string EnsureServerPassword()
    {
        // Fast path: check the runtime value without disk I/O.
        lock (_stateLock)
        {
            if (!string.IsNullOrWhiteSpace(_bridgeServer.ServerPassword))
                return _bridgeServer.ServerPassword;
        }

        // Slow path: load settings outside the lock so steady-state pairing operations
        // (which hold _stateLock for _pendingPairRequests / _linkedWorkers reads) are
        // not blocked by disk I/O.
        var settings = ConnectionSettings.Load();
        string candidatePassword;
        bool needsSave = false;

        if (!string.IsNullOrWhiteSpace(settings.ServerPassword))
        {
            candidatePassword = settings.ServerPassword;
        }
        else
        {
            // Generate a candidate; the final winner is decided inside the lock below.
            candidatePassword = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(18))
                                       .Replace('+', '-').Replace('/', '_').TrimEnd('=');
            needsSave = true;
        }

        // Re-enter the lock to elect exactly one winner.
        // If another thread already stored a password we use that.
        // If we win, we also save — under the lock — so the disk write and the
        // runtime state stay in sync even when two threads race here simultaneously.
        string password;
        lock (_stateLock)
        {
            if (!string.IsNullOrWhiteSpace(_bridgeServer.ServerPassword))
            {
                // Another thread already set it — no save needed.
                password = _bridgeServer.ServerPassword;
            }
            else
            {
                password = candidatePassword;
                _bridgeServer.ServerPassword = password;
                if (needsSave)
                {
                    // Persist inside the lock so disk and runtime value are always the same.
                    // This I/O happens only once per process lifetime (when no password existed).
                    settings.ServerPassword = password;
                    settings.Save();
                    Console.WriteLine("[Fiesta] Auto-generated server password for pairing.");
                }
            }
        }

        return password;
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
            {
                ws.Options.SetRequestHeader("X-Tunnel-Authorization", $"tunnel {worker.Token}");
                ws.Options.SetRequestHeader("X-Bridge-Authorization", worker.Token);
            }

            var wsUri = ToWebSocketUri(worker.BridgeUrl);
            using var connectTimeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            connectTimeoutCts.CancelAfter(TimeSpan.FromSeconds(30));

            await ws.ConnectAsync(new Uri(WsBridgeClient.AddTokenQuery(wsUri, worker.Token)), connectTimeoutCts.Token);
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
            if (messageBuffer.Length > 256 * 1024)
            {
                try { await ws.CloseAsync(WebSocketCloseStatus.MessageTooBig, "Message exceeds 256KB limit", CancellationToken.None); } catch { }
                break; // guard against unbounded frames
            }
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
        var baseDir = Path.GetFullPath(Path.Combine(CopilotService.BaseDir, "workspace"));
        var fullPath = Path.GetFullPath(Path.Combine(baseDir, safeName));

        // Primary guard: reject paths that escape baseDir by path components (covers ".." attacks).
        var relativePath = Path.GetRelativePath(baseDir, fullPath);
        if (relativePath.StartsWith("..", StringComparison.Ordinal))
            throw new InvalidOperationException("Workspace path escapes the base directory.");

        // Secondary guard: if the directory already exists, resolve symlinks and re-validate.
        // Path.GetFullPath does NOT resolve symlinks, so a symlink inside the workspace tree
        // could redirect to an arbitrary location. ResolveLinkTarget(returnFinalTarget: true)
        // follows the full chain. Only needed when the directory exists (pre-created symlinks).
        if (Directory.Exists(fullPath))
        {
            var resolved = Directory.ResolveLinkTarget(fullPath, returnFinalTarget: true)?.FullName;
            if (resolved != null)
            {
                var resolvedRelative = Path.GetRelativePath(baseDir, resolved);
                if (resolvedRelative.StartsWith("..", StringComparison.Ordinal))
                    throw new InvalidOperationException("Workspace directory is a symlink that escapes the base directory.");
            }
        }

        return fullPath;
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

    private static string NormalizeMentionToken(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        var sb = new StringBuilder(value.Length);
        foreach (var ch in value.Trim())
        {
            if (char.IsLetterOrDigit(ch) || ch == '.' || ch == '_' || ch == '-')
                sb.Append(ch);
        }
        return sb.ToString();
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
        // Capture the token struct NOW, before Task.Run queues the work.
        // If Dispose() runs before the thread-pool picks up the lambda, accessing
        // _discoveryCts.Token on a disposed CTS throws ObjectDisposedException.
        // A captured CancellationToken struct remains valid (IsCancellationRequested=true)
        // even after the parent CTS is cancelled and disposed.
        var token = _discoveryCts.Token;
        _broadcastTask = Task.Run(() => BroadcastPresenceLoopAsync(token));
        _listenTask = Task.Run(() => ListenForWorkersLoopAsync(token));
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
                    // Prefer Tailscale IP in the broadcast so peers that receive it can reach us
                    // via Tailscale (works across networks). Fall back to primary LAN IP.
                    string? advertiseIp = (_tailscale?.IsRunning == true)
                        ? (_tailscale.TailscaleIp ?? GetPrimaryLocalIpAddress())
                        : GetPrimaryLocalIpAddress();
                    if (!string.IsNullOrEmpty(advertiseIp))
                    {
                        var announcement = new FiestaDiscoveryAnnouncement
                        {
                            InstanceId = _instanceId,
                            Hostname = Environment.MachineName,
                            BridgeUrl = $"http://{advertiseIp}:{_bridgeServer.BridgePort}",
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
                if (result.Buffer.Length > 4096) continue; // reject oversized discovery packets
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
            string? best = null;
            int bestScore = -1;

            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up) continue;
                if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                if (ni.NetworkInterfaceType == NetworkInterfaceType.Tunnel) continue;
                if (IsVirtualAdapterName(ni.Name)) continue;

                var unicast = ni.GetIPProperties().UnicastAddresses
                    .FirstOrDefault(a => a.Address.AddressFamily == AddressFamily.InterNetwork);
                if (unicast == null) continue;

                var addr = unicast.Address.ToString();
                if (IsVirtualAdapterIp(addr)) continue;

                int score = ScoreNetworkInterface(ni.NetworkInterfaceType, addr);
                if (score > bestScore)
                {
                    bestScore = score;
                    best = addr;
                }
            }

            return best;
        }
        catch
        {
            // Ignore and return null.
        }
        return null;
    }

    private static bool IsVirtualAdapterName(string name) =>
        name.StartsWith("vEthernet", StringComparison.OrdinalIgnoreCase) ||   // Hyper-V
        name.StartsWith("br-", StringComparison.OrdinalIgnoreCase) ||          // Docker bridge
        name.StartsWith("virbr", StringComparison.OrdinalIgnoreCase) ||        // libvirt
        name.Contains("docker", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("WSL", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("VMware", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("VirtualBox", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("ZeroTier", StringComparison.OrdinalIgnoreCase);

    private static bool IsVirtualAdapterIp(string ip)
    {
        // Filter known virtual/container subnets that Docker and VM managers use by default.
        // 172.17–172.24 covers Docker's default bridge (172.17), Docker custom networks
        // (typically 172.18–172.24), and common VMware/VirtualBox host-only subnets.
        // 172.25–172.31 are also in RFC-1918 /12 but are less commonly assigned by tooling;
        // we leave them through so legitimate corporate LANs in that range still work.
        // The name-based filter (IsVirtualAdapterName) is the primary defense for adapters
        // with names like "br-*", "docker*", "vEthernet", etc.
        if (ip.StartsWith("172.", StringComparison.Ordinal))
        {
            var parts = ip.Split('.');
            if (parts.Length >= 2 && int.TryParse(parts[1], out var oct) && oct >= 17 && oct <= 24)
                return true;
        }
        return false;
    }

    private static bool IsRfc1918_172(string ip)
    {
        var parts = ip.Split('.');
        return parts.Length >= 2 && int.TryParse(parts[1], out var oct) && oct >= 16 && oct <= 31;
    }

    private static int ScoreNetworkInterface(NetworkInterfaceType type, string ip)
    {
        // Prefer RFC-1918 private ranges (real LAN) vs others
        bool isPrivateLan = ip.StartsWith("192.168.", StringComparison.Ordinal)
                         || ip.StartsWith("10.", StringComparison.Ordinal)
                         || (ip.StartsWith("172.", StringComparison.Ordinal) && IsRfc1918_172(ip));

        return type switch
        {
            NetworkInterfaceType.Ethernet => isPrivateLan ? 100 : 60,
            NetworkInterfaceType.Wireless80211 => isPrivateLan ? 90 : 50,
            NetworkInterfaceType.GigabitEthernet => isPrivateLan ? 100 : 60,
            NetworkInterfaceType.FastEthernetT => isPrivateLan ? 100 : 60,
            _ => isPrivateLan ? 20 : 5,
        };
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
