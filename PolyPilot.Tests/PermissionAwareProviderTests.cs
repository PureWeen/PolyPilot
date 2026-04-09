using PolyPilot.Provider;

namespace PolyPilot.Tests;

/// <summary>
/// Tests for IPermissionAwareProvider interface, ProviderPermissionRequest model,
/// and the CopilotService.Providers.cs permission wiring.
/// </summary>
public class PermissionAwareProviderTests
{
    // --- ProviderPermissionRequest model ---

    [Fact]
    public void ProviderPermissionRequest_SetsRequiredProperties()
    {
        var req = new ProviderPermissionRequest
        {
            Id = "perm-1",
            ToolName = "file_write",
            AgentId = "agent-1",
            AgentName = "Security Reviewer",
            Description = "Write to /tmp/output.txt",
            Arguments = "{\"path\": \"/tmp/output.txt\"}",
        };

        Assert.Equal("perm-1", req.Id);
        Assert.Equal("file_write", req.ToolName);
        Assert.Equal("agent-1", req.AgentId);
        Assert.Equal("Security Reviewer", req.AgentName);
        Assert.Equal("Write to /tmp/output.txt", req.Description);
        Assert.NotEqual(default, req.RequestedAt);
    }

    [Fact]
    public void ProviderPermissionRequest_DefaultsRequestedAt()
    {
        var before = DateTime.UtcNow;
        var req = new ProviderPermissionRequest { Id = "perm-2", ToolName = "bash" };
        var after = DateTime.UtcNow;

        Assert.InRange(req.RequestedAt, before, after);
    }

    [Fact]
    public void ProviderPermissionRequest_OptionalFieldsAreNullByDefault()
    {
        var req = new ProviderPermissionRequest { Id = "perm-3", ToolName = "exec" };

        Assert.Null(req.AgentId);
        Assert.Null(req.AgentName);
        Assert.Null(req.Description);
        Assert.Null(req.Arguments);
    }

    // --- IPermissionAwareProvider interface ---

    [Fact]
    public void IPermissionAwareProvider_ExtendsISessionProvider()
    {
        // The interface should be a subtype of ISessionProvider
        Assert.True(typeof(ISessionProvider).IsAssignableFrom(typeof(IPermissionAwareProvider)));
    }

    [Fact]
    public void IPermissionAwareProvider_HasRequiredMembers()
    {
        var type = typeof(IPermissionAwareProvider);

        // Events
        Assert.NotNull(type.GetEvent("OnPermissionRequested"));
        Assert.NotNull(type.GetEvent("OnPermissionResolved"));

        // Methods
        Assert.NotNull(type.GetMethod("GetPendingPermissions"));
        Assert.NotNull(type.GetMethod("ApprovePermissionAsync"));
        Assert.NotNull(type.GetMethod("DenyPermissionAsync"));
    }

    [Fact]
    public void MockPermissionProvider_CanFireAndHandlePermissions()
    {
        var provider = new MockPermissionProvider();
        ProviderPermissionRequest? received = null;
        string? resolvedId = null;

        provider.OnPermissionRequested += req => received = req;
        provider.OnPermissionResolved += id => resolvedId = id;

        // Simulate a permission request
        provider.SimulatePermissionRequest("perm-42", "bash", "Execute shell command");

        Assert.NotNull(received);
        Assert.Equal("perm-42", received!.Id);
        Assert.Equal("bash", received.ToolName);

        // Approve it
        provider.ApprovePermissionAsync("perm-42").Wait();
        Assert.Equal("perm-42", resolvedId);
        Assert.Empty(provider.GetPendingPermissions());
    }

    [Fact]
    public void MockPermissionProvider_DenyRemovesFromPending()
    {
        var provider = new MockPermissionProvider();
        provider.SimulatePermissionRequest("perm-99", "file_write", "Write file");

        Assert.Single(provider.GetPendingPermissions());

        provider.DenyPermissionAsync("perm-99").Wait();
        Assert.Empty(provider.GetPendingPermissions());
    }

    [Fact]
    public void MockPermissionProvider_MultiplePendingPermissions()
    {
        var provider = new MockPermissionProvider();
        provider.SimulatePermissionRequest("perm-1", "bash", "Shell");
        provider.SimulatePermissionRequest("perm-2", "file_write", "Write");
        provider.SimulatePermissionRequest("perm-3", "http_fetch", "Fetch");

        Assert.Equal(3, provider.GetPendingPermissions().Count);

        provider.ApprovePermissionAsync("perm-2").Wait();
        Assert.Equal(2, provider.GetPendingPermissions().Count);
        Assert.DoesNotContain(provider.GetPendingPermissions(), r => r.Id == "perm-2");
    }

    // --- Structural: CopilotService.Providers.cs wiring ---

    [Fact]
    public void CopilotServiceProviders_HasPermissionWiringCode()
    {
        var source = File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "PolyPilot",
            "Services", "CopilotService.Providers.cs"));

        Assert.Contains("IPermissionAwareProvider", source);
        Assert.Contains("WirePermissionEvents", source);
        Assert.Contains("ApproveProviderPermissionAsync", source);
        Assert.Contains("DenyProviderPermissionAsync", source);
        Assert.Contains("GetPendingPermissions", source);
        Assert.Contains("IsPermissionAwareProvider", source);
    }

    // --- Mock provider for testing ---

    private class MockPermissionProvider : IPermissionAwareProvider
    {
        private readonly List<ProviderPermissionRequest> _pending = new();

        // ISessionProvider required members
        public string ProviderId => "mock-perm";
        public string DisplayName => "Mock Permission Provider";
        public string Icon => "🔐";
        public string AccentColor => "#ff0000";
        public string GroupName => "Mock Permissions";
        public string GroupDescription => "Test";
        public bool IsInitialized => true;
        public bool IsInitializing => false;
        public string LeaderDisplayName => "Mock Leader";
        public string LeaderIcon => "🔐";
        public bool IsProcessing => false;
        public IReadOnlyList<ProviderChatMessage> History => [];
        public Task InitializeAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task ShutdownAsync() => Task.CompletedTask;
        public Task<string> SendMessageAsync(string message, CancellationToken ct = default) => Task.FromResult("ok");
        public IReadOnlyList<ProviderMember> GetMembers() => [];

        // Events (ISessionProvider)
        public event Action? OnMembersChanged;
        public event Action<string>? OnContentReceived;
        public event Action<string, string>? OnReasoningReceived;
        public event Action<string>? OnReasoningComplete;
        public event Action<string, string, string?>? OnToolStarted;
        public event Action<string, string, bool>? OnToolCompleted;
        public event Action<string>? OnIntentChanged;
        public event Action? OnTurnStart;
        public event Action? OnTurnEnd;
        public event Action<string>? OnError;
        public event Action? OnStateChanged;
        public event Action<string, string>? OnMemberContentReceived;
        public event Action<string>? OnMemberTurnStart;
        public event Action<string>? OnMemberTurnEnd;
        public event Action<string, string>? OnMemberError;

        // IPermissionAwareProvider
        public event Action<ProviderPermissionRequest>? OnPermissionRequested;
        public event Action<string>? OnPermissionResolved;

        public IReadOnlyList<ProviderPermissionRequest> GetPendingPermissions() => _pending.AsReadOnly();

        public Task ApprovePermissionAsync(string permissionId, CancellationToken ct = default)
        {
            _pending.RemoveAll(r => r.Id == permissionId);
            OnPermissionResolved?.Invoke(permissionId);
            return Task.CompletedTask;
        }

        public Task DenyPermissionAsync(string permissionId, CancellationToken ct = default)
        {
            _pending.RemoveAll(r => r.Id == permissionId);
            OnPermissionResolved?.Invoke(permissionId);
            return Task.CompletedTask;
        }

        public void SimulatePermissionRequest(string id, string toolName, string description)
        {
            var req = new ProviderPermissionRequest
            {
                Id = id,
                ToolName = toolName,
                Description = description,
            };
            _pending.Add(req);
            OnPermissionRequested?.Invoke(req);
        }

        // Suppress unused event warnings
        private void SuppressWarnings()
        {
            OnMembersChanged?.Invoke();
            OnContentReceived?.Invoke("");
            OnReasoningReceived?.Invoke("", "");
            OnReasoningComplete?.Invoke("");
            OnToolStarted?.Invoke("", "", null);
            OnToolCompleted?.Invoke("", "", true);
            OnIntentChanged?.Invoke("");
            OnTurnStart?.Invoke();
            OnTurnEnd?.Invoke();
            OnError?.Invoke("");
            OnStateChanged?.Invoke();
            OnMemberContentReceived?.Invoke("", "");
            OnMemberTurnStart?.Invoke("");
            OnMemberTurnEnd?.Invoke("");
            OnMemberError?.Invoke("", "");
        }
    }
}
