using PolyPilot.Services;

namespace PolyPilot.Tests;

/// <summary>
/// Tests for orphaned temp session directory cleanup (SweepOrphanedTempSessionDirs).
/// Verifies that startup sweep correctly identifies and removes directories
/// not referenced by any persisted session, while preserving active ones.
/// </summary>
public class TempSessionSweepTests : IDisposable
{
    private readonly string _tempBase;

    public TempSessionSweepTests()
    {
        _tempBase = Path.Combine(Path.GetTempPath(), "polypilot-sweep-test-" + Guid.NewGuid().ToString()[..8]);
        Directory.CreateDirectory(_tempBase);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempBase, recursive: true); } catch { }
    }

    [Fact]
    public void Sweep_NoTempBase_DoesNotThrow()
    {
        var nonExistent = Path.Combine(Path.GetTempPath(), "polypilot-sweep-nonexistent-" + Guid.NewGuid().ToString()[..8]);
        CopilotService.SweepOrphanedTempSessionDirs(nonExistent, []);
        Assert.False(Directory.Exists(nonExistent));
    }

    [Fact]
    public void Sweep_EmptyTempBase_DoesNothing()
    {
        CopilotService.SweepOrphanedTempSessionDirs(_tempBase, []);
        Assert.True(Directory.Exists(_tempBase));
    }

    [Fact]
    public void Sweep_DeletesOrphanedDirectories()
    {
        var orphan1 = Path.Combine(_tempBase, "abc12345");
        var orphan2 = Path.Combine(_tempBase, "def67890");
        Directory.CreateDirectory(orphan1);
        Directory.CreateDirectory(orphan2);

        CopilotService.SweepOrphanedTempSessionDirs(_tempBase, []);

        Assert.False(Directory.Exists(orphan1));
        Assert.False(Directory.Exists(orphan2));
    }

    [Fact]
    public void Sweep_PreservesReferencedDirectories()
    {
        var active = Path.Combine(_tempBase, "active01");
        var orphan = Path.Combine(_tempBase, "orphan01");
        Directory.CreateDirectory(active);
        Directory.CreateDirectory(orphan);

        CopilotService.SweepOrphanedTempSessionDirs(_tempBase, [active]);

        Assert.True(Directory.Exists(active));
        Assert.False(Directory.Exists(orphan));
    }

    [Fact]
    public void Sweep_HandlesNullWorkingDirectories()
    {
        var orphan = Path.Combine(_tempBase, "orphan01");
        Directory.CreateDirectory(orphan);

        // Persisted list includes nulls — should not crash
        CopilotService.SweepOrphanedTempSessionDirs(_tempBase, [null, "", null]);

        Assert.False(Directory.Exists(orphan));
    }

    [Fact]
    public void Sweep_CaseInsensitiveMatch()
    {
        var dir = Path.Combine(_tempBase, "MixedCase");
        Directory.CreateDirectory(dir);

        // Reference with different case
        var refPath = Path.Combine(_tempBase, "mixedcase");
        CopilotService.SweepOrphanedTempSessionDirs(_tempBase, [refPath]);

        // On case-insensitive file systems (macOS/Windows), the dir should be preserved.
        // On Linux (case-sensitive), it may be deleted since the paths differ.
        // The test validates that the code doesn't crash regardless.
    }

    [Fact]
    public void Sweep_PreservesMultipleReferencedDirs()
    {
        var dir1 = Path.Combine(_tempBase, "sess0001");
        var dir2 = Path.Combine(_tempBase, "sess0002");
        var dir3 = Path.Combine(_tempBase, "sess0003");
        Directory.CreateDirectory(dir1);
        Directory.CreateDirectory(dir2);
        Directory.CreateDirectory(dir3);

        CopilotService.SweepOrphanedTempSessionDirs(_tempBase, [dir1, dir3]);

        Assert.True(Directory.Exists(dir1));
        Assert.False(Directory.Exists(dir2));
        Assert.True(Directory.Exists(dir3));
    }

    [Fact]
    public void Sweep_IgnoresNonChildPaths()
    {
        // A persisted working dir that points outside the temp base
        var orphan = Path.Combine(_tempBase, "orphan01");
        Directory.CreateDirectory(orphan);
        var outsidePath = Path.Combine(Path.GetTempPath(), "some-other-dir");

        CopilotService.SweepOrphanedTempSessionDirs(_tempBase, [outsidePath]);

        // The orphan should still be deleted
        Assert.False(Directory.Exists(orphan));
    }

    [Fact]
    public void Sweep_DeletesDirectoriesWithContents()
    {
        var orphan = Path.Combine(_tempBase, "orphan01");
        Directory.CreateDirectory(orphan);
        File.WriteAllText(Path.Combine(orphan, "file.txt"), "test");
        Directory.CreateDirectory(Path.Combine(orphan, "subdir"));
        File.WriteAllText(Path.Combine(orphan, "subdir", "nested.txt"), "test");

        CopilotService.SweepOrphanedTempSessionDirs(_tempBase, []);

        Assert.False(Directory.Exists(orphan));
    }

    [Fact]
    public void TempSessionsBase_IsRedirectedBySetBaseDirForTesting()
    {
        // SetBaseDirForTesting is called by TestSetup.Initialize, so TempSessionsBase
        // should point to the test directory, not the real system temp dir.
        var tempBase = CopilotService.TempSessionsBase;
        Assert.Contains("polypilot-tests-", tempBase);
    }
}
