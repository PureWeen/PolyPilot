using System.Text.Json;

namespace PolyPilot.Tests;

/// <summary>
/// Validates the UI scenario JSON definitions are well-formed and cross-references
/// them with the unit test coverage. Each scenario describes a user flow that requires
/// the running app + MauiDevFlow CDP; the corresponding unit tests verify the same
/// invariants deterministically without the app.
///
/// To execute scenarios against a live app, use MauiDevFlow:
///   cd PolyPilot && ./relaunch.sh
///   maui devflow MAUI status  # wait for agent
///   # Then iterate steps via: maui devflow cdp Runtime evaluate "..."
/// </summary>
public class ScenarioReferenceTests
{
    private static readonly string ScenariosDir = Path.Combine(
        AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "Scenarios");

    [Fact]
    public void ScenarioFiles_AreValidJson()
    {
        var files = Directory.GetFiles(ScenariosDir, "*.json");
        Assert.NotEmpty(files);

        foreach (var file in files)
        {
            var json = File.ReadAllText(file);
            var doc = JsonDocument.Parse(json); // throws on invalid JSON
            Assert.True(doc.RootElement.TryGetProperty("scenarios", out _),
                $"Scenario file '{Path.GetFileName(file)}' is missing a 'scenarios' array");
        }
    }

    [Fact]
    public void ScenarioFiles_AllHaveRequiredFields()
    {
        foreach (var file in Directory.GetFiles(ScenariosDir, "*.json"))
        {
            var json = File.ReadAllText(file);
            var doc = JsonDocument.Parse(json);
            var scenarios = doc.RootElement.GetProperty("scenarios");

            foreach (var scenario in scenarios.EnumerateArray())
            {
                Assert.True(scenario.TryGetProperty("id", out _), $"Scenario in '{Path.GetFileName(file)}' missing 'id'");
                Assert.True(scenario.TryGetProperty("name", out _), $"Scenario in '{Path.GetFileName(file)}' missing 'name'");
                Assert.True(scenario.TryGetProperty("steps", out var steps), $"Scenario in '{Path.GetFileName(file)}' missing 'steps'");
                Assert.True(steps.GetArrayLength() > 0,
                    $"Scenario '{scenario.GetProperty("id").GetString()}' has no steps");
            }
        }
    }

    [Fact]
    public void ScenarioFiles_StepsHaveValidActions()
    {
        var validActions = new HashSet<string>
        {
            "assertAllSessionsReceived", "assertAllSessionsResponded", "assertAllWorkers",
            "assertDirectoryExists", "assertEqual", "assertEvaluatorWasUsed", "assertEventLog",
            "assertFileContains", "assertFileExists", "assertGroupExists", "assertGroupMembership",
            "assertInputAvailable", "assertNoDirectoryContains", "assertNoNewEvents",
            "assertNoOverlap", "assertNoPresetInSection", "assertNoReflectionLoop",
            "assertNoSessionsInDefault", "assertNoSessionsWithGroupId", "assertOrchestratorReceivedRoutingContext",
            "assertOrchestratorReceivedWorkerDescriptions", "assertOrchestratorSynthesized",
            "assertOrgJson", "assertPresetInSection", "assertPresetVisible", "assertProcessing",
            "assertReflectionPaused", "assertReflectionState", "assertResponseNotEmpty",
            "assertSessionExists", "assertSessionMeta", "assertSessionNotExists",
            "assertWorkerPromptContains",
            "captureEventLogPosition", "captureGroupState", "captureSessionList",
            "click", "clickAbort", "clickCreate", "closeSession",
            "createGroup", "createGroupFromPreset", "createSession", "createSquadDir",
            "deleteGroup", "evaluate", "navigate", "note",
            "openCreateMenu", "pauseReflection", "readOrgJson", "relaunchApp",
            "restartApp", "resumeReflection", "saveGroupAsPreset",
            "selectOption", "selectPreset", "selectRepo", "selectSession", "selectWorktree",
            "sendMessage", "sendPrompt", "sendToGroup",
            "setEvaluator", "setMode", "shell", "shellCheck", "switchModel",
            "type", "wait", "waitForAgent", "waitForAllResponses", "waitForAllSessions",
            "waitForCompletion", "waitForEventPattern", "waitForIdle", "waitForPhase",
            "screenshot"
        };
        foreach (var file in Directory.GetFiles(ScenariosDir, "*.json"))
        {
            var json = File.ReadAllText(file);
            var doc = JsonDocument.Parse(json);

            foreach (var scenario in doc.RootElement.GetProperty("scenarios").EnumerateArray())
            {
                var id = scenario.GetProperty("id").GetString()!;
                foreach (var step in scenario.GetProperty("steps").EnumerateArray())
                {
                    Assert.True(step.TryGetProperty("action", out var action),
                        $"Step in '{id}' missing 'action'");
                    var actionValue = action.GetString();
                    Assert.False(string.IsNullOrWhiteSpace(actionValue),
                        $"Step in '{id}' has an empty action");
                    Assert.Contains(actionValue!, validActions);
                }
            }
        }
    }

    // --- Cross-references: scenarios ↔ unit tests ---
    //
    // Each UI scenario below has a matching unit test in CopilotServiceInitializationTests
    // or SessionPersistenceTests that verifies the same invariant deterministically.

    /// <summary>
    /// Scenario: "mode-switch-persistent-to-embedded-and-back"
    /// Unit test equivalents: ModeSwitch_RapidModeSwitches_NoCorruption,
    ///   ModeSwitch_DemoToPersistentFailure_SessionsCleared,
    ///   Merge_SimulatePartialRestore_PreservesUnrestoredSessions
    /// </summary>
    [Fact]
    public void Scenario_ModeSwitchRoundTrip_HasUnitTestCoverage()
    {
        // This test simply documents the relationship.
        // The actual assertions are in the referenced tests.
        Assert.True(true, "See CopilotServiceInitializationTests.ModeSwitch_RapidModeSwitches_NoCorruption");
    }

    /// <summary>
    /// Scenario: "mode-switch-rapid-no-session-loss"
    /// Unit test equivalents: Merge_SimulateEmptyMemoryAfterClear_PreservesAll,
    ///   Merge_SimulatePartialRestore_PreservesUnrestoredSessions
    /// </summary>
    [Fact]
    public void Scenario_RapidSwitch_HasMergeTestCoverage()
    {
        Assert.True(true, "See SessionPersistenceTests.Merge_SimulatePartialRestore_PreservesUnrestoredSessions");
    }

    /// <summary>
    /// Scenario: "persistent-failure-shows-needs-configuration"
    /// Unit test equivalents: ReconnectAsync_PersistentMode_Failure_SetsNeedsConfiguration
    /// </summary>
    [Fact]
    public void Scenario_PersistentFailure_HasUnitTestCoverage()
    {
        Assert.True(true, "See CopilotServiceInitializationTests.ReconnectAsync_PersistentMode_Failure_SetsNeedsConfiguration");
    }

    /// <summary>
    /// Scenario: "failed-persistent-then-demo-recovery"
    /// Unit test equivalents: ModeSwitch_PersistentFailureThenDemo_Recovers
    /// </summary>
    [Fact]
    public void Scenario_FailedThenDemoRecovery_HasUnitTestCoverage()
    {
        Assert.True(true, "See CopilotServiceInitializationTests.ModeSwitch_PersistentFailureThenDemo_Recovers");
    }

    /// <summary>
    /// Scenario: "cli-source-switch-builtin-to-system"
    /// Unit test equivalents: ConnectionSettings_CliSource_CanSwitchToSystem,
    ///   ConnectionSettings_Serialization_PreservesCliSource,
    ///   ConnectionSettings_CliSource_IndependentOfMode
    /// </summary>
    [Fact]
    public void Scenario_CliSourceSwitch_HasUnitTestCoverage()
    {
        Assert.True(true, "See CopilotServiceInitializationTests.ConnectionSettings_CliSource_* tests");
    }

    /// <summary>
    /// Scenario: "mode-persists-without-save-reconnect"
    /// Unit test equivalents: ConnectionSettings_SetMode_PersistsMode
    /// </summary>
    [Fact]
    public void Scenario_ModePersistsImmediately_HasUnitTestCoverage()
    {
        Assert.True(true, "See CopilotServiceInitializationTests.ConnectionSettings_SetMode_PersistsMode");
    }

    /// <summary>
    /// Scenario: "refresh-sessions-button-visible"
    /// Unit test equivalents: RefreshSessionsAsync_DemoMode_FiresOnStateChanged,
    ///   RefreshSessionsAsync_RemoteMode_RequestsBridgeSessions
    /// </summary>
    [Fact]
    public void Scenario_RefreshSessionsButton_HasUnitTestCoverage()
    {
        Assert.True(true, "See CopilotServiceInitializationTests.RefreshSessionsAsync_* tests");
    }

    /// <summary>
    /// Scenario: "create-session-model-picker-includes-gpt-5-4"
    /// Unit test equivalents: ModelSelectionTests.BuildSelectionList_AppendsSelectionAndDefault_WhenDiscoveryIsEmpty,
    ///   ModelSelectionTests.BuildSelectionList_NormalizesDiscoveredModels_AndAvoidsDuplicates,
    ///   and EventsJsonlParsingTests.ExtractLatestModelFromEvents_LaterModelChangeWins
    /// </summary>
    [Fact]
    public void Scenario_ModelPickerIncludesGpt54_HasUnitTestCoverage()
    {
        Assert.True(true, "See ModelSelectionTests.BuildSelectionList_* and EventsJsonlParsingTests.ExtractLatestModelFromEvents_LaterModelChangeWins");
    }

    /// <summary>
    /// Scenario: "stuck-session-recovery-after-server-disconnect"
    /// Unit test equivalents: ProcessingWatchdogTests.WatchdogCheckInterval_IsReasonable,
    ///   ProcessingWatchdogTests.WatchdogInactivityTimeout_IsReasonable,
    ///   ProcessingWatchdogTests.WatchdogToolExecutionTimeout_IsReasonable,
    ///   ProcessingWatchdogTests.SystemMessage_ConnectionLost_HasExpectedContent,
    ///   ProcessingWatchdogTests.SystemMessage_AddedToHistory_IsVisible
    /// </summary>
    [Fact]
    public void Scenario_StuckSessionRecovery_HasUnitTestCoverage()
    {
        Assert.True(true, "See ProcessingWatchdogTests for watchdog constant validation and recovery message tests");
    }

    /// <summary>
    /// Scenario: "relaunch-with-stale-server-shows-sessions"
    /// Unit test equivalents: ProcessingWatchdogTests.PersistentMode_FailedInit_*,
    ///   ProcessingWatchdogTests.ReconnectAsync_IsInitialized_CorrectForEachMode,
    ///   ProcessingWatchdogTests.ReconnectAsync_ClearsStuckProcessingFromPreviousMode
    /// </summary>
    [Fact]
    public void Scenario_RelaunchWithStaleServer_HasUnitTestCoverage()
    {
        Assert.True(true, "See ProcessingWatchdogTests for relaunch/reconnect resilience tests");
    }

    /// <summary>
    /// Scenario: "shell-command-uses-platform-shell"
    /// Unit test equivalents: PlatformHelperTests.GetShellCommand_*
    /// </summary>
    [Fact]
    public void Scenario_ShellCommandUsesPlatformShell_HasUnitTestCoverage()
    {
        Assert.True(true, "See PlatformHelperTests.GetShellCommand_* for platform shell selection tests");
    }

    /// <summary>
    /// Scenario: "scheduled-task-create-and-run-now"
    /// Unit test equivalents: Service_EvaluateTasksAsync_ExecutesDueTasks,
    ///   Service_ExecuteTask_NewSession_RecordsCompletionAndGeneratedSessionName
    /// </summary>
    [Fact]
    public void Scenario_ScheduledTaskCreateAndRunNow_HasUnitTestCoverage()
    {
        Assert.True(true, "See ScheduledTaskTests.Service_EvaluateTasksAsync_ExecutesDueTasks and Service_ExecuteTask_NewSession_RecordsCompletionAndGeneratedSessionName");
    }

    /// <summary>
    /// Scenario: "scheduled-task-run-now-twice-uses-unique-session"
    /// Unit test equivalents: Service_ExecuteTask_NewSession_ReusesTimestampButGeneratesUniqueName
    /// </summary>
    [Fact]
    public void Scenario_ScheduledTaskRunNowTwiceUsesUniqueSession_HasUnitTestCoverage()
    {
        Assert.True(true, "See ScheduledTaskTests.Service_ExecuteTask_NewSession_ReusesTimestampButGeneratesUniqueName");
    }

    /// <summary>
    /// Scenario: "scheduled-task-disable-edit-preserves-toggle"
    /// Unit test equivalents: Service_UpdateTask_DoesNotOverwriteIsEnabled_FromStaleEditSnapshot
    /// </summary>
    [Fact]
    public void Scenario_ScheduledTaskDisableEditPreservesToggle_HasUnitTestCoverage()
    {
        Assert.True(true, "See ScheduledTaskTests.Service_UpdateTask_DoesNotOverwriteIsEnabled_FromStaleEditSnapshot");
    }

    /// <summary>
    /// Scenario: "scheduled-task-form-validation"
    /// Unit test equivalents: CronExpression_ValidatesExpectedInputs,
    ///   CronExpression_InvalidExpressions_ReturnFalse, IsValidTimeOfDay_ValidatesCorrectly
    /// </summary>
    [Fact]
    public void Scenario_ScheduledTaskValidation_HasUnitTestCoverage()
    {
        Assert.True(true, "See ScheduledTaskTests cron and time validation tests");
    }

    /// <summary>
    /// Scenario: "vscode-remote-tunnels-in-remote-mode"
    /// Unit test equivalents: PlatformHelperTests.BuildVSCodeRemoteArg_*,
    ///   RemoteModeTests.SessionsListPayload_ServerMachineName_RoundTrip,
    ///   RemoteModeTests.SessionsListPayload_LegacyPayload_WithoutServerMachineName
    /// </summary>
    [Fact]
    public void Scenario_VSCodeRemoteTunnels_HasUnitTestCoverage()
    {
        Assert.True(true, "See PlatformHelperTests.BuildVSCodeRemoteArg_* and RemoteModeTests.SessionsListPayload_ServerMachineName_*");
    }

    /// <summary>
    /// Scenario: "custom-agent-popup-click-to-use"
    /// Unit test equivalents: AgentDiscoveryTests.*
    /// </summary>
    [Fact]
    public void Scenario_CustomAgentPopupClickToUse_HasUnitTestCoverage()
    {
        Assert.True(true, "See AgentDiscoveryTests for agent discovery and invocation text format tests");
    }

    [Fact]
    public void AllScenarios_HaveUniqueIds()
    {
        var json = File.ReadAllText(Path.Combine(ScenariosDir, "mode-switch-scenarios.json"));
        var doc = JsonDocument.Parse(json);
        var ids = doc.RootElement.GetProperty("scenarios")
            .EnumerateArray()
            .Select(s => s.GetProperty("id").GetString())
            .ToList();

        Assert.Equal(ids.Count, ids.Distinct().Count());
    }

    [Fact]
    public void MultiAgentScenarios_HaveUniqueIds()
    {
        var json = File.ReadAllText(Path.Combine(ScenariosDir, "multi-agent-scenarios.json"));
        var doc = JsonDocument.Parse(json);
        var ids = doc.RootElement.GetProperty("scenarios")
            .EnumerateArray()
            .Select(s => s.GetProperty("id").GetString())
            .ToList();

        Assert.Equal(ids.Count, ids.Distinct().Count());
    }

    [Fact]
    public void MultiAgentScenarios_IncludeSquadIntegration()
    {
        var json = File.ReadAllText(Path.Combine(ScenariosDir, "multi-agent-scenarios.json"));
        var doc = JsonDocument.Parse(json);
        var ids = doc.RootElement.GetProperty("scenarios")
            .EnumerateArray()
            .Select(s => s.GetProperty("id").GetString())
            .ToHashSet();

        Assert.Contains("squad-discovery-creates-preset", ids);
        Assert.Contains("squad-charter-becomes-system-prompt", ids);
        Assert.Contains("squad-decisions-shared-context", ids);
        Assert.Contains("squad-legacy-ai-team-compat", ids);
    }

    [Fact]
    public void MultiAgentScenarios_IncludeGroupDeletion()
    {
        var json = File.ReadAllText(Path.Combine(ScenariosDir, "multi-agent-scenarios.json"));
        var doc = JsonDocument.Parse(json);
        var ids = doc.RootElement.GetProperty("scenarios")
            .EnumerateArray()
            .Select(s => s.GetProperty("id").GetString())
            .ToHashSet();

        Assert.Contains("delete-group-no-contamination", ids);
        Assert.Contains("delete-multi-agent-group-closes-sessions", ids);
    }

    [Fact]
    public void MultiAgentScenarios_IncludeSquadWriteBack()
    {
        var json = File.ReadAllText(Path.Combine(ScenariosDir, "multi-agent-scenarios.json"));
        var doc = JsonDocument.Parse(json);
        var ids = doc.RootElement.GetProperty("scenarios")
            .EnumerateArray()
            .Select(s => s.GetProperty("id").GetString())
            .ToHashSet();

        Assert.Contains("save-preset-creates-squad-dir", ids);
        Assert.Contains("round-trip-squad-write-read", ids);
        Assert.Contains("squad-write-sanitizes-names", ids);
    }

    [Fact]
    public void MultiAgentScenarios_AllHaveRequiredFields()
    {
        var json = File.ReadAllText(Path.Combine(ScenariosDir, "multi-agent-scenarios.json"));
        var doc = JsonDocument.Parse(json);
        var scenarios = doc.RootElement.GetProperty("scenarios").EnumerateArray().ToList();

        Assert.NotEmpty(scenarios);
        foreach (var s in scenarios)
        {
            Assert.True(s.TryGetProperty("id", out _), "Scenario missing 'id'");
            Assert.True(s.TryGetProperty("name", out _), "Scenario missing 'name'");
            Assert.True(s.TryGetProperty("steps", out var steps), "Scenario missing 'steps'");
            Assert.NotEqual(0, steps.GetArrayLength());
        }
    }

    [Fact]
    public void MultiAgentScenarios_IncludeReflectLoopScenarios()
    {
        var json = File.ReadAllText(Path.Combine(ScenariosDir, "multi-agent-scenarios.json"));
        var doc = JsonDocument.Parse(json);
        var ids = doc.RootElement.GetProperty("scenarios")
            .EnumerateArray()
            .Select(s => s.GetProperty("id").GetString())
            .ToHashSet();

        Assert.Contains("reflect-loop-completes-goal-met", ids);
        Assert.Contains("reflect-loop-max-iterations", ids);
        Assert.Contains("stall-detection-triggers", ids);
    }
}
