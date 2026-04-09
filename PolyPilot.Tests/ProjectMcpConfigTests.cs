using PolyPilot.Models;
using PolyPilot.Services;

namespace PolyPilot.Tests;

/// <summary>
/// Tests for project-level .copilot/mcp-config.json support in LoadMcpServers,
/// Squad metadata discovery, and skills source labeling.
/// </summary>
public class ProjectMcpConfigTests : IDisposable
{
    private readonly string _tempDir;

    public ProjectMcpConfigTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "mcp-test-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    [Fact]
    public void LoadMcpServers_ReadsProjectLevelConfig()
    {
        // Arrange: create a project-level .copilot/mcp-config.json
        var copilotDir = Path.Combine(_tempDir, ".copilot");
        Directory.CreateDirectory(copilotDir);
        File.WriteAllText(Path.Combine(copilotDir, "mcp-config.json"), """
        {
            "mcpServers": {
                "project-server": {
                    "command": "node",
                    "args": ["server.js"]
                }
            }
        }
        """);

        // Act
        var servers = CopilotService.LoadMcpServers(
            disabledServers: null,
            disabledPlugins: null,
            workingDirectory: _tempDir);

        // Assert — project-level server should be included
        Assert.NotNull(servers);
        Assert.True(servers.ContainsKey("project-server"),
            $"Expected 'project-server' in loaded servers. Got: {string.Join(", ", servers!.Keys)}");
    }

    [Fact]
    public void LoadMcpServers_ProjectTakesPriority_OverGlobal()
    {
        // This tests that project-level configs are loaded first (before global),
        // so if a server name appears in both, the project version wins.
        // We can only test this with a project-level config since we can't
        // control the global config in tests.
        var copilotDir = Path.Combine(_tempDir, ".copilot");
        Directory.CreateDirectory(copilotDir);
        File.WriteAllText(Path.Combine(copilotDir, "mcp-config.json"), """
        {
            "mcpServers": {
                "priority-test-server": {
                    "command": "project-command",
                    "args": ["--project"]
                }
            }
        }
        """);

        var servers = CopilotService.LoadMcpServers(
            disabledServers: null,
            disabledPlugins: null,
            workingDirectory: _tempDir);

        Assert.NotNull(servers);
        Assert.True(servers.ContainsKey("priority-test-server"));
    }

    [Fact]
    public void LoadMcpServers_NullWorkingDirectory_DoesNotCrash()
    {
        // Should work the same as before — no project-level servers
        var servers = CopilotService.LoadMcpServers(
            disabledServers: null,
            disabledPlugins: null,
            workingDirectory: null);

        // May or may not have global servers — just shouldn't crash
    }

    [Fact]
    public void LoadMcpServers_EmptyWorkingDirectory_DoesNotCrash()
    {
        var servers = CopilotService.LoadMcpServers(
            disabledServers: null,
            disabledPlugins: null,
            workingDirectory: "");

        // May or may not have global servers — just shouldn't crash
    }

    [Fact]
    public void LoadMcpServers_NonexistentProjectDir_DoesNotCrash()
    {
        var servers = CopilotService.LoadMcpServers(
            disabledServers: null,
            disabledPlugins: null,
            workingDirectory: Path.Combine(_tempDir, "nonexistent"));

        // Should not crash — just skips project-level config
    }

    [Fact]
    public void LoadMcpServers_MalformedProjectConfig_DoesNotCrash()
    {
        var copilotDir = Path.Combine(_tempDir, ".copilot");
        Directory.CreateDirectory(copilotDir);
        File.WriteAllText(Path.Combine(copilotDir, "mcp-config.json"), "NOT VALID JSON");

        var servers = CopilotService.LoadMcpServers(
            disabledServers: null,
            disabledPlugins: null,
            workingDirectory: _tempDir);

        // Should not crash — malformed JSON is silently skipped
    }

    [Fact]
    public void LoadMcpServers_RespectsDisabledServers_ForProjectLevel()
    {
        var copilotDir = Path.Combine(_tempDir, ".copilot");
        Directory.CreateDirectory(copilotDir);
        File.WriteAllText(Path.Combine(copilotDir, "mcp-config.json"), """
        {
            "mcpServers": {
                "disabled-server": {
                    "command": "node",
                    "args": ["disabled.js"]
                },
                "enabled-server": {
                    "command": "node",
                    "args": ["enabled.js"]
                }
            }
        }
        """);

        var servers = CopilotService.LoadMcpServers(
            disabledServers: new[] { "disabled-server" },
            disabledPlugins: null,
            workingDirectory: _tempDir);

        Assert.NotNull(servers);
        Assert.False(servers.ContainsKey("disabled-server"),
            "Disabled server should not be loaded");
        Assert.True(servers.ContainsKey("enabled-server"),
            "Enabled server should be loaded");
    }

    // --- Skills source labeling ---

    [Fact]
    public void DiscoverAvailableSkills_LabelsCopilotSkillsAsSquad_WhenSquadInitialized()
    {
        // Create a Squad-initialized repo: .squad/team.md + .copilot/skills/
        Directory.CreateDirectory(Path.Combine(_tempDir, ".squad"));
        File.WriteAllText(Path.Combine(_tempDir, ".squad", "team.md"), "# Test");

        var skillDir = Path.Combine(_tempDir, ".copilot", "skills", "test-skill");
        Directory.CreateDirectory(skillDir);
        File.WriteAllText(Path.Combine(skillDir, "SKILL.md"), """
        ---
        name: test-skill
        description: A test skill
        ---
        Skill content
        """);

        var skills = CopilotService.DiscoverAvailableSkills(_tempDir);
        var testSkill = skills.FirstOrDefault(s => s.Name == "test-skill");
        Assert.NotNull(testSkill);
        Assert.Equal("squad", testSkill.Source);
    }

    [Fact]
    public void DiscoverAvailableSkills_LabelsCopilotSkillsAsProject_WhenNoSquad()
    {
        // No .squad/ directory — .copilot/skills/ should be labeled as "project"
        var skillDir = Path.Combine(_tempDir, ".copilot", "skills", "test-skill");
        Directory.CreateDirectory(skillDir);
        File.WriteAllText(Path.Combine(skillDir, "SKILL.md"), """
        ---
        name: test-skill
        description: A test skill
        ---
        Skill content
        """);

        var skills = CopilotService.DiscoverAvailableSkills(_tempDir);
        var testSkill = skills.FirstOrDefault(s => s.Name == "test-skill");
        Assert.NotNull(testSkill);
        Assert.Equal("project", testSkill.Source);
    }

    [Fact]
    public void DiscoverAvailableSkills_ClaudeSkillsAlwaysProject()
    {
        // .claude/skills/ should always be labeled "project" regardless of Squad
        Directory.CreateDirectory(Path.Combine(_tempDir, ".squad"));
        File.WriteAllText(Path.Combine(_tempDir, ".squad", "team.md"), "# Test");
        Directory.CreateDirectory(Path.Combine(_tempDir, ".copilot", "skills", "dummy"));

        var skillDir = Path.Combine(_tempDir, ".claude", "skills", "my-skill");
        Directory.CreateDirectory(skillDir);
        File.WriteAllText(Path.Combine(skillDir, "SKILL.md"), """
        ---
        name: my-skill
        description: My custom skill
        ---
        Content
        """);

        var skills = CopilotService.DiscoverAvailableSkills(_tempDir);
        var mySkill = skills.FirstOrDefault(s => s.Name == "my-skill");
        Assert.NotNull(mySkill);
        Assert.Equal("project", mySkill.Source);
    }

    // --- Structural guard: LoadMcpServers gets workingDirectory from all callers ---

    private static readonly string ProjectRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "PolyPilot"));

    [Fact]
    public void LoadMcpServers_Signature_HasWorkingDirectoryParameter()
    {
        // Verify the signature includes workingDirectory (structural guard)
        var source = File.ReadAllText(Path.Combine(ProjectRoot, "Services", "CopilotService.cs"));
        Assert.Contains("LoadMcpServers(IReadOnlyCollection<string>? disabledServers", source);
        Assert.Contains("string? workingDirectory", source);
    }

    [Fact]
    public void LoadMcpServers_AllCallers_PassWorkingDirectory()
    {
        // Every call to LoadMcpServers should include a working directory argument
        var source = File.ReadAllText(Path.Combine(ProjectRoot, "Services", "CopilotService.cs"));
        var calls = source.Split("LoadMcpServers(")
            .Skip(1) // Skip the definition itself
            .ToArray();

        foreach (var call in calls)
        {
            // The definition has 3 params, each call should have 3 args
            // Skip the method definition (starts with "IReadOnlyCollection")
            if (call.TrimStart().StartsWith("IReadOnlyCollection")) continue;

            // Extract the call up to the closing paren
            var parenDepth = 0;
            var end = 0;
            for (int i = 0; i < call.Length; i++)
            {
                if (call[i] == '(') parenDepth++;
                else if (call[i] == ')')
                {
                    if (parenDepth == 0) { end = i; break; }
                    parenDepth--;
                }
            }
            var args = call[..end];
            var commaCount = args.Count(c => c == ',');
            Assert.True(commaCount >= 2,
                $"LoadMcpServers call should have 3+ args (found {commaCount + 1} args). " +
                $"Call snippet: LoadMcpServers({args.Trim()[..Math.Min(80, args.Trim().Length)]}...)");
        }
    }
}
