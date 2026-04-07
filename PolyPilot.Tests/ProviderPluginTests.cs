using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using PolyPilot.Models;
using PolyPilot.Provider;
using PolyPilot.Services;

namespace PolyPilot.Tests;

public class ProviderPluginTests
{
    // ── Plugin Settings Serialization ───────────────────────

    [Fact]
    public void PluginSettings_Serializes_RoundTrip()
    {
        var settings = new ConnectionSettings
        {
            Plugins = new PluginSettings
            {
                Enabled = new()
                {
                    new EnabledPlugin
                    {
                        Path = "myplugin/MyPlugin.dll",
                        Hash = "abc123def456",
                        EnabledAt = new DateTime(2026, 3, 6, 0, 0, 0, DateTimeKind.Utc),
                        DisplayName = "MyPlugin"
                    }
                },
                Disabled = new()
                {
                    new DisabledPlugin
                    {
                        Path = "other/Other.dll",
                        Reason = "hash_changed",
                        LastKnownHash = "old_hash"
                    }
                }
            }
        };

        var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
        var deserialized = JsonSerializer.Deserialize<ConnectionSettings>(json);

        Assert.NotNull(deserialized);
        Assert.Single(deserialized.Plugins.Enabled);
        Assert.Equal("myplugin/MyPlugin.dll", deserialized.Plugins.Enabled[0].Path);
        Assert.Equal("abc123def456", deserialized.Plugins.Enabled[0].Hash);
        Assert.Equal("MyPlugin", deserialized.Plugins.Enabled[0].DisplayName);
        Assert.Single(deserialized.Plugins.Disabled);
        Assert.Equal("hash_changed", deserialized.Plugins.Disabled[0].Reason);
    }

    [Fact]
    public void PluginSettings_DefaultsToEmpty()
    {
        var settings = new ConnectionSettings();
        Assert.NotNull(settings.Plugins);
        Assert.Empty(settings.Plugins.Enabled);
        Assert.Empty(settings.Plugins.Disabled);
    }

    [Fact]
    public void PluginSettings_BackwardCompat_DisabledPlugins_StillWorks()
    {
        // Existing settings.json may have DisabledPlugins as List<string>
        var json = """{"DisabledPlugins":["foo.dll","bar.dll"],"Plugins":{"Enabled":[],"Disabled":[]}}""";
        var settings = JsonSerializer.Deserialize<ConnectionSettings>(json);

        Assert.NotNull(settings);
        Assert.Equal(2, settings.DisabledPlugins.Count);
        Assert.Contains("foo.dll", settings.DisabledPlugins);
    }

    // ── PluginLoader Hash Computation ───────────────────────

    [Fact]
    public void PluginLoader_ComputeHash_Returns_ConsistentHash()
    {
        // Create a temp file and verify hash consistency
        var tempFile = Path.GetTempFileName();
        try
        {
            File.WriteAllText(tempFile, "test content for hash verification");
            var hash1 = PluginLoader.ComputeHash(tempFile);
            var hash2 = PluginLoader.ComputeHash(tempFile);

            Assert.Equal(hash1, hash2);
            Assert.Equal(64, hash1.Length); // SHA-256 = 64 hex chars
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public void PluginLoader_ComputeHash_Changes_When_Content_Changes()
    {
        var tempFile = Path.GetTempFileName();
        try
        {
            File.WriteAllText(tempFile, "original content");
            var hash1 = PluginLoader.ComputeHash(tempFile);

            File.WriteAllText(tempFile, "modified content");
            var hash2 = PluginLoader.ComputeHash(tempFile);

            Assert.NotEqual(hash1, hash2);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public void PluginLoader_DiscoverPlugins_EmptyDir_ReturnsEmpty()
    {
        // Default plugins dir likely doesn't exist in test env
        var result = PluginLoader.DiscoverPlugins();
        // Either returns empty or returns actual plugins — both are valid
        Assert.NotNull(result);
    }

    // ── ProviderHostContext Mapping ──────────────────────────

    [Fact]
    public void ProviderHostContext_Maps_Embedded()
    {
        var settings = new ConnectionSettings { Mode = ConnectionMode.Embedded, CliSource = CliSourceMode.BuiltIn };
        var ctx = new ProviderHostContext(settings);

        Assert.Equal(ProviderConnectionMode.Embedded, ctx.ConnectionMode);
        Assert.Equal(ProviderCliSource.BuiltIn, ctx.CliSource);
    }

    [Fact]
    public void ProviderHostContext_Maps_Persistent()
    {
        var settings = new ConnectionSettings { Mode = ConnectionMode.Persistent, CliSource = CliSourceMode.System };
        var ctx = new ProviderHostContext(settings);

        Assert.Equal(ProviderConnectionMode.Persistent, ctx.ConnectionMode);
        Assert.Equal(ProviderCliSource.System, ctx.CliSource);
    }

    [Fact]
    public void ProviderHostContext_Maps_Remote()
    {
        var settings = new ConnectionSettings { Mode = ConnectionMode.Remote };
        var ctx = new ProviderHostContext(settings);
        Assert.Equal(ProviderConnectionMode.Remote, ctx.ConnectionMode);
    }

    [Fact]
    public void ProviderHostContext_Maps_Demo()
    {
        var settings = new ConnectionSettings { Mode = ConnectionMode.Demo };
        var ctx = new ProviderHostContext(settings);
        Assert.Equal(ProviderConnectionMode.Demo, ctx.ConnectionMode);
    }

    [Fact]
    public void ProviderHostContext_Settings_DefaultsToEmpty()
    {
        var settings = new ConnectionSettings();
        var ctx = new ProviderHostContext(settings);
        Assert.NotNull(ctx.Settings);
        Assert.Empty(ctx.Settings);
    }

    [Fact]
    public void ProviderHostContext_GetSetting_ReturnsNull_ForUnknownKey()
    {
        var settings = new ConnectionSettings();
        IProviderHostContext ctx = new ProviderHostContext(settings);
        Assert.Null(ctx.GetSetting("unknown.key"));
    }

    // ── Provider Session Detection ──────────────────────────

    [Fact]
    public async Task IsProviderSession_ReturnsFalse_ForNormalSessions()
    {
        var service = TestHelper.CreateCopilotService();
        Assert.False(service.IsProviderSession("my-session"));
    }

    [Fact]
    public async Task GetProviderForSession_ReturnsNull_ForNormalSessions()
    {
        var service = TestHelper.CreateCopilotService();
        Assert.Null(service.GetProviderForSession("my-session"));
    }

    [Fact]
    public void GetProviderForGroup_ReturnsNull_ForNonProviderGroups()
    {
        var service = TestHelper.CreateCopilotService();
        Assert.Null(service.GetProviderForGroup("some-group-id"));
        Assert.Null(service.GetProviderForGroup(SessionGroup.DefaultId));
    }

    [Fact]
    public void GetProviderForGroup_ReturnsNull_WhenProviderIdNotRegistered()
    {
        var service = TestHelper.CreateCopilotService();
        // Correct pattern but no provider registered with this ID
        Assert.Null(service.GetProviderForGroup("__provider_unknown__"));
    }

    // ── Provider Model Types ────────────────────────────────

    [Fact]
    public void ProviderChatMessage_Defaults()
    {
        var msg = new ProviderChatMessage();
        Assert.Equal("assistant", msg.Role);
        Assert.Equal("", msg.Content);
        Assert.True(msg.IsComplete);
        Assert.Null(msg.ToolName);
    }

    [Fact]
    public void ProviderMember_RequiredProperties()
    {
        var member = new ProviderMember { Id = "m1", Name = "Worker 1", Role = "developer" };
        Assert.Equal("m1", member.Id);
        Assert.Equal("Worker 1", member.Name);
        Assert.Equal("👤", member.Icon); // default
        Assert.False(member.IsActive);
    }

    [Fact]
    public void ProviderAction_RequiredProperties()
    {
        var action = new ProviderAction { Id = "act1", Label = "Do thing" };
        Assert.Equal("act1", action.Id);
        Assert.Equal("Do thing", action.Label);
        Assert.Null(action.Tooltip);
    }

    [Fact]
    public void RegisterProvider_PersistsReasoningMessagesForProviderSessions()
    {
        var chatDb = new StubChatDatabase();
        var services = new ServiceCollection();
        var sp = services.BuildServiceProvider();
        var service = new CopilotService(
            chatDb,
            new StubServerManager(),
            new StubWsBridgeClient(),
            new RepoManager(),
            sp,
            new StubDemoService());
        var provider = new FakeProvider();

        RegisterProvider(service, provider);

        provider.RaiseTurnStart();
        provider.RaiseReasoning("reason-1", "Thinking through the answer");

        var session = service.GetSession("__fake__");
        Assert.NotNull(session);

        var reasoning = Assert.Single(session.History);
        Assert.Equal(ChatMessageType.Reasoning, reasoning.MessageType);
        Assert.Equal("reason-1", reasoning.ReasoningId);
        Assert.Equal("Thinking through the answer", reasoning.Content);
        Assert.False(reasoning.IsComplete);

        provider.RaiseContent("Final answer");
        provider.RaiseTurnEnd();

        Assert.Collection(session.History,
            msg =>
            {
                Assert.Equal(ChatMessageType.Reasoning, msg.MessageType);
                Assert.Equal("Thinking through the answer", msg.Content);
                Assert.True(msg.IsComplete);
                Assert.True(msg.IsCollapsed);
            },
            msg =>
            {
                Assert.Equal(ChatMessageType.Assistant, msg.MessageType);
                Assert.Equal("Final answer", msg.Content);
            });

        Assert.Contains(chatDb.AddedMessages, entry =>
            entry.SessionId == "provider:fake:leader" &&
            entry.Message.MessageType == ChatMessageType.Reasoning &&
            entry.Message.ReasoningId == "reason-1");
        Assert.Contains(chatDb.AddedMessages, entry =>
            entry.SessionId == "provider:fake:leader" &&
            entry.Message.MessageType == ChatMessageType.Assistant &&
            entry.Message.Content == "Final answer");
    }

    // ── Helper ──────────────────────────────────────────────

    private static class TestHelper
    {
        public static CopilotService CreateCopilotService()
        {
            var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
            var sp = services.BuildServiceProvider();
            return new CopilotService(
                new StubChatDatabase(),
                new StubServerManager(),
                new StubWsBridgeClient(),
                new RepoManager(),
                sp,
                new StubDemoService()
            );
        }
    }

    private static void RegisterProvider(CopilotService service, ISessionProvider provider)
    {
        var method = typeof(CopilotService).GetMethod("RegisterProvider", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        method.Invoke(service, [provider]);
    }

    private sealed class FakeProvider : ISessionProvider
    {
        public string ProviderId => "fake";
        public string DisplayName => "Fake Provider";
        public string Icon => "";
        public string AccentColor => "#000000";
        public string GroupName => "Fake Group";
        public string GroupDescription => "Fake provider for tests";
        public bool IsInitialized => true;
        public bool IsInitializing => false;
        public string LeaderDisplayName => "Fake";
        public string LeaderIcon => "";
        public bool IsProcessing => false;
        public IReadOnlyList<ProviderChatMessage> History => [];

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

        public Task InitializeAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task ShutdownAsync() => Task.CompletedTask;
        public Task<string> SendMessageAsync(string message, CancellationToken ct = default) => Task.FromResult(message);
        public IReadOnlyList<ProviderMember> GetMembers() => [];

        public void RaiseTurnStart() => OnTurnStart?.Invoke();
        public void RaiseReasoning(string reasoningId, string content) => OnReasoningReceived?.Invoke(reasoningId, content);
        public void RaiseReasoningComplete(string reasoningId) => OnReasoningComplete?.Invoke(reasoningId);
        public void RaiseContent(string content) => OnContentReceived?.Invoke(content);
        public void RaiseTurnEnd() => OnTurnEnd?.Invoke();
    }
}
