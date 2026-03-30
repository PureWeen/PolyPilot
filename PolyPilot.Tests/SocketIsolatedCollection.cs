using Xunit;

namespace PolyPilot.Tests;

/// <summary>
/// xUnit collection with DisableParallelization=true for tests that hook
/// TaskScheduler.UnobservedTaskException globally and call GC.Collect().
/// Without this, SocketExceptions from unrelated parallel tests get surfaced
/// during GC and falsely trigger the handler.
/// </summary>
[CollectionDefinition("SocketIsolated", DisableParallelization = true)]
public class SocketIsolatedCollection { }
