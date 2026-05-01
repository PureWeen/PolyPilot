using GitHub.Copilot.SDK;
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

    #region Worker Prompt — Task-Only Content

    [Fact]
    public void WorkerPrompt_ContainsOnlyTaskContent()
    {
        var workerPrompt = CopilotService.BuildWorkerPrompt("Fix the tests", "Run the unit tests");

        Assert.Contains("Original User Request", workerPrompt);
        Assert.Contains("Fix the tests", workerPrompt);
        Assert.Contains("Your Assigned Task", workerPrompt);
        Assert.Contains("Run the unit tests", workerPrompt);
        // System-level content is now in sections, not the user prompt
        Assert.DoesNotContain("CRITICAL: Tool Usage & Honesty Policy", workerPrompt);
        Assert.DoesNotContain("NEVER fabricate", workerPrompt);
        // No dynamic context when called without optional params
        Assert.DoesNotContain("Your Role", workerPrompt);
        Assert.DoesNotContain("Team Context (latest)", workerPrompt);
    }

    [Fact]
    public void WorkerPrompt_IncludesFreshIdentityWhenProvided()
    {
        var workerPrompt = CopilotService.BuildWorkerPrompt(
            "Fix the tests", "Run the unit tests",
            freshIdentity: "You are a security auditor.");

        Assert.Contains("Your Role", workerPrompt);
        Assert.Contains("You are a security auditor.", workerPrompt);
    }

    [Fact]
    public void WorkerPrompt_IncludesFreshSharedContextWhenProvided()
    {
        var workerPrompt = CopilotService.BuildWorkerPrompt(
            "Fix the tests", "Run the unit tests",
            freshSharedContext: "Always use TDD.");

        Assert.Contains("Team Context (latest)", workerPrompt);
        Assert.Contains("Always use TDD.", workerPrompt);
    }

    #endregion

    #region Worker System Message Sections — Tool Honesty

    [Fact]
    public void WorkerSystemMessageSections_ContainsToolHonestyInstructions()
    {
        var sections = CopilotService.BuildWorkerSystemMessageSections(
            "You are a worker agent. Complete the following task thoroughly.",
            worktreeNote: "",
            sharedContext: "");

        Assert.True(sections.ContainsKey(SystemPromptSections.ToolEfficiency));
        var toolSection = sections[SystemPromptSections.ToolEfficiency];
        Assert.Equal(SectionOverrideAction.Append, toolSection.Action);
        Assert.Contains("CRITICAL: Tool Usage & Honesty Policy", toolSection.Content);
        Assert.Contains("NEVER fabricate", toolSection.Content);
        Assert.Contains("TOOL_FAILURE:", toolSection.Content);
        Assert.Contains("REPORT THE FAILURE", toolSection.Content);
        Assert.Contains("NEVER evaluate or assess", toolSection.Content);
    }

    [Fact]
    public void WorkerSystemMessageSections_ContainsIdentity()
    {
        var charter = "You are a code review specialist.";
        var sections = CopilotService.BuildWorkerSystemMessageSections(
            charter, worktreeNote: "", sharedContext: "");

        Assert.True(sections.ContainsKey(SystemPromptSections.Identity));
        var identitySection = sections[SystemPromptSections.Identity];
        Assert.Equal(SectionOverrideAction.Append, identitySection.Action);
        Assert.Contains(charter, identitySection.Content);
        Assert.Contains("synthesized with other workers", identitySection.Content);
    }

    [Fact]
    public void WorkerSystemMessageSections_IncludesWorktreeNote()
    {
        var worktreeNote = "\n\n## Your Worktree\nYou have an isolated git worktree at `/tmp/wt` (branch: main).\n";
        var sections = CopilotService.BuildWorkerSystemMessageSections(
            "worker", worktreeNote: worktreeNote, sharedContext: "");

        Assert.True(sections.ContainsKey(SystemPromptSections.EnvironmentContext));
        var envSection = sections[SystemPromptSections.EnvironmentContext];
        Assert.Equal(SectionOverrideAction.Append, envSection.Action);
        Assert.Contains("/tmp/wt", envSection.Content);
    }

    [Fact]
    public void WorkerSystemMessageSections_OmitsWorktreeWhenEmpty()
    {
        var sections = CopilotService.BuildWorkerSystemMessageSections(
            "worker", worktreeNote: "", sharedContext: "");

        Assert.False(sections.ContainsKey(SystemPromptSections.EnvironmentContext));
    }

    [Fact]
    public void WorkerSystemMessageSections_IncludesSharedContext()
    {
        var sharedContext = "Always use TDD. Run tests before committing.";
        var sections = CopilotService.BuildWorkerSystemMessageSections(
            "worker", worktreeNote: "", sharedContext: sharedContext);

        Assert.True(sections.ContainsKey(SystemPromptSections.CustomInstructions));
        var customSection = sections[SystemPromptSections.CustomInstructions];
        Assert.Equal(SectionOverrideAction.Append, customSection.Action);
        Assert.Contains("Team Context", customSection.Content);
        Assert.Contains(sharedContext, customSection.Content);
    }

    [Fact]
    public void WorkerSystemMessageSections_OmitsSharedContextWhenEmpty()
    {
        var sections = CopilotService.BuildWorkerSystemMessageSections(
            "worker", worktreeNote: "", sharedContext: "");

        Assert.False(sections.ContainsKey(SystemPromptSections.CustomInstructions));
    }

    [Fact]
    public void WorkerSystemMessageSections_AllSectionsUseAppendAction()
    {
        var sections = CopilotService.BuildWorkerSystemMessageSections(
            "You are a specialist.",
            "\n\n## Your Worktree\nAt /tmp/wt (branch: dev).\n",
            "Shared team decisions.");

        foreach (var (key, section) in sections)
        {
            Assert.Equal(SectionOverrideAction.Append, section.Action);
        }
    }

    #endregion

    #region MergeDynamicContentIntoSections

    [Fact]
    public void MergeDynamicContent_AddsToEnvironmentContext_WhenNoExistingSection()
    {
        var sections = CopilotService.BuildWorkerSystemMessageSections(
            "worker", worktreeNote: "", sharedContext: "");

        Assert.False(sections.ContainsKey(SystemPromptSections.EnvironmentContext));

        var merged = CopilotService.MergeDynamicContentIntoSections(sections, "MCP guidance here");

        Assert.True(merged.ContainsKey(SystemPromptSections.EnvironmentContext));
        Assert.Contains("MCP guidance here", merged[SystemPromptSections.EnvironmentContext].Content);
    }

    [Fact]
    public void MergeDynamicContent_MergesWithExistingEnvironmentContext()
    {
        var sections = CopilotService.BuildWorkerSystemMessageSections(
            "worker", worktreeNote: "\n\n## Your Worktree\nAt /tmp/wt\n", sharedContext: "");

        Assert.True(sections.ContainsKey(SystemPromptSections.EnvironmentContext));

        var merged = CopilotService.MergeDynamicContentIntoSections(sections, "Relaunch instructions");

        var envContent = merged[SystemPromptSections.EnvironmentContext].Content;
        Assert.Contains("/tmp/wt", envContent);
        Assert.Contains("Relaunch instructions", envContent);
    }

    [Fact]
    public void MergeDynamicContent_NoOpWhenContentEmpty()
    {
        var sections = CopilotService.BuildWorkerSystemMessageSections(
            "worker", worktreeNote: "", sharedContext: "");

        var merged = CopilotService.MergeDynamicContentIntoSections(sections, "  ");

        Assert.False(merged.ContainsKey(SystemPromptSections.EnvironmentContext));
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
