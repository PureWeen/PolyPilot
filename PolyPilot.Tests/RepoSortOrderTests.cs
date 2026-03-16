using PolyPilot.Models;
using PolyPilot.Services;

namespace PolyPilot.Tests;

[Collection("BaseDir")]
public class RepoSortOrderTests
{
    [Fact]
    public void Repositories_SortedByLastUsedAtDescending()
    {
        var rm = new RepoManager();
        var state = new RepositoryState
        {
            Repositories = new List<RepositoryInfo>
            {
                new() { Id = "old", Name = "OldRepo", AddedAt = DateTime.UtcNow.AddDays(-10), LastUsedAt = DateTime.UtcNow.AddDays(-5) },
                new() { Id = "recent", Name = "RecentRepo", AddedAt = DateTime.UtcNow.AddDays(-20), LastUsedAt = DateTime.UtcNow.AddDays(-1) },
                new() { Id = "middle", Name = "MiddleRepo", AddedAt = DateTime.UtcNow.AddDays(-15), LastUsedAt = DateTime.UtcNow.AddDays(-3) },
            }
        };
        SetState(rm, state);

        var repos = rm.Repositories;

        Assert.Equal("recent", repos[0].Id);
        Assert.Equal("middle", repos[1].Id);
        Assert.Equal("old", repos[2].Id);
    }

    [Fact]
    public void Repositories_NullLastUsedAt_FallsBackToAddedAt()
    {
        var rm = new RepoManager();
        var state = new RepositoryState
        {
            Repositories = new List<RepositoryInfo>
            {
                new() { Id = "old-added", Name = "OldAdded", AddedAt = DateTime.UtcNow.AddDays(-30), LastUsedAt = null },
                new() { Id = "used", Name = "Used", AddedAt = DateTime.UtcNow.AddDays(-30), LastUsedAt = DateTime.UtcNow.AddDays(-1) },
                new() { Id = "new-added", Name = "NewAdded", AddedAt = DateTime.UtcNow.AddDays(-2), LastUsedAt = null },
            }
        };
        SetState(rm, state);

        var repos = rm.Repositories;

        // "used" has most recent LastUsedAt, "new-added" falls back to AddedAt (-2d), "old-added" falls back to AddedAt (-30d)
        Assert.Equal("used", repos[0].Id);
        Assert.Equal("new-added", repos[1].Id);
        Assert.Equal("old-added", repos[2].Id);
    }

    [Fact]
    public void TouchRepository_UpdatesLastUsedAt()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"repo-sort-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            RepoManager.SetBaseDirForTesting(tempDir);

            var rm = new RepoManager();
            var state = new RepositoryState
            {
                Repositories = new List<RepositoryInfo>
                {
                    new() { Id = "repo-a", Name = "RepoA", AddedAt = DateTime.UtcNow.AddDays(-10), LastUsedAt = null },
                    new() { Id = "repo-b", Name = "RepoB", AddedAt = DateTime.UtcNow.AddDays(-5), LastUsedAt = null },
                }
            };
            SetState(rm, state);

            // Touch repo-a — it should now have a recent LastUsedAt
            var before = DateTime.UtcNow;
            rm.TouchRepository("repo-a");

            var repos = rm.Repositories;

            // repo-a should now be first (most recently used)
            Assert.Equal("repo-a", repos[0].Id);
            Assert.NotNull(repos[0].LastUsedAt);
            Assert.True(repos[0].LastUsedAt >= before);
        }
        finally
        {
            RepoManager.SetBaseDirForTesting(TestSetup.TestBaseDir);
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public void TouchRepository_NonexistentRepo_DoesNotThrow()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"repo-sort-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            RepoManager.SetBaseDirForTesting(tempDir);

            var rm = new RepoManager();
            var state = new RepositoryState
            {
                Repositories = new List<RepositoryInfo>
                {
                    new() { Id = "repo-a", Name = "RepoA" },
                }
            };
            SetState(rm, state);

            // Should not throw for nonexistent repo
            rm.TouchRepository("nonexistent");
        }
        finally
        {
            RepoManager.SetBaseDirForTesting(TestSetup.TestBaseDir);
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public void LastUsedAt_DeserializesFromJson()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"repo-sort-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            RepoManager.SetBaseDirForTesting(tempDir);

            // Write a repos.json that includes LastUsedAt
            var json = """
            {
                "Repositories": [
                    { "Id": "old", "Name": "Old", "Url": "", "BareClonePath": "", "AddedAt": "2025-01-01T00:00:00Z", "LastUsedAt": "2025-06-01T00:00:00Z" },
                    { "Id": "new", "Name": "New", "Url": "", "BareClonePath": "", "AddedAt": "2025-01-01T00:00:00Z", "LastUsedAt": "2025-12-01T00:00:00Z" }
                ],
                "Worktrees": []
            }
            """;
            File.WriteAllText(Path.Combine(tempDir, "repos.json"), json);

            var rm = new RepoManager();
            rm.Load();

            var repos = rm.Repositories;

            // "new" has the most recent LastUsedAt
            Assert.Equal("new", repos[0].Id);
            Assert.Equal("old", repos[1].Id);
        }
        finally
        {
            RepoManager.SetBaseDirForTesting(TestSetup.TestBaseDir);
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public void LastUsedAt_MissingFromJson_DeserializesAsNull()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"repo-sort-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            RepoManager.SetBaseDirForTesting(tempDir);

            // Legacy repos.json without LastUsedAt field
            var json = """
            {
                "Repositories": [
                    { "Id": "legacy", "Name": "Legacy", "Url": "", "BareClonePath": "", "AddedAt": "2025-01-01T00:00:00Z" }
                ],
                "Worktrees": []
            }
            """;
            File.WriteAllText(Path.Combine(tempDir, "repos.json"), json);

            var rm = new RepoManager();
            rm.Load();

            var repos = rm.Repositories;
            Assert.Single(repos);
            Assert.Null(repos[0].LastUsedAt);
        }
        finally
        {
            RepoManager.SetBaseDirForTesting(TestSetup.TestBaseDir);
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    // ── Helpers ──

    private static readonly System.Reflection.BindingFlags NonPublic =
        System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;

    private static void SetState(RepoManager rm, RepositoryState state)
    {
        var field = rm.GetType().GetField("_state", NonPublic)!;
        field.SetValue(rm, state);
        // Mark as loaded so EnsureLoaded doesn't overwrite
        var loadedField = rm.GetType().GetField("_loaded", NonPublic)!;
        loadedField.SetValue(rm, true);
        var successField = rm.GetType().GetField("_loadedSuccessfully", NonPublic)!;
        successField.SetValue(rm, true);
    }
}
