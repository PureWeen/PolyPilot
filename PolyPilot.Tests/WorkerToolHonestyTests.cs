using Microsoft.Extensions.DependencyInjection;
using PolyPilot.Models;
using PolyPilot.Services;

namespace PolyPilot.Tests;

/// <summary>
/// Tests that orchestration prompts include tool-honesty instructions
/// to prevent workers from fabricating results when tools fail.
/// Regression tests for: "workers must not make up results if CLI tools don't run"
/// </summary>
public class WorkerToolHonestyTests
{
    private CopilotService CreateService()
    {
        var services = new ServiceCollection();
        return new CopilotService(
            new StubChatDatabase(), new StubServerManager(), new StubWsBridgeClient(),
            new RepoManager(), services.BuildServiceProvider(), new StubDemoService());
    }

    #region Worker Prompt Tool-Honesty Instructions

    [Fact]
    public void WorkerPrompt_ContainsToolHonestyInstructions()
    {
        var svc = CreateService();
        var method = typeof(CopilotService).GetMethod("BuildWorkerPrompt",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Assert.NotNull(method);

        var workerPrompt = (string)method!.Invoke(null, new object[] {
            "You are a worker agent. Complete the following task thoroughly.",
            "", "", "Fix the tests", "Run the unit tests"
        })!;

        Assert.Contains("CRITICAL: Tool Usage & Honesty Policy", workerPrompt);
        Assert.Contains("NEVER fabricate", workerPrompt);
        Assert.Contains("TOOL_FAILURE:", workerPrompt);
        Assert.Contains("REPORT THE FAILURE", workerPrompt);
        Assert.Contains("NEVER evaluate or assess", workerPrompt);
    }

    #endregion

    #region BuildSynthesisPrompt Tool-Verification Instructions

    [Fact]
    public void BuildSynthesisPrompt_ContainsToolVerificationGuidance()
    {
        var svc = CreateService();
        var method = typeof(CopilotService).GetMethod("BuildSynthesisPrompt",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        Assert.NotNull(method);

        var workerResultType = typeof(CopilotService).GetNestedType("WorkerResult",
            System.Reflection.BindingFlags.NonPublic);
        Assert.NotNull(workerResultType);

        var result = Activator.CreateInstance(workerResultType!, "worker-1", "Test passed!", true, (string?)null, TimeSpan.FromSeconds(5));
        var results = (System.Collections.IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(workerResultType!))!;
        results.Add(result!);

        var prompt = (string)method!.Invoke(svc, new object[] { "Run tests", results })!;

        Assert.Contains("fabricated results", prompt);
        Assert.Contains("TOOL_FAILURE", prompt);
        Assert.Contains("evidence of actual tool usage", prompt);
    }

    [Fact]
    public void BuildSynthesisPrompt_DoNotGuessToolFailures()
    {
        var svc = CreateService();
        var method = typeof(CopilotService).GetMethod("BuildSynthesisPrompt",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        Assert.NotNull(method);

        var workerResultType = typeof(CopilotService).GetNestedType("WorkerResult",
            System.Reflection.BindingFlags.NonPublic);
        Assert.NotNull(workerResultType);

        // Worker reports TOOL_FAILURE
        var result = Activator.CreateInstance(workerResultType!, "worker-1",
            "TOOL_FAILURE: Could not run dotnet test - CLI not available", true, (string?)null, TimeSpan.FromSeconds(2));
        var results = (System.Collections.IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(workerResultType!))!;
        results.Add(result!);

        var prompt = (string)method!.Invoke(svc, new object[] { "Run tests and report results", results })!;

        // The synthesis prompt must instruct not to fill in missing results
        Assert.Contains("do NOT attempt to fill in or guess the missing results", prompt);
    }

    #endregion

    #region BuildEvaluatorPrompt Tool-Verification Dimension

    [Fact]
    public void BuildEvaluatorPrompt_IncludesToolVerificationDimension()
    {
        var svc = CreateService();
        var method = typeof(CopilotService).GetMethod("BuildEvaluatorPrompt",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Assert.NotNull(method);

        var state = ReflectionCycle.Create("Run all tests and report results");
        var prompt = (string)method!.Invoke(null, new object[] { "Run tests", "All 42 tests passed", state })!;

        Assert.Contains("Tool Verification", prompt);
        Assert.Contains("fabricated results", prompt);
        Assert.Contains("average of 5 dimensions", prompt);
    }

    #endregion

    #region BuildSynthesisWithEvalPrompt Tool-Verification

    [Fact]
    public void BuildSynthesisWithEvalPrompt_IncludesToolVerificationAssessment()
    {
        var svc = CreateService();
        var method = typeof(CopilotService).GetMethod("BuildSynthesisWithEvalPrompt",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        Assert.NotNull(method);

        var workerResultType = typeof(CopilotService).GetNestedType("WorkerResult",
            System.Reflection.BindingFlags.NonPublic);
        Assert.NotNull(workerResultType);

        var result = Activator.CreateInstance(workerResultType!, "worker-1", "Done!", true, (string?)null, TimeSpan.FromSeconds(3));
        var results = (System.Collections.IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(workerResultType!))!;
        results.Add(result!);

        var state = ReflectionCycle.Create("Test the app");
        var prompt = (string)method!.Invoke(svc, new object[] {
            "Test the app", results, state, (string?)null, (HashSet<string>?)null, (List<string>?)null
        })!;

        Assert.Contains("Tool Verification", prompt);
        Assert.Contains("fabricated", prompt);
    }

    #endregion

    #region ReflectionCycle.BuildEvaluatorPrompt Tool-Verification

    [Fact]
    public void ReflectionCycle_BuildEvaluatorPrompt_ContainsToolVerification()
    {
        var cycle = ReflectionCycle.Create("Run tests and verify results");
        cycle.Advance("First attempt");

        var prompt = cycle.BuildEvaluatorPrompt("All tests passed with flying colors!");

        Assert.Contains("Tool Verification", prompt);
        Assert.Contains("fabricated", prompt);
        Assert.Contains("actual tool execution", prompt);
    }

    [Fact]
    public void ReflectionCycle_BuildEvaluatorPrompt_StillContainsCoreInstructions()
    {
        var cycle = ReflectionCycle.Create("Fix the bug");
        cycle.Advance("First attempt");

        var prompt = cycle.BuildEvaluatorPrompt("Here is my fix");

        // Existing functionality preserved
        Assert.Contains("Fix the bug", prompt);
        Assert.Contains("Here is my fix", prompt);
        Assert.Contains("PASS", prompt);
        Assert.Contains("FAIL:", prompt);
        // New tool verification also present
        Assert.Contains("Tool Verification", prompt);
    }

    #endregion
}
