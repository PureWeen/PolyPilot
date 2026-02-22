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
    private FiestaService? _fiestaService;
    private RepoManager? _repoManager;
    private readonly ConcurrentDictionary<string, WebSocket> _clients = new();
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _clientSendLocks = new();

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
                new ToolStartedPayload { SessionName = session, ToolName = tool, CallId = callId, ToolInput = input }));
        _copilot.OnToolCompleted += (session, callId, result, success) =>
        {
            var payload = new ToolCompletedPayload { SessionName = session, CallId = callId, Result = result, Success = success };
            // Check if this is a show_image result — include image data for remote clients
            if (success)
            {
                var (imgPath, caption) = ShowImageTool.ParseResult(result);
                if (!string.IsNullOrEmpty(imgPath) && File.Exists(imgPath))
                {
                    try
                    {
                        var bytes = File.ReadAllBytes(imgPath);
                        payload.ImageData = Convert.ToBase64String(bytes);
                        payload.ImageMimeType = ImageMimeType(imgPath);
                        payload.Caption = caption;
                    }
                    catch { /* fall through — remote client won't get the image */ }
                }
            }
            Broadcast(BridgeMessage.Create(BridgeMessageTypes.ToolCompleted, payload));
        };
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
        {
            Broadcast(BridgeMessage.Create(BridgeMessageTypes.SessionComplete,
                new SessionCompletePayload { SessionName = session, Summary = summary }));
            // Only notify when session is truly complete and waiting for user input
            BroadcastAttentionNeeded(session, AttentionReason.ReadyForMore, TruncateSummary(summary));
        };
        _copilot.OnError += (session, error) =>
        {
            Broadcast(BridgeMessage.Create(BridgeMessageTypes.ErrorEvent,
                new ErrorPayload { SessionName = session, Error = error }));
            BroadcastAttentionNeeded(session, AttentionReason.Error, TruncateSummary(error));
        };
    }

    public void SetFiestaService(FiestaService fiestaService)
    {
        _fiestaService ??= fiestaService;
    }

    public void SetRepoManager(RepoManager repoManager)
    {
        _repoManager ??= repoManager;
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
        // If neither token nor password is configured, allow all (local-only mode)
        if (string.IsNullOrEmpty(AccessToken) && string.IsNullOrEmpty(ServerPassword))
            return true;

        // Loopback connections are trusted — DevTunnel proxies appear as localhost
        if (IsLoopbackRequest(request))
            return true;

        // Extract token from header or query string
        string? providedToken = null;
        var authHeader = request.Headers["X-Tunnel-Authorization"];
        if (!string.IsNullOrEmpty(authHeader))
        {
            providedToken = authHeader.StartsWith("tunnel ", StringComparison.OrdinalIgnoreCase)
                ? authHeader["tunnel ".Length..].Trim()
                : authHeader.Trim();
        }
        providedToken ??= request.QueryString["token"];

        if (string.IsNullOrEmpty(providedToken))
            return false;

        // Accept if it matches either the tunnel access token or the server password
        if (!string.IsNullOrEmpty(AccessToken) && string.Equals(providedToken, AccessToken, StringComparison.Ordinal))
            return true;
        if (!string.IsNullOrEmpty(ServerPassword) && string.Equals(providedToken, ServerPassword, StringComparison.Ordinal))
            return true;

        return false;
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

        try
        {
            var wsContext = await httpContext.AcceptWebSocketAsync(null);
            ws = wsContext.WebSocket;
            _clients[clientId] = ws;
            _clientSendLocks[clientId] = new SemaphoreSlim(1, 1);
            Console.WriteLine($"[WsBridge] Client {clientId} connected ({_clients.Count} total)");

            // Send initial state
            if (_copilot != null)
            {
                await SendToClientAsync(clientId, ws,
                    BridgeMessage.Create(BridgeMessageTypes.SessionsList, BuildSessionsListPayload()), ct);
                await SendToClientAsync(clientId, ws,
                    BridgeMessage.Create(BridgeMessageTypes.OrganizationState, _copilot.Organization), ct);
            }
            else
            {
                await SendToClientAsync(clientId, ws,
                    BridgeMessage.Create(BridgeMessageTypes.SessionsList, new SessionsListPayload()), ct);
                await SendToClientAsync(clientId, ws,
                    BridgeMessage.Create(BridgeMessageTypes.OrganizationState, new OrganizationState()), ct);
            }
            await SendPersistedToClient(clientId, ws, ct);

            // Send history for all active sessions so mobile has full state on connect
            if (_copilot != null)
            {
                foreach (var session in _copilot.GetAllSessions())
                {
                    if (session.History.Count > 0)
                        await SendSessionHistoryToClient(clientId, ws, session.Name, ct);
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
                    if (sendReq != null && !string.IsNullOrWhiteSpace(sendReq.SessionName) && !string.IsNullOrWhiteSpace(sendReq.Message))
                    {
                        Console.WriteLine($"[WsBridge] Client sending message to '{sendReq.SessionName}'");
                        await _copilot.SendPromptAsync(sendReq.SessionName, sendReq.Message, cancellationToken: ct);
                    }
                    break;

                case BridgeMessageTypes.CreateSession:
                    var createReq = msg.GetPayload<CreateSessionPayload>();
                    if (createReq != null && !string.IsNullOrWhiteSpace(createReq.Name))
                    {
                        // Normalize empty WorkingDirectory to null (mobile sends "" when no dir is specified)
                        if (string.IsNullOrWhiteSpace(createReq.WorkingDirectory))
                            createReq.WorkingDirectory = null;

                        // Validate WorkingDirectory if provided — must be an absolute path that exists
                        if (createReq.WorkingDirectory != null)
                        {
                            if (!Path.IsPathRooted(createReq.WorkingDirectory) ||
                                createReq.WorkingDirectory.Contains("..") ||
                                !Directory.Exists(createReq.WorkingDirectory))
                            {
                                Console.WriteLine($"[WsBridge] Rejected invalid WorkingDirectory: {createReq.WorkingDirectory}");
                                await SendToClientAsync(clientId, ws,
                                    BridgeMessage.Create(BridgeMessageTypes.ErrorEvent,
                                        new ErrorPayload { SessionName = createReq.Name, Error = $"Working directory not found on server: {createReq.WorkingDirectory}" }), ct);
                                break;
                            }
                        }
                        Console.WriteLine($"[WsBridge] Client creating session '{createReq.Name}'");
                        await _copilot.CreateSessionAsync(createReq.Name, createReq.Model, createReq.WorkingDirectory, ct);
                        BroadcastSessionsList();
                        BroadcastOrganizationState();
                    }
                    break;

                case BridgeMessageTypes.SwitchSession:
                    var switchReq = msg.GetPayload<SwitchSessionPayload>();
                    if (switchReq != null)
                    {
                        _copilot.SetActiveSession(switchReq.SessionName);
                        BroadcastSessionsList();
                        await SendSessionHistoryToClient(clientId, ws, switchReq.SessionName, ct);
                    }
                    break;

                case BridgeMessageTypes.QueueMessage:
                    var queueReq = msg.GetPayload<QueueMessagePayload>();
                    if (queueReq != null && !string.IsNullOrWhiteSpace(queueReq.SessionName) && !string.IsNullOrWhiteSpace(queueReq.Message))
                        _copilot.EnqueueMessage(queueReq.SessionName, queueReq.Message);
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
                            await _copilot.ResumeSessionAsync(resumeReq.SessionId, displayName, workingDirectory: null, model: null, cancellationToken: ct);
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
                    if (closeReq != null && !string.IsNullOrWhiteSpace(closeReq.SessionName))
                    {
                        Console.WriteLine($"[WsBridge] Client closing session '{closeReq.SessionName}'");
                        await _copilot.CloseSessionAsync(closeReq.SessionName);
                    }
                    break;

                case BridgeMessageTypes.AbortSession:
                    var abortReq = msg.GetPayload<SessionNamePayload>();
                    if (abortReq != null && !string.IsNullOrWhiteSpace(abortReq.SessionName))
                    {
                        Console.WriteLine($"[WsBridge] Client aborting session '{abortReq.SessionName}'");
                        await _copilot.AbortSessionAsync(abortReq.SessionName);
                    }
                    break;

                case BridgeMessageTypes.ChangeModel:
                    var changeModelReq = msg.GetPayload<ChangeModelPayload>();
                    if (changeModelReq != null && !string.IsNullOrWhiteSpace(changeModelReq.SessionName))
                    {
                        Console.WriteLine($"[WsBridge] Client changing model for '{changeModelReq.SessionName}' to '{changeModelReq.NewModel}'");
                        var modelChanged = await _copilot.ChangeModelAsync(changeModelReq.SessionName, changeModelReq.NewModel);
                        if (!modelChanged)
                        {
                            await SendToClientAsync(clientId, ws,
                                BridgeMessage.Create(BridgeMessageTypes.ErrorEvent,
                                    new ErrorPayload { SessionName = changeModelReq.SessionName, Error = "Failed to change model. Session may be processing or model is invalid." }), ct);
                        }
                        // Always broadcast latest session state so client stays in sync
                        BroadcastSessionsList();
                    }
                    break;

                case BridgeMessageTypes.RenameSession:
                    var renameReq = msg.GetPayload<RenameSessionPayload>();
                    if (renameReq != null && !string.IsNullOrWhiteSpace(renameReq.OldName) && !string.IsNullOrWhiteSpace(renameReq.NewName))
                    {
                        Console.WriteLine($"[WsBridge] Client renaming session '{renameReq.OldName}' to '{renameReq.NewName}'");
                        var renamed = _copilot.RenameSession(renameReq.OldName, renameReq.NewName);
                        if (!renamed)
                        {
                            await SendToClientAsync(clientId, ws,
                                BridgeMessage.Create(BridgeMessageTypes.ErrorEvent,
                                    new ErrorPayload { SessionName = renameReq.OldName, Error = "Failed to rename session. Name may already exist." }), ct);
                        }
                        BroadcastSessionsList();
                        BroadcastOrganizationState();
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

                case BridgeMessageTypes.ListDirectories:
                    var dirReq = msg.GetPayload<ListDirectoriesPayload>();
                    var dirPath = dirReq?.Path;
                    if (string.IsNullOrWhiteSpace(dirPath))
                        dirPath = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

                    var dirResult = new DirectoriesListPayload { Path = dirPath!, RequestId = dirReq?.RequestId };
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

                case BridgeMessageTypes.MultiAgentBroadcast:
                    var maReq = msg.GetPayload<MultiAgentBroadcastPayload>();
                    if (maReq != null && _copilot != null)
                    {
                        _ = _copilot.SendToMultiAgentGroupAsync(maReq.GroupId, maReq.Message, ct);
                    }
                    break;

                case BridgeMessageTypes.MultiAgentCreateGroup:
                    var maCreateReq = msg.GetPayload<MultiAgentCreateGroupPayload>();
                    if (maCreateReq != null && _copilot != null)
                    {
                        var mode = Enum.TryParse<MultiAgentMode>(maCreateReq.Mode, out var m) ? m : MultiAgentMode.Broadcast;
                        _copilot.CreateMultiAgentGroup(maCreateReq.Name, mode, maCreateReq.OrchestratorPrompt, maCreateReq.SessionNames);
                    }
                    break;

                case BridgeMessageTypes.MultiAgentSetRole:
                    var maRoleReq = msg.GetPayload<MultiAgentSetRolePayload>();
                    if (maRoleReq != null && _copilot != null)
                    {
                        var role = Enum.TryParse<MultiAgentRole>(maRoleReq.Role, out var r) ? r : MultiAgentRole.Worker;
                        _copilot.SetSessionRole(maRoleReq.SessionName, role);
                    }
                    break;

                case BridgeMessageTypes.FetchImage:
                    var imgReq = msg.GetPayload<FetchImagePayload>();
                    if (imgReq != null)
                    {
                        var imgResponse = new FetchImageResponsePayload { RequestId = imgReq.RequestId };
                        try
                        {
                            if (!string.IsNullOrEmpty(imgReq.Path) && File.Exists(imgReq.Path))
                            {
                                var bytes = await File.ReadAllBytesAsync(imgReq.Path);
                                imgResponse.ImageData = Convert.ToBase64String(bytes);
                                imgResponse.MimeType = ImageMimeType(imgReq.Path);
                            }
                            else
                            {
                                imgResponse.Error = "File not found";
                            }
                        }
                        catch (Exception ex) { imgResponse.Error = ex.Message; }
                        await SendToClientAsync(clientId, ws,
                            BridgeMessage.Create(BridgeMessageTypes.FetchImageResponse, imgResponse), ct);
                    }
                    break;

                case BridgeMessageTypes.ListRepos:
                    if (_repoManager != null)
                    {
                        var listReq = msg.GetPayload<ListReposPayload>();
                        var repos = _repoManager.Repositories.Select(r => new RepoSummary
                        {
                            Id = r.Id, Name = r.Name, Url = r.Url
                        }).ToList();
                        var worktrees = _repoManager.Worktrees.Select(w => new WorktreeSummary
                        {
                            Id = w.Id, RepoId = w.RepoId, Branch = w.Branch, Path = w.Path, PrNumber = w.PrNumber, Remote = w.Remote
                        }).ToList();
                        await SendToClientAsync(clientId, ws,
                            BridgeMessage.Create(BridgeMessageTypes.ReposList,
                                new ReposListPayload { RequestId = listReq?.RequestId, Repos = repos, Worktrees = worktrees }), ct);
                    }
                    break;

                case BridgeMessageTypes.AddRepo:
                    var addReq = msg.GetPayload<AddRepoPayload>();
                    if (addReq != null && _repoManager != null)
                    {
                        _ = Task.Run(async () =>
                        {
                            try
                            {
                                var repo = await _repoManager.AddRepositoryAsync(addReq.Url, progress =>
                                {
                                    _ = SendToClientAsync(clientId, ws,
                                        BridgeMessage.Create(BridgeMessageTypes.RepoProgress,
                                            new RepoProgressPayload { RequestId = addReq.RequestId, Message = progress }), ct);
                                }, ct);
                                _copilot?.GetOrCreateRepoGroup(repo.Id, repo.Name);
                                await SendToClientAsync(clientId, ws,
                                    BridgeMessage.Create(BridgeMessageTypes.RepoAdded,
                                        new RepoAddedPayload
                                        {
                                            RequestId = addReq.RequestId,
                                            RepoId = repo.Id,
                                            RepoName = repo.Name,
                                            Url = repo.Url
                                        }), ct);
                            }
                            catch (Exception ex)
                            {
                                await SendToClientAsync(clientId, ws,
                                    BridgeMessage.Create(BridgeMessageTypes.RepoError,
                                        new RepoErrorPayload { RequestId = addReq.RequestId, Error = ex.Message }), ct);
                            }
                        }, ct);
                    }
                    break;

                case BridgeMessageTypes.RemoveRepo:
                    var removeReq = msg.GetPayload<RemoveRepoPayload>();
                    if (removeReq != null && _repoManager != null)
                    {
                        try
                        {
                            await _repoManager.RemoveRepositoryAsync(removeReq.RepoId, removeReq.DeleteFromDisk, ct);
                            if (!string.IsNullOrEmpty(removeReq.GroupId))
                                _copilot?.DeleteGroup(removeReq.GroupId);
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"[WsBridgeServer] RemoveRepo error: {ex.Message}");
                        }
                    }
                    break;

                case BridgeMessageTypes.CreateWorktree:
                    var wtReq = msg.GetPayload<CreateWorktreePayload>();
                    if (wtReq != null && _repoManager != null)
                    {
                        _ = Task.Run(async () =>
                        {
                            try
                            {
                                WorktreeInfo wt;
                                if (wtReq.PrNumber.HasValue)
                                    wt = await _repoManager.CreateWorktreeFromPrAsync(wtReq.RepoId, wtReq.PrNumber.Value, ct);
                                else
                                    wt = await _repoManager.CreateWorktreeAsync(wtReq.RepoId, wtReq.BranchName ?? "main", null, ct);
                                await SendToClientAsync(clientId, ws,
                                    BridgeMessage.Create(BridgeMessageTypes.WorktreeCreated,
                                        new WorktreeCreatedPayload
                                        {
                                            RequestId = wtReq.RequestId,
                                            WorktreeId = wt.Id,
                                            RepoId = wt.RepoId,
                                            Branch = wt.Branch,
                                            Path = wt.Path,
                                            PrNumber = wt.PrNumber,
                                            Remote = wt.Remote
                                        }), ct);
                            }
                            catch (Exception ex)
                            {
                                await SendToClientAsync(clientId, ws,
                                    BridgeMessage.Create(BridgeMessageTypes.WorktreeError,
                                        new RepoErrorPayload { RequestId = wtReq.RequestId, Error = ex.Message }), ct);
                            }
                        }, ct);
                    }
                    break;

                case BridgeMessageTypes.RemoveWorktree:
                    var rmWtReq = msg.GetPayload<RemoveWorktreePayload>();
                    if (rmWtReq != null && _repoManager != null)
                    {
                        try
                        {
                            await _repoManager.RemoveWorktreeAsync(rmWtReq.WorktreeId, ct);
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"[WsBridgeServer] RemoveWorktree error: {ex.Message}");
                            await SendToClientAsync(clientId, ws,
                                BridgeMessage.Create(BridgeMessageTypes.WorktreeError,
                                    new RepoErrorPayload { RequestId = rmWtReq.WorktreeId, Error = ex.Message }), ct);
                        }
                    }
                    break;

                case BridgeMessageTypes.FiestaAssign:
                case BridgeMessageTypes.FiestaPing:
                    if (_fiestaService != null)
                        await _fiestaService.HandleBridgeMessageAsync(clientId, ws, msg, ct);
                    break;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[WsBridge] Error handling {msg.Type}: {ex.Message}");
        }
    }

    // --- Send helpers (per-client lock to prevent concurrent SendAsync) ---

    public Task SendBridgeMessageAsync(string clientId, WebSocket ws, BridgeMessage msg, CancellationToken ct) =>
        SendToClientAsync(clientId, ws, msg, ct);

    private async Task SendToClientAsync(string clientId, WebSocket ws, BridgeMessage msg, CancellationToken ct)
    {
        if (ws.State != WebSocketState.Open) return;
        if (!_clientSendLocks.TryGetValue(clientId, out var sendLock)) return;

        var bytes = Encoding.UTF8.GetBytes(msg.Serialize());
        try
        {
            await sendLock.WaitAsync(ct);
        }
        catch (ObjectDisposedException) { return; }
        try
        {
            if (ws.State == WebSocketState.Open)
                await ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, ct);
        }
        finally
        {
            try { sendLock.Release(); } catch (ObjectDisposedException) { }
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

        // Populate ImageDataUri for Image messages so mobile can render them
        foreach (var m in session.History)
        {
            if (m.MessageType == ChatMessageType.Image && string.IsNullOrEmpty(m.ImageDataUri) && !string.IsNullOrEmpty(m.ImagePath))
            {
                try
                {
                    if (File.Exists(m.ImagePath))
                    {
                        var bytes = await File.ReadAllBytesAsync(m.ImagePath);
                        m.ImageDataUri = $"data:{ImageMimeType(m.ImagePath)};base64,{Convert.ToBase64String(bytes)}";
                    }
                }
                catch { /* best effort */ }
            }
        }

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
            ProcessingStartedAt = s.ProcessingStartedAt,
            ToolCallCount = s.ToolCallCount,
            ProcessingPhase = s.ProcessingPhase,
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
                try
                {
                    await sendLock.WaitAsync();
                    if (ws.State == WebSocketState.Open)
                        await ws.SendAsync(new ArraySegment<byte>(bytes),
                            WebSocketMessageType.Text, true, CancellationToken.None);
                }
                catch (ObjectDisposedException)
                {
                    _clients.TryRemove(clientId, out _);
                    return;
                }
                catch
                {
                    _clients.TryRemove(clientId, out _);
                }
                finally
                {
                    try { sendLock.Release(); } catch (ObjectDisposedException) { }
                    // Clean up lock for removed clients
                    if (!_clients.ContainsKey(clientId))
                        if (_clientSendLocks.TryRemove(clientId, out var lk)) lk.Dispose();
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

    private void BroadcastAttentionNeeded(string sessionName, AttentionReason reason, string summary)
    {
        if (_clients.IsEmpty) return;
        
        var sessionInfo = _copilot?.GetSession(sessionName);
        var payload = new AttentionNeededPayload
        {
            SessionName = sessionName,
            SessionId = sessionInfo?.SessionId,
            Reason = reason,
            Summary = summary
        };
        Broadcast(BridgeMessage.Create(BridgeMessageTypes.AttentionNeeded, payload));
    }

    private static string TruncateSummary(string text, int maxLength = 100)
    {
        if (string.IsNullOrEmpty(text)) return "";
        text = text.Replace("\n", " ").Replace("\r", "").Trim();
        return text.Length <= maxLength ? text : text[..(maxLength - 3)] + "...";
    }

    private static string ImageMimeType(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".gif" => "image/gif",
        ".webp" => "image/webp",
        ".bmp" => "image/bmp",
        ".svg" => "image/svg+xml",
        _ => "image/png"
    };
}
