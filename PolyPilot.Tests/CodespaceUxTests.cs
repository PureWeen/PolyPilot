using PolyPilot.Models;

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
