using System.Collections.Concurrent;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using AutoPilot.App.Models;

namespace AutoPilot.App.Services;

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

    public int BridgePort => _bridgePort;
    public bool IsRunning => _listener?.IsListening == true;

    /// <summary>
    /// Access token that clients must provide via X-Tunnel-Authorization header or query param.
    /// </summary>
    public string? AccessToken { get; set; }

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
                new SessionNamePayload { SessionName = session }));
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
                    _ = Task.Run(() => HandleClientAsync(context, ct), ct);
                }
                else if (context.Request.Url?.AbsolutePath == "/token" && context.Request.HttpMethod == "GET")
                {
                    context.Response.StatusCode = 200;
                    context.Response.ContentType = "text/plain";
                    context.Response.Headers.Add("Access-Control-Allow-Origin", "*");
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

    private async Task HandleClientAsync(HttpListenerContext httpContext, CancellationToken ct)
    {
        WebSocket? ws = null;
        var clientId = Guid.NewGuid().ToString("N")[..8];

        try
        {
            var wsContext = await httpContext.AcceptWebSocketAsync(null);
            ws = wsContext.WebSocket;
            _clients[clientId] = ws;
            _clientSendLocks[clientId] = new SemaphoreSlim(1, 1);
            Console.WriteLine($"[WsBridge] Client {clientId} connected ({_clients.Count} total)");

            // Send initial state
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
        if (msg == null || _copilot == null) return;

        try
        {
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
                    if (sendReq != null)
                    {
                        Console.WriteLine($"[WsBridge] Client sending message to '{sendReq.SessionName}'");
                        await _copilot.SendPromptAsync(sendReq.SessionName, sendReq.Message, cancellationToken: ct);
                    }
                    break;

                case BridgeMessageTypes.CreateSession:
                    var createReq = msg.GetPayload<CreateSessionPayload>();
                    if (createReq != null)
                    {
                        Console.WriteLine($"[WsBridge] Client creating session '{createReq.Name}'");
                        await _copilot.CreateSessionAsync(createReq.Name, createReq.Model, createReq.WorkingDirectory, ct);
                        BroadcastSessionsList();
                    }
                    break;

                case BridgeMessageTypes.SwitchSession:
                    var switchReq = msg.GetPayload<SwitchSessionPayload>();
                    if (switchReq != null)
                    {
                        _copilot.SetActiveSession(switchReq.SessionName);
                        await SendSessionHistoryToClient(clientId, ws, switchReq.SessionName, ct);
                    }
                    break;

                case BridgeMessageTypes.QueueMessage:
                    var queueReq = msg.GetPayload<QueueMessagePayload>();
                    if (queueReq != null)
                        _copilot.EnqueueMessage(queueReq.SessionName, queueReq.Message);
                    break;

                case BridgeMessageTypes.GetPersistedSessions:
                    await SendPersistedToClient(clientId, ws, ct);
                    break;

                case BridgeMessageTypes.ResumeSession:
                    var resumeReq = msg.GetPayload<ResumeSessionPayload>();
                    if (resumeReq != null)
                    {
                        Console.WriteLine($"[WsBridge] Client resuming session '{resumeReq.SessionId}'");
                        var displayName = resumeReq.DisplayName ?? "Resumed";
                        try
                        {
                            await _copilot.ResumeSessionAsync(resumeReq.SessionId, displayName, ct);
                            Console.WriteLine($"[WsBridge] Session resumed successfully, broadcasting updated list");
                            BroadcastSessionsList();
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
                        await _copilot.CloseSessionAsync(closeReq.SessionName);
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
            ActiveSession = _copilot.ActiveSessionName
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
        Stop();
        _cts?.Dispose();
        GC.SuppressFinalize(this);
    }
}
