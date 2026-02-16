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

        Assert.True(cycle.IsGoalMet("Some work done.\n[[RALPH_COMPLETE]]\n"));
    }

    [Fact]
    public void IsGoalMet_WithSentinelAtEnd_ReturnsTrue()
    {
        var cycle = ReflectionCycle.Create("Test goal");

        Assert.True(cycle.IsGoalMet("All done!\n[[RALPH_COMPLETE]]"));
    }

    [Fact]
    public void IsGoalMet_WithSentinelWithWhitespace_ReturnsTrue()
    {
        var cycle = ReflectionCycle.Create("Test goal");

        Assert.True(cycle.IsGoalMet("Done.\n  [[RALPH_COMPLETE]]  \nExtra text"));
    }

    [Fact]
    public void IsGoalMet_SentinelEmbeddedInText_ReturnsFalse()
    {
        var cycle = ReflectionCycle.Create("Test goal");

        // Sentinel must be on its own line, not embedded in prose
        Assert.False(cycle.IsGoalMet("I said [[RALPH_COMPLETE]] in the middle of a sentence"));
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

        Assert.False(cycle.Advance("Done!\n[[RALPH_COMPLETE]]"));
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

        // Second response — nearly identical (stall #1, allowed)
        Assert.True(cycle.Advance("Working on the task with specific details about implementation"));

        // Third response — still identical (stall #2, stops)
        Assert.False(cycle.Advance("Working on the task with specific details about implementation"));
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
        Assert.True(cycle.Advance("Completely different approach B with new strategy and different words entirely")); // progress resets
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
        Assert.Contains("[[RALPH_COMPLETE]]", prompt);
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

        Assert.Contains("Ralph's Loop", prompt);
    }

    [Fact]
    public void FullCycle_GoalMetOnSecondIteration()
    {
        var cycle = ReflectionCycle.Create("Fix the issue", maxIterations: 5);

        Assert.True(cycle.Advance("Still working on the fix with new approach..."));
        Assert.Equal(1, cycle.CurrentIteration);
        Assert.True(cycle.IsActive);

        Assert.False(cycle.Advance("Issue resolved successfully!\n[[RALPH_COMPLETE]]"));
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
    }

    [Fact]
    public void CompletionSentinel_IsExposed()
    {
        Assert.Equal("[[RALPH_COMPLETE]]", ReflectionCycle.CompletionSentinel);
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
