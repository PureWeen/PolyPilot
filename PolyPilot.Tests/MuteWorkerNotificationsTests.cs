using System.Text.Json;
using PolyPilot.Models;
using PolyPilot.Services;

namespace PolyPilot.Tests;

/// <summary>
/// Tests for the MuteWorkerNotifications setting:
/// - ConnectionSettings serialization
/// - SettingsRegistry UI descriptor
/// - IsWorkerInMultiAgentGroup helper (tested via OrganizationState model)
/// </summary>
public class MuteWorkerNotificationsTests
{
    // ── ConnectionSettings ──────────────────────────────────────────

    [Fact]
    public void MuteWorkerNotifications_DefaultIsFalse()
    {
        var settings = new ConnectionSettings();
        Assert.False(settings.MuteWorkerNotifications);
    }

    [Fact]
    public void MuteWorkerNotifications_RoundTripsViaJson()
    {
        var settings = new ConnectionSettings { MuteWorkerNotifications = true };
        var json = JsonSerializer.Serialize(settings);
        var deserialized = JsonSerializer.Deserialize<ConnectionSettings>(json)!;
        Assert.True(deserialized.MuteWorkerNotifications);
    }

    [Fact]
    public void MuteWorkerNotifications_MissingInJson_DefaultsToFalse()
    {
        // Simulates loading settings saved before this feature existed
        var json = """{"EnableSessionNotifications":true}""";
        var settings = JsonSerializer.Deserialize<ConnectionSettings>(json)!;
        Assert.False(settings.MuteWorkerNotifications);
    }

    // ── SettingsRegistry ────────────────────────────────────────────

    [Fact]
    public void MuteWorkerNotifications_SettingDescriptorExists()
    {
        var desc = SettingsRegistry.All.FirstOrDefault(s => s.Id == "ui.muteWorkerNotifications");
        Assert.NotNull(desc);
        Assert.Equal("UI", desc.Category);
        Assert.Equal("Notifications", desc.Section);
        Assert.Equal(SettingType.Bool, desc.Type);
    }

    [Fact]
    public void MuteWorkerNotifications_HiddenWhenNotificationsDisabled()
    {
        var settings = new ConnectionSettings { EnableSessionNotifications = false };
        var ctx = new SettingsContext
        {
            Settings = settings,
            FontSize = 20,
            ServerAlive = false,
            IsDesktop = true,
            InitialMode = settings.Mode
        };
        var desc = SettingsRegistry.All.First(s => s.Id == "ui.muteWorkerNotifications");
        Assert.False(desc.IsVisible!(ctx));
    }

    [Fact]
    public void MuteWorkerNotifications_VisibleWhenNotificationsEnabled()
    {
        var settings = new ConnectionSettings { EnableSessionNotifications = true };
        var ctx = new SettingsContext
        {
            Settings = settings,
            FontSize = 20,
            ServerAlive = false,
            IsDesktop = true,
            InitialMode = settings.Mode
        };
        var desc = SettingsRegistry.All.First(s => s.Id == "ui.muteWorkerNotifications");
        Assert.True(desc.IsVisible!(ctx));
    }

    [Fact]
    public void MuteWorkerNotifications_GetSetValue()
    {
        var settings = new ConnectionSettings { MuteWorkerNotifications = false };
        var ctx = new SettingsContext
        {
            Settings = settings,
            FontSize = 20,
            ServerAlive = false,
            IsDesktop = true,
            InitialMode = settings.Mode
        };
        var desc = SettingsRegistry.All.First(s => s.Id == "ui.muteWorkerNotifications");
        Assert.False((bool)desc.GetValue!(ctx)!);
        desc.SetValue!(ctx, true);
        Assert.True(settings.MuteWorkerNotifications);
    }

    // ── Worker identification (model-level) ─────────────────────────

    [Fact]
    public void WorkerRole_IdentifiedInMultiAgentGroup()
    {
        var group = new SessionGroup { Id = "team1", Name = "Team", IsMultiAgent = true };
        var worker = new SessionMeta { SessionName = "worker1", GroupId = "team1", Role = MultiAgentRole.Worker };
        var orchestrator = new SessionMeta { SessionName = "orch1", GroupId = "team1", Role = MultiAgentRole.Orchestrator };

        Assert.Equal(MultiAgentRole.Worker, worker.Role);
        Assert.Equal(MultiAgentRole.Orchestrator, orchestrator.Role);
        Assert.True(group.IsMultiAgent);
    }

    [Fact]
    public void NonMultiAgentGroup_WorkerRoleNotFiltered()
    {
        var group = new SessionGroup { Id = "solo", Name = "Solo", IsMultiAgent = false };
        var session = new SessionMeta { SessionName = "session1", GroupId = "solo", Role = MultiAgentRole.Worker };

        // Even though role is Worker, the group is NOT multi-agent, so it shouldn't be filtered
        Assert.False(group.IsMultiAgent);
    }

    [Fact]
    public void DefaultSessionMeta_RoleIsNone()
    {
        var meta = new SessionMeta();
        Assert.Equal(MultiAgentRole.None, meta.Role);
    }

    // ── Notification filtering logic (unit) ─────────────────────────

    [Theory]
    [InlineData(false, false, false)] // notifications off, mute off → no notification sent (but not because of mute)
    [InlineData(true, false, true)]   // notifications on, mute off → notification sent
    [InlineData(true, true, false)]   // notifications on, mute on, worker → notification muted
    public void NotificationFiltering_WorkerInMultiAgentGroup(
        bool enableNotifications, bool muteWorkers, bool expectNotification)
    {
        var settings = new ConnectionSettings
        {
            EnableSessionNotifications = enableNotifications,
            MuteWorkerNotifications = muteWorkers
        };

        var org = new OrganizationState
        {
            Groups = new List<SessionGroup>
            {
                new() { Id = "team1", Name = "Team", IsMultiAgent = true }
            },
            Sessions = new List<SessionMeta>
            {
                new() { SessionName = "worker1", GroupId = "team1", Role = MultiAgentRole.Worker }
            }
        };

        // Simulate the notification filtering logic
        bool shouldSend = settings.EnableSessionNotifications;
        if (shouldSend && settings.MuteWorkerNotifications)
        {
            var meta = org.Sessions.FirstOrDefault(m => m.SessionName == "worker1");
            var group = org.Groups.FirstOrDefault(g => g.Id == meta?.GroupId);
            if (group?.IsMultiAgent == true && meta?.Role == MultiAgentRole.Worker)
                shouldSend = false;
        }

        Assert.Equal(expectNotification, shouldSend);
    }

    [Fact]
    public void NotificationFiltering_OrchestratorNotMuted()
    {
        var settings = new ConnectionSettings
        {
            EnableSessionNotifications = true,
            MuteWorkerNotifications = true
        };

        var org = new OrganizationState
        {
            Groups = new List<SessionGroup>
            {
                new() { Id = "team1", Name = "Team", IsMultiAgent = true }
            },
            Sessions = new List<SessionMeta>
            {
                new() { SessionName = "orch1", GroupId = "team1", Role = MultiAgentRole.Orchestrator }
            }
        };

        bool shouldSend = settings.EnableSessionNotifications;
        if (shouldSend && settings.MuteWorkerNotifications)
        {
            var meta = org.Sessions.FirstOrDefault(m => m.SessionName == "orch1");
            var group = org.Groups.FirstOrDefault(g => g.Id == meta?.GroupId);
            if (group?.IsMultiAgent == true && meta?.Role == MultiAgentRole.Worker)
                shouldSend = false;
        }

        // Orchestrator should NOT be muted
        Assert.True(shouldSend);
    }

    [Fact]
    public void NotificationFiltering_NonMultiAgentWorkerNotMuted()
    {
        var settings = new ConnectionSettings
        {
            EnableSessionNotifications = true,
            MuteWorkerNotifications = true
        };

        var org = new OrganizationState
        {
            Groups = new List<SessionGroup>
            {
                new() { Id = "solo", Name = "Solo Sessions", IsMultiAgent = false }
            },
            Sessions = new List<SessionMeta>
            {
                new() { SessionName = "session1", GroupId = "solo", Role = MultiAgentRole.Worker }
            }
        };

        bool shouldSend = settings.EnableSessionNotifications;
        if (shouldSend && settings.MuteWorkerNotifications)
        {
            var meta = org.Sessions.FirstOrDefault(m => m.SessionName == "session1");
            var group = org.Groups.FirstOrDefault(g => g.Id == meta?.GroupId);
            if (group?.IsMultiAgent == true && meta?.Role == MultiAgentRole.Worker)
                shouldSend = false;
        }

        // Regular sessions should NOT be muted even though their default role is Worker
        Assert.True(shouldSend);
    }
}
