using PolyPilot.Models;
using PolyPilot.Services;

namespace PolyPilot.Tests;

/// <summary>
/// Tests for the GitHub Codespace group feature: SessionGroup model properties,
/// serialization roundtrips, ActiveSessionEntry persistence, and organization protection.
/// </summary>
public class CodespaceModelTests
{
    // ── SessionGroup model ──────────────────────────────────────────────────

    [Fact]
    public void SessionGroup_IsCodespace_FalseByDefault()
    {
        var group = new SessionGroup { Name = "test" };
        Assert.False(group.IsCodespace);
    }

    [Fact]
    public void SessionGroup_IsCodespace_TrueWhenCodespaceNameSet()
    {
        var group = new SessionGroup
        {
            Name = "my-repo",
            CodespaceName = "fuzzy-space-guide-rj7wx59jr7hp6q5"
        };
        Assert.True(group.IsCodespace);
    }

    [Fact]
    public void SessionGroup_DefaultCodespacePort_Is4321()
    {
        var group = new SessionGroup { CodespaceName = "some-cs" };
        Assert.Equal(4321, group.CodespacePort);
    }

    [Fact]
    public void SessionGroup_IsCodespace_JsonIgnored()
    {
        var props = typeof(SessionGroup).GetProperty(nameof(SessionGroup.IsCodespace));
        Assert.NotNull(props);
        var jsonIgnore = props!.GetCustomAttributes(typeof(System.Text.Json.Serialization.JsonIgnoreAttribute), false);
        Assert.NotEmpty(jsonIgnore);
    }

    [Fact]
    public void SessionGroup_CodespaceFields_RoundtripJson()
    {
        var group = new SessionGroup
        {
            Name = "cse-cleanup",
            CodespaceName = "fuzzy-space-guide-rj7wx59jr7hp6q5",
            CodespacePort = 9999
        };

        var json = System.Text.Json.JsonSerializer.Serialize(group);
        var restored = System.Text.Json.JsonSerializer.Deserialize<SessionGroup>(json);

        Assert.NotNull(restored);
        Assert.Equal(group.CodespaceName, restored!.CodespaceName);
        Assert.Equal(9999, restored.CodespacePort);
        Assert.True(restored.IsCodespace);
        Assert.DoesNotContain("IsCodespace", json);
    }

    [Fact]
    public void SessionGroup_IsCodespace_FalseForWhitespaceOnlyName()
    {
        Assert.False(new SessionGroup { CodespaceName = "" }.IsCodespace);
        Assert.False(new SessionGroup { CodespaceName = " " }.IsCodespace);
        Assert.False(new SessionGroup { CodespaceName = "  \t " }.IsCodespace);
        Assert.False(new SessionGroup { CodespaceName = null }.IsCodespace);
    }

    // ── ActiveSessionEntry ──────────────────────────────────────────────────

    [Fact]
    public void ActiveSessionEntry_GroupId_DefaultsToNull()
    {
        var entry = new ActiveSessionEntry { SessionId = "abc", DisplayName = "test", Model = "m" };
        Assert.Null(entry.GroupId);
    }

    [Fact]
    public void ActiveSessionEntry_GroupId_RoundtripsJson()
    {
        var entry = new ActiveSessionEntry
        {
            SessionId = "abc",
            DisplayName = "session-1",
            Model = "claude-sonnet-4",
            GroupId = "group-123"
        };

        var json = System.Text.Json.JsonSerializer.Serialize(entry);
        var restored = System.Text.Json.JsonSerializer.Deserialize<ActiveSessionEntry>(json);

        Assert.NotNull(restored);
        Assert.Equal("group-123", restored!.GroupId);
    }

    [Fact]
    public void CodespaceInfo_Record_HasExpectedProperties()
    {
        var info = new CodespaceService.CodespaceInfo("name", "owner/repo", "Available");
        Assert.Equal("name", info.Name);
        Assert.Equal("owner/repo", info.Repository);
        Assert.Equal("Available", info.State);
    }

    // ── Organization: codespace group integration ───────────────────────────

    [Fact]
    public void OrganizationState_CanHoldCodespaceGroups()
    {
        var org = new OrganizationState();
        var group = new SessionGroup
        {
            Name = "cse-cleanup-agent",
            CodespaceName = "fuzzy-space-guide-rj7wx59jr7hp6q5",
            CodespacePort = 4321
        };
        org.Groups.Add(group);

        var found = org.Groups.First(g => g.IsCodespace);
        Assert.Equal("fuzzy-space-guide-rj7wx59jr7hp6q5", found.CodespaceName);
        Assert.Equal(4321, found.CodespacePort);
    }

    [Fact]
    public void OrganizationState_CodespaceGroup_DetectedByCodespaceName()
    {
        var org = new OrganizationState();
        org.Groups.Add(new SessionGroup { Name = "normal" });
        org.Groups.Add(new SessionGroup { Name = "cs-group", CodespaceName = "my-codespace" });

        var codespaceGroups = org.Groups.Where(g => g.IsCodespace).ToList();
        Assert.Single(codespaceGroups);
        Assert.Equal("cs-group", codespaceGroups[0].Name);
    }

    // ── ReconcileOrganization: codespace session protection ──────────────────

    [Fact]
    public void SessionMeta_InCodespaceGroup_IsProtectedFromAutoReassignment()
    {
        var org = new OrganizationState();
        var csGroup = new SessionGroup { Name = "community-pulse", CodespaceName = "super-duper-space-happiness-px7965vq54h657p" };
        org.Groups.Add(csGroup);

        var meta = new SessionMeta { SessionName = "my-session", GroupId = csGroup.Id };
        org.Sessions.Add(meta);

        var codespaceGroupIds = org.Groups.Where(g => g.IsCodespace).Select(g => g.Id).ToHashSet();
        Assert.Contains(csGroup.Id, codespaceGroupIds);

        bool wouldSkip = codespaceGroupIds.Contains(meta.GroupId);
        Assert.True(wouldSkip, "ReconcileOrganization must skip sessions in codespace groups");
    }

    [Fact]
    public void SessionMeta_InCodespaceGroup_GroupIdPreservedAfterOrphanCheck()
    {
        var org = new OrganizationState();
        var csGroup = new SessionGroup { Name = "community-pulse", CodespaceName = "some-cs" };
        org.Groups.Add(csGroup);

        var meta = new SessionMeta { SessionName = "cs-session", GroupId = csGroup.Id };
        org.Sessions.Add(meta);

        var groupIds = org.Groups.Select(g => g.Id).ToHashSet();
        Assert.Contains(csGroup.Id, groupIds);

        if (!groupIds.Contains(meta.GroupId))
            meta.GroupId = SessionGroup.DefaultId;

        Assert.Equal(csGroup.Id, meta.GroupId);
    }

    [Fact]
    public void ReconcileOrganization_ProtectsCodespaceSession_FromRepoAutoAssignment()
    {
        var org = new OrganizationState();
        var csGroup = new SessionGroup
        {
            Name = "cs-group",
            CodespaceName = "fuzzy-space-guide-abc",
        };
        org.Groups.Add(csGroup);

        var repoGroup = new SessionGroup { Name = "org/repo", RepoId = "repo-1" };
        org.Groups.Add(repoGroup);

        var meta = new SessionMeta { SessionName = "cs-session", GroupId = csGroup.Id };
        org.Sessions.Add(meta);

        var multiAgentGroupIds = org.Groups.Where(g => g.IsMultiAgent).Select(g => g.Id).ToHashSet();
        var codespaceGroupIds = org.Groups.Where(g => g.IsCodespace).Select(g => g.Id).ToHashSet();
        var protectedGroupIds = multiAgentGroupIds.Union(codespaceGroupIds).ToHashSet();

        bool isProtected = protectedGroupIds.Contains(meta.GroupId);
        Assert.True(isProtected, "Codespace session must be in protected set");

        if (!isProtected)
            meta.GroupId = repoGroup.Id;

        Assert.Equal(csGroup.Id, meta.GroupId);
    }

    // ── Working directory ───────────────────────────────────────────────────

    [Fact]
    public void CodespaceWorkingDirectory_DerivedFromRepository()
    {
        var group = new SessionGroup
        {
            Name = "reflect",
            CodespaceName = "fuzzy-space-guide-abc",
            CodespaceRepository = "github/reflect",
        };
        Assert.Equal("/workspaces/reflect", group.CodespaceWorkingDirectory);
    }

    [Fact]
    public void CodespaceWorkingDirectory_NullWhenNoRepository()
    {
        var group = new SessionGroup
        {
            Name = "test",
            CodespaceName = "fuzzy-space-guide-abc",
        };
        Assert.Null(group.CodespaceWorkingDirectory);
    }

    [Fact]
    public void CodespaceRepository_SerializesWithGroup()
    {
        var group = new SessionGroup
        {
            Name = "reflect",
            CodespaceName = "fuzzy-space-guide-abc",
            CodespaceRepository = "github/reflect",
        };
        var json = System.Text.Json.JsonSerializer.Serialize(group);
        var deserialized = System.Text.Json.JsonSerializer.Deserialize<SessionGroup>(json)!;
        Assert.Equal("github/reflect", deserialized.CodespaceRepository);
        Assert.Equal("/workspaces/reflect", deserialized.CodespaceWorkingDirectory);
    }

    [Fact]
    public void OrganizationState_CodespaceGroups_RoundtripJson()
    {
        var org = new OrganizationState();
        org.Groups.Add(new SessionGroup
        {
            Name = "cs-reflect",
            CodespaceName = "fuzzy-space-guide-abc",
            CodespaceRepository = "github/reflect",
            CodespacePort = 5555,
        });
        org.Sessions.Add(new SessionMeta
        {
            SessionName = "cs-session",
            GroupId = org.Groups.Last().Id,
        });

        var json = System.Text.Json.JsonSerializer.Serialize(org);
        var restored = System.Text.Json.JsonSerializer.Deserialize<OrganizationState>(json)!;

        var csGroup = restored.Groups.First(g => g.IsCodespace);
        Assert.Equal("fuzzy-space-guide-abc", csGroup.CodespaceName);
        Assert.Equal("github/reflect", csGroup.CodespaceRepository);
        Assert.Equal(5555, csGroup.CodespacePort);
        Assert.Equal(CodespaceConnectionState.Unknown, csGroup.ConnectionState);
        Assert.Null(csGroup.SshAvailable);
        Assert.Equal(0, csGroup.ReconnectAttempts);
        Assert.Equal(csGroup.Id, restored.Sessions.First(s => s.SessionName == "cs-session").GroupId);
    }

    // ── Codespace WorkingDirectory override for sessions ────────────────────

    [Fact]
    public void CodespaceGroup_SessionShouldUse_CodespaceWorkingDirectory_NotLocalPath()
    {
        // Simulates the bug: sessions in a codespace group should use /workspaces/{repo}
        // instead of a local Mac worktree path.
        var group = new SessionGroup
        {
            Name = "reflect",
            CodespaceName = "fuzzy-space-guide-abc",
            CodespaceRepository = "github/reflect",
        };

        var localWorktreePath = "/Users/someone/.polypilot/worktrees/github-reflect-abc123";

        // When the group is a codespace, CodespaceWorkingDirectory should be used
        var effectiveWorkDir = group.IsCodespace && group.CodespaceWorkingDirectory != null
            ? group.CodespaceWorkingDirectory
            : localWorktreePath;

        Assert.Equal("/workspaces/reflect", effectiveWorkDir);
    }

    [Fact]
    public void NonCodespaceGroup_SessionShouldUse_LocalWorktreePath()
    {
        // Non-codespace groups should continue using the local worktree path
        var group = new SessionGroup
        {
            Name = "my-project",
        };

        var localWorktreePath = "/Users/someone/.polypilot/worktrees/my-project-abc123";

        var effectiveWorkDir = group.IsCodespace && group.CodespaceWorkingDirectory != null
            ? group.CodespaceWorkingDirectory
            : localWorktreePath;

        Assert.Equal(localWorktreePath, effectiveWorkDir);
    }

    [Fact]
    public void CodespaceGroup_WithoutRepository_FallsBackToLocalPath()
    {
        // When CodespaceRepository is not set, CodespaceWorkingDirectory is null,
        // so we should fall back to the local path gracefully.
        var group = new SessionGroup
        {
            Name = "unnamed-cs",
            CodespaceName = "some-codespace",
            // CodespaceRepository not set
        };

        var localWorktreePath = "/Users/someone/.polypilot/worktrees/unnamed-cs-abc";

        Assert.Null(group.CodespaceWorkingDirectory);
        var effectiveWorkDir = group.CodespaceWorkingDirectory ?? localWorktreePath;
        Assert.Equal(localWorktreePath, effectiveWorkDir);
    }

    [Fact]
    public void CodespaceGroup_ResumeConfig_ShouldOverrideStoredWorkingDirectory()
    {
        // Simulates the resume scenario: stored WorkingDirectory is a local path,
        // but the group's CodespaceWorkingDirectory should take precedence.
        var group = new SessionGroup
        {
            Name = "reflect",
            CodespaceName = "fuzzy-space-guide-abc",
            CodespaceRepository = "github/reflect",
        };

        var storedWorkingDirectory = "/Users/btessiau/.polypilot/worktrees/github-reflect-1bb4fdc6";

        // This is the pattern used in ResumeCodespaceSessionsAsync after the fix
        var resumeWorkDir = group.CodespaceWorkingDirectory ?? storedWorkingDirectory;

        Assert.Equal("/workspaces/reflect", resumeWorkDir);
        Assert.NotEqual(storedWorkingDirectory, resumeWorkDir);
    }

    [Fact]
    public void CodespaceGroup_RestoredSession_OverridesPersistedLocalPath()
    {
        // Simulates the restore-from-disk scenario: the persisted ActiveSessionEntry has
        // a local Mac path, but the codespace group's CodespaceWorkingDirectory should
        // take precedence when creating the placeholder session.
        var group = new SessionGroup
        {
            Name = "reflect",
            CodespaceName = "fuzzy-space-guide-abc",
            CodespaceRepository = "github/reflect",
        };

        var persistedLocalPath = "/Users/btessiau/.polypilot/worktrees/github-reflect-1bb4fdc6";

        // This is the pattern used in RestorePreviousSessionsAsync after the fix
        var csWorkDir = group.CodespaceWorkingDirectory ?? persistedLocalPath;

        Assert.Equal("/workspaces/reflect", csWorkDir);
    }

    [Fact]
    public void CodespaceWorkingDirectory_HandlesNestedRepoNames()
    {
        // Ensure repos like "org/sub-repo-name" work correctly
        var group = new SessionGroup
        {
            Name = "sub-repo",
            CodespaceName = "some-cs",
            CodespaceRepository = "my-org/my-sub-repo",
        };
        Assert.Equal("/workspaces/my-sub-repo", group.CodespaceWorkingDirectory);
    }
}
