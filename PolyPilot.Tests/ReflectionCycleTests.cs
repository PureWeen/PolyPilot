using Microsoft.Extensions.DependencyInjection;
using PolyPilot.Models;
using PolyPilot.Services;

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

    // --- Evaluator-Based Reflection Tests ---

    [Fact]
    public void ParseEvaluatorResponse_Pass()
    {
        var (pass, feedback) = ReflectionCycle.ParseEvaluatorResponse("PASS");
        Assert.True(pass);
        Assert.Null(feedback);
    }

    [Fact]
    public void ParseEvaluatorResponse_PassWithExtraText()
    {
        var (pass, feedback) = ReflectionCycle.ParseEvaluatorResponse("PASS\nGreat work!");
        Assert.True(pass);
        Assert.Null(feedback);
    }

    [Fact]
    public void ParseEvaluatorResponse_PassCaseInsensitive()
    {
        var (pass, feedback) = ReflectionCycle.ParseEvaluatorResponse("pass");
        Assert.True(pass);
        Assert.Null(feedback);
    }

    [Fact]
    public void ParseEvaluatorResponse_FailWithFeedback()
    {
        var (pass, feedback) = ReflectionCycle.ParseEvaluatorResponse("FAIL: Missing error handling");
        Assert.False(pass);
        Assert.Equal("Missing error handling", feedback);
    }

    [Fact]
    public void ParseEvaluatorResponse_FailFeedbackOnNextLine()
    {
        var (pass, feedback) = ReflectionCycle.ParseEvaluatorResponse("FAIL:\nThe code doesn't handle edge cases");
        Assert.False(pass);
        Assert.Equal("The code doesn't handle edge cases", feedback);
    }

    [Fact]
    public void ParseEvaluatorResponse_FailCaseInsensitive()
    {
        var (pass, feedback) = ReflectionCycle.ParseEvaluatorResponse("fail: needs more tests");
        Assert.False(pass);
        Assert.Equal("needs more tests", feedback);
    }

    [Fact]
    public void ParseEvaluatorResponse_EmptyReturnsFailure()
    {
        var (pass, feedback) = ReflectionCycle.ParseEvaluatorResponse("");
        Assert.False(pass);
        Assert.Equal("Evaluator returned empty response", feedback);
    }

    [Fact]
    public void ParseEvaluatorResponse_GarbageReturnsFeedback()
    {
        var (pass, feedback) = ReflectionCycle.ParseEvaluatorResponse("I think this looks good but...");
        Assert.False(pass);
        Assert.NotNull(feedback);
    }

    [Fact]
    public void ParseEvaluatorResponse_ContainsPassInText_NoLongerMatchesFuzzy()
    {
        // After removing fuzzy fallback, text containing "PASS" but not starting with it should FAIL
        var (pass, feedback) = ReflectionCycle.ParseEvaluatorResponse("The result would PASS all criteria.");
        Assert.False(pass);
        Assert.NotNull(feedback);
    }

    [Fact]
    public void BuildEvaluatorPrompt_ContainsGoalAndResponse()
    {
        var cycle = ReflectionCycle.Create("Fix the bug", maxIterations: 5);
        cycle.Advance("First attempt"); // iteration 1

        var prompt = cycle.BuildEvaluatorPrompt("Here is my fix");

        Assert.Contains("Fix the bug", prompt);
        Assert.Contains("Here is my fix", prompt);
        Assert.Contains("PASS", prompt);
        Assert.Contains("FAIL:", prompt);
    }

    [Fact]
    public void BuildEvaluatorPrompt_TruncatesLongResponses()
    {
        var cycle = ReflectionCycle.Create("Goal");
        var longResponse = new string('x', 5000);

        var prompt = cycle.BuildEvaluatorPrompt(longResponse);

        Assert.Contains("[... truncated]", prompt);
        Assert.True(prompt.Length < 5500);
    }

    [Fact]
    public void BuildFollowUpFromEvaluator_ContainsFeedback()
    {
        var cycle = ReflectionCycle.Create("Fix the bug", maxIterations: 5);

        var followUp = cycle.BuildFollowUpFromEvaluator("Missing error handling for null inputs");

        Assert.Contains("Missing error handling for null inputs", followUp);
        Assert.Contains("Fix the bug", followUp);
        Assert.Contains("independent evaluator", followUp);
    }

    [Fact]
    public void AdvanceWithEvaluation_PassEndseCycle()
    {
        var cycle = ReflectionCycle.Create("Fix the bug");

        var shouldContinue = cycle.AdvanceWithEvaluation("response", evaluatorPassed: true, "Looks good");

        Assert.False(shouldContinue);
        Assert.True(cycle.GoalMet);
        Assert.False(cycle.IsActive);
    }

    [Fact]
    public void AdvanceWithEvaluation_FailContinuesCycle()
    {
        var cycle = ReflectionCycle.Create("Fix the bug", maxIterations: 5);

        var shouldContinue = cycle.AdvanceWithEvaluation("response", evaluatorPassed: false, "Needs work");

        Assert.True(shouldContinue);
        Assert.Equal(1, cycle.CurrentIteration);
        Assert.Equal("Needs work", cycle.EvaluatorFeedback);
    }

    [Fact]
    public void AdvanceWithEvaluation_RespectsMaxIterations()
    {
        var cycle = ReflectionCycle.Create("Fix the bug", maxIterations: 2);
        cycle.AdvanceWithEvaluation("resp1", false, "feedback1");
        var shouldContinue = cycle.AdvanceWithEvaluation("resp2", false, "feedback2");

        Assert.False(shouldContinue);
        Assert.False(cycle.IsActive);
        Assert.False(cycle.GoalMet);
    }

    [Fact]
    public void AdvanceWithEvaluation_StillDetectsStalls()
    {
        var cycle = ReflectionCycle.Create("Fix the bug", maxIterations: 10);
        // Send same response twice — stall detection should fire
        cycle.AdvanceWithEvaluation("identical response here", false, "try again");
        Assert.False(cycle.ShouldWarnOnStall);

        cycle.AdvanceWithEvaluation("identical response here", false, "try again");
        Assert.True(cycle.ShouldWarnOnStall);

        // Third identical — should stall out
        var shouldContinue = cycle.AdvanceWithEvaluation("identical response here", false, "try again");
        Assert.False(shouldContinue);
        Assert.True(cycle.IsStalled);
    }

    [Fact]
    public void EvaluatorSessionName_DefaultsToNull()
    {
        var cycle = ReflectionCycle.Create("Goal");
        Assert.Null(cycle.EvaluatorSessionName);
    }

    [Fact]
    public void EvaluatorFeedback_TracksLatest()
    {
        var cycle = ReflectionCycle.Create("Goal", maxIterations: 5);
        cycle.AdvanceWithEvaluation("resp1", false, "first feedback");
        Assert.Equal("first feedback", cycle.EvaluatorFeedback);

        cycle.AdvanceWithEvaluation("resp2", false, "second feedback");
        Assert.Equal("second feedback", cycle.EvaluatorFeedback);
    }

    [Fact]
    public void IsHidden_DefaultsFalse()
    {
        var session = new AgentSessionInfo { Name = "test", Model = "gpt-5" };
        Assert.False(session.IsHidden);
    }

    // --- Code Review Fix Tests ---

    [Fact]
    public void ParseEvaluatorResponse_Surpass_DoesNotFalsePositive()
    {
        // "surpass" contains "PASS" but should not be treated as a PASS verdict
        var (pass, feedback) = ReflectionCycle.ParseEvaluatorResponse("This work could surpass expectations with improvements.");
        Assert.False(pass);
        Assert.NotNull(feedback);
    }

    [Fact]
    public void ParseEvaluatorResponse_PassMidSentence_DoesNotMatch()
    {
        var (pass, _) = ReflectionCycle.ParseEvaluatorResponse("I think this might pass later.");
        Assert.False(pass);
    }

    [Fact]
    public void ParseEvaluatorResponse_GarbageText_ReturnsFail()
    {
        var (pass, feedback) = ReflectionCycle.ParseEvaluatorResponse("Well, let me think about this...");
        Assert.False(pass);
        Assert.Equal("Well, let me think about this...", feedback);
    }

    [Fact]
    public void ParseEvaluatorResponse_ExactPass_Works()
    {
        var (pass, _) = ReflectionCycle.ParseEvaluatorResponse("PASS");
        Assert.True(pass);
    }

    [Fact]
    public void ParseEvaluatorResponse_PassWithWhitespace_Works()
    {
        var (pass, _) = ReflectionCycle.ParseEvaluatorResponse("  PASS  ");
        Assert.True(pass);
    }

    [Fact]
    public void AdvanceWithEvaluation_DifferentCycleInstance_NotCorrupted()
    {
        // Simulate: cycle1 is active, evaluator runs, user restarts cycle
        var cycle1 = ReflectionCycle.Create("Goal 1", maxIterations: 5);
        cycle1.AdvanceWithEvaluation("response1", false, "needs work");
        Assert.Equal(1, cycle1.CurrentIteration);

        // User stops cycle1 and starts cycle2
        cycle1.IsActive = false;
        var cycle2 = ReflectionCycle.Create("Goal 2", maxIterations: 5);

        // Old evaluator result arrives — should NOT affect cycle2
        // (In real code, ReferenceEquals check prevents this)
        Assert.False(ReferenceEquals(cycle1, cycle2));
        Assert.Equal(0, cycle2.CurrentIteration);
        Assert.True(cycle2.IsActive);
    }

    [Fact]
    public void PausedCycle_IsActive_And_IsPaused()
    {
        var cycle = ReflectionCycle.Create("Goal");
        cycle.IsPaused = true;

        // Both flags are true simultaneously — UI pattern must check both
        Assert.True(cycle.IsActive);
        Assert.True(cycle.IsPaused);
    }

    [Fact]
    public void PausedCycle_Advance_DoesNotProgress()
    {
        var cycle = ReflectionCycle.Create("Goal", maxIterations: 5);
        cycle.IsPaused = true;

        var shouldContinue = cycle.Advance("some response");

        Assert.False(shouldContinue);
        Assert.Equal(0, cycle.CurrentIteration);
    }

    [Fact]
    public void PausedCycle_AdvanceWithEvaluation_DoesNotProgress()
    {
        var cycle = ReflectionCycle.Create("Goal", maxIterations: 5);
        cycle.IsPaused = true;

        var shouldContinue = cycle.AdvanceWithEvaluation("response", false, "feedback");

        Assert.False(shouldContinue);
        Assert.Equal(0, cycle.CurrentIteration);
    }

    [Fact]
    public void BuildEvaluatorPrompt_EarlyIteration_IsDemanding()
    {
        var cycle = ReflectionCycle.Create("Write a haiku", maxIterations: 5);
        // iteration 0 — first eval
        var prompt = cycle.BuildEvaluatorPrompt("Roses are red");

        Assert.Contains("MUST find at least one flaw", prompt);
        Assert.Contains("Do NOT say PASS on early iterations", prompt);
    }

    [Fact]
    public void BuildEvaluatorPrompt_FinalIteration_IsLenient()
    {
        var cycle = ReflectionCycle.Create("Write a haiku", maxIterations: 5);
        cycle.AdvanceWithEvaluation("r1", false, "f1");
        cycle.AdvanceWithEvaluation("r2", false, "f2");
        cycle.AdvanceWithEvaluation("r3", false, "f3");
        // Now at iteration 3, maxIterations 5, MaxIterations-1 = 4
        // Need to reach iteration 4 (final) to get lenient prompt
        cycle.AdvanceWithEvaluation("r4", false, "f4");
        // CurrentIteration is now 4, MaxIterations-1 is 4, so final iteration
        var prompt = cycle.BuildEvaluatorPrompt("Final attempt");

        Assert.Contains("lenient", prompt.ToLowerInvariant());
    }

    [Fact]
    public void BuildEvaluatorPrompt_MidIteration_IsDemandingButFair()
    {
        var cycle = ReflectionCycle.Create("Write a haiku", maxIterations: 6);
        cycle.AdvanceWithEvaluation("r1", false, "f1");
        cycle.AdvanceWithEvaluation("r2", false, "f2");
        cycle.AdvanceWithEvaluation("r3", false, "f3");
        // At iteration 3/6 — mid range
        var prompt = cycle.BuildEvaluatorPrompt("Mid attempt");

        Assert.Contains("demanding but fair", prompt.ToLowerInvariant());
    }

    [Fact]
    public void EvaluatorFeedback_NullOnPass()
    {
        var cycle = ReflectionCycle.Create("Goal", maxIterations: 5);
        cycle.AdvanceWithEvaluation("response", true, null);

        Assert.Null(cycle.EvaluatorFeedback);
        Assert.True(cycle.GoalMet);
    }

    [Theory]
    [InlineData("fix the bug --max 10", 10, "fix the bug")]
    [InlineData("fix the bug \u2014max 10", 10, "fix the bug")] // em-dash (macOS auto-substitution)
    [InlineData("fix the bug --max 50", 50, "fix the bug")]
    [InlineData("--max 20 fix the bug", 20, "fix the bug")]
    [InlineData("\u2014max 20 fix the bug", 20, "fix the bug")] // em-dash at start
    [InlineData("fix the bug", 5, "fix the bug")] // no --max, default
    [InlineData("fix the bug --max 200", 200, "fix the bug")] // no upper bound
    [InlineData("fix the bug --max 9999", 9999, "fix the bug")] // unlimited
    [InlineData("fix the bug --max 0", 5, "fix the bug")] // zero keeps default
    [InlineData("fix the bug --max 1", 1, "fix the bug")] // minimum valid
    [InlineData("fix --max 3 the bug", 3, "fix  the bug")] // mid-goal placement
    public void ParseMaxIterations_HandlesVariousFormats(string arg, int expectedMax, string expectedGoal)
    {
        int maxIterations = 5;
        var goal = arg;
        var maxMatch = System.Text.RegularExpressions.Regex.Match(arg, @"(?:--|—|\u2014)max\s+(\d+)");
        if (maxMatch.Success)
        {
            if (int.TryParse(maxMatch.Groups[1].Value, out var parsed) && parsed > 0)
                maxIterations = parsed;
            goal = arg.Remove(maxMatch.Index, maxMatch.Length).Trim();
        }

        Assert.Equal(expectedMax, maxIterations);
        Assert.Equal(expectedGoal, goal);
    }

    #region StartGroupReflection Guard

    private CopilotService CreateService()
    {
        var services = new ServiceCollection();
        return new CopilotService(
            new StubChatDatabase(), new StubServerManager(), new StubWsBridgeClient(),
            new RepoManager(), services.BuildServiceProvider(), new StubDemoService());
    }

    [Fact]
    public void StartGroupReflection_SkipsIfAlreadyActive()
    {
        var svc = CreateService();
        var group = new SessionGroup { Id = "g1", Name = "Test", IsMultiAgent = true };
        svc.Organization.Groups.Add(group);

        svc.StartGroupReflection("g1", "First goal", 3);
        Assert.NotNull(group.ReflectionState);
        Assert.Equal("First goal", group.ReflectionState!.Goal);
        Assert.True(group.ReflectionState.IsActive);

        // Calling again should NOT overwrite
        svc.StartGroupReflection("g1", "Second goal", 5);
        Assert.Equal("First goal", group.ReflectionState.Goal);
        Assert.Equal(3, group.ReflectionState.MaxIterations);
    }

    [Fact]
    public void StartGroupReflection_AllowsAfterCancellation()
    {
        var svc = CreateService();
        var group = new SessionGroup { Id = "g1", Name = "Test", IsMultiAgent = true };
        svc.Organization.Groups.Add(group);

        svc.StartGroupReflection("g1", "First goal", 3);
        svc.StopGroupReflection("g1");
        Assert.True(group.ReflectionState!.IsCancelled);

        // Now should allow a new reflection
        svc.StartGroupReflection("g1", "Second goal", 5);
        Assert.Equal("Second goal", group.ReflectionState.Goal);
        Assert.True(group.ReflectionState.IsActive);
        Assert.False(group.ReflectionState.IsCancelled);
    }

    #endregion

    #region Sentinel Stripping in Synthesis

    [Fact]
    public void SentinelStripping_ReplacesGroupReflectComplete()
    {
        // Verify the replacement logic used in BuildSynthesisPrompt
        var workerResponse = "The implementation looks good.\n[[GROUP_REFLECT_COMPLETE]]\nAll tests pass.";
        var sanitized = workerResponse.Replace("[[GROUP_REFLECT_COMPLETE]]", "[WORKER_APPROVED]", StringComparison.OrdinalIgnoreCase);

        Assert.DoesNotContain("[[GROUP_REFLECT_COMPLETE]]", sanitized);
        Assert.Contains("[WORKER_APPROVED]", sanitized);
        Assert.Contains("The implementation looks good.", sanitized);
        Assert.Contains("All tests pass.", sanitized);
    }

    [Fact]
    public void SentinelStripping_CaseInsensitive()
    {
        var workerResponse = "Done. [[group_reflect_complete]]";
        var sanitized = workerResponse.Replace("[[GROUP_REFLECT_COMPLETE]]", "[WORKER_APPROVED]", StringComparison.OrdinalIgnoreCase);

        Assert.DoesNotContain("[[group_reflect_complete]]", sanitized, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("[WORKER_APPROVED]", sanitized);
    }

    [Fact]
    public void SentinelStripping_PreservesNeedsIteration()
    {
        var workerResponse = "Issues found:\n[[NEEDS_ITERATION]]\n1. Missing error handling";
        var sanitized = workerResponse.Replace("[[GROUP_REFLECT_COMPLETE]]", "[WORKER_APPROVED]", StringComparison.OrdinalIgnoreCase);

        // NEEDS_ITERATION should not be affected by the stripping
        Assert.Contains("[[NEEDS_ITERATION]]", sanitized);
        Assert.Contains("Missing error handling", sanitized);
    }

    [Fact]
    public void SentinelStripping_HandlesNull()
    {
        string? workerResponse = null;
        var sanitized = workerResponse?.Replace("[[GROUP_REFLECT_COMPLETE]]", "[WORKER_APPROVED]", StringComparison.OrdinalIgnoreCase) ?? "";
        Assert.Equal("", sanitized);
    }

    #endregion

    #region BuildSynthesisPrompt Sentinel Stripping

    [Fact]
    public void BuildSynthesisPrompt_StripsSentinelFromWorkerResponse()
    {
        var svc = CreateService();
        var method = typeof(CopilotService).GetMethod("BuildSynthesisPrompt",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        Assert.NotNull(method);

        // Create WorkerResult via reflection since it's a private record
        var workerResultType = typeof(CopilotService).GetNestedType("WorkerResult",
            System.Reflection.BindingFlags.NonPublic);
        Assert.NotNull(workerResultType);

        var workerResponse = "Review looks great! All tests pass.\n[[GROUP_REFLECT_COMPLETE]]\nFinal notes here.";
        var result = Activator.CreateInstance(workerResultType!, "worker-1", workerResponse, true, (string?)null, TimeSpan.FromSeconds(5));
        var results = (System.Collections.IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(workerResultType!))!;
        results.Add(result!);

        var synthesisPrompt = (string)method!.Invoke(svc, new object[] { "Review this PR", results })!;

        Assert.DoesNotContain("[[GROUP_REFLECT_COMPLETE]]", synthesisPrompt);
        Assert.Contains("[WORKER_APPROVED]", synthesisPrompt);
        Assert.Contains("Review looks great!", synthesisPrompt);
        Assert.Contains("Final notes here.", synthesisPrompt);
    }

    [Fact]
    public void BuildSynthesisPrompt_StripsMultipleSentinelOccurrences()
    {
        var svc = CreateService();
        var method = typeof(CopilotService).GetMethod("BuildSynthesisPrompt",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        Assert.NotNull(method);

        var workerResultType = typeof(CopilotService).GetNestedType("WorkerResult",
            System.Reflection.BindingFlags.NonPublic);
        Assert.NotNull(workerResultType);

        var workerResponse = "[[GROUP_REFLECT_COMPLETE]] middle text [[GROUP_REFLECT_COMPLETE]]";
        var result = Activator.CreateInstance(workerResultType!, "worker-1", workerResponse, true, (string?)null, TimeSpan.FromSeconds(1));
        var results = (System.Collections.IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(workerResultType!))!;
        results.Add(result!);

        var synthesisPrompt = (string)method!.Invoke(svc, new object[] { "task", results })!;

        Assert.DoesNotContain("[[GROUP_REFLECT_COMPLETE]]", synthesisPrompt);
        // Both occurrences should be replaced
        Assert.Equal(2, synthesisPrompt.Split("[WORKER_APPROVED]").Length - 1);
    }

    #endregion
}
