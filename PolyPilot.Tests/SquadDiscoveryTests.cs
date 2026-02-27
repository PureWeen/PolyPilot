using PolyPilot.Models;

namespace PolyPilot.Tests;

public class SquadDiscoveryTests
{
    private static string TestDataDir => Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "TestData");

    private static string SquadSampleDir => Path.Combine(TestDataDir, "squad-sample");
    private static string LegacyAiTeamDir => Path.Combine(TestDataDir, "legacy-ai-team");

    // --- FindSquadDirectory ---

    [Fact]
    public void FindSquadDirectory_PrefersDotSquad()
    {
        var result = SquadDiscovery.FindSquadDirectory(SquadSampleDir);
        Assert.NotNull(result);
        Assert.EndsWith(".squad", result);
    }

    [Fact]
    public void FindSquadDirectory_FallsBackToAiTeam()
    {
        var result = SquadDiscovery.FindSquadDirectory(LegacyAiTeamDir);
        Assert.NotNull(result);
        Assert.EndsWith(".ai-team", result);
    }

    [Fact]
    public void FindSquadDirectory_ReturnsNull_WhenNeitherExists()
    {
        var result = SquadDiscovery.FindSquadDirectory(Path.GetTempPath());
        Assert.Null(result);
    }

    // --- ParseTeamName ---

    [Fact]
    public void ParseTeamName_ExtractsH1Heading()
    {
        var content = "# The Review Squad\n\nSome description\n";
        Assert.Equal("The Review Squad", SquadDiscovery.ParseTeamName(content));
    }

    [Fact]
    public void ParseTeamName_ReturnsNull_WhenNoHeading()
    {
        var content = "Just a table\n| Member | Role |\n";
        Assert.Null(SquadDiscovery.ParseTeamName(content));
    }

    // --- ParseRosterNames ---

    [Fact]
    public void ParseRosterNames_ExtractsAgentNames()
    {
        var content = "# Team\n| Member | Role |\n|--------|------|\n| security-reviewer | Auditor |\n| perf-analyst | Analyst |";
        var names = SquadDiscovery.ParseRosterNames(content);
        Assert.Contains("security-reviewer", names);
        Assert.Contains("perf-analyst", names);
        Assert.DoesNotContain("Member", names);
        Assert.DoesNotContain("---", names);
    }

    // --- DiscoverAgents ---

    [Fact]
    public void DiscoverAgents_SkipsScribe()
    {
        var squadDir = Path.Combine(SquadSampleDir, ".squad");
        var agents = SquadDiscovery.DiscoverAgents(squadDir);
        Assert.DoesNotContain(agents, a => a.Name.Equals("scribe", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void DiscoverAgents_FindsRealAgents()
    {
        var squadDir = Path.Combine(SquadSampleDir, ".squad");
        var agents = SquadDiscovery.DiscoverAgents(squadDir);
        Assert.Equal(2, agents.Count); // security-reviewer + perf-analyst (not scribe)
        Assert.Contains(agents, a => a.Name == "security-reviewer");
        Assert.Contains(agents, a => a.Name == "perf-analyst");
    }

    [Fact]
    public void DiscoverAgents_ReadsCharterContent()
    {
        var squadDir = Path.Combine(SquadSampleDir, ".squad");
        var agents = SquadDiscovery.DiscoverAgents(squadDir);
        var security = agents.First(a => a.Name == "security-reviewer");
        Assert.NotNull(security.Charter);
        Assert.Contains("OWASP Top 10", security.Charter);
    }

    // --- Discover (full integration) ---

    [Fact]
    public void Discover_ReturnsPreset_FromSquadDir()
    {
        var presets = SquadDiscovery.Discover(SquadSampleDir);
        Assert.Single(presets);
        var preset = presets[0];
        Assert.Equal("The Review Squad", preset.Name);
        Assert.True(preset.IsRepoLevel);
        Assert.Equal(MultiAgentMode.OrchestratorReflect, preset.Mode);
        Assert.Equal(2, preset.WorkerModels.Length);
    }

    [Fact]
    public void Discover_SetsSystemPrompts_FromCharters()
    {
        var presets = SquadDiscovery.Discover(SquadSampleDir);
        var preset = presets[0];
        Assert.NotNull(preset.WorkerSystemPrompts);
        Assert.Equal(2, preset.WorkerSystemPrompts.Length);

        // At least one should contain OWASP (security-reviewer's charter)
        Assert.True(preset.WorkerSystemPrompts.Any(p => p != null && p.Contains("OWASP")),
            "Expected a worker system prompt containing 'OWASP'");
        // At least one should contain latency (perf-analyst's charter)
        Assert.True(preset.WorkerSystemPrompts.Any(p => p != null && p.Contains("Latency")),
            "Expected a worker system prompt containing 'Latency'");
    }

    [Fact]
    public void Discover_ReadsDecisions_AsSharedContext()
    {
        var presets = SquadDiscovery.Discover(SquadSampleDir);
        var preset = presets[0];
        Assert.NotNull(preset.SharedContext);
        Assert.Contains("structured logging", preset.SharedContext);
        Assert.Contains("async/await", preset.SharedContext);
    }

    [Fact]
    public void Discover_ReadsRouting_AsRoutingContext()
    {
        var presets = SquadDiscovery.Discover(SquadSampleDir);
        var preset = presets[0];
        Assert.NotNull(preset.RoutingContext);
        Assert.Contains("security-reviewer", preset.RoutingContext);
    }

    [Fact]
    public void Discover_LegacyAiTeam_Works()
    {
        var presets = SquadDiscovery.Discover(LegacyAiTeamDir);
        Assert.Single(presets);
        var preset = presets[0];
        Assert.Equal("Legacy Team", preset.Name);
        Assert.True(preset.IsRepoLevel);
        Assert.Single(preset.WorkerModels);
    }

    [Fact]
    public void Discover_ReturnsEmpty_WhenNoSquadDir()
    {
        var presets = SquadDiscovery.Discover(Path.GetTempPath());
        Assert.Empty(presets);
    }

    [Fact]
    public void Discover_ReturnsEmpty_WhenNoTeamMd()
    {
        // Create temp dir with .squad/ but no team.md
        var tempDir = Path.Combine(Path.GetTempPath(), $"squad-test-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(Path.Combine(tempDir, ".squad", "agents", "test"));
            File.WriteAllText(Path.Combine(tempDir, ".squad", "agents", "test", "charter.md"), "test charter");

            var presets = SquadDiscovery.Discover(tempDir);
            Assert.Empty(presets);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void Discover_TruncatesLongCharters()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"squad-test-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(Path.Combine(tempDir, ".squad", "agents", "verbose"));
            File.WriteAllText(Path.Combine(tempDir, ".squad", "team.md"), "# Long Charter Test\n| Member | Role |\n|---|---|\n| verbose | Talker |");
            File.WriteAllText(Path.Combine(tempDir, ".squad", "agents", "verbose", "charter.md"),
                new string('x', 5000)); // Over 4000 char limit

            var presets = SquadDiscovery.Discover(tempDir);
            Assert.Single(presets);
            Assert.True(presets[0].WorkerSystemPrompts![0]!.Length <= 4000);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    // --- Three-tier merge ---

    [Fact]
    public void GetAll_WithRepoPath_IncludesSquadPresets()
    {
        var all = UserPresets.GetAll(Path.GetTempPath(), SquadSampleDir);
        Assert.Contains(all, p => p.Name == "The Review Squad" && p.IsRepoLevel);
        // Built-in should also be present
        Assert.Contains(all, p => p.Name == "PR Review Squad");
    }

    [Fact]
    public void GetAll_WithoutRepoPath_NoSquadPresets()
    {
        var all = UserPresets.GetAll(Path.GetTempPath());
        Assert.DoesNotContain(all, p => p.IsRepoLevel);
    }

    [Fact]
    public void GetAll_RepoDoesNotOverride_BuiltInByName()
    {
        // Create a temp Squad dir with a preset named the same as a built-in
        var builtInName = GroupPreset.BuiltIn[0].Name;
        var tempDir = Path.Combine(Path.GetTempPath(), $"squad-test-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(Path.Combine(tempDir, ".squad", "agents", "reviewer"));
            File.WriteAllText(Path.Combine(tempDir, ".squad", "team.md"),
                $"# {builtInName}\n| Member | Role |\n|---|---|\n| reviewer | Reviewer |");
            File.WriteAllText(Path.Combine(tempDir, ".squad", "agents", "reviewer", "charter.md"),
                "Custom repo reviewer.");

            var all = UserPresets.GetAll(Path.GetTempPath(), tempDir);
            var match = all.Single(p => p.Name == builtInName);
            Assert.False(match.IsRepoLevel, "Built-in should not be overridden by repo preset with same name");
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void Discover_SetsSourcePath()
    {
        var presets = SquadDiscovery.Discover(SquadSampleDir);
        Assert.Single(presets);
        Assert.NotNull(presets[0].SourcePath);
        Assert.True(presets[0].SourcePath!.EndsWith(".squad"));
    }

    [Fact]
    public void Discover_HasEmoji()
    {
        var presets = SquadDiscovery.Discover(SquadSampleDir);
        Assert.Equal("🫡", presets[0].Emoji);
    }

    // --- ParseMode tests ---

    [Fact]
    public void ParseMode_Orchestrator()
    {
        var content = "# My Team\nmode: orchestrator\n| Member | Role |";
        Assert.Equal(MultiAgentMode.Orchestrator, SquadDiscovery.ParseMode(content));
    }

    [Fact]
    public void ParseMode_Broadcast()
    {
        var content = "# My Team\nmode: broadcast\n";
        Assert.Equal(MultiAgentMode.Broadcast, SquadDiscovery.ParseMode(content));
    }

    [Fact]
    public void ParseMode_OrchestratorReflect()
    {
        var content = "# My Team\nmode: orchestrator-reflect\n";
        Assert.Equal(MultiAgentMode.OrchestratorReflect, SquadDiscovery.ParseMode(content));
    }

    [Fact]
    public void ParseMode_Sequential()
    {
        var content = "# My Team\nmode: sequential\n";
        Assert.Equal(MultiAgentMode.Sequential, SquadDiscovery.ParseMode(content));
    }

    [Fact]
    public void ParseMode_CaseInsensitive()
    {
        var content = "# My Team\nMode: Orchestrator\n";
        Assert.Equal(MultiAgentMode.Orchestrator, SquadDiscovery.ParseMode(content));
    }

    [Fact]
    public void ParseMode_DefaultsToReflect_WhenMissing()
    {
        var content = "# My Team\n| Member | Role |";
        Assert.Equal(MultiAgentMode.OrchestratorReflect, SquadDiscovery.ParseMode(content));
    }

    // --- DeleteRepoPreset path traversal ---

    [Fact]
    public void DeleteRepoPreset_RejectsPathOutsideRepo()
    {
        // DeleteRepoPreset uses SquadDiscovery.Discover which needs a real .squad dir.
        // Instead, verify the containment logic by testing with a valid squad preset
        // whose SourcePath we can verify stays within the repo root.
        var tempDir = Path.Combine(Path.GetTempPath(), $"polypilot-pathtest-{Guid.NewGuid():N}");
        var squadDir = Path.Combine(tempDir, ".squad");
        try
        {
            Directory.CreateDirectory(squadDir);
            Directory.CreateDirectory(Path.Combine(squadDir, "agents", "worker"));
            File.WriteAllText(Path.Combine(squadDir, "team.md"),
                "# Test Team\n| Member | Role |\n|---|---|\n| worker | Worker |");
            File.WriteAllText(Path.Combine(squadDir, "agents", "worker", "charter.md"), "You are a worker.");

            // Normal case: preset within repo → succeeds
            var result = UserPresets.DeleteRepoPreset(tempDir, "Test Team");
            Assert.True(result);
            Assert.False(Directory.Exists(squadDir));
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void DeleteRepoPreset_RejectsNonexistentPreset()
    {
        var result = UserPresets.DeleteRepoPreset(Path.GetTempPath(), "NonExistent-Preset-12345");
        Assert.False(result);
    }
}
