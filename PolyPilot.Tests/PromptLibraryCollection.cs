using Xunit;

namespace PolyPilot.Tests;

/// <summary>
/// xUnit collection that serializes test classes that mutate
/// PromptLibraryService._userPromptsDir (via SetUserPromptsDirForTesting).
/// Without this, PromptCommandTests and PromptLibraryTests race on the
/// shared static field, causing flaky failures.
/// </summary>
[CollectionDefinition("PromptLibrary")]
public class PromptLibraryCollection : ICollectionFixture<PromptLibraryCollectionFixture> { }

public class PromptLibraryCollectionFixture { }
