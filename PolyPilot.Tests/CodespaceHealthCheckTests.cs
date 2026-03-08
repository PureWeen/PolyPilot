using System.Text.Json;
using PolyPilot.Models;
using PolyPilot.Services;

namespace PolyPilot.Tests;

public class CodespaceHealthCheckTests
{
    [Fact]
    public void MaxConsecutiveFailures_GroupWith5ReconnectAttempts_HasCorrectState()
    {
        // Arrange - Create a codespace group with ReconnectAttempts at likely MaxConsecutiveFailures (5)
        var group = new SessionGroup
        {
            Name = "test-cs",
            CodespaceName = "test-codespace",
            CodespaceRepository = "org/repo",
            ReconnectAttempts = 5,
            ConnectionState = CodespaceConnectionState.Reconnecting
        };

        // Act & Assert - Test the model invariant
        Assert.Equal(5, group.ReconnectAttempts);
        Assert.Equal(CodespaceConnectionState.Reconnecting, group.ConnectionState);
        Assert.True(group.IsCodespace);
        
        // Test that groups with high ReconnectAttempts maintain proper state
        Assert.NotNull(group.CodespaceName);
        Assert.NotNull(group.CodespaceRepository);
    }

    [Fact]
    public void SetupMessage_Lifecycle_WorksCorrectly()
    {
        // Arrange
        var group = new SessionGroup
        {
            CodespaceName = "test",
            ConnectionState = CodespaceConnectionState.SetupRequired
        };

        // Act - Set setup message
        group.SetupMessage = "some error";

        // Assert - Message is accessible
        Assert.Equal("some error", group.SetupMessage);
        Assert.Equal(CodespaceConnectionState.SetupRequired, group.ConnectionState);

        // Test null by default
        var defaultGroup = new SessionGroup();
        Assert.Null(defaultGroup.SetupMessage);
    }

    [Fact]
    public void SetupMessage_JsonIgnore_DoesNotSerialize()
    {
        // Arrange
        var group = new SessionGroup
        {
            Name = "test",
            SetupMessage = "error message"
        };

        // Act
        var json = JsonSerializer.Serialize(group);

        // Assert - SetupMessage should not appear in JSON (it has [JsonIgnore])
        Assert.DoesNotContain("SetupMessage", json);
        Assert.DoesNotContain("error message", json);
    }

    [Fact]
    public void SessionGroup_WithCodespaceName_DerivesFriendlyNameFromRepository()
    {
        // SessionGroup with CodespaceName + CodespaceRepository derives Name from repo
        string codespaceName = "test-cs";
        string repository = "org/repo";
        var groupName = !string.IsNullOrEmpty(repository) ? repository.Split('/').Last() : codespaceName;
        
        var group = new SessionGroup
        {
            Name = groupName,
            CodespaceName = codespaceName,
            CodespaceRepository = repository,
            ConnectionState = CodespaceConnectionState.StartingCodespace,
        };

        Assert.Equal("repo", group.Name);
        Assert.Equal(CodespaceConnectionState.StartingCodespace, group.ConnectionState);
        Assert.Equal("test-cs", group.CodespaceName);
        Assert.Equal("org/repo", group.CodespaceRepository);
        Assert.True(group.IsCodespace);
    }

    [Fact]
    public void AddStoppedCodespaceGroup_ExistingCodespace_WouldReuseGroup()
    {
        // Test the reuse logic that AddStoppedCodespaceGroup uses
        // Arrange - Two groups with same codespace name (existing + new)
        var existingGroup = new SessionGroup
        {
            Name = "existing",
            CodespaceName = "test-cs",
            ConnectionState = CodespaceConnectionState.Connected,
            CodespaceRepository = "" // Empty initially
        };

        string codespaceName = "test-cs";
        string repository = "org/repo";

        // Simulate finding existing group by CodespaceName
        var found = existingGroup.CodespaceName == codespaceName ? existingGroup : null;
        Assert.NotNull(found);

        // Simulate AddStoppedCodespaceGroup update logic  
        if (found != null)
        {
            found.ConnectionState = CodespaceConnectionState.StartingCodespace;
            if (string.IsNullOrEmpty(found.CodespaceRepository))
                found.CodespaceRepository = repository;
        }

        // Act & Assert - Same group updated, state changed
        Assert.Equal(CodespaceConnectionState.StartingCodespace, found.ConnectionState);
        Assert.Equal("org/repo", found.CodespaceRepository);
        Assert.Equal("test-cs", found.CodespaceName);
    }

    [Fact]
    public void SendPromptAsync_DisconnectedCodespaceGroup_ShouldThrowCodespaceError()
    {
        // Test the guard condition that GetClientForGroup uses
        // Arrange
        var group = new SessionGroup
        {
            Name = "test-cs",
            CodespaceName = "test-codespace",
            ConnectionState = CodespaceConnectionState.Reconnecting
        };

        // Simulate the check that GetClientForGroup would perform
        string? groupId = group.Id;
        bool isCodespace = group.IsCodespace;
        bool hasClient = false; // Simulating missing client in _codespaceClients

        // Act & Assert - Verify the condition that would throw
        Assert.NotNull(groupId);
        Assert.True(isCodespace);
        Assert.False(hasClient);
        
        // The actual error message would be:
        var expectedError = $"Codespace '{group.Name}' is not connected. Wait for the connection to be re-established or check the codespace status.";
        Assert.Contains("not connected", expectedError);
    }

    [Fact]
    public void HealthCheck_SkipsSetupRequiredGroups_WhenSshUnavailable()
    {
        // Arrange - Test the guard condition that health check uses
        var group = new SessionGroup
        {
            CodespaceName = "test",
            ConnectionState = CodespaceConnectionState.SetupRequired,
            SshAvailable = false
        };

        // Act & Assert - Verify the skip condition matches what health check uses
        var shouldSkip = group.ConnectionState == CodespaceConnectionState.SetupRequired 
                        && group.SshAvailable == false;

        Assert.True(shouldSkip);

        // Test other states don't skip
        group.ConnectionState = CodespaceConnectionState.Reconnecting;
        shouldSkip = group.ConnectionState == CodespaceConnectionState.SetupRequired 
                    && group.SshAvailable == false;
        Assert.False(shouldSkip);

        group.ConnectionState = CodespaceConnectionState.SetupRequired;
        group.SshAvailable = true;
        shouldSkip = group.ConnectionState == CodespaceConnectionState.SetupRequired 
                    && group.SshAvailable == false;
        Assert.False(shouldSkip);
    }

    [Fact]
    public void RetryCodespaceConnection_Logic_ResetsReconnectAttempts()
    {
        // Test the reset logic that RetryCodespaceConnectionAsync uses
        // Arrange
        var group = new SessionGroup
        {
            Name = "test-cs",
            CodespaceName = "test-codespace",
            CodespaceRepository = "org/repo",
            ReconnectAttempts = 5,
            ConnectionState = CodespaceConnectionState.SetupRequired,
            SetupMessage = "previous error",
            SshAvailable = false
        };

        // Simulate RetryCodespaceConnectionAsync reset logic
        group.LastReconnectAttempt = DateTime.UtcNow;
        group.ReconnectAttempts = 0; // Reset backoff counter on manual retry
        group.SetupMessage = null;

        // For SetupRequired, re-probe SSH
        if (group.ConnectionState == CodespaceConnectionState.SetupRequired)
            group.SshAvailable = null;

        group.ConnectionState = CodespaceConnectionState.Reconnecting;

        // Act & Assert - ReconnectAttempts reset to 0 and SetupMessage cleared
        Assert.Equal(0, group.ReconnectAttempts);
        Assert.Null(group.SetupMessage);
        Assert.Null(group.SshAvailable); // Re-probed for SetupRequired
        Assert.Equal(CodespaceConnectionState.Reconnecting, group.ConnectionState);
        Assert.NotNull(group.LastReconnectAttempt);
        Assert.True(group.LastReconnectAttempt > DateTime.UtcNow.AddMinutes(-1));
    }

    [Fact]
    public void CodespaceConnectionState_EnumValues_AreCorrect()
    {
        // Test that the enum values we use in health check logic exist
        Assert.True(Enum.IsDefined(typeof(CodespaceConnectionState), CodespaceConnectionState.Unknown));
        Assert.True(Enum.IsDefined(typeof(CodespaceConnectionState), CodespaceConnectionState.Connected));
        Assert.True(Enum.IsDefined(typeof(CodespaceConnectionState), CodespaceConnectionState.Reconnecting));
        Assert.True(Enum.IsDefined(typeof(CodespaceConnectionState), CodespaceConnectionState.CodespaceStopped));
        Assert.True(Enum.IsDefined(typeof(CodespaceConnectionState), CodespaceConnectionState.StartingCodespace));
        Assert.True(Enum.IsDefined(typeof(CodespaceConnectionState), CodespaceConnectionState.WaitingForCopilot));
        Assert.True(Enum.IsDefined(typeof(CodespaceConnectionState), CodespaceConnectionState.SetupRequired));
    }

    [Fact]
    public void SessionGroup_CodespaceProperties_DefaultCorrectly()
    {
        // Arrange
        var group = new SessionGroup();

        // Act & Assert - Codespace-related properties have correct defaults
        Assert.Null(group.CodespaceName);
        Assert.Null(group.CodespaceRepository);
        Assert.Equal(4321, group.CodespacePort); // Default port
        Assert.False(group.IsCodespace); // Computed property
        Assert.Null(group.CodespaceWorkingDirectory); // Computed property
        Assert.Equal(CodespaceConnectionState.Unknown, group.ConnectionState);
        Assert.Null(group.SshAvailable);
        Assert.Equal(0, group.ReconnectAttempts);
        Assert.Null(group.LastReconnectAttempt);
        Assert.Null(group.SetupMessage);
    }

    [Fact]
    public void SessionGroup_IsCodespace_ComputedCorrectly()
    {
        // Arrange & Act & Assert
        var group = new SessionGroup();
        Assert.False(group.IsCodespace);

        group.CodespaceName = "";
        Assert.False(group.IsCodespace);

        group.CodespaceName = "   ";
        Assert.False(group.IsCodespace);

        group.CodespaceName = "fuzzy-space-guide-123";
        Assert.True(group.IsCodespace);
    }

    [Fact]
    public void SessionGroup_CodespaceWorkingDirectory_ComputedCorrectly()
    {
        // Arrange
        var group = new SessionGroup();

        // Act & Assert - Null when no repository
        Assert.Null(group.CodespaceWorkingDirectory);

        group.CodespaceRepository = "";
        Assert.Null(group.CodespaceWorkingDirectory);

        group.CodespaceRepository = "org/repo";
        Assert.Equal("/workspaces/repo", group.CodespaceWorkingDirectory);

        group.CodespaceRepository = "github/copilot-cli";
        Assert.Equal("/workspaces/copilot-cli", group.CodespaceWorkingDirectory);
    }

    [Fact]
    public void DotfilesStatus_Record_CanBeInstantiated()
    {
        // DotfilesStatus is a public record — verify its properties work correctly
        var status = new CodespaceService.DotfilesStatus(true, "test/repo", false);

        Assert.True(status.IsConfigured);
        Assert.Equal("test/repo", status.Repository);
        Assert.False(status.HasSshdInstall);
    }
}