# Testing Guide

PolyPilot uses a two-layer testing strategy: **deterministic unit tests** that run without the app, and **executable UI scenarios** that validate against a running instance using [MauiDevFlow](https://github.com/ArdaKit/Redth.MauiDevFlow).

## Unit Tests (xUnit)

### Quick Start

```bash
cd PolyPilot.Tests
dotnet test                                              # Run all tests
dotnet test --filter "FullyQualifiedName~ChatMessageTests"  # One test class
dotnet test --filter "FullyQualifiedName~ChatMessageTests.UserMessage_SetsRoleAndType"  # Single test
```

### Architecture

The test project targets `net10.0` (not MAUI) so tests run on any machine without platform SDKs. Since the MAUI project can't be directly referenced from a plain .NET project, the test `.csproj` uses `<Compile Include>` links to pull in source files from the main project:

```xml
<!-- PolyPilot.Tests.csproj -->
<Compile Include="..\PolyPilot\Models\ChatMessage.cs" Link="Linked\ChatMessage.cs" />
<Compile Include="..\PolyPilot\Services\CopilotService.cs" Link="Linked\CopilotService.cs" />
<!-- ... 70+ linked files -->
```

This means **any new model or service class** with no MAUI dependencies can be tested by adding a `<Compile Include>` entry to the test project.

### Test Coverage

| Area | Test Files | What's Covered |
|------|-----------|----------------|
| **Models** | ChatMessageTests, AgentSessionInfoTests, BridgeMessageTests, FiestaModelTests, ConnectionSettingsTests | Serialization, round-trip JSON, enum parsing, property defaults |
| **Multi-Agent** | MultiAgentRegressionTests, MultiAgentGapTests, ReflectionCycleTests, WorktreeStrategyTests | Orchestration modes, reflection loops, stall detection, worktree isolation strategies |
| **Organization** | SessionOrganizationTests, SquadDiscoveryTests, SquadWriterTests | Group stability, .squad directory parsing, charter round-tripping |
| **Services** | CopilotServiceInitializationTests, ServerManagerTests, RepoManagerTests | Mode switching, reconnection, save guards, thread safety |
| **Watchdog** | ProcessingWatchdogTests, StuckSessionRecoveryTests | Timeout tiers, stuck session detection, recovery messages |
| **Persistence** | SessionPersistenceTests, UiStatePersistenceTests | Session merge, partial restore, UI state round-trip |
| **Parsing** | DiffParserTests, CommandParserTests, EventsJsonlParsingTests | Git diffs, slash commands, JSONL event streams |
| **UI Logic** | InputSelectionTests, SlashCommandAutocompleteTests, BottomBarTooltipTests | CSS classes, autocomplete ranking, tooltip formatting |
| **Bridge** | WsBridgeIntegrationTests, WsBridgeServerAuthTests | WebSocket messaging, authentication flow |

### Key Conventions

- **`[Collection("BaseDir")]`** — Any test class that calls `SetBaseDirForTesting()` must use this xUnit collection attribute. It prevents parallel test execution that would corrupt the shared base directory.
- **Fake services** — Tests use lightweight fakes (e.g., `FakeRepoManager`, `FakeCopilotService`) rather than mocks. Look in `TestStubs.cs` and individual test files for patterns.
- **No network required** — All unit tests are fully offline. Copilot SDK interactions are faked.

## Executable Scenarios (MauiDevFlow CDP)

Scenarios are JSON files in `PolyPilot.Tests/Scenarios/` that describe end-to-end user flows. They run against a **live PolyPilot instance** using MauiDevFlow's CDP (Chrome DevTools Protocol) commands to interact with the Blazor WebView.

### Available Scenario Suites

| File | Scenarios | What's Covered |
|------|-----------|----------------|
| `multi-agent-scenarios.json` | 25 | OrchestratorReflect loop, stall detection, group lifecycle, Squad discovery, preset creation, charter injection, round-trip write-back |
| `mode-switch-scenarios.json` | 10+ | Persistent ↔ Embedded ↔ Remote mode switching, session persistence across restarts, CLI source switching, stuck session recovery |

### Running Scenarios

1. **Build and launch the app:**
   ```bash
   cd PolyPilot

   # macOS
   ./relaunch.sh

   # Windows
   powershell -ExecutionPolicy Bypass -File relaunch.ps1
   ```

2. **Wait for the MauiDevFlow agent to connect:**
   ```bash
   maui-devflow MAUI status    # Poll until connected
   ```

3. **Execute scenario steps via CDP:**
   ```bash
   # Navigate
   maui-devflow cdp Input dispatchClickEvent "a[href='/settings']"

   # Read state
   maui-devflow cdp Runtime evaluate "document.querySelectorAll('.session-item').length"

   # Fill input
   maui-devflow cdp Input fill ".branch-input" "feature/my-branch"

   # Take screenshot for visual verification
   maui-devflow MAUI screenshot --output check.png
   ```

### Scenario Format

Each scenario is a JSON object with an `id`, `name`, `description`, optional `invariants` (what must be true), and `steps`:

```json
{
  "id": "reflect-loop-completes-goal-met",
  "name": "OrchestratorReflect loop runs to goal completion",
  "invariants": [
    "ReflectionState.GoalMet == true on exit",
    "ReflectionState.IsActive == false on exit"
  ],
  "steps": [
    { "action": "createGroup", "mode": "OrchestratorReflect", "workers": 2 },
    { "action": "sendPrompt", "text": "Analyze the project structure" },
    { "action": "waitForPhase", "phase": "Complete", "timeout": 600 },
    { "action": "assertReflectionState", "field": "GoalMet", "expected": true }
  ]
}
```

### Cross-Referencing Scenarios and Unit Tests

`ScenarioReferenceTests.cs` bridges the two layers — it validates that scenario JSON files are well-formed and documents which unit tests cover the same invariants as each scenario. This ensures that every CDP scenario has a fast, deterministic unit-test equivalent.

For example, the scenario `"mode-switch-persistent-to-embedded-and-back"` is cross-referenced to `ModeSwitch_RapidModeSwitches_NoCorruption` in the unit test suite.

## Adding New Tests

### New unit test
1. Create `YourFeatureTests.cs` in `PolyPilot.Tests/`
2. If testing a new source file, add a `<Compile Include>` to `PolyPilot.Tests.csproj`
3. Use `[Fact]` for single cases, `[Theory]` with `[InlineData]` for parameterized
4. If your test uses `SetBaseDirForTesting()`, add `[Collection("BaseDir")]`

### New scenario
1. Add a scenario object to the appropriate JSON file in `Scenarios/`
2. Add a cross-reference test in `ScenarioReferenceTests.cs`
3. Write a matching deterministic unit test that covers the same invariant

## Architecture Docs

For the multi-agent orchestration architecture, invariants, and detailed test matrix, see [`docs/multi-agent-orchestration.md`](multi-agent-orchestration.md).
