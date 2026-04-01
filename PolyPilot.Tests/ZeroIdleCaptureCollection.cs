using Xunit;

namespace PolyPilot.Tests;

/// <summary>
/// xUnit collection with DisableParallelization=true for tests that override
/// the global zero-idle capture directory. Without this, unrelated tests that
/// mutate CopilotService base-dir state can reset the override mid-test.
/// </summary>
[CollectionDefinition("ZeroIdleCapture", DisableParallelization = true)]
public class ZeroIdleCaptureCollection { }
