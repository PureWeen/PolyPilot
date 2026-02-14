using System.Collections.Concurrent;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using PolyPilot.Models;

namespace PolyPilot.Services;

/// <summary>
/// WebSocket server that exposes CopilotService state to remote viewer clients.
/// Clients receive live session/chat updates and can send commands back.
/// </summary>
public class WsBridgeServer : IDisposable
{
    private HttpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _acceptTask;
    private int _bridgePort;
    private CopilotService? _copilot;
    private readonly ConcurrentDictionary<string, WebSocket> _clients = new();
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _clientSendLocks = new();
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, byte>> _clientFiestaAuthorizations = new();
    private readonly ConcurrentDictionary<string, ClientAuthScope> _clientAuthScopes = new();
    private readonly ConcurrentDictionary<string, string> _pendingFiestaClientIds = new();
    private readonly ConcurrentDictionary<string, string> _pendingFiestaIds = new();
    private FiestaCoordinatorService? _fiestaCoordinator;

    private enum ClientAuthScope
    {
        None,
        FiestaOnly,
        Full
    }

    public int BridgePort => _bridgePort;
    public bool IsRunning => _listener?.IsListening == true;

    /// <summary>
    /// Access token that clients must provide via X-Tunnel-Authorization header or query param.
    /// </summary>
    public string? AccessToken { get; set; }

    /// <summary>
    /// Server password for direct connection (LAN/Tailscale/VPN) auth.
    /// </summary>
    public string? ServerPassword { get; set; }

    public event Action? OnStateChanged;

    /// <summary>
    /// Start the bridge server. Now only needs the port — connects to CopilotService directly.
    /// The targetPort parameter is kept for API compat but ignored.
    /// </summary>
    public void Start(int bridgePort, int targetPort)
    {
        if (IsRunning) return;

        _bridgePort = bridgePort;
        _cts = new CancellationTokenSource();

        _listener = new HttpListener();
        _listener.Prefixes.Add($"http://+:{bridgePort}/");

        try
        {
            _listener.Start();
            Console.WriteLine($"[WsBridge] Listening on port {bridgePort} (state-sync mode)");
            _acceptTask = AcceptLoopAsync(_cts.Token);
            OnStateChanged?.Invoke();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[WsBridge] Failed to start on wildcard: {ex.Message}");
            try
            {
                _listener = new HttpListener();
                _listener.Prefixes.Add($"http://localhost:{bridgePort}/");
                _listener.Start();
                Console.WriteLine($"[WsBridge] Listening on localhost:{bridgePort} (state-sync mode)");
                _acceptTask = AcceptLoopAsync(_cts.Token);
                OnStateChanged?.Invoke();
            }
            catch (Exception ex2)
            {
                Console.WriteLine($"[WsBridge] Failed to start on localhost: {ex2.Message}");
            }
        }
    }

    /// <summary>
    /// Set the CopilotService instance and hook its events for broadcasting to clients.
    /// </summary>
    public void SetCopilotService(CopilotService copilot)
    {
        if (_copilot != null) return;
        _copilot = copilot;

        _copilot.OnStateChanged += () => BroadcastSessionsList();
        _copilot.OnContentReceived += (session, content) =>
            Broadcast(BridgeMessage.Create(BridgeMessageTypes.ContentDelta,
                new ContentDeltaPayload { SessionName = session, Content = content }));
        _copilot.OnToolStarted += (session, tool, callId, input) =>
            Broadcast(BridgeMessage.Create(BridgeMessageTypes.ToolStarted,
                new ToolStartedPayload { SessionName = session, ToolName = tool, CallId = callId }));
        _copilot.OnToolCompleted += (session, callId, result, success) =>
            Broadcast(BridgeMessage.Create(BridgeMessageTypes.ToolCompleted,
                new ToolCompletedPayload { SessionName = session, CallId = callId, Result = result, Success = success }));
        _copilot.OnReasoningReceived += (session, reasoningId, content) =>
            Broadcast(BridgeMessage.Create(BridgeMessageTypes.ReasoningDelta,
                new ReasoningDeltaPayload { SessionName = session, ReasoningId = reasoningId, Content = content }));
        _copilot.OnReasoningComplete += (session, reasoningId) =>
            Broadcast(BridgeMessage.Create(BridgeMessageTypes.ReasoningComplete,
                new ReasoningCompletePayload { SessionName = session, ReasoningId = reasoningId }));
        _copilot.OnIntentChanged += (session, intent) =>
            Broadcast(BridgeMessage.Create(BridgeMessageTypes.IntentChanged,
                new IntentChangedPayload { SessionName = session, Intent = intent }));
        _copilot.OnUsageInfoChanged += (session, usage) =>
            Broadcast(BridgeMessage.Create(BridgeMessageTypes.UsageInfo,
                new UsageInfoPayload
                {
                    SessionName = session, Model = usage.Model,
                    CurrentTokens = usage.CurrentTokens, TokenLimit = usage.TokenLimit,
                    InputTokens = usage.InputTokens, OutputTokens = usage.OutputTokens
                }));
        _copilot.OnTurnStart += (session) =>
            Broadcast(BridgeMessage.Create(BridgeMessageTypes.TurnStart,
                new SessionNamePayload { SessionName = session }));
        _copilot.OnTurnEnd += (session) =>
            Broadcast(BridgeMessage.Create(BridgeMessageTypes.TurnEnd,
                new SessionNamePayload { SessionName = session }));
        _copilot.OnSessionComplete += (session, summary) =>
            Broadcast(BridgeMessage.Create(BridgeMessageTypes.SessionComplete,
                new SessionCompletePayload { SessionName = session, Summary = summary }));
        _copilot.OnError += (session, error) =>
            Broadcast(BridgeMessage.Create(BridgeMessageTypes.ErrorEvent,
                new ErrorPayload { SessionName = session, Error = error }));
    }

    public void SetFiestaCoordinator(FiestaCoordinatorService coordinator)
    {
        if (_fiestaCoordinator == coordinator) return;
        if (_fiestaCoordinator != null)
            _fiestaCoordinator.OnJoinRequestResolved -= HandleJoinRequestResolved;
        _fiestaCoordinator = coordinator;
        _fiestaCoordinator.OnJoinRequestResolved += HandleJoinRequestResolved;
    }

    public void Stop()
    {
        _cts?.Cancel();
        // Close all client connections
        foreach (var kvp in _clients)
        {
            try { kvp.Value.CloseAsync(WebSocketCloseStatus.NormalClosure, "shutdown", CancellationToken.None).Wait(1000); }
            catch { }
        }
        _clients.Clear();
        foreach (var kvp in _clientSendLocks) kvp.Value.Dispose();
        _clientSendLocks.Clear();
        _clientFiestaAuthorizations.Clear();
        _clientAuthScopes.Clear();
        _pendingFiestaClientIds.Clear();
        _pendingFiestaIds.Clear();
        try { _listener?.Stop(); } catch { }
        _listener = null;
        Console.WriteLine("[WsBridge] Stopped");
        OnStateChanged?.Invoke();
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _listener?.IsListening == true)
        {
            try
            {
                var context = await _listener.GetContextAsync();

                if (context.Request.IsWebSocketRequest)
                {
                    if (!ValidateClientToken(context.Request))
                    {
                        context.Response.StatusCode = 401;
                        context.Response.Close();
                        Console.WriteLine("[WsBridge] Rejected unauthenticated WebSocket connection");
                        continue;
                    }
                    _ = Task.Run(() => HandleClientAsync(context, ct), ct);
                }
                else if (context.Request.Url?.AbsolutePath == "/token" && context.Request.HttpMethod == "GET")
                {
                    // Only serve token to loopback clients (localhost)
                    if (!IsLoopbackRequest(context.Request))
                    {
                        context.Response.StatusCode = 403;
                        context.Response.Close();
                        continue;
                    }
                    context.Response.StatusCode = 200;
                    context.Response.ContentType = "text/plain";
                    var tokenBytes = Encoding.UTF8.GetBytes(AccessToken ?? "");
                    await context.Response.OutputStream.WriteAsync(tokenBytes, ct);
                    context.Response.Close();
                }
                else
                {
                    context.Response.StatusCode = 200;
                    context.Response.ContentType = "text/plain";
                    var buffer = Encoding.UTF8.GetBytes("WsBridge OK");
                    await context.Response.OutputStream.WriteAsync(buffer, ct);
                    context.Response.Close();
                }
            }
            catch (ObjectDisposedException) { break; }
            catch (HttpListenerException) { break; }
            catch (Exception ex)
            {
                Console.WriteLine($"[WsBridge] Accept error: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Validate client token from X-Tunnel-Authorization header or query string.
    /// If no AccessToken is configured, all connections are allowed (local-only mode).
    /// Loopback connections are always allowed — they're either local or proxied
    /// through the DevTunnel (which validates tokens at the tunnel layer).
    /// </summary>
    private bool ValidateClientToken(HttpListenerRequest request)
    {
        return ResolveClientAuthScope(request) != ClientAuthScope.None;
    }

    private ClientAuthScope ResolveClientAuthScope(HttpListenerRequest request)
    {
        // If no tokens are configured at all, keep existing local-mode behavior
        if (string.IsNullOrEmpty(AccessToken) &&
            string.IsNullOrEmpty(ServerPassword) &&
            string.IsNullOrEmpty(_fiestaCoordinator?.CurrentJoinCode))
            return ClientAuthScope.Full;

        // Loopback connections are trusted — DevTunnel proxies appear as localhost
        if (IsLoopbackRequest(request))
            return ClientAuthScope.Full;

        var providedToken = ExtractProvidedToken(request);
        if (string.IsNullOrEmpty(providedToken))
            return ClientAuthScope.None;

        if (!string.IsNullOrEmpty(AccessToken) && string.Equals(providedToken, AccessToken, StringComparison.Ordinal))
            return ClientAuthScope.Full;
        if (!string.IsNullOrEmpty(ServerPassword) && string.Equals(providedToken, ServerPassword, StringComparison.Ordinal))
            return ClientAuthScope.Full;
        if (!string.IsNullOrEmpty(_fiestaCoordinator?.CurrentJoinCode) && string.Equals(providedToken, _fiestaCoordinator.CurrentJoinCode, StringComparison.Ordinal))
            return ClientAuthScope.FiestaOnly;

        return ClientAuthScope.None;
    }

    private static string? ExtractProvidedToken(HttpListenerRequest request)
    {
        string? providedToken = null;
        var authHeader = request.Headers["X-Tunnel-Authorization"];
        if (!string.IsNullOrEmpty(authHeader))
        {
            providedToken = authHeader.StartsWith("tunnel ", StringComparison.OrdinalIgnoreCase)
                ? authHeader["tunnel ".Length..].Trim()
                : authHeader.Trim();
        }

        providedToken ??= request.QueryString["token"];
        return providedToken;
    }

    private static bool IsLoopbackRequest(HttpListenerRequest request)
    {
        var remoteAddr = request.RemoteEndPoint?.Address;
        return remoteAddr != null && IPAddress.IsLoopback(remoteAddr);
    }

    private async Task HandleClientAsync(HttpListenerContext httpContext, CancellationToken ct)
    {
        WebSocket? ws = null;
        var clientId = Guid.NewGuid().ToString("N")[..8];
        var authScope = ResolveClientAuthScope(httpContext.Request);

        try
        {
            var wsContext = await httpContext.AcceptWebSocketAsync(null);
            ws = wsContext.WebSocket;
            _clients[clientId] = ws;
            _clientSendLocks[clientId] = new SemaphoreSlim(1, 1);
            _clientAuthScopes[clientId] = authScope;
            Console.WriteLine($"[WsBridge] Client {clientId} connected ({_clients.Count} total)");

            // Send initial state only to full-access clients.
            if (authScope == ClientAuthScope.Full)
            {
                await SendToClientAsync(clientId, ws,
                    BridgeMessage.Create(BridgeMessageTypes.SessionsList, BuildSessionsListPayload()), ct);
                await SendToClientAsync(clientId, ws,
                    BridgeMessage.Create(BridgeMessageTypes.OrganizationState, _copilot?.Organization ?? new OrganizationState()), ct);
                await SendPersistedToClient(clientId, ws, ct);

                // Send active session history
                if (_copilot != null)
                {
                    var active = _copilot.GetActiveSession();
                    if (active != null)
                        await SendSessionHistoryToClient(clientId, ws, active.Name, ct);
                }
            }

            // Read client commands (with fragmentation support)
            var buffer = new byte[65536];
            var messageBuffer = new StringBuilder();
            while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                var result = await ws.ReceiveAsync(buffer, ct);
                if (result.MessageType == WebSocketMessageType.Close) break;

                messageBuffer.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));

                if (result.EndOfMessage)
                {
                    var json = messageBuffer.ToString();
                    messageBuffer.Clear();
                    await HandleClientMessage(clientId, ws, json, ct);
                }
            }
        }
        catch (WebSocketException) { }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Console.WriteLine($"[WsBridge] Client {clientId} error: {ex.Message}");
        }
        finally
        {
            _clients.TryRemove(clientId, out _);
            if (_clientSendLocks.TryRemove(clientId, out var lk)) lk.Dispose();
            _clientFiestaAuthorizations.TryRemove(clientId, out _);
            _clientAuthScopes.TryRemove(clientId, out _);
            foreach (var pending in _pendingFiestaClientIds.Where(kvp => kvp.Value == clientId).ToList())
            {
                _pendingFiestaClientIds.TryRemove(pending.Key, out _);
                _pendingFiestaIds.TryRemove(pending.Key, out _);
            }
            if (ws?.State == WebSocketState.Open)
            {
                try { await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None); }
                catch { }
            }
            ws?.Dispose();
            Console.WriteLine($"[WsBridge] Client {clientId} disconnected ({_clients.Count} remaining)");
        }
    }

    private async Task HandleClientMessage(string clientId, WebSocket ws, string json, CancellationToken ct)
    {
        var msg = BridgeMessage.Deserialize(json);
        if (msg == null) return;

        if (_clientAuthScopes.TryGetValue(clientId, out var scope) &&
            scope == ClientAuthScope.FiestaOnly &&
            !IsFiestaMessageType(msg.Type))
        {
            await SendToClientAsync(clientId, ws,
                BridgeMessage.Create(BridgeMessageTypes.ErrorEvent, new ErrorPayload
                {
                    SessionName = "",
                    Error = "Fiesta join code clients can only use Fiesta commands."
                }), ct);
            return;
        }

        try
        {
            var copilot = _copilot;
            if (copilot == null && !IsFiestaMessageType(msg.Type))
                return;

            switch (msg.Type)
            {
                case BridgeMessageTypes.GetSessions:
                    await SendToClientAsync(clientId, ws,
                        BridgeMessage.Create(BridgeMessageTypes.SessionsList, BuildSessionsListPayload()), ct);
                    break;

                case BridgeMessageTypes.GetHistory:
                    var histReq = msg.GetPayload<GetHistoryPayload>();
                    if (histReq != null)
                        await SendSessionHistoryToClient(clientId, ws, histReq.SessionName, ct);
                    break;

                case BridgeMessageTypes.SendMessage:
                    var sendReq = msg.GetPayload<SendMessagePayload>();
                    if (sendReq != null && !string.IsNullOrWhiteSpace(sendReq.SessionName) && !string.IsNullOrWhiteSpace(sendReq.Message))
                    {
                        Console.WriteLine($"[WsBridge] Client sending message to '{sendReq.SessionName}'");
                        await copilot!.SendPromptAsync(sendReq.SessionName, sendReq.Message, cancellationToken: ct);
                    }
                    break;

                case BridgeMessageTypes.CreateSession:
                    var createReq = msg.GetPayload<CreateSessionPayload>();
                    if (createReq != null && !string.IsNullOrWhiteSpace(createReq.Name))
                    {
                        // Validate WorkingDirectory if provided — must be an absolute path that exists
                        if (createReq.WorkingDirectory != null)
                        {
                            if (!Path.IsPathRooted(createReq.WorkingDirectory) ||
                                createReq.WorkingDirectory.Contains("..") ||
                                !Directory.Exists(createReq.WorkingDirectory))
                            {
                                Console.WriteLine($"[WsBridge] Rejected invalid WorkingDirectory: {createReq.WorkingDirectory}");
                                break;
                            }
                        }
                        Console.WriteLine($"[WsBridge] Client creating session '{createReq.Name}'");
                        await copilot!.CreateSessionAsync(createReq.Name, createReq.Model, createReq.WorkingDirectory, ct);
                        BroadcastSessionsList();
                        BroadcastOrganizationState();
                    }
                    break;

                case BridgeMessageTypes.SwitchSession:
                    var switchReq = msg.GetPayload<SwitchSessionPayload>();
                    if (switchReq != null)
                    {
                        copilot!.SetActiveSession(switchReq.SessionName);
                        await SendSessionHistoryToClient(clientId, ws, switchReq.SessionName, ct);
                    }
                    break;

                case BridgeMessageTypes.QueueMessage:
                    var queueReq = msg.GetPayload<QueueMessagePayload>();
                    if (queueReq != null && !string.IsNullOrWhiteSpace(queueReq.SessionName) && !string.IsNullOrWhiteSpace(queueReq.Message))
                        copilot!.EnqueueMessage(queueReq.SessionName, queueReq.Message);
                    break;

                case BridgeMessageTypes.GetPersistedSessions:
                    await SendPersistedToClient(clientId, ws, ct);
                    break;

                case BridgeMessageTypes.ResumeSession:
                    var resumeReq = msg.GetPayload<ResumeSessionPayload>();
                    if (resumeReq != null && !string.IsNullOrWhiteSpace(resumeReq.SessionId))
                    {
                        // Validate session ID is a valid GUID to prevent path traversal
                        if (!Guid.TryParse(resumeReq.SessionId, out _))
                        {
                            Console.WriteLine($"[WsBridge] Rejected invalid session ID format: {resumeReq.SessionId}");
                            await SendToClientAsync(clientId, ws,
                                BridgeMessage.Create(BridgeMessageTypes.ErrorEvent,
                                    new ErrorPayload { SessionName = resumeReq.DisplayName ?? "Unknown", Error = "Invalid session ID format" }), ct);
                            break;
                        }
                        Console.WriteLine($"[WsBridge] Client resuming session '{resumeReq.SessionId}'");
                        var displayName = resumeReq.DisplayName ?? "Resumed";
                        try
                        {
                            await copilot!.ResumeSessionAsync(resumeReq.SessionId, displayName, workingDirectory: null, model: null, cancellationToken: ct);
                            Console.WriteLine($"[WsBridge] Session resumed successfully, broadcasting updated list");
                            BroadcastSessionsList();
                            BroadcastOrganizationState();
                        }
                        catch (Exception resumeEx)
                        {
                            Console.WriteLine($"[WsBridge] Resume failed: {resumeEx.Message}");
                            await SendToClientAsync(clientId, ws,
                                BridgeMessage.Create(BridgeMessageTypes.ErrorEvent,
                                    new ErrorPayload { SessionName = displayName, Error = $"Resume failed: {resumeEx.Message}" }), ct);
                        }
                    }
                    break;

                case BridgeMessageTypes.CloseSession:
                    var closeReq = msg.GetPayload<SessionNamePayload>();
                    if (closeReq != null)
                    {
                        Console.WriteLine($"[WsBridge] Client closing session '{closeReq.SessionName}'");
                        await copilot!.CloseSessionAsync(closeReq.SessionName);
                    }
                    break;

                case BridgeMessageTypes.AbortSession:
                    var abortReq = msg.GetPayload<SessionNamePayload>();
                    if (abortReq != null && !string.IsNullOrWhiteSpace(abortReq.SessionName))
                    {
                        Console.WriteLine($"[WsBridge] Client aborting session '{abortReq.SessionName}'");
                        await copilot!.AbortSessionAsync(abortReq.SessionName);
                    }
                    break;

                case BridgeMessageTypes.OrganizationCommand:
                    var orgCmd = msg.GetPayload<OrganizationCommandPayload>();
                    if (orgCmd != null)
                    {
                        HandleOrganizationCommand(orgCmd);
                        BroadcastOrganizationState();
                    }
                    break;

                case BridgeMessageTypes.FiestaJoinRequest:
                    var fiestaJoin = msg.GetPayload<FiestaJoinRequestPayload>();
                    if (fiestaJoin != null)
                    {
                        await HandleFiestaJoinRequestAsync(clientId, ws, fiestaJoin, ct);
                    }
                    break;

                case BridgeMessageTypes.FiestaDispatchPrompt:
                    var fiestaDispatch = msg.GetPayload<FiestaDispatchPromptPayload>();
                    if (fiestaDispatch != null)
                    {
                        await HandleFiestaDispatchPromptAsync(clientId, ws, fiestaDispatch, ct);
                    }
                    break;

                case BridgeMessageTypes.FiestaSessionCommand:
                    var fiestaCommand = msg.GetPayload<FiestaSessionCommandPayload>();
                    if (fiestaCommand != null)
                    {
                        await HandleFiestaSessionCommandAsync(clientId, ws, fiestaCommand, ct);
                    }
                    break;

                case BridgeMessageTypes.ListDirectories:
                    var dirReq = msg.GetPayload<ListDirectoriesPayload>();
                    var dirPath = dirReq?.Path;
                    if (string.IsNullOrWhiteSpace(dirPath))
                        dirPath = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

                    var dirResult = new DirectoriesListPayload { Path = dirPath! };
                    try
                    {
                        if (!Path.IsPathRooted(dirPath!) || dirPath!.Contains(".."))
                        {
                            dirResult.Error = "Invalid path";
                        }
                        else if (!Directory.Exists(dirPath))
                        {
                            dirResult.Error = "Directory not found";
                        }
                        else
                        {
                            dirResult.IsGitRepo = Directory.Exists(Path.Combine(dirPath, ".git"));
                            dirResult.Directories = Directory.GetDirectories(dirPath)
                                .Select(d => new DirectoryInfo(d))
                                .Where(d => !d.Name.StartsWith('.'))
                                .OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase)
                                .Select(d => new DirectoryEntry
                                {
                                    Name = d.Name,
                                    IsGitRepo = Directory.Exists(Path.Combine(d.FullName, ".git"))
                                })
                                .ToList();
                        }
                    }
                    catch (UnauthorizedAccessException)
                    {
                        dirResult.Error = "Access denied";
                    }
                    catch (Exception ex)
                    {
                        dirResult.Error = ex.Message;
                    }
                    await SendToClientAsync(clientId, ws,
                        BridgeMessage.Create(BridgeMessageTypes.DirectoriesList, dirResult), ct);
                    break;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[WsBridge] Error handling {msg.Type}: {ex.Message}");
        }
    }

    // --- Send helpers (per-client lock to prevent concurrent SendAsync) ---

    private async Task SendToClientAsync(string clientId, WebSocket ws, BridgeMessage msg, CancellationToken ct)
    {
        if (ws.State != WebSocketState.Open) return;
        if (!_clientSendLocks.TryGetValue(clientId, out var sendLock)) return;

        var bytes = Encoding.UTF8.GetBytes(msg.Serialize());
        await sendLock.WaitAsync(ct);
        try
        {
            if (ws.State == WebSocketState.Open)
                await ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, ct);
        }
        finally
        {
            sendLock.Release();
        }
    }

    private async Task SendPersistedToClient(string clientId, WebSocket ws, CancellationToken ct)
    {
        if (_copilot == null) return;

        var activeSessionIds = _copilot.GetAllSessions()
            .Select(s => s.SessionId)
            .Where(id => id != null)
            .ToHashSet();

        var persisted = _copilot.GetPersistedSessions()
            .Where(p => !activeSessionIds.Contains(p.SessionId))
            .Select(p => new PersistedSessionSummary
            {
                SessionId = p.SessionId,
                Title = p.Title,
                Preview = p.Preview,
                WorkingDirectory = p.WorkingDirectory,
                LastModified = p.LastModified,
            })
            .ToList();

        var msg = BridgeMessage.Create(BridgeMessageTypes.PersistedSessionsList,
            new PersistedSessionsPayload { Sessions = persisted });
        await SendToClientAsync(clientId, ws, msg, ct);
    }

    private async Task SendSessionHistoryToClient(string clientId, WebSocket ws, string sessionName, CancellationToken ct)
    {
        if (_copilot == null) return;

        var session = _copilot.GetSession(sessionName);
        if (session == null) return;

        var payload = new SessionHistoryPayload
        {
            SessionName = sessionName,
            Messages = session.History.ToList()
        };
        var msg = BridgeMessage.Create(BridgeMessageTypes.SessionHistory, payload);
        await SendToClientAsync(clientId, ws, msg, ct);
    }

    private SessionsListPayload BuildSessionsListPayload()
    {
        var sessions = _copilot!.GetAllSessions().Select(s => new SessionSummary
        {
            Name = s.Name,
            Model = s.Model,
            CreatedAt = s.CreatedAt,
            MessageCount = s.History.Count,
            IsProcessing = s.IsProcessing,
            SessionId = s.SessionId,
            WorkingDirectory = s.WorkingDirectory,
            QueueCount = s.MessageQueue.Count,
        }).ToList();

        return new SessionsListPayload
        {
            Sessions = sessions,
            ActiveSession = _copilot.ActiveSessionName,
            GitHubAvatarUrl = _copilot.GitHubAvatarUrl,
            GitHubLogin = _copilot.GitHubLogin,
        };
    }

    private void BroadcastSessionsList()
    {
        if (_copilot == null || _clients.IsEmpty) return;
        var msg = BridgeMessage.Create(BridgeMessageTypes.SessionsList, BuildSessionsListPayload());
        Broadcast(msg);
    }

    private void BroadcastOrganizationState()
    {
        if (_copilot == null) return;
        var msg = BridgeMessage.Create(BridgeMessageTypes.OrganizationState, _copilot.Organization);
        Broadcast(msg);
    }

    private void HandleOrganizationCommand(OrganizationCommandPayload cmd)
    {
        if (_copilot == null) return;
        switch (cmd.Command)
        {
            case "pin":
                if (cmd.SessionName != null) _copilot.PinSession(cmd.SessionName, true);
                break;
            case "unpin":
                if (cmd.SessionName != null) _copilot.PinSession(cmd.SessionName, false);
                break;
            case "move":
                if (cmd.SessionName != null && cmd.GroupId != null) _copilot.MoveSession(cmd.SessionName, cmd.GroupId);
                break;
            case "create_group":
                if (cmd.Name != null) _copilot.CreateGroup(cmd.Name);
                break;
            case "rename_group":
                if (cmd.GroupId != null && cmd.Name != null) _copilot.RenameGroup(cmd.GroupId, cmd.Name);
                break;
            case "delete_group":
                if (cmd.GroupId != null) _copilot.DeleteGroup(cmd.GroupId);
                break;
            case "toggle_collapsed":
                if (cmd.GroupId != null) _copilot.ToggleGroupCollapsed(cmd.GroupId);
                break;
            case "set_sort":
                if (cmd.SortMode != null && Enum.TryParse<SessionSortMode>(cmd.SortMode, out var mode))
                    _copilot.SetSortMode(mode);
                break;
        }
    }

    private async Task HandleFiestaJoinRequestAsync(string clientId, WebSocket ws, FiestaJoinRequestPayload payload, CancellationToken ct)
    {
        var workerId = _fiestaCoordinator?.InstanceId ?? "";
        var workerName = _fiestaCoordinator?.MachineName ?? Environment.MachineName;

        if (_fiestaCoordinator == null)
        {
            await SendToClientAsync(clientId, ws,
                BridgeMessage.Create(BridgeMessageTypes.FiestaJoinStatus, new FiestaJoinStatusPayload
                {
                    RequestId = payload.RequestId,
                    FiestaId = payload.FiestaId,
                    Status = FiestaJoinState.Rejected,
                    WorkerInstanceId = workerId,
                    WorkerMachineName = workerName,
                    Reason = "Fiesta coordinator is not available"
                }, payload.FiestaId), ct);
            return;
        }

        if (string.IsNullOrWhiteSpace(payload.RequestId) || string.IsNullOrWhiteSpace(payload.FiestaId))
        {
            await SendToClientAsync(clientId, ws,
                BridgeMessage.Create(BridgeMessageTypes.FiestaJoinStatus, new FiestaJoinStatusPayload
                {
                    RequestId = payload.RequestId,
                    FiestaId = payload.FiestaId,
                    Status = FiestaJoinState.Rejected,
                    WorkerInstanceId = workerId,
                    WorkerMachineName = workerName,
                    Reason = "Invalid fiesta join payload"
                }, payload.FiestaId), ct);
            return;
        }

        if (!_fiestaCoordinator.ValidateJoinCode(payload.JoinCode))
        {
            await SendToClientAsync(clientId, ws,
                BridgeMessage.Create(BridgeMessageTypes.FiestaJoinStatus, new FiestaJoinStatusPayload
                {
                    RequestId = payload.RequestId,
                    FiestaId = payload.FiestaId,
                    Status = FiestaJoinState.Rejected,
                    WorkerInstanceId = workerId,
                    WorkerMachineName = workerName,
                    Reason = "Invalid join code"
                }, payload.FiestaId), ct);
            return;
        }

        _pendingFiestaClientIds[payload.RequestId] = clientId;
        _pendingFiestaIds[payload.RequestId] = payload.FiestaId;

        _fiestaCoordinator.RegisterIncomingJoinRequest(payload, null);

        await SendToClientAsync(clientId, ws,
            BridgeMessage.Create(BridgeMessageTypes.FiestaJoinStatus, new FiestaJoinStatusPayload
            {
                RequestId = payload.RequestId,
                FiestaId = payload.FiestaId,
                Status = FiestaJoinState.Pending,
                WorkerInstanceId = workerId,
                WorkerMachineName = workerName
            }, payload.FiestaId), ct);
    }

    private async Task HandleFiestaDispatchPromptAsync(string clientId, WebSocket ws, FiestaDispatchPromptPayload payload, CancellationToken ct)
    {
        if (_copilot == null)
        {
            await SendToClientAsync(clientId, ws,
                BridgeMessage.Create(BridgeMessageTypes.FiestaDispatchResult, new FiestaDispatchResultPayload
                {
                    RequestId = payload.RequestId,
                    FiestaId = payload.FiestaId,
                    WorkerInstanceId = _fiestaCoordinator?.InstanceId ?? "",
                    WorkerMachineName = _fiestaCoordinator?.MachineName ?? Environment.MachineName,
                    SessionName = payload.SessionName,
                    Success = false,
                    Error = "Copilot service is not available"
                }, payload.FiestaId), ct);
            return;
        }

        if (!IsClientAuthorizedForFiesta(clientId, payload.FiestaId))
        {
            var unauthorizedDispatch = new FiestaDispatchResultPayload
            {
                RequestId = payload.RequestId,
                FiestaId = payload.FiestaId,
                WorkerInstanceId = _fiestaCoordinator?.InstanceId ?? "",
                WorkerMachineName = _fiestaCoordinator?.MachineName ?? Environment.MachineName,
                SessionName = payload.SessionName,
                Success = false,
                Error = "Client is not authorized for this fiesta"
            };
            await SendToClientAsync(clientId, ws, BridgeMessage.Create(BridgeMessageTypes.FiestaDispatchResult, unauthorizedDispatch, payload.FiestaId), ct);
            return;
        }

        var workerId = _fiestaCoordinator?.InstanceId ?? "";
        var workerName = _fiestaCoordinator?.MachineName ?? Environment.MachineName;

        var result = new FiestaDispatchResultPayload
        {
            RequestId = payload.RequestId,
            FiestaId = payload.FiestaId,
            WorkerInstanceId = workerId,
            WorkerMachineName = workerName,
            SessionName = payload.SessionName
        };

        try
        {
            if (payload.CreateSessionIfMissing && _copilot.GetSession(payload.SessionName) == null)
            {
                await _copilot.CreateSessionAsync(payload.SessionName, payload.Model, payload.WorkingDirectory, ct);
            }

            await _copilot.SendPromptAsync(payload.SessionName, payload.Message, cancellationToken: ct);
            result.Success = true;
            result.Summary = "Prompt completed";
        }
        catch (Exception ex)
        {
            result.Success = false;
            result.Error = ex.Message;
        }

        await SendToClientAsync(clientId, ws, BridgeMessage.Create(BridgeMessageTypes.FiestaDispatchResult, result, payload.FiestaId), ct);
    }

    private async Task HandleFiestaSessionCommandAsync(string clientId, WebSocket ws, FiestaSessionCommandPayload payload, CancellationToken ct)
    {
        if (_copilot == null)
        {
            await SendToClientAsync(clientId, ws,
                BridgeMessage.Create(BridgeMessageTypes.FiestaSessionCommandResult, new FiestaSessionCommandResultPayload
                {
                    RequestId = payload.RequestId,
                    FiestaId = payload.FiestaId,
                    WorkerInstanceId = _fiestaCoordinator?.InstanceId ?? "",
                    WorkerMachineName = _fiestaCoordinator?.MachineName ?? Environment.MachineName,
                    Command = payload.Command,
                    SessionName = payload.SessionName,
                    Success = false,
                    Error = "Copilot service is not available"
                }, payload.FiestaId), ct);
            return;
        }

        if (!IsClientAuthorizedForFiesta(clientId, payload.FiestaId))
        {
            await SendUnauthorizedFiestaResultAsync(clientId, ws, payload.RequestId, payload.FiestaId, payload.Command, payload.SessionName, ct);
            return;
        }

        var workerId = _fiestaCoordinator?.InstanceId ?? "";
        var workerName = _fiestaCoordinator?.MachineName ?? Environment.MachineName;

        var result = new FiestaSessionCommandResultPayload
        {
            RequestId = payload.RequestId,
            FiestaId = payload.FiestaId,
            WorkerInstanceId = workerId,
            WorkerMachineName = workerName,
            Command = payload.Command,
            SessionName = payload.SessionName
        };

        try
        {
            switch (payload.Command?.Trim().ToLowerInvariant())
            {
                case "create":
                    if (_copilot.GetSession(payload.SessionName) == null)
                        await _copilot.CreateSessionAsync(payload.SessionName, payload.Model, payload.WorkingDirectory, ct);
                    break;
                case "close":
                    await _copilot.CloseSessionAsync(payload.SessionName);
                    break;
                case "switch":
                    _copilot.SwitchSession(payload.SessionName);
                    break;
                default:
                    throw new InvalidOperationException($"Unsupported fiesta session command '{payload.Command}'.");
            }

            result.Success = true;
        }
        catch (Exception ex)
        {
            result.Success = false;
            result.Error = ex.Message;
        }

        await SendToClientAsync(clientId, ws, BridgeMessage.Create(BridgeMessageTypes.FiestaSessionCommandResult, result, payload.FiestaId), ct);
    }

    private async Task SendUnauthorizedFiestaResultAsync(string clientId, WebSocket ws, string requestId, string fiestaId, string command, string sessionName, CancellationToken ct)
    {
        var workerId = _fiestaCoordinator?.InstanceId ?? "";
        var workerName = _fiestaCoordinator?.MachineName ?? Environment.MachineName;
        var result = new FiestaSessionCommandResultPayload
        {
            RequestId = requestId,
            FiestaId = fiestaId,
            WorkerInstanceId = workerId,
            WorkerMachineName = workerName,
            Command = command,
            SessionName = sessionName,
            Success = false,
            Error = "Client is not authorized for this fiesta"
        };
        await SendToClientAsync(clientId, ws, BridgeMessage.Create(BridgeMessageTypes.FiestaSessionCommandResult, result, fiestaId), ct);
    }

    private bool IsClientAuthorizedForFiesta(string clientId, string fiestaId)
    {
        if (string.IsNullOrWhiteSpace(fiestaId)) return false;
        if (!_clientFiestaAuthorizations.TryGetValue(clientId, out var rooms)) return false;
        return rooms.ContainsKey(fiestaId);
    }

    private static bool IsFiestaMessageType(string type) =>
        type == BridgeMessageTypes.FiestaJoinRequest ||
        type == BridgeMessageTypes.FiestaDispatchPrompt ||
        type == BridgeMessageTypes.FiestaSessionCommand;

    private void HandleJoinRequestResolved(FiestaJoinRequest request)
    {
        if (!_pendingFiestaClientIds.TryRemove(request.RequestId, out var clientId))
            return;

        _pendingFiestaIds.TryRemove(request.RequestId, out _);

        if (!_clients.TryGetValue(clientId, out var ws) || ws.State != WebSocketState.Open)
            return;

        if (request.Status == FiestaJoinState.Approved)
        {
            var roomAuth = _clientFiestaAuthorizations.GetOrAdd(clientId, _ => new ConcurrentDictionary<string, byte>(StringComparer.Ordinal));
            roomAuth[request.FiestaId] = 0;
        }

        var payload = new FiestaJoinStatusPayload
        {
            RequestId = request.RequestId,
            FiestaId = request.FiestaId,
            Status = request.Status,
            WorkerInstanceId = _fiestaCoordinator?.InstanceId ?? "",
            WorkerMachineName = _fiestaCoordinator?.MachineName ?? Environment.MachineName,
            Reason = request.Reason
        };

        var message = BridgeMessage.Create(BridgeMessageTypes.FiestaJoinStatus, payload, request.FiestaId);
        _ = Task.Run(async () =>
        {
            if (_clientSendLocks.TryGetValue(clientId, out var sendLock))
            {
                try
                {
                    var token = _cts?.Token ?? CancellationToken.None;
                    await sendLock.WaitAsync(token);
                    try
                    {
                        if (ws.State == WebSocketState.Open)
                        {
                            var bytes = Encoding.UTF8.GetBytes(message.Serialize());
                            await ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, token);
                        }
                    }
                    finally
                    {
                        sendLock.Release();
                    }
                }
                catch (OperationCanceledException) { }
                catch (Exception ex)
                {
                    Console.WriteLine($"[WsBridge] Failed to send fiesta join resolution to client {clientId}: {ex.Message}");
                }
            }
        });
    }

    // --- Broadcast/Send ---

    private void Broadcast(BridgeMessage msg)
    {
        if (_clients.IsEmpty) return;
        var json = msg.Serialize();
        var bytes = Encoding.UTF8.GetBytes(json);

        foreach (var (id, ws) in _clients)
        {
            if (ws.State != WebSocketState.Open)
            {
                _clients.TryRemove(id, out _);
                if (_clientSendLocks.TryRemove(id, out var lk)) lk.Dispose();
                continue;
            }
            if (!_clientSendLocks.TryGetValue(id, out var sendLock)) continue;

            var clientId = id;
            _ = Task.Run(async () =>
            {
                await sendLock.WaitAsync();
                try
                {
                    if (ws.State == WebSocketState.Open)
                        await ws.SendAsync(new ArraySegment<byte>(bytes),
                            WebSocketMessageType.Text, true, CancellationToken.None);
                }
                catch
                {
                    _clients.TryRemove(clientId, out _);
                    if (_clientSendLocks.TryRemove(clientId, out var lk2)) lk2.Dispose();
                }
                finally
                {
                    sendLock.Release();
                }
            });
        }
    }

    public void Dispose()
    {
        if (_fiestaCoordinator != null)
            _fiestaCoordinator.OnJoinRequestResolved -= HandleJoinRequestResolved;
        Stop();
        _cts?.Dispose();
        GC.SuppressFinalize(this);
    }
}
