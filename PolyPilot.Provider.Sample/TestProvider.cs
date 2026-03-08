using Microsoft.Extensions.DependencyInjection;
using PolyPilot.Provider;

namespace PolyPilot.Provider.Sample;

public class TestProviderFactory : ISessionProviderFactory
{
    public void ConfigureServices(IServiceCollection services, string pluginDirectory)
    {
        Console.WriteLine($"[TestPlugin] ConfigureServices called! pluginDirectory={pluginDirectory}");
        services.AddSingleton<ISessionProvider>(sp =>
        {
            Console.WriteLine("[TestPlugin] ISessionProvider resolved from DI");
            return new TestProvider();
        });
    }
}

public class TestProvider : ISessionProvider
{
    public string ProviderId => "test-plugin";
    public string DisplayName => "Test Plugin";
    public string Icon => "🧪";
    public string AccentColor => "#e74c3c";
    public string GroupName => "🧪 Test Plugin";
    public string GroupDescription => "A test plugin to verify the plugin system works";

    public bool IsInitialized { get; private set; }
    public bool IsInitializing { get; private set; }

    public string LeaderDisplayName => "Test Leader";
    public string LeaderIcon => "🧪";
    public bool IsProcessing => false;
    public IReadOnlyList<ProviderChatMessage> History => _history;

    private readonly List<ProviderChatMessage> _history = new();
    private readonly List<ProviderMember> _members = new()
    {
        new() { Id = "worker-1", Name = "Test Worker Alpha", Role = "tester", Icon = "🔬" },
        new() { Id = "worker-2", Name = "Test Worker Beta", Role = "validator", Icon = "✅" }
    };

    // Leader events
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

    // Member events
    public event Action<string, string>? OnMemberContentReceived;
    public event Action<string>? OnMemberTurnStart;
    public event Action<string>? OnMemberTurnEnd;
    public event Action<string, string>? OnMemberError;

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        IsInitializing = true;
        Console.WriteLine("[TestPlugin] InitializeAsync called — plugin is initializing...");
        await Task.Delay(100, ct);
        IsInitialized = true;
        IsInitializing = false;
        Console.WriteLine("[TestPlugin] InitializeAsync complete — plugin is ready!");
    }

    public Task ShutdownAsync()
    {
        Console.WriteLine("[TestPlugin] ShutdownAsync called — plugin shutting down");
        return Task.CompletedTask;
    }

    public Task<string> SendMessageAsync(string message, CancellationToken ct = default)
    {
        Console.WriteLine($"[TestPlugin] Leader received: {message}");
        var response = $"🧪 Leader echo: \"{message}\"\n\nThe plugin system is working! Try clicking on a worker session and sending a message there too.";

        OnTurnStart?.Invoke();
        OnContentReceived?.Invoke(response);
        OnTurnEnd?.Invoke();

        return Task.FromResult(response);
    }

    public Task<string> SendToMemberAsync(string memberId, string message, CancellationToken ct = default)
    {
        Console.WriteLine($"[TestPlugin] Member '{memberId}' received: {message}");
        var member = _members.FirstOrDefault(m => m.Id == memberId);
        var memberName = member?.Name ?? memberId;
        var memberIcon = member?.Icon ?? "👤";

        var response = $"{memberIcon} **{memberName}** responding:\n\n> {message}\n\nI processed your request! My role is **{member?.Role ?? "unknown"}**.";

        OnMemberTurnStart?.Invoke(memberId);
        OnMemberContentReceived?.Invoke(memberId, response);
        OnMemberTurnEnd?.Invoke(memberId);

        return Task.FromResult(response);
    }

    public IReadOnlyList<ProviderMember> GetMembers() => _members;

    public IReadOnlyList<ProviderAction> GetActions() => new List<ProviderAction>
    {
        new() { Id = "ping", Label = "🏓 Ping", Tooltip = "Test connectivity" },
        new() { Id = "status", Label = "📊 Status", Tooltip = "Show plugin status" }
    };

    public Task<string?> ExecuteActionAsync(string actionId, CancellationToken ct = default)
    {
        Console.WriteLine($"[TestPlugin] Action '{actionId}' executed");
        return Task.FromResult<string?>(actionId switch
        {
            "ping" => "🏓 Pong! Plugin is alive and responding.",
            "status" => $"✅ Initialized={IsInitialized}, Messages={_history.Count}, Workers={_members.Count}",
            _ => null
        });
    }
}
