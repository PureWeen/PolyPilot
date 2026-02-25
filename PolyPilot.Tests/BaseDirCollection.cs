using Xunit;

namespace PolyPilot.Tests;

/// <summary>
/// xUnit collection that serializes all test classes that read or mutate
/// CopilotService.BaseDir (via SetBaseDirForTesting). Without this,
/// parallel test classes race on the shared static _polyPilotBaseDir field,
/// causing flaky failures in TestIsolationGuardTests.
/// </summary>
[CollectionDefinition("BaseDir")]
public class BaseDirCollection : ICollectionFixture<BaseDirCollectionFixture> { }

public class BaseDirCollectionFixture { }
