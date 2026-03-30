using System.Net;
using System.Net.WebSockets;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using PolyPilot.Models;
using PolyPilot.Services;

namespace PolyPilot.Tests;

/// <summary>
/// Tests for Fiesta pairing features: pairing string encode/decode,
/// ApprovePairRequestAsync TCS behavior on failure, and RequestPairAsync
/// with a malformed approval response (Approved=true but null BridgeUrl).
/// </summary>
public class FiestaPairingTests : IDisposable
{
    private readonly WsBridgeServer _bridgeServer;
    private readonly CopilotService _copilot;
    private readonly FiestaService _fiesta;

    public FiestaPairingTests()
    {
        _bridgeServer = new WsBridgeServer();
        // Pre-set the server password so EnsureServerPassword() never falls through to
        // ConnectionSettings.Load()/Save(), which would touch the real ~/.polypilot/settings.json.
        _bridgeServer.ServerPassword = "test-token-isolation";
        _copilot = new CopilotService(
            new StubChatDatabase(),
            new StubServerManager(),
            new StubWsBridgeClient(),
            new RepoManager(),
            new ServiceCollection().BuildServiceProvider(),
            new StubDemoService());
        _fiesta = new FiestaService(_copilot, _bridgeServer, new TailscaleService());
    }

    public void Dispose()
    {
        _fiesta.Dispose();
        _bridgeServer.Dispose();
    }

    // ---- Helpers ----

    private static string BuildPairingString(string url, string token, string hostname)
    {
        var payload = new FiestaPairingPayload { Url = url, Token = token, Hostname = hostname };
        var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        var b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(json))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
        return $"pp+{b64}";
    }

    private static int GetFreePort()
    {
        using var l = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    // ---- Test 1: Pairing string roundtrip ----

    [Fact]
    public void ParseAndLinkPairingString_Roundtrip_CorrectWorkerFields()
    {
        const string url = "http://192.168.1.50:4322";
        const string token = "test-token-abc123";
        const string hostname = "devbox-1";

        var pairingString = BuildPairingString(url, token, hostname);
        Assert.StartsWith("pp+", pairingString);

        var linked = _fiesta.ParseAndLinkPairingString(pairingString);

        Assert.Equal(url, linked.BridgeUrl);
        Assert.Equal(token, linked.Token);
        Assert.Equal(hostname, linked.Name);
        Assert.Single(_fiesta.LinkedWorkers);
    }

    [Fact]
    public void ParseAndLinkPairingString_InvalidPrefix_ThrowsFormatException()
    {
        Assert.Throws<FormatException>(() => _fiesta.ParseAndLinkPairingString("notvalid"));
        Assert.Throws<FormatException>(() => _fiesta.ParseAndLinkPairingString("pp+!!!notbase64!!!"));
    }

    [Fact]
    public void ParseAndLinkPairingString_MissingUrl_ThrowsFormatException()
    {
        // Build a pairing string that's valid base64 but has no URL field
        var payload = new FiestaPairingPayload { Url = "", Token = "tok", Hostname = "host" };
        var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        var b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(json)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var s = $"pp+{b64}";

        Assert.Throws<FormatException>(() => _fiesta.ParseAndLinkPairingString(s));
    }

    // ---- Test 2: ApprovePairRequestAsync return value + TCS behavior ----

    [Fact]
    public async Task ApprovePairRequestAsync_SendFails_ReturnsFalse()
    {
        const string requestId = "req-test-001";
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        // Inject a pending pair request with a WebSocket that reports Open state
        // but throws on SendAsync, simulating a race-condition socket failure.
        var faultySocket = new FaultyOpenWebSocket();
        var pending = new PendingPairRequest
        {
            RequestId = requestId,
            HostName = "test-host",
            HostInstanceId = "host-id",
            RemoteIp = "127.0.0.1",
            Socket = faultySocket,
            CompletionSource = tcs,
            ExpiresAt = DateTime.UtcNow.AddSeconds(60)
        };

        var dictField = typeof(FiestaService).GetField("_pendingPairRequests", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var dict = (Dictionary<string, PendingPairRequest>)dictField.GetValue(_fiesta)!;
        lock (dict) dict[requestId] = pending;

        var result = await _fiesta.ApprovePairRequestAsync(requestId);

        // Method returns false because SendAsync threw (approval message not delivered)
        Assert.False(result);
        // TCS is claimed true (approve won ownership) before the send attempt
        Assert.True(tcs.Task.IsCompleted);
        Assert.True(await tcs.Task);
    }

    [Fact]
    public async Task ApprovePairRequestAsync_UnknownRequestId_ReturnsFalse()
    {
        var result = await _fiesta.ApprovePairRequestAsync("nonexistent-id");
        Assert.False(result);
    }

    // ---- Test 3: RequestPairAsync with Approved=true but null BridgeUrl ----

    [Fact]
    public async Task RequestPairAsync_ApprovedWithNullBridgeUrl_ReturnsUnreachable()
    {
        var port = GetFreePort();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        // Stand up a minimal WebSocket server that responds with Approved=true but no BridgeUrl
        var serverTask = Task.Run(async () =>
        {
            var listener = new HttpListener();
            listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            listener.Start();
            try
            {
                var ctx = await listener.GetContextAsync().WaitAsync(cts.Token);
                if (!ctx.Request.IsWebSocketRequest) { ctx.Response.StatusCode = 400; ctx.Response.Close(); return; }

                var wsCtx = await ctx.AcceptWebSocketAsync(subProtocol: null);
                var ws = wsCtx.WebSocket;

                // Read (and discard) the pair request
                var buf = new byte[4096];
                await ws.ReceiveAsync(new ArraySegment<byte>(buf), cts.Token);

                // Send back Approved=true with no BridgeUrl / Token
                var response = BridgeMessage.Create(BridgeMessageTypes.FiestaPairResponse,
                    new FiestaPairResponsePayload
                    {
                        RequestId = "req-null-url",
                        Approved = true,
                        BridgeUrl = null,
                        Token = null,
                        WorkerName = "worker"
                    });
                var bytes = Encoding.UTF8.GetBytes(response.Serialize());
                await ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, cts.Token);

                // Best-effort close; client may have already closed
                try { await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", cts.Token); } catch { }
            }
            catch (OperationCanceledException) { /* test timed out */ }
            catch (Exception) { /* ignore server-side cleanup errors */ }
            finally
            {
                listener.Stop();
            }
        }, cts.Token);

        // Give the server a moment to bind
        await Task.Delay(50, cts.Token);

        var worker = new FiestaDiscoveredWorker
        {
            InstanceId = "remote-id",
            Hostname = "remote-box",
            BridgeUrl = $"http://127.0.0.1:{port}"
        };

        var countBefore = _fiesta.LinkedWorkers.Count;
        var result = await _fiesta.RequestPairAsync(worker, cts.Token);

        // An approved response with no BridgeUrl should be treated as Unreachable
        Assert.Equal(PairRequestResult.Unreachable, result);

        // No new worker should have been linked by this call
        Assert.Equal(countBefore, _fiesta.LinkedWorkers.Count);
        Assert.DoesNotContain(_fiesta.LinkedWorkers, w =>
            string.Equals(w.Hostname, "remote-box", StringComparison.OrdinalIgnoreCase) ||
            w.BridgeUrl.Contains($"127.0.0.1:{port}"));

        await serverTask;
    }

    // ---- Test 4: Concurrent approve + deny race — only one send occurs ----

    [Fact]
    public async Task ApprovePairRequestAsync_ConcurrentWithDeny_OnlyOneWins()
    {
        const string requestId = "req-race-001";
        var countingSocket = new CountingSendWebSocket(onSendAsync: (_, _) => Task.CompletedTask);

        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = new PendingPairRequest
        {
            RequestId = requestId,
            HostName = "race-host",
            HostInstanceId = "race-id",
            RemoteIp = "127.0.0.1",
            Socket = countingSocket,
            CompletionSource = tcs,
            ExpiresAt = DateTime.UtcNow.AddSeconds(60)
        };

        var dictField = typeof(FiestaService).GetField("_pendingPairRequests", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var dict = (Dictionary<string, PendingPairRequest>)dictField.GetValue(_fiesta)!;
        lock (dict) dict[requestId] = pending;

        // Race approve and deny concurrently — exactly one TrySetResult wins
        var approveTask = _fiesta.ApprovePairRequestAsync(requestId);
        var denyTask = _fiesta.DenyPairRequestAsync(requestId);
        await Task.WhenAll(approveTask, denyTask);

        // Exactly one send should have occurred (the winner sends, the loser skips)
        Assert.Equal(1, countingSocket.SendCount);
        // TCS should be resolved exactly once
        Assert.True(tcs.Task.IsCompleted);
        // The winner's result should match the TCS value
        var approveWon = await approveTask;
        Assert.Equal(approveWon, await tcs.Task);
    }

    // ---- Test 5: DenyPairRequestAsync sends exactly once, TCS resolves false ----

    [Fact]
    public async Task DenyPairRequestAsync_SendsOnce_TcsIsFalse()
    {
        const string requestId = "req-deny-order-001";
        var countingSocket = new CountingSendWebSocket(onSendAsync: (_, _) => Task.CompletedTask);

        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = new PendingPairRequest
        {
            RequestId = requestId,
            HostName = "deny-host",
            HostInstanceId = "deny-id",
            RemoteIp = "127.0.0.1",
            Socket = countingSocket,
            CompletionSource = tcs,
            ExpiresAt = DateTime.UtcNow.AddSeconds(60)
        };

        var dictField = typeof(FiestaService).GetField("_pendingPairRequests", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var dict = (Dictionary<string, PendingPairRequest>)dictField.GetValue(_fiesta)!;
        lock (dict) dict[requestId] = pending;

        await _fiesta.DenyPairRequestAsync(requestId);

        // Deny claimed TCS first (approve never tried)
        Assert.True(tcs.Task.IsCompleted);
        Assert.False(await tcs.Task);
        Assert.Equal(1, countingSocket.SendCount);
    }

    // ---- Test 6: EnsureServerPassword auto-generates when not pre-set ----

    [Fact]
    public async Task ApprovePairRequestAsync_AutoGeneratesPassword_WhenNotPreSet()
    {
        // Create a fresh bridge server with NO pre-set password so the auto-generate
        // path in EnsureServerPassword is exercised.
        // ConnectionSettings is already redirected to the test dir by TestSetup.Initialize(),
        // so Load()/Save() will NOT touch ~/.polypilot/settings.json.
        var freshBridge = new WsBridgeServer();
        Assert.True(string.IsNullOrWhiteSpace(freshBridge.ServerPassword),
            "Precondition: ServerPassword must be empty before the test");

        var freshFiesta = new FiestaService(_copilot, freshBridge, new TailscaleService());
        try
        {
            const string requestId = "req-autopass-001";
            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var socket = new CountingSendWebSocket(onSendAsync: (_, _) => Task.CompletedTask);
            var pending = new PendingPairRequest
            {
                RequestId = requestId,
                HostName = "auto-host",
                HostInstanceId = "auto-id",
                RemoteIp = "127.0.0.1",
                Socket = socket,
                CompletionSource = tcs,
                ExpiresAt = DateTime.UtcNow.AddSeconds(60)
            };

            var dictField = typeof(FiestaService).GetField("_pendingPairRequests",
                BindingFlags.NonPublic | BindingFlags.Instance)!;
            var dict = (Dictionary<string, PendingPairRequest>)dictField.GetValue(freshFiesta)!;
            lock (dict) dict[requestId] = pending;

            var result = await freshFiesta.ApprovePairRequestAsync(requestId);

            // Should succeed and have auto-generated a non-empty password
            Assert.True(result);
            Assert.False(string.IsNullOrWhiteSpace(freshBridge.ServerPassword),
                "EnsureServerPassword should have set a non-empty password on the bridge server");
            // Password should be URL-safe (no '+' or '/')
            Assert.DoesNotContain("+", freshBridge.ServerPassword);
            Assert.DoesNotContain("/", freshBridge.ServerPassword);
        }
        finally
        {
            freshFiesta.Dispose();
            freshBridge.Dispose();
        }
    }

    // ---- Test 7: MaxPendingPairRequests constant = 5 ----

    [Fact]
    public void MaxPendingPairRequests_ConstantIs5()
    {
        // Verify the limit was raised from 1 to 5 per the review recommendation.
        var field = typeof(FiestaService).GetField("MaxPendingPairRequests",
            BindingFlags.NonPublic | BindingFlags.Static)!;
        var value = (int)field.GetValue(null)!;
        Assert.Equal(5, value);
    }

    // ---- Test 8: HandleIncomingPairHandshakeAsync rejects when at capacity ----

    [Fact]
    public async Task HandleIncomingPairHandshake_AtCapacity_SendsDenialAndSkipsDict()
    {
        // Fill 5 slots directly (MaxPendingPairRequests = 5)
        var dictField = typeof(FiestaService).GetField("_pendingPairRequests",
            BindingFlags.NonPublic | BindingFlags.Instance)!;
        var dict = (Dictionary<string, PendingPairRequest>)dictField.GetValue(_fiesta)!;

        for (int i = 0; i < 5; i++)
        {
            var id = $"slot-full-{i}";
            lock (dict) dict[id] = new PendingPairRequest
            {
                RequestId = id,
                HostName = $"host-{i}",
                HostInstanceId = $"inst-{i}",
                RemoteIp = "127.0.0.1",
                Socket = new CountingSendWebSocket(onSendAsync: (_, _) => Task.CompletedTask),
                CompletionSource = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously),
                ExpiresAt = DateTime.UtcNow.AddSeconds(60)
            };
        }

        int countBefore;
        lock (dict) countBefore = dict.Count;
        Assert.Equal(5, countBefore);

        // Build a FiestaPairRequest message for the 6th connection
        const string overflowId = "slot-overflow";
        var pairRequestPayload = new FiestaPairRequestPayload
        {
            RequestId = overflowId,
            HostName = "overflow-host",
            HostInstanceId = "overflow-inst"
        };
        var pairMsg = BridgeMessage.Create(BridgeMessageTypes.FiestaPairRequest, pairRequestPayload);
        var msgBytes = System.Text.Encoding.UTF8.GetBytes(pairMsg.Serialize());

        // Create a WebSocket that returns the pair request message on first ReceiveAsync,
        // then captures whatever is sent back (should be Approved=false).
        byte[]? responseBytes = null;
        var responseSent = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var readCount = 0;
        var overflowSocket = new CountingSendWebSocket(onSendAsync: (buf, _) =>
        {
            responseBytes = buf.Array![buf.Offset..(buf.Offset + buf.Count)];
            responseSent.TrySetResult(true);
            return Task.CompletedTask;
        })
        {
            ReceiveAsyncOverride = (buffer, ct) =>
            {
                if (Interlocked.Increment(ref readCount) == 1)
                {
                    // Return the FiestaPairRequest message
                    msgBytes.CopyTo(buffer.Array!, buffer.Offset);
                    return Task.FromResult(new WebSocketReceiveResult(msgBytes.Length, WebSocketMessageType.Text, true));
                }
                // Subsequent reads: signal close
                return Task.FromResult(new WebSocketReceiveResult(0, WebSocketMessageType.Close, true));
            }
        };

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await _fiesta.HandleIncomingPairHandshakeAsync(overflowSocket, "127.0.0.1", cts.Token);

        // The overflow slot must NOT be in the pending dict
        int countAfter;
        lock (dict) countAfter = dict.Count;
        Assert.Equal(5, countAfter);
        lock (dict) Assert.False(dict.ContainsKey(overflowId), "Overflow request must not be in pending dict");

        // Must have sent a denial
        await Task.WhenAny(responseSent.Task, Task.Delay(3000));
        Assert.True(responseSent.Task.IsCompleted, "Overflow request should receive a denial response");
        Assert.NotNull(responseBytes);
        var json = System.Text.Encoding.UTF8.GetString(responseBytes!);
        var msg = JsonSerializer.Deserialize<BridgeMessage>(json,
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        Assert.Equal(BridgeMessageTypes.FiestaPairResponse, msg?.Type);
        var resp = msg?.GetPayload<FiestaPairResponsePayload>();
        Assert.NotNull(resp);
        Assert.False(resp!.Approved, "Overflow request must be denied");
    }

    // ---- Test 8b: MaxPendingPairRequestsPerIp constant = 2 ----

    [Fact]
    public void MaxPendingPairRequestsPerIp_ConstantIs2()
    {
        var field = typeof(FiestaService).GetField("MaxPendingPairRequestsPerIp",
            BindingFlags.NonPublic | BindingFlags.Static)!;
        Assert.NotNull(field);
        var value = (int)field.GetValue(null)!;
        Assert.Equal(2, value);
    }

    // ---- Test 8c: Per-IP rate limit — third request from same IP is denied ----

    [Fact]
    public async Task HandleIncomingPairHandshake_PerIpLimit_ThirdRequestFromSameIpDenied()
    {
        const string repeatIp = "10.0.0.42";
        var dictField = typeof(FiestaService).GetField("_pendingPairRequests",
            BindingFlags.NonPublic | BindingFlags.Instance)!;
        var dict = (Dictionary<string, PendingPairRequest>)dictField.GetValue(_fiesta)!;

        // Fill 2 slots with the same IP (at per-IP limit, but total < MaxPendingPairRequests)
        for (int i = 0; i < 2; i++)
        {
            var id = $"per-ip-slot-{i}";
            lock (dict) dict[id] = new PendingPairRequest
            {
                RequestId = id,
                HostName = $"host-{i}",
                HostInstanceId = $"inst-{i}",
                RemoteIp = repeatIp,
                Socket = new CountingSendWebSocket(onSendAsync: (_, _) => Task.CompletedTask),
                CompletionSource = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously),
                ExpiresAt = DateTime.UtcNow.AddSeconds(60)
            };
        }

        int countBefore;
        lock (dict) countBefore = dict.Count;
        Assert.Equal(2, countBefore);

        const string thirdId = "per-ip-overflow";
        var pairRequestPayload = new FiestaPairRequestPayload
        {
            RequestId = thirdId,
            HostName = "repeat-host",
            HostInstanceId = "repeat-inst"
        };
        var pairMsg = BridgeMessage.Create(BridgeMessageTypes.FiestaPairRequest, pairRequestPayload);
        var msgBytes = System.Text.Encoding.UTF8.GetBytes(pairMsg.Serialize());

        byte[]? responseBytes = null;
        var responseSent = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var readCount = 0;
        var overflowSocket = new CountingSendWebSocket(onSendAsync: (buf, _) =>
        {
            responseBytes = buf.Array![buf.Offset..(buf.Offset + buf.Count)];
            responseSent.TrySetResult(true);
            return Task.CompletedTask;
        })
        {
            ReceiveAsyncOverride = (buffer, ct) =>
            {
                if (Interlocked.Increment(ref readCount) == 1)
                {
                    msgBytes.CopyTo(buffer.Array!, buffer.Offset);
                    return Task.FromResult(new WebSocketReceiveResult(msgBytes.Length, WebSocketMessageType.Text, true));
                }
                return Task.FromResult(new WebSocketReceiveResult(0, WebSocketMessageType.Close, true));
            }
        };

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await _fiesta.HandleIncomingPairHandshakeAsync(overflowSocket, repeatIp, cts.Token);

        // Must not have added the third request
        int countAfter;
        lock (dict) countAfter = dict.Count;
        Assert.Equal(2, countAfter);
        lock (dict) Assert.False(dict.ContainsKey(thirdId), "Third request from same IP must not be in pending dict");

        // Must have sent a denial
        await Task.WhenAny(responseSent.Task, Task.Delay(3000));
        Assert.True(responseSent.Task.IsCompleted, "Third request from same IP should receive a denial");
        Assert.NotNull(responseBytes);
        var json = System.Text.Encoding.UTF8.GetString(responseBytes!);
        var msg = JsonSerializer.Deserialize<BridgeMessage>(json,
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        Assert.Equal(BridgeMessageTypes.FiestaPairResponse, msg?.Type);
        var resp = msg?.GetPayload<FiestaPairResponsePayload>();
        Assert.NotNull(resp);
        Assert.False(resp!.Approved, "Third request from same IP must be denied");
    }

    // ---- Test 9: DenyPairRequest when TCS already resolved (timeout path) ----

    [Fact]
    public async Task DenyPairRequestAsync_TcsAlreadyResolved_SkipsSend()
    {
        const string requestId = "req-already-resolved";
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        // Pre-resolve TCS to true (approve already won)
        tcs.TrySetResult(true);

        var socket = new CountingSendWebSocket(onSendAsync: (_, _) => Task.CompletedTask);
        var dictField = typeof(FiestaService).GetField("_pendingPairRequests",
            BindingFlags.NonPublic | BindingFlags.Instance)!;
        var dict = (Dictionary<string, PendingPairRequest>)dictField.GetValue(_fiesta)!;
        lock (dict) dict[requestId] = new PendingPairRequest
        {
            RequestId = requestId,
            HostName = "resolved-host",
            HostInstanceId = "resolved-id",
            RemoteIp = "127.0.0.1",
            Socket = socket,
            CompletionSource = tcs,
            ExpiresAt = DateTime.UtcNow.AddSeconds(60)
        };

        await _fiesta.DenyPairRequestAsync(requestId);

        // TCS was already resolved — deny's TrySetResult(false) lost, no send
        Assert.Equal(0, socket.SendCount);
    }

    

    /// <summary>
    /// A WebSocket that counts calls to SendAsync and optionally delegates to a custom action.
    /// ReceiveAsyncOverride can be set to control what ReadSingleMessageAsync receives.
    /// </summary>
    private sealed class CountingSendWebSocket : WebSocket
    {
        private readonly Func<ArraySegment<byte>, CancellationToken, Task> _onSendAsync;
        public int SendCount;

        /// <summary>When set, ReceiveAsync delegates to this instead of returning Close.</summary>
        public Func<ArraySegment<byte>, CancellationToken, Task<WebSocketReceiveResult>>? ReceiveAsyncOverride;

        public CountingSendWebSocket(Func<ArraySegment<byte>, CancellationToken, Task> onSendAsync)
            => _onSendAsync = onSendAsync;

        public override WebSocketState State => WebSocketState.Open;
        public override WebSocketCloseStatus? CloseStatus => null;
        public override string? CloseStatusDescription => null;
        public override string? SubProtocol => null;

        public override void Abort() { }
        public override Task CloseAsync(WebSocketCloseStatus c, string? d, CancellationToken ct) => Task.CompletedTask;
        public override Task CloseOutputAsync(WebSocketCloseStatus c, string? d, CancellationToken ct) => Task.CompletedTask;
        public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken ct)
            => ReceiveAsyncOverride?.Invoke(buffer, ct)
               ?? Task.FromResult(new WebSocketReceiveResult(0, WebSocketMessageType.Close, true));

        public override async Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType type, bool end, CancellationToken ct)
        {
            Interlocked.Increment(ref SendCount);
            await _onSendAsync(buffer, ct);
        }

        public override void Dispose() { }
    }

    /// <summary>
    /// A WebSocket that passes the State == Open guard but throws on SendAsync,
    /// simulating a socket that closes between the state check and the write.
    /// </summary>
    private sealed class FaultyOpenWebSocket : WebSocket
    {
        public override WebSocketState State => WebSocketState.Open;
        public override WebSocketCloseStatus? CloseStatus => null;
        public override string? CloseStatusDescription => null;
        public override string? SubProtocol => null;

        public override void Abort() { }

        public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken ct)
            => Task.CompletedTask;

        public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken ct)
            => Task.CompletedTask;

        public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken ct)
            => Task.FromResult(new WebSocketReceiveResult(0, WebSocketMessageType.Close, true));

        public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken ct)
            => throw new WebSocketException("Simulated send failure after state check");

        public override void Dispose() { }
    }
}
