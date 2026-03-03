using PolyPilot.Models;
using PolyPilot.Services;

namespace PolyPilot.Tests;

[Collection("BaseDir")]
public class RepoDiskSizeTests
{
    private static readonly System.Reflection.BindingFlags NonPublic =
        System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;

    private static void SetField(object obj, string name, object value)
    {
        var field = obj.GetType().GetField(name, NonPublic)!;
        field.SetValue(obj, value);
    }

    #region FormatSize Tests

    [Theory]
    [InlineData(0, "0 B")]
    [InlineData(512, "512 B")]
    [InlineData(1023, "1023 B")]
    [InlineData(1024, "1.0 KB")]
    [InlineData(1536, "1.5 KB")]
    [InlineData(1048576, "1.0 MB")]
    [InlineData(1572864, "1.5 MB")]
    [InlineData(1073741824, "1.0 GB")]
    [InlineData(1610612736, "1.5 GB")]
    public void FormatSize_FormatsCorrectly(long bytes, string expected)
    {
        Assert.Equal(expected, RepoManager.FormatSize(bytes));
    }

    #endregion

    #region GetDirectorySizeBytes Tests

    [Fact]
    public void GetDirectorySizeBytes_EmptyDir_ReturnsZero()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"disksize-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            Assert.Equal(0, RepoManager.GetDirectorySizeBytes(dir));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void GetDirectorySizeBytes_WithFiles_SumsCorrectly()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"disksize-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            // Create files with known sizes
            File.WriteAllBytes(Path.Combine(dir, "a.txt"), new byte[100]);
            File.WriteAllBytes(Path.Combine(dir, "b.txt"), new byte[200]);
            var subDir = Path.Combine(dir, "sub");
            Directory.CreateDirectory(subDir);
            File.WriteAllBytes(Path.Combine(subDir, "c.txt"), new byte[300]);

            Assert.Equal(600, RepoManager.GetDirectorySizeBytes(dir));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void GetDirectorySizeBytes_NonExistentDir_ReturnsZero()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"disksize-nonexistent-{Guid.NewGuid():N}");
        Assert.Equal(0, RepoManager.GetDirectorySizeBytes(dir));
    }

    #endregion

    #region GetRepoDiskSize Cache Tests

    [Fact]
    public void GetRepoDiskSize_ReturnsNull_WhenNotComputed()
    {
        var rm = new RepoManager();
        Assert.Null(rm.GetRepoDiskSize("nonexistent-repo"));
    }

    [Fact]
    public async Task RefreshDiskSizesAsync_PopulatesCache()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"disksize-refresh-{Guid.NewGuid():N}");
        var reposDir = Path.Combine(tempDir, "repos");
        var bareDir = Path.Combine(reposDir, "Test-Repo.git");
        Directory.CreateDirectory(bareDir);
        File.WriteAllBytes(Path.Combine(bareDir, "pack"), new byte[1024]);

        try
        {
            var state = new RepositoryState();
            state.Repositories.Add(new RepositoryInfo
            {
                Id = "Test-Repo",
                Name = "Repo",
                Url = "https://github.com/Test/Repo",
                BareClonePath = bareDir
            });

            var rm = new RepoManager();
            SetField(rm, "_loaded", true);
            SetField(rm, "_loadedSuccessfully", true);
            SetField(rm, "_state", state);

            RepoManager.SetBaseDirForTesting(tempDir);
            try
            {
                // Before refresh, size should be null
                Assert.Null(rm.GetRepoDiskSize("Test-Repo"));

                await rm.RefreshDiskSizesAsync();

                // After refresh, size should be populated
                var size = rm.GetRepoDiskSize("Test-Repo");
                Assert.NotNull(size);
                Assert.Equal(1024, size!.Value);
            }
            finally
            {
                RepoManager.SetBaseDirForTesting(TestSetup.TestBaseDir);
            }
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task RefreshDiskSizesAsync_IncludesWorktrees()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"disksize-wt-{Guid.NewGuid():N}");
        var reposDir = Path.Combine(tempDir, "repos");
        var bareDir = Path.Combine(reposDir, "Test-Repo.git");
        var wtDir = Path.Combine(tempDir, "worktrees", "Test-Repo-abc123");
        Directory.CreateDirectory(bareDir);
        Directory.CreateDirectory(wtDir);
        File.WriteAllBytes(Path.Combine(bareDir, "pack"), new byte[500]);
        File.WriteAllBytes(Path.Combine(wtDir, "file.cs"), new byte[300]);

        try
        {
            var state = new RepositoryState();
            state.Repositories.Add(new RepositoryInfo
            {
                Id = "Test-Repo",
                Name = "Repo",
                Url = "https://github.com/Test/Repo",
                BareClonePath = bareDir
            });
            state.Worktrees.Add(new WorktreeInfo
            {
                Id = "abc123",
                RepoId = "Test-Repo",
                Branch = "main",
                Path = wtDir
            });

            var rm = new RepoManager();
            SetField(rm, "_loaded", true);
            SetField(rm, "_loadedSuccessfully", true);
            SetField(rm, "_state", state);

            RepoManager.SetBaseDirForTesting(tempDir);
            try
            {
                await rm.RefreshDiskSizesAsync();

                var size = rm.GetRepoDiskSize("Test-Repo");
                Assert.NotNull(size);
                Assert.Equal(800, size!.Value); // 500 bare + 300 worktree
            }
            finally
            {
                RepoManager.SetBaseDirForTesting(TestSetup.TestBaseDir);
            }
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task RefreshDiskSizesAsync_HandlesNonExistentPaths()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"disksize-missing-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            var state = new RepositoryState();
            state.Repositories.Add(new RepositoryInfo
            {
                Id = "Ghost-Repo",
                Name = "Ghost",
                Url = "https://github.com/Ghost/Repo",
                BareClonePath = Path.Combine(tempDir, "does-not-exist")
            });

            var rm = new RepoManager();
            SetField(rm, "_loaded", true);
            SetField(rm, "_loadedSuccessfully", true);
            SetField(rm, "_state", state);

            RepoManager.SetBaseDirForTesting(tempDir);
            try
            {
                await rm.RefreshDiskSizesAsync();

                var size = rm.GetRepoDiskSize("Ghost-Repo");
                Assert.NotNull(size);
                Assert.Equal(0, size!.Value);
            }
            finally
            {
                RepoManager.SetBaseDirForTesting(TestSetup.TestBaseDir);
            }
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    #endregion
}
