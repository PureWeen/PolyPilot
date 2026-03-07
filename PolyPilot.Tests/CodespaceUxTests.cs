using PolyPilot.Models;
using PolyPilot.Services;

namespace PolyPilot.Tests;

/// <summary>
/// Tests for codespace UX behavior: session naming, editor preference, menu guards.
/// </summary>
public class CodespaceUxTests
{
    // --- Session Naming ---

    [Fact]
    public void QuickCreateNaming_FirstSession_NamedMain()
    {
        var existingNames = new HashSet<string>();
        var name = NextCodespaceSessionName(existingNames);
        Assert.Equal("Main", name);
    }

    [Fact]
    public void QuickCreateNaming_MainExists_NamedMain2()
    {
        var existingNames = new HashSet<string> { "Main" };
        var name = NextCodespaceSessionName(existingNames);
        Assert.Equal("Main 2", name);
    }

    [Fact]
    public void QuickCreateNaming_Main_And_Main2_Exist_NamedMain3()
    {
        var existingNames = new HashSet<string> { "Main", "Main 2" };
        var name = NextCodespaceSessionName(existingNames);
        Assert.Equal("Main 3", name);
    }

    [Fact]
    public void QuickCreateNaming_GapInSequence_FillsGap()
    {
        // Main 2 was deleted, Main and Main 3 remain
        var existingNames = new HashSet<string> { "Main", "Main 3" };
        var name = NextCodespaceSessionName(existingNames);
        Assert.Equal("Main 2", name);
    }

    [Fact]
    public void QuickCreateNaming_EmptyGroup_NamedMain()
    {
        var existingNames = new HashSet<string>();
        var name = NextCodespaceSessionName(existingNames);
        Assert.Equal("Main", name);
    }

    [Fact]
    public void QuickCreateNaming_UnrelatedNames_StillMain()
    {
        // Other sessions exist but not "Main"
        var existingNames = new HashSet<string> { "Debug", "Feature work" };
        var name = NextCodespaceSessionName(existingNames);
        Assert.Equal("Main", name);
    }

    // --- VS Code Editor Preference ---

    [Fact]
    public void VsCodeVariant_Stable_CommandIsCode()
    {
        Assert.Equal("code", VsCodeVariant.Stable.Command());
    }

    [Fact]
    public void VsCodeVariant_Insiders_CommandIsCodeInsiders()
    {
        Assert.Equal("code-insiders", VsCodeVariant.Insiders.Command());
    }

    [Fact]
    public void VsCodeVariant_Stable_DisplayNameIsVSCode()
    {
        Assert.Equal("VS Code", VsCodeVariant.Stable.DisplayName());
    }

    [Fact]
    public void VsCodeVariant_Insiders_DisplayNameIsVSCodeInsiders()
    {
        Assert.Equal("VS Code Insiders", VsCodeVariant.Insiders.DisplayName());
    }

    // --- Codespace Group Menu Guards ---

    [Fact]
    public void CodespaceGroup_IsCodespace_True()
    {
        var group = new SessionGroup { CodespaceName = "my-codespace-abc123" };
        Assert.True(group.IsCodespace);
    }

    [Fact]
    public void RegularGroup_IsCodespace_False()
    {
        var group = new SessionGroup { Name = "My Group" };
        Assert.False(group.IsCodespace);
    }

    [Fact]
    public void CodespaceGroup_WorkingDirectory_DerivedFromRepo()
    {
        var group = new SessionGroup
        {
            CodespaceName = "my-codespace",
            CodespaceRepository = "github/cue"
        };
        Assert.Equal("/workspaces/cue", group.CodespaceWorkingDirectory);
    }

    [Fact]
    public void CodespaceGroup_WorkingDirectory_NoRepo_Null()
    {
        var group = new SessionGroup { CodespaceName = "my-codespace" };
        Assert.Null(group.CodespaceWorkingDirectory);
    }

    // --- Move Guard: Codespace sessions can't be moved ---

    [Fact]
    public void MoveTargets_ExcludesCodespaceGroups()
    {
        var groups = new List<SessionGroup>
        {
            new() { Id = "g1", Name = "Sessions" },
            new() { Id = "g2", Name = "cs-group", CodespaceName = "codespace-123" },
            new() { Id = "g3", Name = "Another" }
        };

        // Simulate the sidebar filter: exclude codespace groups as move targets
        var moveTargets = groups.Where(g => !g.IsCodespace).ToList();

        Assert.Equal(2, moveTargets.Count);
        Assert.DoesNotContain(moveTargets, g => g.IsCodespace);
    }

    // --- FindGhPath always returns a value (never throws) ---

    [Fact]
    public void FindGhPath_AlwaysReturnsNonNull()
    {
        var path = PolyPilot.Services.CodespaceService.FindGhPath();
        Assert.NotNull(path);
        Assert.NotEmpty(path);
    }

    // --- ConnectionState defaults ---

    [Fact]
    public void NewCodespaceGroup_DefaultState_Unknown()
    {
        var group = new SessionGroup { CodespaceName = "test" };
        Assert.Equal(CodespaceConnectionState.Unknown, group.ConnectionState);
    }

    [Fact]
    public void NewCodespaceGroup_DefaultPort_4321()
    {
        var group = new SessionGroup { CodespaceName = "test" };
        Assert.Equal(4321, group.CodespacePort);
    }

    // --- Session naming helper (mirrors SessionSidebar.razor logic) ---
    private static string NextCodespaceSessionName(HashSet<string> existingNames)
    {
        var sessionName = "Main";
        if (existingNames.Contains(sessionName))
        {
            var n = 2;
            while (existingNames.Contains($"Main {n}")) n++;
            sessionName = $"Main {n}";
        }
        return sessionName;
    }
}

/// <summary>
/// Tests for the CodespacesEnabled feature toggle in ConnectionSettings.
/// </summary>
public class CodespacesEnabledToggleTests
{
    [Fact]
    public void CodespacesEnabled_DefaultsToFalse()
    {
        var settings = new ConnectionSettings();
        Assert.False(settings.CodespacesEnabled);
    }

    [Fact]
    public void CodespacesEnabled_RoundTripsViaSerialization()
    {
        var settings = new ConnectionSettings { CodespacesEnabled = true };
        var json = System.Text.Json.JsonSerializer.Serialize(settings);
        var deserialized = System.Text.Json.JsonSerializer.Deserialize<ConnectionSettings>(json)!;
        Assert.True(deserialized.CodespacesEnabled);
    }

    [Fact]
    public void CodespacesEnabled_MissingInJson_DefaultsFalse()
    {
        // Simulates loading old settings.json that doesn't have the property
        var json = """{"Mode":0,"Host":"localhost","Port":4321}""";
        var settings = System.Text.Json.JsonSerializer.Deserialize<ConnectionSettings>(json)!;
        Assert.False(settings.CodespacesEnabled);
    }

    [Fact]
    public void SettingsRegistry_HasCodespacesDescriptor()
    {
        var ctx = new SettingsContext
        {
            Settings = new ConnectionSettings(),
            IsDesktop = true
        };
        var codespaces = SettingsRegistry.All.FirstOrDefault(d => d.Id == "ui.codespaces");
        Assert.NotNull(codespaces);
        Assert.Equal(SettingType.Bool, codespaces.Type);
        Assert.Equal("UI", codespaces.Category);
    }

    [Fact]
    public void SettingsRegistry_CodespacesHiddenOnMobile()
    {
        var ctx = new SettingsContext
        {
            Settings = new ConnectionSettings(),
            IsDesktop = false,
            IsMobile = true
        };
        var codespaces = SettingsRegistry.All.FirstOrDefault(d => d.Id == "ui.codespaces");
        Assert.NotNull(codespaces);
        Assert.False(codespaces.IsVisible?.Invoke(ctx) ?? true);
    }

    [Fact]
    public void SettingsRegistry_CodespacesGetSetValue()
    {
        var ctx = new SettingsContext
        {
            Settings = new ConnectionSettings(),
            IsDesktop = true,
            InitialMode = ConnectionMode.Embedded
        };
        var codespaces = SettingsRegistry.All.First(d => d.Id == "ui.codespaces");

        // Default is false
        Assert.Equal(false, codespaces.GetValue?.Invoke(ctx));

        // Set to true (allowed in Embedded mode)
        codespaces.SetValue?.Invoke(ctx, true);
        Assert.True(ctx.Settings.CodespacesEnabled);
        Assert.Equal(true, codespaces.GetValue?.Invoke(ctx));
    }

    [Fact]
    public void SettingsRegistry_CodespacesBlockedInPersistentMode()
    {
        var ctx = new SettingsContext
        {
            Settings = new ConnectionSettings(),
            IsDesktop = true,
            InitialMode = ConnectionMode.Persistent
        };
        var codespaces = SettingsRegistry.All.First(d => d.Id == "ui.codespaces");

        // Enabling should be blocked in Persistent mode
        codespaces.SetValue?.Invoke(ctx, true);
        Assert.False(ctx.Settings.CodespacesEnabled);
    }

    [Fact]
    public void SettingsRegistry_CodespacesCanDisableInAnyMode()
    {
        var ctx = new SettingsContext
        {
            Settings = new ConnectionSettings { CodespacesEnabled = true },
            IsDesktop = true,
            InitialMode = ConnectionMode.Persistent
        };
        var codespaces = SettingsRegistry.All.First(d => d.Id == "ui.codespaces");

        // Disabling should always work, even in Persistent mode
        codespaces.SetValue?.Invoke(ctx, false);
        Assert.False(ctx.Settings.CodespacesEnabled);
    }
}
