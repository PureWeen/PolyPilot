using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using PolyPilot.Models;
using PolyPilot.Provider;

namespace PolyPilot.Services;

/// <summary>DTO for serializing provider actions to JS with named properties.
/// ValueTuple serializes as Item1/Item2; this record uses camelCase-friendly names.</summary>
public record ProviderCommandDto(string ProviderId, string Id, string Label, string? Tooltip);

public partial class CopilotService
{
    // ── Provider State ──────────────────────────────────────
    private readonly ConcurrentDictionary<string, ISessionProvider> _providers = new();
    private readonly ConcurrentDictionary<string, string> _sessionToProviderId = new();

    /// <summary>
    /// Resolves all ISessionProvider instances from DI, registers them into the session model,
    /// and initializes each one. Called at the end of InitializeAsync.
    /// </summary>
    private async Task InitializeProvidersAsync(CancellationToken ct)
    {
        if (_serviceProvider == null) return;

        List<ISessionProvider> providers;
        try
        {
            // Materialize eagerly so DI construction errors are caught here
            providers = _serviceProvider.GetServices<ISessionProvider>().ToList();
            Debug($"Resolved {providers.Count} ISessionProvider(s) from DI");
        }
        catch (Exception ex)
        {
            Debug($"Failed to resolve ISessionProvider services: {ex}");
            // Also log to plugin system log for visibility
            try
            {
                var systemLog = new PluginFileLogger("_system");
                systemLog.Error($"DI resolution of ISessionProvider failed: {ex}");
            }
            catch { }
            return;
        }

        foreach (var provider in providers)
        {
            _providers[provider.ProviderId] = provider;
            RegisterProvider(provider);
            Debug($"Provider '{provider.ProviderId}' ({provider.DisplayName}) registered");

            try
            {
                await provider.InitializeAsync(ct);
                Debug($"Provider '{provider.ProviderId}' initialized successfully");

                // Re-sync members after init — the initial sync during RegisterProvider
                // runs before the provider has loaded its agents
                var groupId = $"__provider_{provider.ProviderId}__";
                SyncProviderMembers(provider, groupId);
                var memberCount = provider.GetMembers().Count;
                Debug($"Provider '{provider.ProviderId}' has {memberCount} member(s) after init");
            }
            catch (Exception ex)
            {
                Debug($"Provider '{provider.ProviderId}' init failed: {ex.Message}");
                // Log to plugin system log
                try
                {
                    var systemLog = new PluginFileLogger("_system");
                    systemLog.Error($"Provider '{provider.ProviderId}' init failed: {ex}");
                }
                catch { }
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
                IsMultiAgent = false,
                SortOrder = Organization.Groups.Any() ? Organization.Groups.Max(g => g.SortOrder) + 1 : 0
            };
            AddGroup(group);
        }
        else
        {
            // Ensure provider groups are never treated as multi-agent
            existingGroup.IsMultiAgent = false;
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
                AddSessionMeta(leaderMeta);
            }

            _sessions[leaderName] = new SessionState
            {
                Session = null!,
                Info = leaderInfo
            };
            _sessionToProviderId[leaderName] = provider.ProviderId;
        }

        // Wire streaming events for leader
        WireProviderEvents(provider, leaderName);

        // Wire member-scoped streaming events
        WireProviderMemberEvents(provider);

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
                state.Info.ToolCallCount = 0;
                state.Info.IsResumed = false;
            }
            OnTurnEnd?.Invoke(sessionName);
            OnStateChanged?.Invoke();
        });

        provider.OnError += error => InvokeOnUI(() =>
        {
            if (_sessions.TryGetValue(sessionName, out var state))
            {
                // Flush any partial content before clearing state
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
                state.Info.ToolCallCount = 0;
                state.Info.IsResumed = false;
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
    /// Subscribes to a provider's member-scoped streaming events.
    /// Member events include the memberId, which maps to session name __providerId_memberId__.
    /// </summary>
    private void WireProviderMemberEvents(ISessionProvider provider)
    {
        var prefix = $"__{provider.ProviderId}_";
        var suffix = "__";

        provider.OnMemberContentReceived += (memberId, content) => InvokeOnUI(() =>
        {
            var sessionName = $"{prefix}{memberId}{suffix}";
            if (_sessions.TryGetValue(sessionName, out var state))
            {
                state.CurrentResponse.Append(content);
                state.Info.MessageCount = state.Info.History.Count;
            }
            OnContentReceived?.Invoke(sessionName, content);
            OnStateChanged?.Invoke();
        });

        provider.OnMemberTurnStart += memberId => InvokeOnUI(() =>
        {
            var sessionName = $"{prefix}{memberId}{suffix}";
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

        provider.OnMemberTurnEnd += memberId => InvokeOnUI(() =>
        {
            var sessionName = $"{prefix}{memberId}{suffix}";
            if (_sessions.TryGetValue(sessionName, out var state))
            {
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
                state.Info.ToolCallCount = 0;
                state.Info.IsResumed = false;
            }
            OnTurnEnd?.Invoke(sessionName);
            OnStateChanged?.Invoke();
        });

        provider.OnMemberError += (memberId, error) => InvokeOnUI(() =>
        {
            var sessionName = $"{prefix}{memberId}{suffix}";
            if (_sessions.TryGetValue(sessionName, out var state))
            {
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
                state.Info.ToolCallCount = 0;
                state.Info.IsResumed = false;
            }
            OnError?.Invoke(sessionName, error);
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
        var leaderName = $"__{provider.ProviderId}__";

        // Expected session names for current members
        var expectedNames = members.Select(m => $"{prefix}{m.Id}{suffix}").ToHashSet();

        // Remove stale member sessions (but never the leader)
        foreach (var existing in _sessions.Keys
            .Where(k => k != leaderName && k.StartsWith(prefix) && k.EndsWith(suffix) && !expectedNames.Contains(k))
            .ToList())
        {
            _sessions.TryRemove(existing, out _);
            _sessionToProviderId.TryRemove(existing, out _);
            var meta = Organization.Sessions.FirstOrDefault(m => m.SessionName == existing);
            if (meta != null) RemoveSessionMeta(meta);
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
                    AddSessionMeta(meta);
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
    /// Routes a user message to the appropriate provider method.
    /// Leader sessions use SendMessageAsync, member sessions use SendToMemberAsync.
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

        // Check if this is a member session
        var leaderName = $"__{providerId}__";
        if (sessionName != leaderName)
        {
            var prefix = $"__{providerId}_";
            var suffix = "__";
            if (sessionName.StartsWith(prefix) && sessionName.EndsWith(suffix))
            {
                var memberId = sessionName[prefix.Length..^suffix.Length];
                return await provider.SendToMemberAsync(memberId, message, ct);
            }
        }

        return await provider.SendMessageAsync(message, ct);
    }

    /// <summary>
    /// Executes a provider action and posts the result as a message in the leader's chat.
    /// </summary>
    public async Task ExecuteProviderActionAsync(ISessionProvider provider, string actionId)
    {
        var result = await provider.ExecuteActionAsync(actionId, default);
        if (!string.IsNullOrEmpty(result))
        {
            var leaderName = $"__{provider.ProviderId}__";
            if (_sessions.TryGetValue(leaderName, out var state))
            {
                state.Info.History.Add(ChatMessage.AssistantMessage($"⚡ **{actionId}**: {result}"));
                state.Info.MessageCount = state.Info.History.Count;
                InvokeOnUI(() => OnStateChanged?.Invoke());
            }
        }
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
    /// Returns all provider actions available for the given session's provider, or empty if not a provider session.
    /// Actions are returned with their provider reference for execution.
    /// </summary>
    public IReadOnlyList<(ISessionProvider Provider, ProviderAction Action)> GetProviderActionsForSession(string sessionName)
    {
        var provider = GetProviderForSession(sessionName);
        if (provider == null) return [];
        return provider.GetActions().Select(a => (provider, a)).ToList();
    }

    /// <summary>
    /// Returns all provider actions across all registered providers.
    /// Each action is prefixed with the provider's display name for disambiguation.
    /// </summary>
    public IReadOnlyList<(string ProviderId, ProviderAction Action)> GetAllProviderActions()
    {
        var result = new List<(string, ProviderAction)>();
        foreach (var (id, provider) in _providers)
        {
            foreach (var action in provider.GetActions())
                result.Add((id, action));
        }
        return result;
    }

    /// <summary>
    /// Returns all provider actions as DTOs suitable for JSON serialization to JS.
    /// Uses named properties (ProviderId, Id, Label, Tooltip) instead of ValueTuple Item1/Item2.
    /// </summary>
    public IReadOnlyList<ProviderCommandDto> GetAllProviderCommandDtos()
    {
        var result = new List<ProviderCommandDto>();
        foreach (var (id, provider) in _providers)
            foreach (var action in provider.GetActions())
                result.Add(new ProviderCommandDto(id, action.Id, action.Label, action.Tooltip));
        return result;
    }

    /// <summary>
    /// Executes a provider action looked up by provider ID. Called from the JS slash-command bridge.
    /// </summary>
    public async Task ExecuteProviderSlashCommandAsync(string providerId, string actionId)
    {
        if (_providers.TryGetValue(providerId, out var provider))
            await ExecuteProviderActionAsync(provider, actionId);
    }

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
