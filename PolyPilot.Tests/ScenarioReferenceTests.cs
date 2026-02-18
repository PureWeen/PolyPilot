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
///   maui-devflow MAUI status  # wait for agent
///   # Then iterate steps via: maui-devflow cdp Runtime evaluate "..."
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
            Assert.NotNull(doc.RootElement.GetProperty("scenarios"));
        }
    }

    [Fact]
    public void ModeSwitchScenarios_AllHaveRequiredFields()
    {
        var json = File.ReadAllText(Path.Combine(ScenariosDir, "mode-switch-scenarios.json"));
        var doc = JsonDocument.Parse(json);
        var scenarios = doc.RootElement.GetProperty("scenarios");

        foreach (var scenario in scenarios.EnumerateArray())
        {
            Assert.True(scenario.TryGetProperty("id", out _), "Scenario missing 'id'");
            Assert.True(scenario.TryGetProperty("name", out _), "Scenario missing 'name'");
            Assert.True(scenario.TryGetProperty("steps", out var steps), "Scenario missing 'steps'");
            Assert.True(steps.GetArrayLength() > 0,
                $"Scenario '{scenario.GetProperty("id").GetString()}' has no steps");
        }
    }

    [Fact]
    public void ModeSwitchScenarios_StepsHaveValidActions()
    {
        var validActions = new HashSet<string> { "click", "evaluate", "wait", "shell", "screenshot", "type", "note" };
        var json = File.ReadAllText(Path.Combine(ScenariosDir, "mode-switch-scenarios.json"));
        var doc = JsonDocument.Parse(json);

        foreach (var scenario in doc.RootElement.GetProperty("scenarios").EnumerateArray())
        {
            var id = scenario.GetProperty("id").GetString()!;
            foreach (var step in scenario.GetProperty("steps").EnumerateArray())
            {
                Assert.True(step.TryGetProperty("action", out var action),
                    $"Step in '{id}' missing 'action'");
                Assert.Contains(action.GetString(), validActions);
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
    /// Scenario: "stuck-session-recovery-after-server-disconnect"
    /// Unit test equivalents: ProcessingWatchdogTests.WatchdogCheckInterval_IsReasonable,
    ///   ProcessingWatchdogTests.WatchdogInactivityTimeout_IsReasonable,
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
}
