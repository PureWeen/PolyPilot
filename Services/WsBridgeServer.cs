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
        _copilot.OnToolStarted += (session, tool, callId) =>
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
            Console.WriteLine($"[WsBridge] Client {clientId} connected ({_clients.Count} total)");

            // Send initial state
            await SendSessionsList(ws, ct);
            await SendPersistedSessions(ws, ct);

            // Send active session history
            if (_copilot != null)
            {
                var active = _copilot.GetActiveSession();
                if (active != null)
                    await SendSessionHistory(ws, active.Name, ct);
            }

            // Read client commands
            var buffer = new byte[8192];
            while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                var result = await ws.ReceiveAsync(buffer, ct);
                if (result.MessageType == WebSocketMessageType.Close) break;
                if (result.Count > 0)
                {
                    var json = Encoding.UTF8.GetString(buffer, 0, result.Count);
                    await HandleClientMessage(ws, json, ct);
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
            if (ws?.State == WebSocketState.Open)
            {
                try { await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None); }
                catch { }
            }
            ws?.Dispose();
            Console.WriteLine($"[WsBridge] Client {clientId} disconnected ({_clients.Count} remaining)");
        }
    }

    private async Task HandleClientMessage(WebSocket ws, string json, CancellationToken ct)
    {
        var msg = BridgeMessage.Deserialize(json);
        if (msg == null || _copilot == null) return;

        try
        {
            switch (msg.Type)
            {
                case BridgeMessageTypes.GetSessions:
                    await SendSessionsList(ws, ct);
                    break;

                case BridgeMessageTypes.GetHistory:
                    var histReq = msg.GetPayload<GetHistoryPayload>();
                    if (histReq != null)
                        await SendSessionHistory(ws, histReq.SessionName, ct);
                    break;

                case BridgeMessageTypes.SendMessage:
                    var sendReq = msg.GetPayload<SendMessagePayload>();
                    if (sendReq != null)
                    {
                        Console.WriteLine($"[WsBridge] Client sending message to '{sendReq.SessionName}'");
                        await _copilot.SendPromptAsync(sendReq.SessionName, sendReq.Message, ct);
                    }
                    break;

                case BridgeMessageTypes.CreateSession:
                    var createReq = msg.GetPayload<CreateSessionPayload>();
                    if (createReq != null)
                    {
                        Console.WriteLine($"[WsBridge] Client creating session '{createReq.Name}'");
                        await _copilot.CreateSessionAsync(createReq.Name, createReq.Model, createReq.WorkingDirectory, ct);
                    }
                    break;

                case BridgeMessageTypes.SwitchSession:
                    var switchReq = msg.GetPayload<SwitchSessionPayload>();
                    if (switchReq != null)
                    {
                        _copilot.SetActiveSession(switchReq.SessionName);
                        await SendSessionHistory(ws, switchReq.SessionName, ct);
                    }
                    break;

                case BridgeMessageTypes.QueueMessage:
                    var queueReq = msg.GetPayload<QueueMessagePayload>();
                    if (queueReq != null)
                        _copilot.EnqueueMessage(queueReq.SessionName, queueReq.Message);
                    break;

                case BridgeMessageTypes.GetPersistedSessions:
                    await SendPersistedSessions(ws, ct);
                    break;

                case BridgeMessageTypes.ResumeSession:
                    var resumeReq = msg.GetPayload<ResumeSessionPayload>();
                    if (resumeReq != null)
                    {
                        Console.WriteLine($"[WsBridge] Client resuming session '{resumeReq.SessionId}'");
                        var displayName = resumeReq.DisplayName ?? "Resumed";
                        await _copilot.ResumeSessionAsync(resumeReq.SessionId, displayName, ct);
                    }
                    break;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[WsBridge] Error handling {msg.Type}: {ex.Message}");
        }
    }

    // --- Send helpers ---

    private async Task SendSessionsList(WebSocket ws, CancellationToken ct)
    {
        if (_copilot == null) return;

        var payload = BuildSessionsListPayload();
        var msg = BridgeMessage.Create(BridgeMessageTypes.SessionsList, payload);
        await SendAsync(ws, msg, ct);
    }

    private async Task SendPersistedSessions(WebSocket ws, CancellationToken ct)
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
        await SendAsync(ws, msg, ct);
    }

    private async Task SendSessionHistory(WebSocket ws, string sessionName, CancellationToken ct)
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
        await SendAsync(ws, msg, ct);
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
                continue;
            }
            _ = Task.Run(async () =>
            {
                try
                {
                    await ws.SendAsync(new ArraySegment<byte>(bytes),
                        WebSocketMessageType.Text, true, CancellationToken.None);
                }
                catch
                {
                    _clients.TryRemove(id, out _);
                }
            });
        }
    }

    private static async Task SendAsync(WebSocket ws, BridgeMessage msg, CancellationToken ct)
    {
        if (ws.State != WebSocketState.Open) return;
        var bytes = Encoding.UTF8.GetBytes(msg.Serialize());
        await ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, ct);
    }

    public void Dispose()
    {
        Stop();
        _cts?.Dispose();
        GC.SuppressFinalize(this);
    }
}
