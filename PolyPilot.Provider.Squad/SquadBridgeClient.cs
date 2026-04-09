using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace PolyPilot.Provider.Squad;

/// <summary>
/// WebSocket client for Squad's RemoteBridge.
/// Handles connection lifecycle, ticket-based auth, keepalive pings,
/// and dispatches parsed RC events to the provider.
/// </summary>
public class SquadBridgeClient : IAsyncDisposable
{
    private readonly Uri _baseUri;
    private ClientWebSocket? _ws;
    private CancellationTokenSource? _cts;
    private Task? _receiveLoop;
    private Task? _pingLoop;
    private readonly IPluginLogger? _logger;
    private string? _sessionToken;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private bool _disposed;

    /// <summary>Maximum WebSocket message size (4 MB). Messages exceeding this are rejected.</summary>
    public const int MaxMessageSize = 4 * 1024 * 1024;

    public bool IsConnected => _ws?.State == WebSocketState.Open;
    public string? Repo { get; private set; }
    public string? Branch { get; private set; }
    public string? Machine { get; private set; }

    /// <summary>Fires when a parsed RC event is received.</summary>
    public event Action<RCEvent>? OnEvent;

    /// <summary>Fires when the connection is established.</summary>
    public event Action? OnConnected;

    /// <summary>Fires when the connection drops.</summary>
    public event Action<string>? OnDisconnected;

    public SquadBridgeClient(string host, int port, IPluginLogger? logger = null)
    {
        if (port is < 1 or > 65535)
            throw new ArgumentOutOfRangeException(nameof(port), port, "Port must be between 1 and 65535");
        _baseUri = new Uri($"http://{host}:{port}");
        _logger = logger;
    }

    /// <summary>
    /// Connect to the RemoteBridge. Acquires a one-time ticket via HTTP POST,
    /// then upgrades to WebSocket using the ticket.
    /// </summary>
    public async Task ConnectAsync(string sessionToken, CancellationToken ct = default)
    {
        if (_ws?.State == WebSocketState.Open)
            throw new InvalidOperationException("Already connected. Call DisconnectAsync first.");

        _sessionToken = sessionToken;
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        // Step 1: Exchange session token for a one-time WebSocket ticket
        var ticket = await AcquireTicketAsync(_cts.Token);

        // Step 2: Connect WebSocket with the ticket (prefer ticket over token-in-URL)
        _ws = new ClientWebSocket();
        var wsUri = new UriBuilder(_baseUri)
        {
            Scheme = _baseUri.Scheme == "https" ? "wss" : "ws",
            Path = "/",
            Query = ticket != null ? $"ticket={ticket}" : ""
        }.Uri;

        try
        {
            await _ws.ConnectAsync(wsUri, _cts.Token);
        }
        catch
        {
            _ws.Dispose();
            _ws = null;
            _cts.Dispose();
            _cts = null;
            throw;
        }

        _logger?.Info($"WebSocket connected to {_baseUri}");
        OnConnected?.Invoke();

        // Start receive and ping loops
        _receiveLoop = Task.Run(() => ReceiveLoopAsync(_cts.Token), _cts.Token);
        _pingLoop = Task.Run(() => PingLoopAsync(_cts.Token), _cts.Token);
    }

    private async Task<string?> AcquireTicketAsync(CancellationToken ct)
    {
        try
        {
            using var http = new HttpClient();
            http.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _sessionToken);

            using var response = await http.PostAsync(new Uri(_baseUri, "/api/auth/ticket"), null, ct);
            if (!response.IsSuccessStatusCode)
            {
                _logger?.Warning($"Ticket endpoint returned {response.StatusCode}, falling back to token auth");
                return null;
            }

            var json = await response.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("ticket", out var ticketProp))
                return ticketProp.GetString();

            _logger?.Warning("Ticket response missing 'ticket' property");
            return null;
        }
        catch (Exception ex)
        {
            _logger?.Warning($"Ticket acquisition failed, falling back to token auth: {ex.Message}");
            return null;
        }
    }

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        var buffer = new byte[16 * 1024];
        using var messageBuffer = new MemoryStream();

        try
        {
            while (!ct.IsCancellationRequested && _ws?.State == WebSocketState.Open)
            {
                var result = await _ws.ReceiveAsync(buffer, ct);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    _logger?.Info("Server sent close frame");
                    OnDisconnected?.Invoke("Server closed connection");
                    break;
                }

                messageBuffer.Write(buffer, 0, result.Count);

                // Enforce max message size to prevent OOM
                if (messageBuffer.Length > MaxMessageSize)
                {
                    _logger?.Warning($"Message exceeds {MaxMessageSize} bytes, dropping");
                    messageBuffer.SetLength(0);
                    // Drain remaining frames of this message
                    while (!result.EndOfMessage && !ct.IsCancellationRequested)
                        result = await _ws.ReceiveAsync(buffer, ct);
                    continue;
                }

                if (!result.EndOfMessage) continue;

                var text = Encoding.UTF8.GetString(messageBuffer.GetBuffer(), 0, (int)messageBuffer.Length);
                messageBuffer.SetLength(0);

                DispatchEvent(text);
            }
        }
        catch (OperationCanceledException) { /* normal shutdown */ }
        catch (WebSocketException ex)
        {
            _logger?.Warning($"WebSocket error: {ex.Message}");
            OnDisconnected?.Invoke(ex.Message);
        }
    }

    private void DispatchEvent(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("type", out var typeProp))
            {
                _logger?.Debug("RC event missing 'type' property");
                return;
            }
            var type = typeProp.GetString();

            RCEvent? evt = type switch
            {
                "status" => JsonSerializer.Deserialize<RCStatusEvent>(json, SquadProtocol.JsonOptions),
                "history" => JsonSerializer.Deserialize<RCHistoryEvent>(json, SquadProtocol.JsonOptions),
                "delta" => JsonSerializer.Deserialize<RCDeltaEvent>(json, SquadProtocol.JsonOptions),
                "complete" => JsonSerializer.Deserialize<RCCompleteEvent>(json, SquadProtocol.JsonOptions),
                "agents" => JsonSerializer.Deserialize<RCAgentsEvent>(json, SquadProtocol.JsonOptions),
                "tool_call" => JsonSerializer.Deserialize<RCToolCallEvent>(json, SquadProtocol.JsonOptions),
                "permission" => JsonSerializer.Deserialize<RCPermissionEvent>(json, SquadProtocol.JsonOptions),
                "usage" => JsonSerializer.Deserialize<RCUsageEvent>(json, SquadProtocol.JsonOptions),
                "error" => JsonSerializer.Deserialize<RCErrorEvent>(json, SquadProtocol.JsonOptions),
                "pong" => JsonSerializer.Deserialize<RCPongEvent>(json, SquadProtocol.JsonOptions),
                _ => null,
            };

            if (evt != null)
            {
                evt.Type = type!;
                OnEvent?.Invoke(evt);
            }
            else
            {
                _logger?.Debug($"Unknown RC event type: {type}");
            }
        }
        catch (Exception ex)
        {
            _logger?.Warning($"Failed to parse RC event: {ex.Message}");
        }
    }

    private async Task PingLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested && _ws?.State == WebSocketState.Open)
            {
                await Task.Delay(TimeSpan.FromSeconds(30), ct);
                await SendAsync(new RCPingCommand(), ct);
            }
        }
        catch (OperationCanceledException) { /* normal shutdown */ }
        catch (Exception ex)
        {
            _logger?.Warning($"Ping loop error: {ex.Message}");
        }
    }

    /// <summary>Send a prompt to the Squad coordinator.</summary>
    public Task SendPromptAsync(string text, CancellationToken ct = default) =>
        SendAsync(new RCPromptCommand { Text = text }, ct);

    /// <summary>Send a direct message to a specific Squad agent.</summary>
    public Task SendDirectAsync(string agentName, string text, CancellationToken ct = default) =>
        SendAsync(new RCDirectCommand { AgentName = agentName, Text = text }, ct);

    /// <summary>Send a slash command.</summary>
    public Task SendCommandAsync(string name, string[]? args = null, CancellationToken ct = default) =>
        SendAsync(new RCSlashCommand { Name = name, Args = args }, ct);

    /// <summary>Respond to a permission request.</summary>
    public Task SendPermissionResponseAsync(string id, bool approved, CancellationToken ct = default) =>
        SendAsync(new RCPermissionResponse { Id = id, Approved = approved }, ct);

    private async Task SendAsync<T>(T command, CancellationToken ct) where T : class
    {
        if (_ws?.State != WebSocketState.Open) return;
        var json = JsonSerializer.Serialize(command, SquadProtocol.JsonOptions);
        var bytes = Encoding.UTF8.GetBytes(json);

        await _sendLock.WaitAsync(ct);
        try
        {
            if (_ws?.State == WebSocketState.Open)
                await _ws.SendAsync(bytes, WebSocketMessageType.Text, true, ct);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    public async Task DisconnectAsync()
    {
        if (_disposed) return;

        _cts?.Cancel();

        // Await background loops to prevent dispose races
        if (_receiveLoop != null)
        {
            try { await _receiveLoop.WaitAsync(TimeSpan.FromSeconds(5)); }
            catch { /* timeout or cancellation — proceed */ }
            _receiveLoop = null;
        }
        if (_pingLoop != null)
        {
            try { await _pingLoop.WaitAsync(TimeSpan.FromSeconds(5)); }
            catch { /* timeout or cancellation — proceed */ }
            _pingLoop = null;
        }

        if (_ws?.State == WebSocketState.Open)
        {
            try
            {
                using var closeCts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "Client disconnect", closeCts.Token);
            }
            catch { /* best effort */ }
        }

        _ws?.Dispose();
        _ws = null;

        _cts?.Dispose();
        _cts = null;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        await DisconnectAsync();
        _sendLock.Dispose();
    }
}
