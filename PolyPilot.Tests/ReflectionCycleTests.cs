using PolyPilot.Models;

namespace PolyPilot.Tests;

public class ReflectionCycleTests
{
    [Fact]
    public void Create_SetsDefaults()
    {
        var cycle = ReflectionCycle.Create("Fix the bug");

        Assert.Equal("Fix the bug", cycle.Goal);
        Assert.Equal(5, cycle.MaxIterations);
        Assert.Equal(0, cycle.CurrentIteration);
        Assert.True(cycle.IsActive);
        Assert.False(cycle.GoalMet);
        Assert.False(cycle.IsStalled);
        Assert.Equal("", cycle.EvaluationPrompt);
        Assert.NotNull(cycle.StartedAt);
        Assert.Null(cycle.CompletedAt);
        Assert.False(cycle.IsPaused);
    }

    [Fact]
    public void Create_WithCustomMaxIterations()
    {
        var cycle = ReflectionCycle.Create("Optimize code", maxIterations: 10);

        Assert.Equal(10, cycle.MaxIterations);
    }

    [Fact]
    public void Create_WithCustomEvaluationPrompt()
    {
        var cycle = ReflectionCycle.Create("Refactor", evaluationPrompt: "Check for clean code");

        Assert.Equal("Check for clean code", cycle.EvaluationPrompt);
    }

    [Fact]
    public void IsGoalMet_WithSentinelOnOwnLine_ReturnsTrue()
    {
        var cycle = ReflectionCycle.Create("Test goal");

        Assert.True(cycle.IsGoalMet("Some work done.\n[[REFLECTION_COMPLETE]]\n"));
    }

    [Fact]
    public void IsGoalMet_WithSentinelAtEnd_ReturnsTrue()
    {
        var cycle = ReflectionCycle.Create("Test goal");

        Assert.True(cycle.IsGoalMet("All done!\n[[REFLECTION_COMPLETE]]"));
    }

    [Fact]
    public void IsGoalMet_WithSentinelWithWhitespace_ReturnsTrue()
    {
        var cycle = ReflectionCycle.Create("Test goal");

        Assert.True(cycle.IsGoalMet("Done.\n  [[REFLECTION_COMPLETE]]  \nExtra text"));
    }

    [Fact]
    public void IsGoalMet_SentinelEmbeddedInText_ReturnsFalse()
    {
        var cycle = ReflectionCycle.Create("Test goal");

        // Sentinel must be on its own line, not embedded in prose
        Assert.False(cycle.IsGoalMet("I said [[REFLECTION_COMPLETE]] in the middle of a sentence"));
    }

    [Fact]
    public void IsGoalMet_WithoutSentinel_ReturnsFalse()
    {
        var cycle = ReflectionCycle.Create("Test goal");

        Assert.False(cycle.IsGoalMet("Still working on it..."));
    }

    [Fact]
    public void IsGoalMet_OldMarkerDoesNotTrigger()
    {
        var cycle = ReflectionCycle.Create("Test goal");

        // The old "Goal complete" marker should NOT trigger completion
        Assert.False(cycle.IsGoalMet("✅ Goal complete - everything looks good"));
        Assert.False(cycle.IsGoalMet("Goal complete - all done"));
        Assert.False(cycle.IsGoalMet("GOAL COMPLETE - finished"));
    }

    [Fact]
    public void IsGoalMet_NaturalProseFalsePositive_ReturnsFalse()
    {
        var cycle = ReflectionCycle.Create("Test goal");

        // Sentences that naturally contain "goal complete" should not trigger
        Assert.False(cycle.IsGoalMet("The goal complete with all requirements met."));
        Assert.False(cycle.IsGoalMet("I have not made the goal complete yet."));
    }

    [Fact]
    public void IsGoalMet_EmptyResponse_ReturnsFalse()
    {
        var cycle = ReflectionCycle.Create("Test goal");

        Assert.False(cycle.IsGoalMet(""));
        Assert.False(cycle.IsGoalMet(null!));
    }

    [Fact]
    public void Advance_ActiveCycle_NoGoalMet_ReturnsTrue()
    {
        var cycle = ReflectionCycle.Create("Test goal", maxIterations: 3);

        Assert.True(cycle.Advance("Working on it..."));
        Assert.Equal(1, cycle.CurrentIteration);
    }

    [Fact]
    public void Advance_GoalMet_ReturnsFalse()
    {
        var cycle = ReflectionCycle.Create("Test goal");

        Assert.False(cycle.Advance("Done!\n[[REFLECTION_COMPLETE]]"));
        Assert.True(cycle.GoalMet);
        Assert.False(cycle.IsActive);
        Assert.Equal(1, cycle.CurrentIteration);
    }

    [Fact]
    public void Advance_MaxIterationsReached_ReturnsFalse()
    {
        var cycle = ReflectionCycle.Create("Test goal", maxIterations: 2);

        Assert.True(cycle.Advance("Trying..."));
        Assert.False(cycle.Advance("Still going with different content and new ideas..."));
        Assert.False(cycle.IsActive);
        Assert.False(cycle.GoalMet);
        Assert.Equal(2, cycle.CurrentIteration);
    }

    [Fact]
    public void Advance_InactiveCycle_ReturnsFalse()
    {
        var cycle = ReflectionCycle.Create("Test goal");
        cycle.IsActive = false;

        Assert.False(cycle.Advance("Any response"));
        Assert.Equal(0, cycle.CurrentIteration);
    }

    [Fact]
    public void Advance_IncrementsIteration()
    {
        var cycle = ReflectionCycle.Create("Goal", maxIterations: 10);

        cycle.Advance("response 1 with unique content alpha");
        Assert.Equal(1, cycle.CurrentIteration);

        cycle.Advance("response 2 with different content beta");
        Assert.Equal(2, cycle.CurrentIteration);

        cycle.Advance("response 3 with more unique content gamma");
        Assert.Equal(3, cycle.CurrentIteration);
    }

    [Fact]
    public void Advance_StallDetection_StopsAfterTwoConsecutiveStalls()
    {
        var cycle = ReflectionCycle.Create("Goal", maxIterations: 10);

        // First response — establishes baseline
        Assert.True(cycle.Advance("Working on the task with specific details about implementation"));
        Assert.False(cycle.ShouldWarnOnStall);

        // Second response — nearly identical (stall #1, allowed)
        Assert.True(cycle.Advance("Working on the task with specific details about implementation"));
        Assert.True(cycle.ShouldWarnOnStall);

        // Third response — still identical (stall #2, stops)
        Assert.False(cycle.Advance("Working on the task with specific details about implementation"));
        Assert.False(cycle.ShouldWarnOnStall);
        Assert.True(cycle.IsStalled);
        Assert.False(cycle.IsActive);
        Assert.False(cycle.GoalMet);
    }

    [Fact]
    public void Advance_StallDetection_ResetsOnProgress()
    {
        var cycle = ReflectionCycle.Create("Goal", maxIterations: 10);

        Assert.True(cycle.Advance("First attempt at solving the problem with approach A"));
        Assert.True(cycle.Advance("First attempt at solving the problem with approach A")); // stall #1
        Assert.True(cycle.ShouldWarnOnStall);
        Assert.True(cycle.Advance("Completely different approach B with new strategy and different words entirely")); // progress resets
        Assert.False(cycle.ShouldWarnOnStall);
        Assert.False(cycle.IsStalled);
    }

    [Fact]
    public void BuildFollowUpPrompt_UsesGoalWhenNoEvaluationPrompt()
    {
        var cycle = ReflectionCycle.Create("Fix all tests");
        cycle.CurrentIteration = 1;

        var prompt = cycle.BuildFollowUpPrompt("some response");

        Assert.Contains("Fix all tests", prompt);
        Assert.Contains("iteration 2/5", prompt);
        Assert.Contains("[[REFLECTION_COMPLETE]]", prompt);
    }

    [Fact]
    public void BuildFollowUpPrompt_UsesCustomEvaluationPrompt()
    {
        var cycle = ReflectionCycle.Create("Goal", evaluationPrompt: "Check test coverage > 80%");

        var prompt = cycle.BuildFollowUpPrompt("some response");

        Assert.Contains("Check test coverage > 80%", prompt);
        Assert.DoesNotContain("The goal is:", prompt);
    }

    [Fact]
    public void BuildFollowUpPrompt_IncludesIterationCount()
    {
        var cycle = ReflectionCycle.Create("Goal", maxIterations: 10);
        cycle.CurrentIteration = 4;

        var prompt = cycle.BuildFollowUpPrompt("response");

        Assert.Contains("5/10", prompt);
    }

    [Fact]
    public void BuildFollowUpStatus_MatchesNextIteration()
    {
        var cycle = ReflectionCycle.Create("Goal", maxIterations: 5);
        cycle.CurrentIteration = 1;

        var status = cycle.BuildFollowUpStatus();
        using var doc = System.Text.Json.JsonDocument.Parse(status);
        var root = doc.RootElement;
        
        Assert.Equal(2, root.GetProperty("iteration").GetInt32());
        Assert.Equal(5, root.GetProperty("max").GetInt32());
        Assert.Equal("Goal", root.GetProperty("goal").GetString());
        // New: summary field includes goal text
        var summary = root.GetProperty("summary").GetString();
        Assert.Contains("2/5", summary);
        Assert.Contains("Goal", summary);
    }

    [Fact]
    public void IsReflectionFollowUpPrompt_DetectsGeneratedPrompt()
    {
        Assert.True(ReflectionCycle.IsReflectionFollowUpPrompt("[Reflection cycle — iteration 2/5]\n\nContinue"));
        Assert.False(ReflectionCycle.IsReflectionFollowUpPrompt("user typed this"));
    }

    [Fact]
    public void BuildFollowUpPrompt_IncludesProgressAssessment()
    {
        var cycle = ReflectionCycle.Create("Fix all tests");
        var prompt = cycle.BuildFollowUpPrompt("some response");

        Assert.Contains("assess what progress", prompt);
    }

    [Fact]
    public void BuildFollowUpPrompt_DiscouragemsPrematureCompletion()
    {
        var cycle = ReflectionCycle.Create("Fix all tests");
        var prompt = cycle.BuildFollowUpPrompt("some response");

        Assert.Contains("genuinely, fully achieved", prompt);
        Assert.Contains("NOT complete", prompt);
    }

    [Fact]
    public void BuildFollowUpPrompt_IncludesRalphsLoopBranding()
    {
        var cycle = ReflectionCycle.Create("Goal");
        var prompt = cycle.BuildFollowUpPrompt("response");

        Assert.Contains("Reflection cycle", prompt);
    }

    [Fact]
    public void FullCycle_GoalMetOnSecondIteration()
    {
        var cycle = ReflectionCycle.Create("Fix the issue", maxIterations: 5);

        Assert.True(cycle.Advance("Still working on the fix with new approach..."));
        Assert.Equal(1, cycle.CurrentIteration);
        Assert.True(cycle.IsActive);

        Assert.False(cycle.Advance("Issue resolved successfully!\n[[REFLECTION_COMPLETE]]"));
        Assert.Equal(2, cycle.CurrentIteration);
        Assert.True(cycle.GoalMet);
        Assert.False(cycle.IsActive);
    }

    [Fact]
    public void FullCycle_ExhaustsMaxIterations()
    {
        var cycle = ReflectionCycle.Create("Impossible goal", maxIterations: 2);

        Assert.True(cycle.Advance("Trying with approach alpha..."));
        Assert.Equal(1, cycle.CurrentIteration);

        Assert.False(cycle.Advance("Still trying with approach beta and new ideas..."));
        Assert.Equal(2, cycle.CurrentIteration);
        Assert.False(cycle.GoalMet);
        Assert.False(cycle.IsActive);
    }

    [Fact]
    public void DefaultConstructor_HasSensibleDefaults()
    {
        var cycle = new ReflectionCycle();

        Assert.Equal("", cycle.Goal);
        Assert.Equal(5, cycle.MaxIterations);
        Assert.Equal(0, cycle.CurrentIteration);
        Assert.False(cycle.IsActive);
        Assert.False(cycle.GoalMet);
        Assert.False(cycle.IsStalled);
        Assert.Equal("", cycle.EvaluationPrompt);
        Assert.Null(cycle.StartedAt);
        Assert.Null(cycle.CompletedAt);
        Assert.False(cycle.IsPaused);
    }

    [Fact]
    public void CompletionSentinel_IsExposed()
    {
        Assert.Equal("[[REFLECTION_COMPLETE]]", ReflectionCycle.CompletionSentinel);
    }

    [Fact]
    public void Advance_GoalMet_SetsCompletedAt()
    {
        var cycle = ReflectionCycle.Create("Test goal");

        cycle.Advance("Done!\n[[REFLECTION_COMPLETE]]");

        Assert.NotNull(cycle.CompletedAt);
        Assert.True(cycle.CompletedAt >= cycle.StartedAt);
    }

    [Fact]
    public void Advance_MaxIterations_SetsCompletedAt()
    {
        var cycle = ReflectionCycle.Create("Test goal", maxIterations: 1);

        cycle.Advance("Still working with unique content...");

        Assert.NotNull(cycle.CompletedAt);
    }

    [Fact]
    public void Advance_Stalled_SetsCompletedAt()
    {
        var cycle = ReflectionCycle.Create("Goal", maxIterations: 10);
        cycle.Advance("Working on the task with specific details about implementation");
        cycle.Advance("Working on the task with specific details about implementation");
        cycle.Advance("Working on the task with specific details about implementation");

        Assert.True(cycle.IsStalled);
        Assert.NotNull(cycle.CompletedAt);
    }

    [Fact]
    public void Advance_PausedCycle_ReturnsFalseWithoutIncrementing()
    {
        var cycle = ReflectionCycle.Create("Test goal", maxIterations: 5);
        cycle.IsPaused = true;

        Assert.False(cycle.Advance("Any response"));
        Assert.Equal(0, cycle.CurrentIteration);
        Assert.True(cycle.IsActive);
    }

    [Fact]
    public void Pause_Resume_ContinuesCycle()
    {
        var cycle = ReflectionCycle.Create("Test goal", maxIterations: 5);

        Assert.True(cycle.Advance("First unique response alpha"));
        Assert.Equal(1, cycle.CurrentIteration);

        cycle.IsPaused = true;
        Assert.False(cycle.Advance("This should be ignored"));
        Assert.Equal(1, cycle.CurrentIteration);

        cycle.IsPaused = false;
        Assert.True(cycle.Advance("Second unique response beta"));
        Assert.Equal(2, cycle.CurrentIteration);
    }

    [Fact]
    public void CheckStall_ExposesSimilarity()
    {
        var cycle = ReflectionCycle.Create("Goal");

        cycle.CheckStall("First response with unique words");
        Assert.Equal(0.0, cycle.LastSimilarity);

        cycle.CheckStall("First response with unique words");
        Assert.Equal(1.0, cycle.LastSimilarity);
    }

    [Fact]
    public void CheckStall_JaccardSimilarity_ExposesScore()
    {
        var cycle = ReflectionCycle.Create("Goal");

        cycle.CheckStall("The quick brown fox jumps over the lazy dog repeatedly");
        cycle.CheckStall("The quick brown fox jumps over the lazy cat repeatedly");

        // Most words are the same, so similarity should be high but < 1.0
        Assert.True(cycle.LastSimilarity > 0.7);
        Assert.True(cycle.LastSimilarity < 1.0);
    }

    [Fact]
    public void BuildCompletionSummary_GoalMet()
    {
        var cycle = ReflectionCycle.Create("Fix all tests", maxIterations: 5);
        cycle.Advance("Done!\n[[REFLECTION_COMPLETE]]");

        var summary = cycle.BuildCompletionSummary();

        Assert.Contains("✅", summary);
        Assert.Contains("Fix all tests", summary);
        Assert.Contains("Goal met", summary);
        Assert.Contains("1/5", summary);
    }

    [Fact]
    public void BuildCompletionSummary_Stalled_ShowsSimilarity()
    {
        var cycle = ReflectionCycle.Create("Goal", maxIterations: 10);
        cycle.Advance("Working on the task with specific details about implementation");
        cycle.Advance("Working on the task with specific details about implementation");
        cycle.Advance("Working on the task with specific details about implementation");

        var summary = cycle.BuildCompletionSummary();

        Assert.Contains("⚠️", summary);
        Assert.Contains("Stalled", summary);
        Assert.Contains("similarity", summary);
    }

    [Fact]
    public void BuildCompletionSummary_MaxIterations()
    {
        var cycle = ReflectionCycle.Create("Goal", maxIterations: 2);
        cycle.Advance("Trying with approach alpha...");
        cycle.Advance("Still trying with approach beta and new ideas...");

        var summary = cycle.BuildCompletionSummary();

        Assert.Contains("⏱️", summary);
        Assert.Contains("Max iterations", summary);
        Assert.Contains("2/2", summary);
    }

    [Fact]
    public void BuildCompletionSummary_IncludesDuration()
    {
        var cycle = ReflectionCycle.Create("Goal");
        cycle.StartedAt = DateTime.Now.AddSeconds(-30);
        cycle.Advance("Done!\n[[REFLECTION_COMPLETE]]");

        var summary = cycle.BuildCompletionSummary();

        Assert.Contains("Duration:", summary);
    }

    [Fact]
    public void BuildFollowUpStatus_LongGoal_Truncated()
    {
        var longGoal = "This is a very long goal that should be truncated in the status message for readability";
        var cycle = ReflectionCycle.Create(longGoal, maxIterations: 5);

        var status = cycle.BuildFollowUpStatus();
        using var doc = System.Text.Json.JsonDocument.Parse(status);
        var summary = doc.RootElement.GetProperty("summary").GetString()!;

        Assert.Contains("…", summary);
        Assert.True(summary.Length < longGoal.Length + 30);
    }
}

public class AgentSessionInfoReflectionCycleTests
{
    [Fact]
    public void ReflectionCycle_DefaultsToNull()
    {
        var session = new AgentSessionInfo { Name = "test", Model = "gpt-5" };

        Assert.Null(session.ReflectionCycle);
    }

    [Fact]
    public void ReflectionCycle_CanBeSet()
    {
        var session = new AgentSessionInfo { Name = "test", Model = "gpt-5" };
        session.ReflectionCycle = ReflectionCycle.Create("Build the feature");

        Assert.NotNull(session.ReflectionCycle);
        Assert.Equal("Build the feature", session.ReflectionCycle.Goal);
        Assert.True(session.ReflectionCycle.IsActive);
    }

    [Fact]
    public void ReflectionCycle_CanBeCleared()
    {
        var session = new AgentSessionInfo { Name = "test", Model = "gpt-5" };
        session.ReflectionCycle = ReflectionCycle.Create("Goal");

        session.ReflectionCycle = null;

        Assert.Null(session.ReflectionCycle);
    }
}
