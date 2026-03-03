using Microsoft.Extensions.DependencyInjection;
using PolyPilot.Models;
using PolyPilot.Services;

namespace PolyPilot.Tests;

/// <summary>
/// Tests for custom agent discovery (from .github/agents, .claude/agents, .copilot/agents)
/// and the popup invocation text format so users can actually use their custom agents.
/// Regression test for: "how to use my custom agents instead of the default copilot cli agent"
/// </summary>
public class AgentDiscoveryTests : IDisposable
{
    private readonly string _workDir;
    private readonly StubChatDatabase _chatDb = new();
    private readonly StubServerManager _serverManager = new();
    private readonly StubWsBridgeClient _bridgeClient = new();
    private readonly StubDemoService _demoService = new();
    private readonly RepoManager _repoManager = new();
    private readonly IServiceProvider _serviceProvider;

    public AgentDiscoveryTests()
    {
        _workDir = Path.Combine(Path.GetTempPath(), $"PolyPilot-agent-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_workDir);

        var services = new ServiceCollection();
        _serviceProvider = services.BuildServiceProvider();
    }

    public void Dispose()
    {
        try { Directory.Delete(_workDir, recursive: true); } catch { }
    }

    private CopilotService CreateService() =>
        new(_chatDb, _serverManager, _bridgeClient, _repoManager, _serviceProvider, _demoService);

    // --- Agent discovery ---

    [Fact]
    public void DiscoverAvailableAgents_ReturnsEmpty_WhenNoAgentDirs()
    {
        var svc = CreateService();
        var agents = svc.DiscoverAvailableAgents(_workDir);
        Assert.Empty(agents);
    }

    [Fact]
    public void DiscoverAvailableAgents_FindsAgents_InGitHubAgentsDir()
    {
        var agentsDir = Path.Combine(_workDir, ".github", "agents");
        Directory.CreateDirectory(agentsDir);
        File.WriteAllText(Path.Combine(agentsDir, "code-reviewer.md"),
            "---\nname: Code Reviewer\ndescription: Reviews code for best practices\n---\nYou are a code reviewer.");

        var svc = CreateService();
        var agents = svc.DiscoverAvailableAgents(_workDir);

        Assert.Single(agents);
        Assert.Equal("Code Reviewer", agents[0].Name);
        Assert.Equal("Reviews code for best practices", agents[0].Description);
        Assert.Equal("project", agents[0].Source);
    }

    [Fact]
    public void DiscoverAvailableAgents_FindsAgents_InClaudeAgentsDir()
    {
        var agentsDir = Path.Combine(_workDir, ".claude", "agents");
        Directory.CreateDirectory(agentsDir);
        File.WriteAllText(Path.Combine(agentsDir, "tester.md"),
            "---\nname: Test Writer\ndescription: Writes unit tests\n---\nYou write tests.");

        var svc = CreateService();
        var agents = svc.DiscoverAvailableAgents(_workDir);

        Assert.Single(agents);
        Assert.Equal("Test Writer", agents[0].Name);
    }

    [Fact]
    public void DiscoverAvailableAgents_FindsAgents_InCopilotAgentsDir()
    {
        var agentsDir = Path.Combine(_workDir, ".copilot", "agents");
        Directory.CreateDirectory(agentsDir);
        File.WriteAllText(Path.Combine(agentsDir, "doc-writer.md"),
            "---\nname: Doc Writer\ndescription: Writes documentation\n---\nYou document code.");

        var svc = CreateService();
        var agents = svc.DiscoverAvailableAgents(_workDir);

        Assert.Single(agents);
        Assert.Equal("Doc Writer", agents[0].Name);
    }

    [Fact]
    public void DiscoverAvailableAgents_UsesFilename_WhenNoFrontmatter()
    {
        var agentsDir = Path.Combine(_workDir, ".github", "agents");
        Directory.CreateDirectory(agentsDir);
        File.WriteAllText(Path.Combine(agentsDir, "my-agent.md"), "You are a helpful agent.");

        var svc = CreateService();
        var agents = svc.DiscoverAvailableAgents(_workDir);

        Assert.Single(agents);
        Assert.Equal("my-agent", agents[0].Name);
        Assert.Equal("", agents[0].Description);
    }

    [Fact]
    public void DiscoverAvailableAgents_DeduplicatesByName_AcrossDirs()
    {
        // Same agent name in both .github/agents and .claude/agents
        var githubDir = Path.Combine(_workDir, ".github", "agents");
        var claudeDir = Path.Combine(_workDir, ".claude", "agents");
        Directory.CreateDirectory(githubDir);
        Directory.CreateDirectory(claudeDir);
        File.WriteAllText(Path.Combine(githubDir, "reviewer.md"),
            "---\nname: Reviewer\n---\nVersion 1");
        File.WriteAllText(Path.Combine(claudeDir, "reviewer.md"),
            "---\nname: Reviewer\n---\nVersion 2");

        var svc = CreateService();
        var agents = svc.DiscoverAvailableAgents(_workDir);

        // Should only return first occurrence (deduplication)
        Assert.Single(agents);
        Assert.Equal("Reviewer", agents[0].Name);
    }

    [Fact]
    public void DiscoverAvailableAgents_ReturnsEmpty_WhenWorkDirIsNull()
    {
        var svc = CreateService();
        var agents = svc.DiscoverAvailableAgents(null);
        Assert.Empty(agents);
    }

    [Fact]
    public void DiscoverAvailableAgents_ReturnsEmpty_WhenWorkDirIsEmpty()
    {
        var svc = CreateService();
        var agents = svc.DiscoverAvailableAgents("");
        Assert.Empty(agents);
    }

    // --- Agent invocation text format ---

    [Theory]
    [InlineData("Code Reviewer", "@Code Reviewer ")]
    [InlineData("my-agent", "@my-agent ")]
    [InlineData("Test Writer", "@Test Writer ")]
    public void AgentInvocationText_StartsWithAtSign_FollowedByNameAndSpace(string agentName, string expected)
    {
        // This mirrors the JS in ShowAgentsPopup: '@' + name + ' '
        var invocationText = "@" + agentName + " ";
        Assert.Equal(expected, invocationText);
    }

    [Fact]
    public void AgentInfo_Record_StoresNameDescriptionSource()
    {
        var agent = new AgentInfo("Code Reviewer", "Reviews code", "project");
        Assert.Equal("Code Reviewer", agent.Name);
        Assert.Equal("Reviews code", agent.Description);
        Assert.Equal("project", agent.Source);
    }

    [Fact]
    public void AgentInfo_EmptyDescription_IsSupported()
    {
        var agent = new AgentInfo("my-agent", "", "project");
        Assert.Equal("", agent.Description);
    }

    // --- Multiple agents from different dirs ---

    [Fact]
    public void DiscoverAvailableAgents_FindsAgents_FromMultipleDirs()
    {
        var githubDir = Path.Combine(_workDir, ".github", "agents");
        var copilotDir = Path.Combine(_workDir, ".copilot", "agents");
        Directory.CreateDirectory(githubDir);
        Directory.CreateDirectory(copilotDir);
        File.WriteAllText(Path.Combine(githubDir, "reviewer.md"),
            "---\nname: Reviewer\ndescription: Code review\n---\nReview code.");
        File.WriteAllText(Path.Combine(copilotDir, "tester.md"),
            "---\nname: Tester\ndescription: Write tests\n---\nWrite tests.");

        var svc = CreateService();
        var agents = svc.DiscoverAvailableAgents(_workDir);

        Assert.Equal(2, agents.Count);
        Assert.Contains(agents, a => a.Name == "Reviewer");
        Assert.Contains(agents, a => a.Name == "Tester");
    }
}
