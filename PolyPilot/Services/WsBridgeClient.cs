using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using PolyPilot.Models;

namespace PolyPilot.Services;

/// <summary>
/// Client-side WebSocket receiver for the remote viewer protocol.
/// Connects to WsBridgeServer, receives state updates, and exposes them
/// via events that mirror CopilotService's API for UI binding.
/// </summary>
public class WsBridgeClient : IWsBridgeClient, IDisposable
{
    private ClientWebSocket? _ws;
    private CancellationTokenSource? _cts;
    private Task? _receiveTask;
    private string? _remoteWsUrl;
    private string? _authToken;
    private readonly SemaphoreSlim _sendLock = new(1, 1);

    public bool IsConnected => _ws?.State == WebSocketState.Open;
    public bool HasReceivedSessionsList { get; private set; }

    // --- State mirroring CopilotService ---
    public List<SessionSummary> Sessions { get; private set; } = new();
    public string? ActiveSessionName { get; private set; }
    public ConcurrentDictionary<string, List<ChatMessage>> SessionHistories { get; } = new();
    public ConcurrentDictionary<string, bool> SessionHistoryHasMore { get; } = new();
    public List<PersistedSessionSummary> PersistedSessions { get; private set; } = new();
    public string? GitHubAvatarUrl { get; private set; }
    public string? GitHubLogin { get; private set; }

    // --- Events matching CopilotService signatures ---
    public event Action? OnStateChanged;
    public event Action<string, string>? OnContentReceived;
    public event Action<string, string, string, string?>? OnToolStarted;
    public event Action<string, string, string, bool>? OnToolCompleted;
    public event Action<string, string, string>? OnReasoningReceived;
    public event Action<string, string>? OnReasoningComplete;
    public event Action<string, string, string?, string?>? OnImageReceived;
    public event Action<string, string>? OnIntentChanged;
    public event Action<string, SessionUsageInfo>? OnUsageInfoChanged;
    public event Action<string>? OnTurnStart;
    public event Action<string>? OnTurnEnd;
    public event Action<string, string>? OnSessionComplete;
    public event Action<string, string>? OnError;
    public event Action<OrganizationState>? OnOrganizationStateReceived;
    public event Action<AttentionNeededPayload>? OnAttentionNeeded;
    public event Action<ReposListPayload>? OnReposListReceived;

    /// <summary>
    /// Connect to the remote WsBridgeServer.
    /// </summary>
    public async Task ConnectAsync(string wsUrl, string? authToken = null, CancellationToken ct = default)
    {
        Stop();

        _remoteWsUrl = wsUrl;
        _authToken = authToken;
        _cts = new CancellationTokenSource();

        _ws = new ClientWebSocket();
        if (!string.IsNullOrEmpty(authToken))
            _ws.Options.SetRequestHeader("X-Tunnel-Authorization", $"tunnel {authToken}");

        var uri = new Uri(wsUrl);
        Console.WriteLine($"[WsBridgeClient] Connecting to {wsUrl}...");

        // Use Task.WhenAny as hard timeout — CancellationToken may not be honored on all platforms
        Task connectTask;
        HttpMessageInvoker? invoker = null;

        // DevTunnels uses HTTP/2 via ALPN, which breaks WebSocket upgrade.
        // Use SocketsHttpHandler to force HTTP/1.1 ALPN negotiation.
        // On Android, .NET DNS resolution may fail, so resolve via shell ping fallback.
        try
        {
            // Pre-resolve DNS using the platform resolver
            System.Net.IPAddress[] addresses;
            try
            {
                addresses = await System.Net.Dns.GetHostAddressesAsync(uri.Host, ct);
            }
            catch (Exception dnsEx)
            {
                Console.WriteLine($"[WsBridgeClient] .NET DNS failed ({dnsEx.Message}), using hardcoded IP fallback");
                // Fallback: the DevTunnel IP changes but we can at least try the default connect
                throw;
            }
            Console.WriteLine($"[WsBridgeClient] DNS resolved {uri.Host} to {string.Join(", ", addresses.Select(a => a.ToString()))}");

            var handler = new SocketsHttpHandler
            {
                SslOptions = new System.Net.Security.SslClientAuthenticationOptions
                {
                    ApplicationProtocols = [System.Net.Security.SslApplicationProtocol.Http11],
                    TargetHost = uri.Host
                },
                ConnectCallback = async (context, cancellationToken) =>
                {
                    var socket = new System.Net.Sockets.Socket(System.Net.Sockets.SocketType.Stream, System.Net.Sockets.ProtocolType.Tcp);
                    await socket.ConnectAsync(addresses, context.DnsEndPoint.Port, cancellationToken);
                    return new System.Net.Sockets.NetworkStream(socket, ownsSocket: true);
                }
            };
            invoker = new HttpMessageInvoker(handler);
            connectTask = _ws.ConnectAsync(uri, invoker, ct);
            Console.WriteLine("[WsBridgeClient] Using SocketsHttpHandler with HTTP/1.1 ALPN");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[WsBridgeClient] SocketsHttpHandler failed: {ex.Message}, falling back");
            invoker?.Dispose();
            invoker = null;
            connectTask = _ws.ConnectAsync(uri, ct);
        }

        var timeoutTask = Task.Delay(TimeSpan.FromSeconds(15), ct);
        var completed = await Task.WhenAny(connectTask, timeoutTask);

        if (completed == timeoutTask)
        {
            Console.WriteLine($"[WsBridgeClient] Connection timed out after 15s!");
            // Observe the abandoned connectTask's exception to prevent UnobservedTaskException
            _ = connectTask.ContinueWith(static t => { _ = t.Exception; }, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
            invoker?.Dispose();
            _ws.Dispose();
            _ws = new ClientWebSocket();
            throw new TimeoutException($"Connection to {wsUrl} timed out after 15 seconds");
        }

        // Propagate any connection error
        try
        {
            await connectTask; // Will throw if failed
        }
        catch (Exception ex)
        {
            var inner = ex.InnerException;
            var details = inner != null ? $" -> {inner.GetType().Name}: {inner.Message}" : "";
            Console.WriteLine($"[WsBridgeClient] Connection failed: {ex.GetType().Name}: {ex.Message}{details}");
            invoker?.Dispose();
            throw;
        }
        Console.WriteLine($"[WsBridgeClient] Connected");
        invoker?.Dispose();

        _receiveTask = ReceiveLoopAsync(_cts.Token);
    }

    public void Stop()
    {
        var oldCts = _cts;
        _cts = null;
        oldCts?.Cancel();
        try { oldCts?.Dispose(); } catch { }
        HasReceivedSessionsList = false;
        if (_ws?.State == WebSocketState.Open)
        {
            try { _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None).Wait(1000); }
            catch { }
        }
        _ws?.Dispose();
        _ws = null;
        Console.WriteLine("[WsBridgeClient] Stopped");
    }

    // --- Send commands to server ---

    public async Task RequestSessionsAsync(CancellationToken ct = default) =>
        await SendAsync(new BridgeMessage { Type = BridgeMessageTypes.GetSessions }, ct);

    public async Task RequestHistoryAsync(string sessionName, int? limit = null, CancellationToken ct = default) =>
        await SendAsync(BridgeMessage.Create(BridgeMessageTypes.GetHistory,
            new GetHistoryPayload { SessionName = sessionName, Limit = limit }), ct);

    public async Task SendMessageAsync(string sessionName, string message, string? agentMode = null, CancellationToken ct = default) =>
        await SendAsync(BridgeMessage.Create(BridgeMessageTypes.SendMessage,
            new SendMessagePayload { SessionName = sessionName, Message = message, AgentMode = agentMode }), ct);

    public async Task CreateSessionAsync(string name, string? model = null, string? workingDirectory = null, CancellationToken ct = default) =>
        await SendAsync(BridgeMessage.Create(BridgeMessageTypes.CreateSession,
            new CreateSessionPayload { Name = name, Model = model, WorkingDirectory = workingDirectory }), ct);

    public async Task SwitchSessionAsync(string sessionName, CancellationToken ct = default) =>
        await SendAsync(BridgeMessage.Create(BridgeMessageTypes.SwitchSession,
            new SwitchSessionPayload { SessionName = sessionName }), ct);

    public async Task QueueMessageAsync(string sessionName, string message, string? agentMode = null, CancellationToken ct = default) =>
        await SendAsync(BridgeMessage.Create(BridgeMessageTypes.QueueMessage,
            new QueueMessagePayload { SessionName = sessionName, Message = message, AgentMode = agentMode }), ct);

    public async Task ResumeSessionAsync(string sessionId, string? displayName = null, CancellationToken ct = default) =>
        await SendAsync(BridgeMessage.Create(BridgeMessageTypes.ResumeSession,
            new ResumeSessionPayload { SessionId = sessionId, DisplayName = displayName }), ct);

    public async Task CloseSessionAsync(string sessionName, CancellationToken ct = default) =>
        await SendAsync(BridgeMessage.Create(BridgeMessageTypes.CloseSession,
            new SessionNamePayload { SessionName = sessionName }), ct);

    public async Task AbortSessionAsync(string sessionName, CancellationToken ct = default) =>
        await SendAsync(BridgeMessage.Create(BridgeMessageTypes.AbortSession,
            new SessionNamePayload { SessionName = sessionName }), ct);

    public async Task ChangeModelAsync(string sessionName, string newModel, CancellationToken ct = default) =>
        await SendAsync(BridgeMessage.Create(BridgeMessageTypes.ChangeModel,
            new ChangeModelPayload { SessionName = sessionName, NewModel = newModel }), ct);

    public async Task RenameSessionAsync(string oldName, string newName, CancellationToken ct = default) =>
        await SendAsync(BridgeMessage.Create(BridgeMessageTypes.RenameSession,
            new RenameSessionPayload { OldName = oldName, NewName = newName }), ct);

    public async Task SendOrganizationCommandAsync(OrganizationCommandPayload cmd, CancellationToken ct = default) =>
        await SendAsync(BridgeMessage.Create(BridgeMessageTypes.OrganizationCommand, cmd), ct);

    public async Task SendMultiAgentBroadcastAsync(string groupId, string message, CancellationToken ct = default) =>
        await SendAsync(BridgeMessage.Create(BridgeMessageTypes.MultiAgentBroadcast,
            new MultiAgentBroadcastPayload { GroupId = groupId, Message = message }), ct);

    public async Task CreateMultiAgentGroupAsync(string name, string mode = "Broadcast", string? orchestratorPrompt = null, List<string>? sessionNames = null, CancellationToken ct = default) =>
        await SendAsync(BridgeMessage.Create(BridgeMessageTypes.MultiAgentCreateGroup,
            new MultiAgentCreateGroupPayload { Name = name, Mode = mode, OrchestratorPrompt = orchestratorPrompt, SessionNames = sessionNames }), ct);

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, TaskCompletionSource<DirectoriesListPayload>> _dirListRequests = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, TaskCompletionSource<RepoAddedPayload>> _addRepoRequests = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, Action<string>> _repoProgressCallbacks = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, TaskCompletionSource<FetchImageResponsePayload>> _fetchImageRequests = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, TaskCompletionSource<WorktreeCreatedPayload>> _pendingWorktreeRequests = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, TaskCompletionSource<bool>> _pendingWorktreeRemovals = new();

    public async Task<DirectoriesListPayload> ListDirectoriesAsync(string? path = null, CancellationToken ct = default)
    {
        var requestId = Guid.NewGuid().ToString("N");
        var tcs = new TaskCompletionSource<DirectoriesListPayload>();
        _dirListRequests[requestId] = tcs;
        try
        {
            await SendAsync(BridgeMessage.Create(BridgeMessageTypes.ListDirectories,
                new ListDirectoriesPayload { Path = path, RequestId = requestId }), ct);
            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
            linked.Token.Register(() => tcs.TrySetCanceled());
            return await tcs.Task;
        }
        finally
        {
            _dirListRequests.TryRemove(requestId, out _);
        }
    }

    public async Task<RepoAddedPayload> AddRepoAsync(string url, Action<string>? onProgress = null, CancellationToken ct = default)
    {
        var requestId = Guid.NewGuid().ToString("N");
        var tcs = new TaskCompletionSource<RepoAddedPayload>();
        _addRepoRequests[requestId] = tcs;
        if (onProgress != null)
            _repoProgressCallbacks[requestId] = onProgress;
        try
        {
            await SendAsync(BridgeMessage.Create(BridgeMessageTypes.AddRepo,
                new AddRepoPayload { Url = url, RequestId = requestId }), ct);
            // Cloning can take a while — 5 minute timeout
            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
            linked.Token.Register(() => tcs.TrySetCanceled());
            return await tcs.Task;
        }
        finally
        {
            _addRepoRequests.TryRemove(requestId, out _);
            _repoProgressCallbacks.TryRemove(requestId, out _);
        }
    }

    public async Task RemoveRepoAsync(string repoId, bool deleteFromDisk, string? groupId = null, CancellationToken ct = default) =>
        await SendAsync(BridgeMessage.Create(BridgeMessageTypes.RemoveRepo,
            new RemoveRepoPayload { RepoId = repoId, DeleteFromDisk = deleteFromDisk, GroupId = groupId }), ct);

    public async Task RequestReposAsync(CancellationToken ct = default) =>
        await SendAsync(BridgeMessage.Create(BridgeMessageTypes.ListRepos, new ListReposPayload()), ct);

    public async Task<WorktreeCreatedPayload> CreateWorktreeAsync(string repoId, string? branchName, int? prNumber, CancellationToken ct = default)
    {
        var requestId = Guid.NewGuid().ToString("N");
        var tcs = new TaskCompletionSource<WorktreeCreatedPayload>();
        _pendingWorktreeRequests[requestId] = tcs;
        try
        {
            await SendAsync(BridgeMessage.Create(BridgeMessageTypes.CreateWorktree,
                new CreateWorktreePayload { RequestId = requestId, RepoId = repoId, BranchName = branchName, PrNumber = prNumber }), ct);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromMinutes(2));
            cts.Token.Register(() => tcs.TrySetCanceled());
            return await tcs.Task;
        }
        finally { _pendingWorktreeRequests.TryRemove(requestId, out _); }
    }

    public async Task RemoveWorktreeAsync(string worktreeId, CancellationToken ct = default)
    {
        var requestId = Guid.NewGuid().ToString("N");
        var tcs = new TaskCompletionSource<bool>();
        _pendingWorktreeRemovals[requestId] = tcs;
        try
        {
            await SendAsync(BridgeMessage.Create(BridgeMessageTypes.RemoveWorktree,
                new RemoveWorktreePayload { RequestId = requestId, WorktreeId = worktreeId }), ct);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
            linked.Token.Register(() => tcs.TrySetCanceled());
            await tcs.Task;
        }
        finally
        {
            _pendingWorktreeRemovals.TryRemove(requestId, out _);
        }
    }

    public async Task<FetchImageResponsePayload> FetchImageAsync(string path, CancellationToken ct = default)
    {
        var requestId = Guid.NewGuid().ToString("N");
        var tcs = new TaskCompletionSource<FetchImageResponsePayload>();
        _fetchImageRequests[requestId] = tcs;
        try
        {
            await SendAsync(BridgeMessage.Create(BridgeMessageTypes.FetchImage,
                new FetchImagePayload { Path = path, RequestId = requestId }), ct);
            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
            linked.Token.Register(() => tcs.TrySetCanceled());
            return await tcs.Task;
        }
        finally
        {
            _fetchImageRequests.TryRemove(requestId, out _);
        }
    }

    // --- Receive loop ---

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        var buffer = new byte[65536]; // Large buffer for history payloads
        var messageBuffer = new StringBuilder();

        while (_ws?.State == WebSocketState.Open && !ct.IsCancellationRequested)
        {
            try
            {
                var result = await _ws.ReceiveAsync(buffer, ct);
                if (result.MessageType == WebSocketMessageType.Close) break;

                messageBuffer.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));

                if (result.EndOfMessage)
                {
                    var json = messageBuffer.ToString();
                    messageBuffer.Clear();
                    HandleServerMessage(json);
                }
            }
            catch (WebSocketException) { break; }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                Console.WriteLine($"[WsBridgeClient] Receive error: {ex.Message}");
                break;
            }
        }

        Console.WriteLine("[WsBridgeClient] Receive loop ended");
        // Cancel any pending directory list requests so callers don't hang
        foreach (var kvp in _dirListRequests)
        {
            if (_dirListRequests.TryRemove(kvp.Key, out var tcs))
                tcs.TrySetCanceled();
        }
        // Cancel any pending repo add requests
        foreach (var kvp in _addRepoRequests)
        {
            if (_addRepoRequests.TryRemove(kvp.Key, out var tcs))
                tcs.TrySetCanceled();
        }
        // Cancel any pending worktree create/remove requests
        foreach (var kvp in _pendingWorktreeRequests)
        {
            if (_pendingWorktreeRequests.TryRemove(kvp.Key, out var tcs))
                tcs.TrySetCanceled();
        }
        foreach (var kvp in _pendingWorktreeRemovals)
        {
            if (_pendingWorktreeRemovals.TryRemove(kvp.Key, out var tcs))
                tcs.TrySetCanceled();
        }
        _repoProgressCallbacks.Clear();
        OnStateChanged?.Invoke();

        // Auto-reconnect if not intentionally stopped
        if (!ct.IsCancellationRequested && !string.IsNullOrEmpty(_remoteWsUrl))
        {
            _ = Task.Run(async () => { try { await ReconnectAsync(); } catch { } });
        }
    }

    private async Task ReconnectAsync()
    {
        var maxDelay = 30_000;
        var delay = 2_000;

        // Capture the CTS at the start to prevent ConnectAsync from replacing it mid-loop
        var cts = _cts;
        if (cts == null || cts.IsCancellationRequested) return;

        while (!cts.IsCancellationRequested)
        {
            Console.WriteLine($"[WsBridgeClient] Reconnecting in {delay / 1000}s...");
            try { await Task.Delay(delay, cts.Token); }
            catch (OperationCanceledException) { return; }

            // If a new ConnectAsync replaced _cts, this reconnect loop is stale
            if (_cts != cts) return;

            try
            {
                _ws?.Dispose();
                _ws = new ClientWebSocket();
                if (!string.IsNullOrEmpty(_authToken))
                    _ws.Options.SetRequestHeader("X-Tunnel-Authorization", $"tunnel {_authToken}");

                var uri = new Uri(_remoteWsUrl!);

                HttpMessageInvoker? invoker = null;
                try
                {
                    var addresses = await System.Net.Dns.GetHostAddressesAsync(uri.Host);
                    var handler = new SocketsHttpHandler
                    {
                        SslOptions = new System.Net.Security.SslClientAuthenticationOptions
                        {
                            ApplicationProtocols = [System.Net.Security.SslApplicationProtocol.Http11],
                            TargetHost = uri.Host
                        },
                        ConnectCallback = async (context, cancellationToken) =>
                        {
                            var socket = new System.Net.Sockets.Socket(System.Net.Sockets.SocketType.Stream, System.Net.Sockets.ProtocolType.Tcp);
                            await socket.ConnectAsync(addresses, context.DnsEndPoint.Port, cancellationToken);
                            return new System.Net.Sockets.NetworkStream(socket, ownsSocket: true);
                        }
                    };
                    invoker = new HttpMessageInvoker(handler);
                    await _ws.ConnectAsync(uri, invoker, cts.Token);
                    invoker.Dispose();
                }
                catch
                {
                    invoker?.Dispose();
                    invoker = null;
                    _ws?.Dispose();
                    _ws = new ClientWebSocket();
                    if (!string.IsNullOrEmpty(_authToken))
                        _ws.Options.SetRequestHeader("X-Tunnel-Authorization", $"tunnel {_authToken}");
                    await _ws.ConnectAsync(uri, cts.Token);
                }

                Console.WriteLine("[WsBridgeClient] Reconnected");
                OnStateChanged?.Invoke();

                // Request fresh state
                await RequestSessionsAsync(cts.Token);

                // Resume receive loop
                _receiveTask = ReceiveLoopAsync(cts.Token);
                return;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[WsBridgeClient] Reconnect failed: {ex.Message}");
                delay = Math.Min(delay * 2, maxDelay);
            }
        }
    }

    private void HandleServerMessage(string json)
    {
        var msg = BridgeMessage.Deserialize(json);
        if (msg == null)
        {
            Console.WriteLine($"[WsBridgeClient] Failed to deserialize message: {json[..Math.Min(200, json.Length)]}");
            return;
        }

        Console.WriteLine($"[WsBridgeClient] Received: {msg.Type}");

        switch (msg.Type)
        {
            case BridgeMessageTypes.SessionsList:
                var sessions = msg.GetPayload<SessionsListPayload>();
                if (sessions != null)
                {
                    Sessions = sessions.Sessions;
                    ActiveSessionName = sessions.ActiveSession;
                    GitHubAvatarUrl = sessions.GitHubAvatarUrl;
                    GitHubLogin = sessions.GitHubLogin;
                    HasReceivedSessionsList = true;
                    Console.WriteLine($"[WsBridgeClient] Got {Sessions.Count} sessions, active={ActiveSessionName}");
                    OnStateChanged?.Invoke();
                }
                break;

            case BridgeMessageTypes.SessionHistory:
                var history = msg.GetPayload<SessionHistoryPayload>();
                if (history != null)
                {
                    SessionHistories[history.SessionName] = history.Messages;
                    SessionHistoryHasMore[history.SessionName] = history.HasMore;
                    Console.WriteLine($"[WsBridgeClient] Got history for '{history.SessionName}': {history.Messages.Count} messages (total={history.TotalCount}, hasMore={history.HasMore})");
                    OnStateChanged?.Invoke();
                }
                break;

            case BridgeMessageTypes.ContentDelta:
                var content = msg.GetPayload<ContentDeltaPayload>();
                if (content != null)
                    OnContentReceived?.Invoke(content.SessionName, content.Content);
                break;

            case BridgeMessageTypes.PersistedSessionsList:
                var persisted = msg.GetPayload<PersistedSessionsPayload>();
                if (persisted != null)
                {
                    PersistedSessions = persisted.Sessions;
                    Console.WriteLine($"[WsBridgeClient] Got {PersistedSessions.Count} persisted sessions");
                    OnStateChanged?.Invoke();
                }
                break;

            case BridgeMessageTypes.ToolStarted:
                var toolStart = msg.GetPayload<ToolStartedPayload>();
                if (toolStart != null)
                    OnToolStarted?.Invoke(toolStart.SessionName, toolStart.ToolName, toolStart.CallId, toolStart.ToolInput);
                break;

            case BridgeMessageTypes.ToolCompleted:
                var toolDone = msg.GetPayload<ToolCompletedPayload>();
                if (toolDone != null)
                {
                    OnToolCompleted?.Invoke(toolDone.SessionName, toolDone.CallId, toolDone.Result, toolDone.Success);
                    // If image data is present, fire image event so the handler can convert to Image message
                    if (!string.IsNullOrEmpty(toolDone.ImageData))
                    {
                        var dataUri = $"data:{toolDone.ImageMimeType ?? "image/png"};base64,{toolDone.ImageData}";
                        OnImageReceived?.Invoke(toolDone.SessionName, toolDone.CallId, dataUri, toolDone.Caption);
                    }
                }
                break;

            case BridgeMessageTypes.ReasoningDelta:
                var reasoning = msg.GetPayload<ReasoningDeltaPayload>();
                if (reasoning != null)
                    OnReasoningReceived?.Invoke(reasoning.SessionName, reasoning.ReasoningId, reasoning.Content);
                break;

            case BridgeMessageTypes.ReasoningComplete:
                var reasonDone = msg.GetPayload<ReasoningCompletePayload>();
                if (reasonDone != null)
                    OnReasoningComplete?.Invoke(reasonDone.SessionName, reasonDone.ReasoningId);
                else
                {
                    // Back-compat with older servers that only sent SessionNamePayload.
                    var legacyReasonDone = msg.GetPayload<SessionNamePayload>();
                    if (legacyReasonDone != null)
                        OnReasoningComplete?.Invoke(legacyReasonDone.SessionName, "");
                }
                break;

            case BridgeMessageTypes.IntentChanged:
                var intent = msg.GetPayload<IntentChangedPayload>();
                if (intent != null)
                    OnIntentChanged?.Invoke(intent.SessionName, intent.Intent);
                break;

            case BridgeMessageTypes.UsageInfo:
                var usage = msg.GetPayload<UsageInfoPayload>();
                if (usage != null)
                    OnUsageInfoChanged?.Invoke(usage.SessionName, new SessionUsageInfo(
                        usage.Model, usage.CurrentTokens, usage.TokenLimit,
                        usage.InputTokens, usage.OutputTokens));
                break;

            case BridgeMessageTypes.TurnStart:
                var turnStart = msg.GetPayload<SessionNamePayload>();
                if (turnStart != null)
                    OnTurnStart?.Invoke(turnStart.SessionName);
                break;

            case BridgeMessageTypes.TurnEnd:
                var turnEnd = msg.GetPayload<SessionNamePayload>();
                if (turnEnd != null)
                    OnTurnEnd?.Invoke(turnEnd.SessionName);
                break;

            case BridgeMessageTypes.SessionComplete:
                var complete = msg.GetPayload<SessionCompletePayload>();
                if (complete != null)
                    OnSessionComplete?.Invoke(complete.SessionName, complete.Summary);
                break;

            case BridgeMessageTypes.ErrorEvent:
                var error = msg.GetPayload<ErrorPayload>();
                if (error != null)
                    OnError?.Invoke(error.SessionName, error.Error);
                break;

            case BridgeMessageTypes.OrganizationState:
                var orgState = msg.GetPayload<OrganizationState>();
                if (orgState != null)
                    OnOrganizationStateReceived?.Invoke(orgState);
                break;

            case BridgeMessageTypes.DirectoriesList:
                var dirList = msg.GetPayload<DirectoriesListPayload>();
                if (dirList != null)
                {
                    var reqId = dirList.RequestId;
                    if (reqId != null && _dirListRequests.TryRemove(reqId, out var tcs))
                        tcs.TrySetResult(dirList);
                    else if (reqId == null)
                    {
                        // Fallback: complete the first pending request (legacy server without RequestId)
                        foreach (var kvp in _dirListRequests)
                        {
                            if (_dirListRequests.TryRemove(kvp.Key, out var fallbackTcs))
                            {
                                fallbackTcs.TrySetResult(dirList);
                                break;
                            }
                        }
                    }
                }
                break;

            case BridgeMessageTypes.AttentionNeeded:
                var attention = msg.GetPayload<AttentionNeededPayload>();
                if (attention != null)
                {
                    Console.WriteLine($"[WsBridgeClient] Attention needed: {attention.SessionName} - {attention.Reason}");
                    OnAttentionNeeded?.Invoke(attention);
                }
                break;

            case BridgeMessageTypes.ReposList:
                var reposListPayload = msg.GetPayload<ReposListPayload>();
                if (reposListPayload != null)
                    OnReposListReceived?.Invoke(reposListPayload);
                break;

            case BridgeMessageTypes.RepoAdded:
                var repoAddedPayload = msg.GetPayload<RepoAddedPayload>();
                if (repoAddedPayload != null && _addRepoRequests.TryRemove(repoAddedPayload.RequestId, out var addTcs))
                    addTcs.TrySetResult(repoAddedPayload);
                break;

            case BridgeMessageTypes.RepoProgress:
                var repoProgressPayload = msg.GetPayload<RepoProgressPayload>();
                if (repoProgressPayload != null && _repoProgressCallbacks.TryGetValue(repoProgressPayload.RequestId, out var progressCb))
                    progressCb(repoProgressPayload.Message);
                break;

            case BridgeMessageTypes.RepoError:
                var repoErrorPayload = msg.GetPayload<RepoErrorPayload>();
                if (repoErrorPayload != null)
                {
                    if (_addRepoRequests.TryRemove(repoErrorPayload.RequestId, out var errTcs))
                        errTcs.TrySetException(new InvalidOperationException(repoErrorPayload.Error));
                }
                break;

            case BridgeMessageTypes.WorktreeCreated:
                var wtCreated = msg.GetPayload<WorktreeCreatedPayload>();
                if (wtCreated != null && _pendingWorktreeRequests.TryRemove(wtCreated.RequestId, out var wtTcs))
                    wtTcs.TrySetResult(wtCreated);
                break;

            case BridgeMessageTypes.WorktreeRemoved:
                var wtRemoved = msg.GetPayload<RemoveWorktreePayload>();
                if (wtRemoved != null && _pendingWorktreeRemovals.TryRemove(wtRemoved.RequestId, out var rmTcs))
                    rmTcs.TrySetResult(true);
                break;

            case BridgeMessageTypes.WorktreeError:
                var wtError = msg.GetPayload<RepoErrorPayload>();
                if (wtError != null)
                {
                    // Route to create or remove pending request
                    if (_pendingWorktreeRequests.TryRemove(wtError.RequestId, out var wtErrTcs))
                        wtErrTcs.TrySetException(new InvalidOperationException(wtError.Error));
                    else if (_pendingWorktreeRemovals.TryRemove(wtError.RequestId, out var rmErrTcs))
                        rmErrTcs.TrySetException(new InvalidOperationException(wtError.Error));
                }
                break;

            case BridgeMessageTypes.FetchImageResponse:
                var imgRespPayload = msg.GetPayload<FetchImageResponsePayload>();
                if (imgRespPayload != null && _fetchImageRequests.TryRemove(imgRespPayload.RequestId, out var imgTcs))
                    imgTcs.TrySetResult(imgRespPayload);
                break;
        }
    }

    private async Task SendAsync(BridgeMessage msg, CancellationToken ct)
    {
        if (_ws?.State != WebSocketState.Open) return;
        var bytes = Encoding.UTF8.GetBytes(msg.Serialize());
        try
        {
            await _sendLock.WaitAsync(ct);
        }
        catch (ObjectDisposedException) { return; }
        try
        {
            if (_ws?.State == WebSocketState.Open)
                await _ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, ct);
        }
        finally
        {
            try { _sendLock.Release(); } catch (ObjectDisposedException) { }
        }
    }

    public void Dispose()
    {
        Stop();
        _cts?.Dispose();
        _sendLock.Dispose();
        GC.SuppressFinalize(this);
    }
}
