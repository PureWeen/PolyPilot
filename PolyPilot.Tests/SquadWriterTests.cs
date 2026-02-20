using PolyPilot.Models;

namespace PolyPilot.Tests;

public class SquadWriterTests : IDisposable
{
    private readonly string _tempDir;

    public SquadWriterTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "squad-writer-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    [Fact]
    public void WritePreset_CreatesSquadDirectory()
    {
        var preset = MakePreset("My Team");
        var workers = new List<(string Name, string? SystemPrompt)>
        {
            ("reviewer", "You are a code reviewer. Focus on correctness."),
            ("analyst", "You are a performance analyst.")
        };

        var squadDir = SquadWriter.WritePreset(_tempDir, preset, workers);

        Assert.True(Directory.Exists(squadDir));
        Assert.True(File.Exists(Path.Combine(squadDir, "team.md")));
        Assert.True(File.Exists(Path.Combine(squadDir, "agents", "reviewer", "charter.md")));
        Assert.True(File.Exists(Path.Combine(squadDir, "agents", "analyst", "charter.md")));
    }

    [Fact]
    public void WritePreset_TeamMdHasCorrectFormat()
    {
        var preset = MakePreset("Review Squad");
        var workers = new List<(string Name, string? SystemPrompt)>
        {
            ("security", "You are a security auditor."),
            ("perf", null)
        };

        SquadWriter.WritePreset(_tempDir, preset, workers);

        var content = File.ReadAllText(Path.Combine(_tempDir, ".squad", "team.md"));
        Assert.Contains("# Review Squad", content);
        Assert.Contains("| security |", content);
        Assert.Contains("| perf |", content);
        Assert.Contains("| Member | Role |", content);
    }

    [Fact]
    public void WritePreset_CharterContainsSystemPrompt()
    {
        var preset = MakePreset("Team");
        var workers = new List<(string Name, string? SystemPrompt)>
        {
            ("dev", "You are a full-stack developer. Write clean code.")
        };

        SquadWriter.WritePreset(_tempDir, preset, workers);

        var charter = File.ReadAllText(Path.Combine(_tempDir, ".squad", "agents", "dev", "charter.md"));
        Assert.Equal("You are a full-stack developer. Write clean code.", charter);
    }

    [Fact]
    public void WritePreset_NullPromptGetsDefaultCharter()
    {
        var preset = MakePreset("Team");
        var workers = new List<(string Name, string? SystemPrompt)>
        {
            ("helper", null)
        };

        SquadWriter.WritePreset(_tempDir, preset, workers);

        var charter = File.ReadAllText(Path.Combine(_tempDir, ".squad", "agents", "helper", "charter.md"));
        Assert.Contains("helper", charter);
    }

    [Fact]
    public void WritePreset_WritesDecisionsMd()
    {
        var preset = MakePreset("Team") with { SharedContext = "Always use async/await." };
        var workers = new List<(string Name, string? SystemPrompt)> { ("w1", null) };

        SquadWriter.WritePreset(_tempDir, preset, workers);

        var decisions = File.ReadAllText(Path.Combine(_tempDir, ".squad", "decisions.md"));
        Assert.Equal("Always use async/await.", decisions);
    }

    [Fact]
    public void WritePreset_WritesRoutingMd()
    {
        var preset = MakePreset("Team") with { RoutingContext = "| *.cs | dev | C# code |" };
        var workers = new List<(string Name, string? SystemPrompt)> { ("dev", null) };

        SquadWriter.WritePreset(_tempDir, preset, workers);

        var routing = File.ReadAllText(Path.Combine(_tempDir, ".squad", "routing.md"));
        Assert.Equal("| *.cs | dev | C# code |", routing);
    }

    [Fact]
    public void WritePreset_NoSharedContext_NoDecisionsFile()
    {
        var preset = MakePreset("Team");
        var workers = new List<(string Name, string? SystemPrompt)> { ("w1", null) };

        SquadWriter.WritePreset(_tempDir, preset, workers);

        Assert.False(File.Exists(Path.Combine(_tempDir, ".squad", "decisions.md")));
    }

    [Fact]
    public void RoundTrip_WriteAndReadBack()
    {
        var preset = MakePreset("Round Trip Team") with
        {
            SharedContext = "Use TypeScript only.",
            RoutingContext = "| *.ts | dev | TypeScript |"
        };
        var workers = new List<(string Name, string? SystemPrompt)>
        {
            ("security", "You are a security auditor. Focus on OWASP."),
            ("dev", "You are a developer. Write clean code.")
        };

        SquadWriter.WritePreset(_tempDir, preset, workers);

        // Read back via SquadDiscovery
        var discovered = SquadDiscovery.Discover(_tempDir);
        Assert.Single(discovered);
        var result = discovered[0];
        Assert.Equal("Round Trip Team", result.Name);
        Assert.True(result.IsRepoLevel);
        Assert.Equal(2, result.WorkerModels.Length);
        // Order may vary by directory enumeration — check both prompts are present
        var allPrompts = string.Join(" | ", result.WorkerSystemPrompts!);
        Assert.Contains("OWASP", allPrompts);
        Assert.Contains("clean code", allPrompts);
        Assert.Contains("TypeScript", result.SharedContext);
        Assert.Contains("TypeScript", result.RoutingContext);
    }

    [Fact]
    public void RoundTrip_PreservesTeamName()
    {
        var preset = MakePreset("Special Characters & Stuff");
        var workers = new List<(string Name, string? SystemPrompt)> { ("w1", "Test prompt.") };

        SquadWriter.WritePreset(_tempDir, preset, workers);

        var discovered = SquadDiscovery.Discover(_tempDir);
        Assert.Single(discovered);
        Assert.Equal("Special Characters & Stuff", discovered[0].Name);
    }

    [Fact]
    public void SanitizeAgentName_StripsTeamPrefix()
    {
        var name = SquadWriter.SanitizeAgentName("Code Review Team-worker-1", "Code Review Team");
        Assert.Equal("worker-1", name);
    }

    [Fact]
    public void SanitizeAgentName_LowercasesResult()
    {
        var name = SquadWriter.SanitizeAgentName("MyTeam-SecurityAuditor", "MyTeam");
        Assert.Equal("securityauditor", name);
    }

    [Fact]
    public void SanitizeAgentName_NoPrefix_ReturnsLowerName()
    {
        var name = SquadWriter.SanitizeAgentName("standalone-agent", "Different Team");
        Assert.Equal("standalone-agent", name);
    }

    [Fact]
    public void DeriveRole_ExtractsFromPrompt()
    {
        var preset = MakePreset("Team");
        var workers = new List<(string Name, string? SystemPrompt)>
        {
            ("sec", "You are a security auditor. Focus on OWASP Top 10.")
        };

        SquadWriter.WritePreset(_tempDir, preset, workers);

        var content = File.ReadAllText(Path.Combine(_tempDir, ".squad", "team.md"));
        Assert.Contains("| sec | security auditor |", content);
    }

    [Fact]
    public void WritePreset_OverwritesExisting()
    {
        var preset1 = MakePreset("Team");
        var workers1 = new List<(string Name, string? SystemPrompt)> { ("old-agent", "Old charter.") };
        SquadWriter.WritePreset(_tempDir, preset1, workers1);

        var preset2 = MakePreset("Team v2");
        var workers2 = new List<(string Name, string? SystemPrompt)> { ("new-agent", "New charter.") };
        SquadWriter.WritePreset(_tempDir, preset2, workers2);

        var content = File.ReadAllText(Path.Combine(_tempDir, ".squad", "team.md"));
        Assert.Contains("# Team v2", content);
        Assert.Contains("| new-agent |", content);
        // Old agent dir may still exist (we don't delete, just overwrite)
        Assert.True(Directory.Exists(Path.Combine(_tempDir, ".squad", "agents", "new-agent")));
    }

    [Fact]
    public void WriteFromGroup_CreatesSquadFromSessionData()
    {
        var group = new SessionGroup
        {
            Name = "Live Team",
            IsMultiAgent = true,
            OrchestratorMode = MultiAgentMode.OrchestratorReflect,
            SharedContext = "Be concise.",
        };
        var members = new List<SessionMeta>
        {
            new() { SessionName = "Live Team-orchestrator", Role = MultiAgentRole.Orchestrator, PreferredModel = "claude-opus-4.6" },
            new() { SessionName = "Live Team-worker-1", Role = MultiAgentRole.Worker, PreferredModel = "gpt-5", SystemPrompt = "You are a code reviewer." },
            new() { SessionName = "Live Team-worker-2", Role = MultiAgentRole.Worker, PreferredModel = "claude-sonnet-4.5", SystemPrompt = "You are a test writer." },
        };
        string GetModel(string name) => members.First(m => m.SessionName == name).PreferredModel ?? "default";

        var squadDir = SquadWriter.WriteFromGroup(_tempDir, "Live Team", group, members, GetModel);

        Assert.True(File.Exists(Path.Combine(squadDir, "team.md")));
        Assert.True(File.Exists(Path.Combine(squadDir, "decisions.md")));
        Assert.True(File.Exists(Path.Combine(squadDir, "agents", "worker-1", "charter.md")));
        Assert.True(File.Exists(Path.Combine(squadDir, "agents", "worker-2", "charter.md")));

        // Verify round-trip
        var discovered = SquadDiscovery.Discover(_tempDir);
        Assert.Single(discovered);
        Assert.Equal("Live Team", discovered[0].Name);
        Assert.Equal(2, discovered[0].WorkerModels.Length);
    }

    private static GroupPreset MakePreset(string name) => new(
        name, "Test", "🧪", MultiAgentMode.OrchestratorReflect,
        "claude-opus-4.6", new[] { "gpt-5", "claude-sonnet-4.5" });
}
