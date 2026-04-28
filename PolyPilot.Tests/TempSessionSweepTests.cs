using PolyPilot.Services;

namespace PolyPilot.Tests;

public class TempSessionSweepTests : IDisposable
{
    private readonly string _tempBase;

    public TempSessionSweepTests()
    {
        _tempBase = Path.Combine(Path.GetTempPath(), $"sweep-test-{Guid.NewGuid():N}");
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_tempBase)) Directory.Delete(_tempBase, true); }
        catch { }
    }

    [Fact]
    public void Sweep_NonExistentBase_DoesNotThrow()
    {
        // No directory exists — sweep should be a no-op
        CopilotService.SweepOrphanedTempSessionDirs(_tempBase, []);
    }

    [Fact]
    public void Sweep_EmptyBase_DoesNotThrow()
    {
        Directory.CreateDirectory(_tempBase);
        CopilotService.SweepOrphanedTempSessionDirs(_tempBase, []);
        Assert.True(Directory.Exists(_tempBase));
    }

    [Fact]
    public void Sweep_DeletesOrphanedDirectories()
    {
        Directory.CreateDirectory(_tempBase);
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
        Directory.CreateDirectory(_tempBase);
        var keep = Path.Combine(_tempBase, "keep1234");
        var orphan = Path.Combine(_tempBase, "orphan99");
        Directory.CreateDirectory(keep);
        Directory.CreateDirectory(orphan);

        CopilotService.SweepOrphanedTempSessionDirs(_tempBase, [keep]);

        Assert.True(Directory.Exists(keep));
        Assert.False(Directory.Exists(orphan));
    }

    [Fact]
    public void Sweep_HandlesNullWorkingDirEntries()
    {
        Directory.CreateDirectory(_tempBase);
        var orphan = Path.Combine(_tempBase, "testdir1");
        Directory.CreateDirectory(orphan);

        // Should not crash when persisted dirs include nulls and empty strings
        CopilotService.SweepOrphanedTempSessionDirs(_tempBase, [null, "", null]);

        Assert.False(Directory.Exists(orphan));
    }

    [Fact]
    public void Sweep_PreservesMultipleReferencedDirs()
    {
        Directory.CreateDirectory(_tempBase);
        var keep1 = Path.Combine(_tempBase, "session-a");
        var keep2 = Path.Combine(_tempBase, "session-b");
        var orphan = Path.Combine(_tempBase, "session-c");
        Directory.CreateDirectory(keep1);
        Directory.CreateDirectory(keep2);
        Directory.CreateDirectory(orphan);

        CopilotService.SweepOrphanedTempSessionDirs(_tempBase, [keep1, keep2]);

        Assert.True(Directory.Exists(keep1));
        Assert.True(Directory.Exists(keep2));
        Assert.False(Directory.Exists(orphan));
    }

    [Fact]
    public void Sweep_IgnoresNonChildPaths()
    {
        // A persisted WorkingDirectory that is NOT under tempBase shouldn't affect sweep
        Directory.CreateDirectory(_tempBase);
        var orphan = Path.Combine(_tempBase, "mydir");
        Directory.CreateDirectory(orphan);

        CopilotService.SweepOrphanedTempSessionDirs(_tempBase, ["/some/other/path"]);

        Assert.False(Directory.Exists(orphan));
    }

    [Fact]
    public void Sweep_DeletesNestedContent()
    {
        Directory.CreateDirectory(_tempBase);
        var orphan = Path.Combine(_tempBase, "nested");
        Directory.CreateDirectory(orphan);
        File.WriteAllText(Path.Combine(orphan, "file.txt"), "content");
        Directory.CreateDirectory(Path.Combine(orphan, "subdir"));
        File.WriteAllText(Path.Combine(orphan, "subdir", "deep.txt"), "deep");

        CopilotService.SweepOrphanedTempSessionDirs(_tempBase, []);

        Assert.False(Directory.Exists(orphan));
    }

    [Fact]
    public void TempSessionsBase_RedirectedBySetBaseDirForTesting()
    {
        // Verify test isolation redirects TempSessionsBase under the test base dir
        var expected = Path.Combine(TestSetup.TestBaseDir, "polypilot-sessions");
        Assert.Equal(expected, CopilotService.TempSessionsBase);
    }

    [Fact]
    public void Sweep_CaseInsensitiveMatchOnNonLinux()
    {
        // On non-Linux (macOS, Windows), paths should match case-insensitively
        if (OperatingSystem.IsLinux()) return; // skip on CI Linux — Linux uses case-sensitive

        Directory.CreateDirectory(_tempBase);
        var dir = Path.Combine(_tempBase, "AbCdEf");
        Directory.CreateDirectory(dir);

        // Reference with different casing
        var upperRef = Path.Combine(_tempBase, "ABCDEF");
        CopilotService.SweepOrphanedTempSessionDirs(_tempBase, [upperRef]);

        Assert.True(Directory.Exists(dir), "Should be kept via case-insensitive match");
    }

    [Fact]
    public void Sweep_CaseSensitiveMatchOnLinux()
    {
        if (!OperatingSystem.IsLinux()) return; // only relevant on Linux

        Directory.CreateDirectory(_tempBase);
        var dir = Path.Combine(_tempBase, "AbCdEf");
        Directory.CreateDirectory(dir);

        // Reference with different casing — should NOT match on Linux
        var upperRef = Path.Combine(_tempBase, "ABCDEF");
        CopilotService.SweepOrphanedTempSessionDirs(_tempBase, [upperRef]);

        Assert.False(Directory.Exists(dir), "Should be deleted — case-sensitive mismatch on Linux");
    }
}
