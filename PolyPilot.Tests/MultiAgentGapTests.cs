using PolyPilot.Models;
using PolyPilot.Services;

namespace PolyPilot.Tests;

/// <summary>
/// Gap-coverage tests for multi-agent parsing, model capabilities, and reflection summaries.
/// </summary>
public class MultiAgentGapTests
{
    // --- ParseTaskAssignments ---

    [Fact]
    public void ParseTaskAssignments_EmptyInput_ReturnsEmpty()
    {
        var result = CopilotService.ParseTaskAssignments("", new List<string> { "a", "b" });
        Assert.Empty(result);
    }

    [Fact]
    public void ParseTaskAssignments_SingleWorker_ExtractsTask()
    {
        var response = "@worker:alpha\nDo the thing.\n@end";
        var result = CopilotService.ParseTaskAssignments(response, new List<string> { "alpha" });

        Assert.Single(result);
        Assert.Equal("alpha", result[0].WorkerName);
        Assert.Contains("Do the thing", result[0].Task);
    }

    [Fact]
    public void ParseTaskAssignments_MultipleWorkers_ExtractsAll()
    {
        var response = @"@worker:w1
Task one.
@end
@worker:w2
Task two.
@end
@worker:w3
Task three.
@end";
        var workers = new List<string> { "w1", "w2", "w3" };
        var result = CopilotService.ParseTaskAssignments(response, workers);

        Assert.Equal(3, result.Count);
        Assert.Equal("w1", result[0].WorkerName);
        Assert.Equal("w2", result[1].WorkerName);
        Assert.Equal("w3", result[2].WorkerName);
    }

    [Fact]
    public void ParseTaskAssignments_FuzzyMatch_FindsClosestWorker()
    {
        // "coder" is a substring of "coder-session" → fuzzy match
        var response = "@worker:coder\nWrite the code.\n@end";
        var result = CopilotService.ParseTaskAssignments(response, new List<string> { "coder-session", "reviewer-session" });

        Assert.Single(result);
        Assert.Equal("coder-session", result[0].WorkerName);
    }

    [Fact]
    public void ParseTaskAssignments_UnknownWorker_IsIgnored()
    {
        var response = "@worker:ghost\nDo something.\n@end";
        var result = CopilotService.ParseTaskAssignments(response, new List<string> { "alpha", "beta" });

        Assert.Empty(result);
    }

    [Fact]
    public void ParseTaskAssignments_DuplicateWorker_TakesLast()
    {
        var response = @"@worker:alpha
First task.
@end
@worker:alpha
Second task.
@end";
        var result = CopilotService.ParseTaskAssignments(response, new List<string> { "alpha" });

        // The regex matches both blocks; both are added (last one wins in practice)
        Assert.Equal(2, result.Count);
        Assert.Contains("Second task", result[^1].Task);
    }

    [Fact]
    public void ParseTaskAssignments_WorkerNamesWithSpaces_MatchesAll()
    {
        var response = @"@worker:PR Review Squad-worker-1
Review for bugs.
@end
@worker:PR Review Squad-worker-2
Review for security.
@end
@worker:PR Review Squad-worker-3
Review architecture.
@end";
        var workers = new List<string>
        {
            "PR Review Squad-worker-1",
            "PR Review Squad-worker-2",
            "PR Review Squad-worker-3"
        };
        var result = CopilotService.ParseTaskAssignments(response, workers);

        Assert.Equal(3, result.Count);
        Assert.Equal("PR Review Squad-worker-1", result[0].WorkerName);
        Assert.Equal("PR Review Squad-worker-2", result[1].WorkerName);
        Assert.Equal("PR Review Squad-worker-3", result[2].WorkerName);
        Assert.Contains("bugs", result[0].Task);
        Assert.Contains("security", result[1].Task);
        Assert.Contains("architecture", result[2].Task);
    }

    [Fact]
    public void ParseTaskAssignments_WorkerNamesWithSpaces_NoEnd_MatchesAll()
    {
        // Orchestrators sometimes omit @end — the regex should still capture via lookahead
        var response = @"@worker:My Team-worker-1
Task one content.

@worker:My Team-worker-2
Task two content.
";
        var workers = new List<string> { "My Team-worker-1", "My Team-worker-2" };
        var result = CopilotService.ParseTaskAssignments(response, workers);

        Assert.Equal(2, result.Count);
        Assert.Equal("My Team-worker-1", result[0].WorkerName);
        Assert.Equal("My Team-worker-2", result[1].WorkerName);
    }

    [Fact]
    public void ParseTaskAssignments_MixedSimpleAndSpacedNames_MatchesAll()
    {
        var response = @"@worker:simple-worker
Do task A.
@end
@worker:Squad Team-worker-2
Do task B.
@end";
        var workers = new List<string> { "simple-worker", "Squad Team-worker-2" };
        var result = CopilotService.ParseTaskAssignments(response, workers);

        Assert.Equal(2, result.Count);
        Assert.Equal("simple-worker", result[0].WorkerName);
        Assert.Equal("Squad Team-worker-2", result[1].WorkerName);
    }

    // --- ModelCapabilities ---

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void GetCapabilities_NullOrEmpty_ReturnsNone(string? slug)
    {
        var caps = ModelCapabilities.GetCapabilities(slug!);
        Assert.Equal(ModelCapability.None, caps);
    }

    [Fact]
    public void GetCapabilities_KnownModel_ReturnsFlags()
    {
        var caps = ModelCapabilities.GetCapabilities("gpt-5");
        Assert.True(caps.HasFlag(ModelCapability.ReasoningExpert));
        Assert.True(caps.HasFlag(ModelCapability.CodeExpert));
        Assert.True(caps.HasFlag(ModelCapability.ToolUse));
    }

    [Fact]
    public void GetRoleWarnings_UnknownModel_ReturnsWarning()
    {
        var warnings = ModelCapabilities.GetRoleWarnings("totally-unknown-model", MultiAgentRole.Worker);
        Assert.NotEmpty(warnings);
        Assert.Contains(warnings, w => w.Contains("Unknown model", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void GetRoleWarnings_WeakOrchestrator_ReturnsWarning()
    {
        // claude-haiku-4.5 is CostEfficient + Fast but not ReasoningExpert
        var warnings = ModelCapabilities.GetRoleWarnings("claude-haiku-4.5", MultiAgentRole.Orchestrator);
        Assert.NotEmpty(warnings);
        Assert.Contains(warnings, w => w.Contains("reasoning", StringComparison.OrdinalIgnoreCase));
    }

    // --- BuildCompletionSummary ---

    [Fact]
    public void BuildCompletionSummary_GoalMet_ShowsCheckmark()
    {
        var cycle = ReflectionCycle.Create("Ship the feature", maxIterations: 5);
        cycle.Advance("Done!\n[[REFLECTION_COMPLETE]]");

        var summary = cycle.BuildCompletionSummary();

        Assert.Contains("✅", summary);
        Assert.Contains("Goal met", summary);
    }

    [Fact]
    public void BuildCompletionSummary_Stalled_ShowsWarning()
    {
        var cycle = ReflectionCycle.Create("Improve quality", maxIterations: 10);
        // Feed identical responses to trigger stall detection
        cycle.Advance("Working on the task with specific details about implementation");
        cycle.Advance("Working on the task with specific details about implementation");
        cycle.Advance("Working on the task with specific details about implementation");

        var summary = cycle.BuildCompletionSummary();

        // IsStalled takes priority over IsCancelled in the ternary chain
        Assert.Contains("⚠️", summary);
        Assert.Contains("Stalled", summary);
        Assert.DoesNotContain("⏹️", summary);
    }

    [Fact]
    public void BuildCompletionSummary_Cancelled_ShowsStop()
    {
        var cycle = ReflectionCycle.Create("Long task", maxIterations: 10);
        cycle.Advance("First attempt with unique content here...");
        cycle.IsCancelled = true;
        cycle.IsActive = false;

        var summary = cycle.BuildCompletionSummary();

        Assert.Contains("⏹️", summary);
        Assert.Contains("Cancelled", summary);
    }

    [Fact]
    public void BuildCompletionSummary_MaxIterations_ShowsClock()
    {
        var cycle = ReflectionCycle.Create("Goal", maxIterations: 2);
        cycle.Advance("Trying with approach alpha...");
        cycle.Advance("Still trying with approach beta and new ideas...");

        var summary = cycle.BuildCompletionSummary();

        Assert.Contains("⏱️", summary);
        Assert.Contains("Max iterations", summary);
        Assert.Contains("2/2", summary);
    }
}
