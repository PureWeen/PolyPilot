using System.Text.Json;
using PolyPilot.Provider;
using PolyPilot.Provider.Squad;

namespace PolyPilot.Tests;

/// <summary>
/// Tests for the SquadSessionProvider plugin: protocol models, event handling,
/// provider interface compliance, and factory configuration.
/// </summary>
public class SquadProviderTests
{
    // ── Protocol Model Tests ─────────────────────────────────

    [Fact]
    public void ProtocolVersion_Is1_0()
    {
        Assert.Equal("1.0", SquadProtocol.ProtocolVersion);
    }

    [Fact]
    public void StatusEvent_Deserializes()
    {
        var json = """{"type":"status","version":"1.0","repo":"myrepo","branch":"main","machine":"laptop","squadDir":"/path/.squad","connectedAt":"2026-04-09T12:00:00Z"}""";
        var evt = JsonSerializer.Deserialize<RCStatusEvent>(json, SquadProtocol.JsonOptions);

        Assert.NotNull(evt);
        Assert.Equal("status", evt!.Type);
        Assert.Equal("1.0", evt.Version);
        Assert.Equal("myrepo", evt.Repo);
        Assert.Equal("main", evt.Branch);
        Assert.Equal("laptop", evt.Machine);
    }

    [Fact]
    public void HistoryEvent_Deserializes()
    {
        var json = """{"type":"history","messages":[{"id":"m1","role":"user","content":"hello","timestamp":"2026-04-09T12:00:00Z"},{"id":"m2","role":"agent","agentName":"worker-1","content":"hi back","timestamp":"2026-04-09T12:01:00Z"}]}""";
        var evt = JsonSerializer.Deserialize<RCHistoryEvent>(json, SquadProtocol.JsonOptions);

        Assert.NotNull(evt);
        Assert.Equal(2, evt!.Messages.Count);
        Assert.Equal("user", evt.Messages[0].Role);
        Assert.Equal("agent", evt.Messages[1].Role);
        Assert.Equal("worker-1", evt.Messages[1].AgentName);
    }

    [Fact]
    public void DeltaEvent_Deserializes()
    {
        var json = """{"type":"delta","sessionId":"s1","agentName":"worker-1","content":"chunk of text"}""";
        var evt = JsonSerializer.Deserialize<RCDeltaEvent>(json, SquadProtocol.JsonOptions);

        Assert.NotNull(evt);
        Assert.Equal("worker-1", evt!.AgentName);
        Assert.Equal("chunk of text", evt.Content);
    }

    [Fact]
    public void CompleteEvent_Deserializes()
    {
        var json = """{"type":"complete","message":{"id":"m1","role":"agent","agentName":"coordinator","content":"Done!","timestamp":"2026-04-09T12:00:00Z"}}""";
        var evt = JsonSerializer.Deserialize<RCCompleteEvent>(json, SquadProtocol.JsonOptions);

        Assert.NotNull(evt);
        Assert.Equal("coordinator", evt!.Message.AgentName);
        Assert.Equal("Done!", evt.Message.Content);
    }

    [Fact]
    public void AgentsEvent_Deserializes()
    {
        var json = """{"type":"agents","agents":[{"name":"coordinator","role":"orchestrator","status":"idle"},{"name":"worker-1","role":"coder","status":"working","charterPath":".squad/agents/worker-1/charter.md"}]}""";
        var evt = JsonSerializer.Deserialize<RCAgentsEvent>(json, SquadProtocol.JsonOptions);

        Assert.NotNull(evt);
        Assert.Equal(2, evt!.Agents.Count);
        Assert.Equal("coordinator", evt.Agents[0].Name);
        Assert.Equal("working", evt.Agents[1].Status);
    }

    [Fact]
    public void ToolCallEvent_Deserializes()
    {
        var json = """{"type":"tool_call","agentName":"worker-1","tool":"bash","args":{"command":"ls"},"status":"running"}""";
        var evt = JsonSerializer.Deserialize<RCToolCallEvent>(json, SquadProtocol.JsonOptions);

        Assert.NotNull(evt);
        Assert.Equal("bash", evt!.Tool);
        Assert.Equal("running", evt.Status);
        Assert.NotNull(evt.Args);
    }

    [Fact]
    public void PermissionEvent_Deserializes()
    {
        var json = """{"type":"permission","id":"perm-1","agentName":"worker-2","tool":"file_write","args":{"path":"/tmp/out.txt"},"description":"Write output file"}""";
        var evt = JsonSerializer.Deserialize<RCPermissionEvent>(json, SquadProtocol.JsonOptions);

        Assert.NotNull(evt);
        Assert.Equal("perm-1", evt!.Id);
        Assert.Equal("worker-2", evt.AgentName);
        Assert.Equal("file_write", evt.Tool);
        Assert.Equal("Write output file", evt.Description);
    }

    [Fact]
    public void UsageEvent_Deserializes()
    {
        var json = """{"type":"usage","model":"claude-opus-4.6","inputTokens":1500,"outputTokens":500,"cost":0.05}""";
        var evt = JsonSerializer.Deserialize<RCUsageEvent>(json, SquadProtocol.JsonOptions);

        Assert.NotNull(evt);
        Assert.Equal("claude-opus-4.6", evt!.Model);
        Assert.Equal(1500, evt.InputTokens);
        Assert.Equal(500, evt.OutputTokens);
        Assert.Equal(0.05, evt.Cost);
    }

    [Fact]
    public void ErrorEvent_Deserializes()
    {
        var json = """{"type":"error","message":"Something went wrong","agentName":"worker-1"}""";
        var evt = JsonSerializer.Deserialize<RCErrorEvent>(json, SquadProtocol.JsonOptions);

        Assert.NotNull(evt);
        Assert.Equal("Something went wrong", evt!.Message);
        Assert.Equal("worker-1", evt.AgentName);
    }

    [Fact]
    public void ErrorEvent_AgentNameOptional()
    {
        var json = """{"type":"error","message":"Global error"}""";
        var evt = JsonSerializer.Deserialize<RCErrorEvent>(json, SquadProtocol.JsonOptions);

        Assert.NotNull(evt);
        Assert.Null(evt!.AgentName);
    }

    // ── Client Command Serialization ─────────────────────────

    [Fact]
    public void PromptCommand_Serializes()
    {
        var cmd = new RCPromptCommand { Text = "Build the app" };
        var json = JsonSerializer.Serialize(cmd, SquadProtocol.JsonOptions);

        Assert.Contains("\"type\":\"prompt\"", json);
        Assert.Contains("\"text\":\"Build the app\"", json);
    }

    [Fact]
    public void DirectCommand_Serializes()
    {
        var cmd = new RCDirectCommand { AgentName = "worker-1", Text = "Run tests" };
        var json = JsonSerializer.Serialize(cmd, SquadProtocol.JsonOptions);

        Assert.Contains("\"type\":\"direct\"", json);
        Assert.Contains("\"agentName\":\"worker-1\"", json);
    }

    [Fact]
    public void SlashCommand_Serializes()
    {
        var cmd = new RCSlashCommand { Name = "status" };
        var json = JsonSerializer.Serialize(cmd, SquadProtocol.JsonOptions);

        Assert.Contains("\"type\":\"command\"", json);
        Assert.Contains("\"name\":\"status\"", json);
    }

    [Fact]
    public void PermissionResponse_Serializes()
    {
        var cmd = new RCPermissionResponse { Id = "perm-1", Approved = true };
        var json = JsonSerializer.Serialize(cmd, SquadProtocol.JsonOptions);

        Assert.Contains("\"type\":\"permission_response\"", json);
        Assert.Contains("\"id\":\"perm-1\"", json);
        Assert.Contains("\"approved\":true", json);
    }

    [Fact]
    public void PingCommand_Serializes()
    {
        var cmd = new RCPingCommand();
        var json = JsonSerializer.Serialize(cmd, SquadProtocol.JsonOptions);

        Assert.Contains("\"type\":\"ping\"", json);
    }

    // ── Provider Interface Compliance ────────────────────────

    [Fact]
    public void SquadSessionProvider_ImplementsIPermissionAwareProvider()
    {
        Assert.True(typeof(IPermissionAwareProvider).IsAssignableFrom(typeof(SquadSessionProvider)));
    }

    [Fact]
    public void SquadSessionProvider_ImplementsISessionProvider()
    {
        Assert.True(typeof(ISessionProvider).IsAssignableFrom(typeof(SquadSessionProvider)));
    }

    [Fact]
    public void SquadSessionProvider_ImplementsIAsyncDisposable()
    {
        Assert.True(typeof(IAsyncDisposable).IsAssignableFrom(typeof(SquadSessionProvider)));
    }

    [Fact]
    public void SquadSessionProvider_BrandingProperties()
    {
        var provider = new SquadSessionProvider("localhost", 4242, "test-token");

        Assert.Equal("squad", provider.ProviderId);
        Assert.Equal("Squad", provider.DisplayName);
        Assert.Equal("🫡", provider.Icon);
        Assert.Equal("#6366f1", provider.AccentColor);
        Assert.Equal("🫡 Squad", provider.GroupName);
        Assert.Equal("Squad Coordinator", provider.LeaderDisplayName);
    }

    [Fact]
    public void SquadSessionProvider_InitialState()
    {
        var provider = new SquadSessionProvider("localhost", 4242, "test-token");

        Assert.False(provider.IsInitialized);
        Assert.False(provider.IsInitializing);
        Assert.False(provider.IsProcessing);
        Assert.Empty(provider.History);
        Assert.Empty(provider.GetMembers());
        Assert.Empty(provider.GetPendingPermissions());
    }

    [Fact]
    public void SquadSessionProvider_HasCustomActions()
    {
        var provider = new SquadSessionProvider("localhost", 4242, "test-token");
        var actions = provider.GetActions();

        Assert.Equal(4, actions.Count);
        Assert.Contains(actions, a => a.Id == "status");
        Assert.Contains(actions, a => a.Id == "nap");
        Assert.Contains(actions, a => a.Id == "cast");
        Assert.Contains(actions, a => a.Id == "economy");
    }

    [Fact]
    public async Task SquadSessionProvider_SendMessageThrowsWhenNotConnected()
    {
        var provider = new SquadSessionProvider("localhost", 4242, "test-token");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => provider.SendMessageAsync("hello"));
        Assert.Contains("Not connected", ex.Message);
    }

    [Fact]
    public async Task SquadSessionProvider_SendToMemberThrowsWhenNotConnected()
    {
        var provider = new SquadSessionProvider("localhost", 4242, "test-token");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => provider.SendToMemberAsync("worker-1", "hello"));
        Assert.Contains("Not connected", ex.Message);
    }

    [Fact]
    public async Task SquadSessionProvider_ExecuteActionReturnsMessageWhenNotConnected()
    {
        var provider = new SquadSessionProvider("localhost", 4242, "test-token");

        var result = await provider.ExecuteActionAsync("status");
        Assert.Equal("Not connected to Squad", result);
    }

    [Fact]
    public async Task SquadSessionProvider_ExecuteActionReturnsNotConnectedForUnknownAction()
    {
        var provider = new SquadSessionProvider("localhost", 4242, "test-token");

        // When not connected, all actions return "Not connected to Squad"
        var result = await provider.ExecuteActionAsync("unknown-action");
        Assert.Equal("Not connected to Squad", result);
    }

    [Fact]
    public async Task SquadSessionProvider_DisposeIsIdempotent()
    {
        var provider = new SquadSessionProvider("localhost", 4242, "test-token");

        await provider.DisposeAsync();
        await provider.DisposeAsync(); // should not throw
    }

    // ── Factory Configuration Tests ──────────────────────────

    [Fact]
    public void SquadProviderFactory_ImplementsISessionProviderFactory()
    {
        Assert.True(typeof(ISessionProviderFactory).IsAssignableFrom(typeof(SquadProviderFactory)));
    }

    [Fact]
    public void SquadProviderFactory_HasParameterlessConstructor()
    {
        var factory = new SquadProviderFactory();
        Assert.NotNull(factory);
    }

    // ── Bridge Client Tests ──────────────────────────────────

    [Fact]
    public void SquadBridgeClient_NotConnectedByDefault()
    {
        var client = new SquadBridgeClient("localhost", 4242);
        Assert.False(client.IsConnected);
    }

    [Fact]
    public async Task SquadBridgeClient_DisposeIsIdempotent()
    {
        var client = new SquadBridgeClient("localhost", 4242);
        await client.DisposeAsync();
        await client.DisposeAsync(); // should not throw
    }

    // ── RCMessage Model Tests ────────────────────────────────

    [Fact]
    public void RCMessage_ToolCallsOptional()
    {
        var json = """{"id":"m1","role":"user","content":"hello","timestamp":"2026-04-09"}""";
        var msg = JsonSerializer.Deserialize<RCMessage>(json, SquadProtocol.JsonOptions);

        Assert.NotNull(msg);
        Assert.Null(msg!.ToolCalls);
    }

    [Fact]
    public void RCMessage_WithToolCalls()
    {
        var json = """{"id":"m1","role":"agent","content":"done","timestamp":"2026-04-09","toolCalls":[{"tool":"bash","args":{"command":"ls"},"status":"completed","result":"file1\nfile2"}]}""";
        var msg = JsonSerializer.Deserialize<RCMessage>(json, SquadProtocol.JsonOptions);

        Assert.NotNull(msg);
        Assert.NotNull(msg!.ToolCalls);
        Assert.Single(msg.ToolCalls!);
        Assert.Equal("bash", msg.ToolCalls![0].Tool);
        Assert.Equal("completed", msg.ToolCalls[0].Status);
    }

    // ── Protocol Round-Trip Tests ────────────────────────────

    [Fact]
    public void PromptCommand_RoundTrips()
    {
        var cmd = new RCPromptCommand { Text = "Build the app" };
        var json = JsonSerializer.Serialize(cmd, SquadProtocol.JsonOptions);
        var parsed = JsonSerializer.Deserialize<RCPromptCommand>(json, SquadProtocol.JsonOptions);

        Assert.Equal("prompt", parsed!.Type);
        Assert.Equal("Build the app", parsed.Text);
    }

    [Fact]
    public void PermissionResponse_RoundTrips()
    {
        var cmd = new RCPermissionResponse { Id = "perm-42", Approved = false };
        var json = JsonSerializer.Serialize(cmd, SquadProtocol.JsonOptions);
        var parsed = JsonSerializer.Deserialize<RCPermissionResponse>(json, SquadProtocol.JsonOptions);

        Assert.Equal("permission_response", parsed!.Type);
        Assert.Equal("perm-42", parsed.Id);
        Assert.False(parsed.Approved);
    }
}
