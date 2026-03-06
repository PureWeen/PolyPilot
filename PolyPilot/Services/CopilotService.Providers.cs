using Microsoft.Extensions.DependencyInjection;
using PolyPilot.Models;
using PolyPilot.Provider;

namespace PolyPilot.Services;

public partial class CopilotService
{
    // ── Provider State ──────────────────────────────────────
    private readonly Dictionary<string, ISessionProvider> _providers = new();
    private readonly Dictionary<string, string> _sessionToProviderId = new();
    private readonly Dictionary<string, string> _providerSelectedMode = new(); // groupId → modeId

    /// <summary>
    /// Resolves all ISessionProvider instances from DI, registers them into the session model,
    /// and initializes each one. Called at the end of InitializeAsync.
    /// </summary>
    private async Task InitializeProvidersAsync(CancellationToken ct)
    {
        if (_serviceProvider == null) return;

        IEnumerable<ISessionProvider> providers;
        try
        {
            providers = _serviceProvider.GetServices<ISessionProvider>();
        }
        catch
        {
            return;
        }

        foreach (var provider in providers)
        {
            _providers[provider.ProviderId] = provider;
            RegisterProvider(provider);

            try
            {
                await provider.InitializeAsync(ct);
            }
            catch (Exception ex)
            {
                Debug($"Provider '{provider.ProviderId}' init failed: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Creates the session group and leader session for a provider, wires events, and syncs members.
    /// </summary>
    private void RegisterProvider(ISessionProvider provider)
    {
        var groupId = $"__provider_{provider.ProviderId}__";

        // Create or find the provider group
        var existingGroup = Organization.Groups.FirstOrDefault(g => g.Id == groupId);
        if (existingGroup == null)
        {
            var group = new SessionGroup
            {
                Id = groupId,
                Name = provider.GroupName,
                SortOrder = Organization.Groups.Any() ? Organization.Groups.Max(g => g.SortOrder) + 1 : 0
            };
            Organization.Groups.Add(group);
        }

        // Create leader session
        var leaderName = $"__{provider.ProviderId}__";
        if (!_sessions.ContainsKey(leaderName))
        {
            var leaderInfo = new AgentSessionInfo
            {
                Name = leaderName,
                Model = "provider",
            };
            var leaderMeta = Organization.Sessions.FirstOrDefault(m => m.SessionName == leaderName);
            if (leaderMeta == null)
            {
                leaderMeta = new SessionMeta
                {
                    SessionName = leaderName,
                    GroupId = groupId,
                    IsPinned = true
                };
                Organization.Sessions.Add(leaderMeta);
            }

            _sessions[leaderName] = new SessionState
            {
                Session = null!,
                Info = leaderInfo
            };
            _sessionToProviderId[leaderName] = provider.ProviderId;
        }

        // Wire streaming events
        WireProviderEvents(provider, leaderName);

        // Wire member changes
        provider.OnMembersChanged += () => InvokeOnUI(() =>
            SyncProviderMembers(provider, groupId));

        // Initial member sync
        SyncProviderMembers(provider, groupId);
    }

    /// <summary>
    /// Subscribes to a provider's streaming events and forwards them to CopilotService's
    /// existing event system via InvokeOnUI.
    /// </summary>
    private void WireProviderEvents(ISessionProvider provider, string sessionName)
    {
        provider.OnContentReceived += content => InvokeOnUI(() =>
        {
            if (_sessions.TryGetValue(sessionName, out var state))
            {
                state.CurrentResponse.Append(content);
                state.Info.MessageCount = state.Info.History.Count;
            }
            OnContentReceived?.Invoke(sessionName, content);
            OnStateChanged?.Invoke();
        });

        provider.OnToolStarted += (callId, toolName, intent) => InvokeOnUI(() =>
        {
            if (_sessions.TryGetValue(sessionName, out var state))
            {
                state.Info.ToolCallCount++;
                state.Info.ProcessingPhase = 3;
            }
            OnToolStarted?.Invoke(sessionName, toolName, callId, intent);
            OnStateChanged?.Invoke();
        });

        provider.OnToolCompleted += (callId, toolName, success) => InvokeOnUI(() =>
        {
            OnToolCompleted?.Invoke(sessionName, callId, toolName, success);
            OnStateChanged?.Invoke();
        });

        provider.OnReasoningReceived += (reasoningId, content) => InvokeOnUI(() =>
        {
            OnReasoningReceived?.Invoke(sessionName, reasoningId, content);
        });

        provider.OnReasoningComplete += reasoningId => InvokeOnUI(() =>
        {
            OnReasoningComplete?.Invoke(sessionName, reasoningId);
        });

        provider.OnIntentChanged += intent => InvokeOnUI(() =>
        {
            OnIntentChanged?.Invoke(sessionName, intent);
        });

        provider.OnTurnStart += () => InvokeOnUI(() =>
        {
            if (_sessions.TryGetValue(sessionName, out var state))
            {
                state.Info.IsProcessing = true;
                state.Info.ProcessingStartedAt = DateTime.UtcNow;
                state.Info.ToolCallCount = 0;
                state.Info.ProcessingPhase = 2;
                state.CurrentResponse.Clear();
            }
            OnTurnStart?.Invoke(sessionName);
            OnStateChanged?.Invoke();
        });

        provider.OnTurnEnd += () => InvokeOnUI(() =>
        {
            if (_sessions.TryGetValue(sessionName, out var state))
            {
                // Flush accumulated content to history
                if (state.CurrentResponse.Length > 0)
                {
                    var responseText = state.CurrentResponse.ToString();
                    state.Info.History.Add(ChatMessage.AssistantMessage(responseText));
                    state.Info.MessageCount = state.Info.History.Count;
                    state.CurrentResponse.Clear();
                }
                state.Info.IsProcessing = false;
                state.Info.ProcessingStartedAt = null;
                state.Info.ProcessingPhase = 0;
            }
            OnTurnEnd?.Invoke(sessionName);
            OnStateChanged?.Invoke();
        });

        provider.OnError += error => InvokeOnUI(() =>
        {
            if (_sessions.TryGetValue(sessionName, out var state))
            {
                state.Info.IsProcessing = false;
                state.Info.ProcessingStartedAt = null;
                state.Info.ProcessingPhase = 0;
            }
            OnError?.Invoke(sessionName, error);
            OnStateChanged?.Invoke();
        });

        provider.OnStateChanged += () => InvokeOnUI(() =>
        {
            OnStateChanged?.Invoke();
        });
    }

    /// <summary>
    /// Creates/removes member sessions based on the provider's current member list.
    /// </summary>
    private void SyncProviderMembers(ISessionProvider provider, string groupId)
    {
        var members = provider.GetMembers();
        var prefix = $"__{provider.ProviderId}_";
        var suffix = "__";

        // Expected session names for current members
        var expectedNames = members.Select(m => $"{prefix}{m.Id}{suffix}").ToHashSet();

        // Remove stale member sessions
        foreach (var existing in _sessions.Keys
            .Where(k => k.StartsWith(prefix) && k.EndsWith(suffix) && !expectedNames.Contains(k))
            .ToList())
        {
            _sessions.TryRemove(existing, out _);
            _sessionToProviderId.Remove(existing);
            var meta = Organization.Sessions.FirstOrDefault(m => m.SessionName == existing);
            if (meta != null) Organization.Sessions.Remove(meta);
        }

        // Upsert member sessions
        foreach (var member in members)
        {
            var name = $"{prefix}{member.Id}{suffix}";
            if (!_sessions.ContainsKey(name))
            {
                var info = new AgentSessionInfo
                {
                    Name = name,
                    Model = "provider-member",
                };

                var meta = Organization.Sessions.FirstOrDefault(m => m.SessionName == name);
                if (meta == null)
                {
                    meta = new SessionMeta
                    {
                        SessionName = name,
                        GroupId = groupId,
                        Role = MultiAgentRole.Worker
                    };
                    Organization.Sessions.Add(meta);
                }

                _sessions[name] = new SessionState
                {
                    Session = null!,
                    Info = info
                };
                _sessionToProviderId[name] = provider.ProviderId;
            }
        }

        OnStateChanged?.Invoke();
    }

    /// <summary>
    /// Routes a user message to the appropriate provider's SendMessageAsync.
    /// </summary>
    public async Task<string?> SendToProviderAsync(
        string sessionName, string message, CancellationToken ct = default)
    {
        if (!_sessionToProviderId.TryGetValue(sessionName, out var providerId) ||
            !_providers.TryGetValue(providerId, out var provider))
            return null;

        // Add user message to local history
        if (_sessions.TryGetValue(sessionName, out var state))
        {
            state.Info.History.Add(ChatMessage.UserMessage(message));
            state.Info.MessageCount = state.Info.History.Count;
            OnStateChanged?.Invoke();
        }

        return await provider.SendMessageAsync(message, ct);
    }

    // ── Provider Helpers ────────────────────────────────────

    /// <summary>Returns true if this session belongs to a provider (not a normal Copilot session).</summary>
    public bool IsProviderSession(string sessionName) =>
        _sessionToProviderId.ContainsKey(sessionName);

    /// <summary>Returns the provider for a session, or null if it's a normal Copilot session.</summary>
    public ISessionProvider? GetProviderForSession(string sessionName) =>
        _sessionToProviderId.TryGetValue(sessionName, out var id) &&
        _providers.TryGetValue(id, out var p) ? p : null;

    /// <summary>
    /// Returns a human-readable display name for a provider session.
    /// Leader sessions show LeaderDisplayName, member sessions show the member's Name.
    /// Returns null for non-provider sessions.
    /// </summary>
    public string? GetProviderSessionDisplayName(string sessionName)
    {
        var provider = GetProviderForSession(sessionName);
        if (provider == null) return null;

        var leaderName = $"__{provider.ProviderId}__";
        if (sessionName == leaderName)
            return provider.LeaderDisplayName;

        // Member session: extract member ID from __providerId_memberId__
        var prefix = $"__{provider.ProviderId}_";
        var suffix = "__";
        if (sessionName.StartsWith(prefix) && sessionName.EndsWith(suffix))
        {
            var memberId = sessionName[prefix.Length..^suffix.Length];
            var member = provider.GetMembers().FirstOrDefault(m => m.Id == memberId);
            if (member != null) return member.Name;
        }

        return provider.DisplayName;
    }

    /// <summary>Returns the provider for a group, or null if it's not a provider group.</summary>
    public ISessionProvider? GetProviderForGroup(string groupId)
    {
        // Provider groups use the ID pattern __provider_{providerId}__
        if (!groupId.StartsWith("__provider_") || !groupId.EndsWith("__"))
            return null;
        var providerId = groupId["__provider_".Length..^"__".Length];
        return _providers.TryGetValue(providerId, out var p) ? p : null;
    }

    /// <summary>Gets the currently selected mode for a provider group.</summary>
    public string GetProviderGroupMode(string groupId)
    {
        return _providerSelectedMode.TryGetValue(groupId, out var mode) ? mode : "leader";
    }

    /// <summary>Sets the selected mode for a provider group.</summary>
    public void SetProviderGroupMode(string groupId, string modeId)
    {
        _providerSelectedMode[groupId] = modeId;
        OnStateChanged?.Invoke();
    }

    /// <summary>
    /// Sends a message through a provider group using its currently selected mode.
    /// </summary>
    public async Task SendToProviderGroupAsync(string groupId, string message, CancellationToken ct = default)
    {
        var provider = GetProviderForGroup(groupId);
        if (provider == null) return;

        var modeId = GetProviderGroupMode(groupId);
        var leaderName = $"__{provider.ProviderId}__";

        // Add user message to leader's local history
        if (_sessions.TryGetValue(leaderName, out var state))
        {
            state.Info.History.Add(ChatMessage.UserMessage(message));
            state.Info.MessageCount = state.Info.History.Count;
            OnStateChanged?.Invoke();
        }

        await provider.SendToModeAsync(modeId, message, ct);
    }

    /// <summary>Shuts down all registered providers.</summary>
    private async Task ShutdownProvidersAsync()
    {
        foreach (var provider in _providers.Values)
        {
            try
            {
                await provider.ShutdownAsync();
            }
            catch (Exception ex)
            {
                Debug($"Provider '{provider.ProviderId}' shutdown failed: {ex.Message}");
            }
        }
    }
}
