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
    public void IsGoalMet_WithCompletionMarker_ReturnsTrue()
    {
        var cycle = ReflectionCycle.Create("Test goal");

        Assert.True(cycle.IsGoalMet("✅ Goal complete - everything looks good"));
    }

    [Fact]
    public void IsGoalMet_WithTextMarker_ReturnsTrue()
    {
        var cycle = ReflectionCycle.Create("Test goal");

        Assert.True(cycle.IsGoalMet("Goal complete - all done"));
    }

    [Fact]
    public void IsGoalMet_CaseInsensitive()
    {
        var cycle = ReflectionCycle.Create("Test goal");

        Assert.True(cycle.IsGoalMet("GOAL COMPLETE - finished"));
    }

    [Fact]
    public void IsGoalMet_WithoutMarker_ReturnsFalse()
    {
        var cycle = ReflectionCycle.Create("Test goal");

        Assert.False(cycle.IsGoalMet("Still working on it..."));
    }

    [Fact]
    public void IsGoalMet_EmptyResponse_ReturnsFalse()
    {
        var cycle = ReflectionCycle.Create("Test goal");

        Assert.False(cycle.IsGoalMet(""));
        Assert.False(cycle.IsGoalMet(null!));
    }

    [Fact]
    public void ShouldContinue_ActiveCycle_NoGoalMet_ReturnsTrue()
    {
        var cycle = ReflectionCycle.Create("Test goal", maxIterations: 3);

        Assert.True(cycle.ShouldContinue("Working on it..."));
    }

    [Fact]
    public void ShouldContinue_GoalMet_ReturnsFalse()
    {
        var cycle = ReflectionCycle.Create("Test goal");

        Assert.False(cycle.ShouldContinue("✅ Goal complete"));
        Assert.True(cycle.GoalMet);
        Assert.False(cycle.IsActive);
    }

    [Fact]
    public void ShouldContinue_MaxIterationsReached_ReturnsFalse()
    {
        var cycle = ReflectionCycle.Create("Test goal", maxIterations: 3);
        cycle.CurrentIteration = 3;

        Assert.False(cycle.ShouldContinue("Still going..."));
        Assert.False(cycle.IsActive);
        Assert.False(cycle.GoalMet);
    }

    [Fact]
    public void ShouldContinue_InactiveCycle_ReturnsFalse()
    {
        var cycle = ReflectionCycle.Create("Test goal");
        cycle.IsActive = false;

        Assert.False(cycle.ShouldContinue("Any response"));
    }

    [Fact]
    public void BuildFollowUpPrompt_UsesGoalWhenNoEvaluationPrompt()
    {
        var cycle = ReflectionCycle.Create("Fix all tests");
        cycle.CurrentIteration = 1;

        var prompt = cycle.BuildFollowUpPrompt("some response");

        Assert.Contains("Fix all tests", prompt);
        Assert.Contains("iteration 2/5", prompt);
        Assert.Contains("Goal complete", prompt);
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
    public void FullCycle_GoalMetOnSecondIteration()
    {
        var cycle = ReflectionCycle.Create("Fix the issue", maxIterations: 5);

        // First iteration: goal not met
        cycle.CurrentIteration++;
        Assert.True(cycle.ShouldContinue("Still working..."));
        Assert.True(cycle.IsActive);

        // Second iteration: goal met
        cycle.CurrentIteration++;
        Assert.False(cycle.ShouldContinue("✅ Goal complete - issue resolved"));
        Assert.True(cycle.GoalMet);
        Assert.False(cycle.IsActive);
    }

    [Fact]
    public void FullCycle_ExhaustsMaxIterations()
    {
        var cycle = ReflectionCycle.Create("Impossible goal", maxIterations: 2);

        // First iteration
        cycle.CurrentIteration++;
        Assert.True(cycle.ShouldContinue("Trying..."));

        // Second iteration - hits max
        cycle.CurrentIteration++;
        Assert.False(cycle.ShouldContinue("Still trying..."));
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
        Assert.Equal("", cycle.EvaluationPrompt);
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
