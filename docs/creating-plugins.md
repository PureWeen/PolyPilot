# Creating PolyPilot Plugins

PolyPilot supports a generic plugin system that lets external session engines integrate without any PolyPilot-specific code changes. A plugin provides its own branding, lifecycle, messaging, and streaming events through the stable `ISessionProvider` contract.

## Quick Start

### 1. Create a .NET class library

```bash
dotnet new classlib -n MyPlugin -f net10.0
cd MyPlugin
dotnet add reference ../PolyPilot.Provider.Abstractions/PolyPilot.Provider.Abstractions.csproj
```

> When the abstractions package is published to NuGet, use `dotnet add package PolyPilot.Provider.Abstractions` instead.

### 2. Implement the factory

The plugin loader discovers your plugin by scanning for types that implement `ISessionProviderFactory`:

```csharp
using Microsoft.Extensions.DependencyInjection;
using PolyPilot.Provider;

public class MyProviderFactory : ISessionProviderFactory
{
    public void ConfigureServices(IServiceCollection services, string pluginDirectory)
    {
        // pluginDirectory is the folder containing your DLL — use it
        // to load config files, models, or other resources.
        services.AddSingleton<ISessionProvider, MyProvider>();
    }
}
```

### 3. Implement the provider

```csharp
using PolyPilot.Provider;

public class MyProvider : ISessionProvider
{
    // ── Branding ──
    public string ProviderId => "my-provider";
    public string DisplayName => "My Provider";
    public string Icon => "🔮";
    public string AccentColor => "#8b5cf6";
    public string GroupName => "🔮 My Provider";
    public string GroupDescription => "Description shown in group tooltip";

    // ── Leader session ──
    public string LeaderDisplayName => "My Leader";
    public string LeaderIcon => "🔮";
    public bool IsProcessing => false;
    public IReadOnlyList<ProviderChatMessage> History => _history;

    // ── State ──
    public bool IsInitialized { get; private set; }
    public bool IsInitializing { get; private set; }

    private readonly List<ProviderChatMessage> _history = new();

    // ── Events (leader) ──
    public event Action? OnMembersChanged;
    public event Action<string>? OnContentReceived;        // content
    public event Action<string, string>? OnReasoningReceived; // reasoningId, content
    public event Action<string>? OnReasoningComplete;       // reasoningId
    public event Action<string, string, string?>? OnToolStarted;  // callId, toolName, intent
    public event Action<string, string, bool>? OnToolCompleted;   // callId, toolName, success
    public event Action<string>? OnIntentChanged;           // intent
    public event Action? OnTurnStart;
    public event Action? OnTurnEnd;
    public event Action<string>? OnError;
    public event Action? OnStateChanged;

    // ── Events (member-scoped) ──
    public event Action<string, string>? OnMemberContentReceived; // memberId, content
    public event Action<string>? OnMemberTurnStart;                // memberId
    public event Action<string>? OnMemberTurnEnd;                  // memberId
    public event Action<string, string>? OnMemberError;            // memberId, error

    // ── Lifecycle ──
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        IsInitializing = true;
        // Connect to your backend, load config, etc.
        await Task.Delay(100, ct);
        IsInitialized = true;
        IsInitializing = false;
    }

    public Task ShutdownAsync()
    {
        // Clean up connections, save state
        return Task.CompletedTask;
    }

    // ── Messaging ──
    public Task<string> SendMessageAsync(string message, CancellationToken ct = default)
    {
        // Handle messages sent directly to the leader session.
        // Fire events to stream the response:
        OnTurnStart?.Invoke();
        OnContentReceived?.Invoke("Response from leader");
        OnTurnEnd?.Invoke();
        return Task.FromResult("Response from leader");
    }

    public Task<string> SendToMemberAsync(string memberId, string message, CancellationToken ct = default)
    {
        // Handle messages sent to a specific member session.
        OnMemberTurnStart?.Invoke(memberId);
        OnMemberContentReceived?.Invoke(memberId, $"Response from {memberId}");
        OnMemberTurnEnd?.Invoke(memberId);
        return Task.FromResult($"Response from {memberId}");
    }

    // ── Members ──
    public IReadOnlyList<ProviderMember> GetMembers() => new List<ProviderMember>
    {
        new() { Id = "agent-1", Name = "Agent One", Role = "researcher", Icon = "🔬" },
        new() { Id = "agent-2", Name = "Agent Two", Role = "writer", Icon = "✍️" }
    };

    // ── Actions ──
    public IReadOnlyList<ProviderAction> GetActions() => new List<ProviderAction>
    {
        new() { Id = "ping", Label = "🏓 Ping", Tooltip = "Test connectivity" }
    };

    public Task<string?> ExecuteActionAsync(string actionId, CancellationToken ct = default)
    {
        return Task.FromResult<string?>(actionId switch
        {
            "ping" => "Pong!",
            _ => null
        });
    }
}
```

### 4. Build and deploy

```bash
dotnet build
# Copy the DLL to the plugins directory
mkdir -p ~/.polypilot/plugins/myplugin
cp bin/Debug/net10.0/MyPlugin.dll ~/.polypilot/plugins/myplugin/
```

### 5. Approve in PolyPilot

1. Open PolyPilot → Settings → Plugins
2. Your plugin appears as "Discovered" with its SHA-256 hash
3. Click **Enable** to approve and load it
4. The plugin group appears in the sidebar immediately

## Architecture

### Session naming

PolyPilot creates sessions using this naming convention:

| Session | Name pattern | Example |
|---------|-------------|---------|
| Leader | `__{providerId}__` | `__my-provider__` |
| Member | `__{providerId}_{memberId}__` | `__my-provider_agent-1__` |
| Group | `__provider_{providerId}__` | `__provider_my-provider__` |

### Event flow

When a user sends a message, the flow depends on which session is selected:

1. **Main chat input (leader selected)** → `SendPromptAsync` → `SendToProviderAsync` → `provider.SendMessageAsync(message)`
2. **Main chat input (member selected)** → `SendPromptAsync` → `SendToProviderAsync` → `provider.SendToMemberAsync(memberId, message)`

Your provider fires events to stream responses back:

```
OnTurnStart → OnContentReceived (1..n) → OnTurnEnd
```

Content received via `OnContentReceived` accumulates in a `StringBuilder`. On `OnTurnEnd`, PolyPilot flushes the accumulated content to the session's chat history as an assistant message.

For member sessions, use the member-scoped events:

```
OnMemberTurnStart(memberId) → OnMemberContentReceived(memberId, content) → OnMemberTurnEnd(memberId)
```

### Actions

Actions appear as buttons in the group header. They execute quick commands (like "Ping" or "Status") and post the result as a message in the leader's chat history.

### Dynamic members

Call `OnMembersChanged` when your member list changes. PolyPilot will call `GetMembers()` and create/remove session entries accordingly.

## Security

- Plugins are loaded from `~/.polypilot/plugins/{name}/` only
- Each DLL's SHA-256 hash is computed at discovery and verified against the approved hash
- If a DLL changes (update, rebuild), the user must re-approve it in Settings
- Plugins are **desktop-only** — mobile devices see provider sessions through the WebSocket bridge automatically
- Plugin assemblies run in an isolated `AssemblyLoadContext` that shares only host assemblies (Abstractions, DI, SDK)

## Tips

- **Async responses**: For real LLM backends, fire `OnTurnStart` immediately, stream `OnContentReceived` as tokens arrive, and fire `OnTurnEnd` when complete. This gives the user real-time streaming feedback.
- **Error handling**: Fire `OnError(message)` to display errors in the session. PolyPilot will clear the processing state automatically.
- **Tool activity**: Use `OnToolStarted`/`OnToolCompleted` to show tool execution in the UI status bar (e.g., "Working · 3 tool calls").
- **Reasoning**: Use `OnReasoningReceived`/`OnReasoningComplete` to show chain-of-thought in the reasoning panel.
- **Config files**: Use `pluginDirectory` from `ConfigureServices` to store config files alongside your DLL.
- **Host context**: Inject `IProviderHostContext` to access connection settings and create `CopilotClientOptions` if your provider wraps the Copilot SDK.
