using Microsoft.Extensions.DependencyInjection;
using PolyPilot.Services;
using Xunit;

namespace PolyPilot.Tests;

/// <summary>
/// Guard tests that verify test isolation is working correctly.
/// These tests MUST always pass — if any fail, tests are writing to
/// the real ~/.polypilot/ directory and will corrupt user data.
///
/// Background: Tests that call CopilotService methods (CreateGroup,
/// SaveOrganization, etc.) write to disk. Without isolation, they
/// overwrite the user's real organization.json, active-sessions.json,
/// and other files — destroying squad groups, session metadata, and
/// settings. This has caused production data loss (squad groups destroyed)
/// multiple times before the guard was added.
/// </summary>
[Collection("BaseDir")]
public class TestIsolationGuardTests
{
    [Fact]
    public void BaseDir_IsNotRealPolypilotDir()
    {
        // The real ~/.polypilot/ path
        var realDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".polypilot");

        // CopilotService.BaseDir must NOT point to the real directory
        Assert.NotEqual(realDir, CopilotService.BaseDir);
        Assert.DoesNotContain(".polypilot", CopilotService.BaseDir);
    }

    [Fact]
    public void BaseDir_PointsToTempDirectory()
    {
        var tempRoot = Path.GetTempPath();
        Assert.StartsWith(tempRoot, CopilotService.BaseDir);
        Assert.Contains("polypilot-tests-", CopilotService.BaseDir);
    }

    [Fact]
    public void RepoManager_StateFile_IsNotRealPolypilotDir()
    {
        // Access the static StateFile via reflection to verify it doesn't point to real path
        var stateFileField = typeof(RepoManager).GetField("_stateFile",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        // Force resolution by accessing Repositories (which calls EnsureLoaded -> Load -> StateFile)
        var rm = new RepoManager();
        _ = rm.Repositories;

        var stateFile = (string?)stateFileField.GetValue(null);
        var realReposJson = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".polypilot", "repos.json");

        Assert.NotNull(stateFile);
        Assert.NotEqual(realReposJson, stateFile);
        Assert.DoesNotContain(Path.Combine(".polypilot", "repos.json"), stateFile);
    }

    [Fact]
    public void RepoManager_BaseDir_MatchesTestSetupDir()
    {
        // Verify RepoManager resolves to the same test directory as CopilotService
        var baseDirOverride = typeof(RepoManager).GetField("_baseDirOverride",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        var overrideValue = (string?)baseDirOverride.GetValue(null);

        Assert.NotNull(overrideValue);
        Assert.Equal(TestSetup.TestBaseDir, overrideValue);
    }

    [Fact]
    public void TestSetup_ModuleInitializer_HasRun()
    {
        // TestSetup.Initialize() must have run before any test
        Assert.NotEmpty(TestSetup.TestBaseDir);
        Assert.True(Directory.Exists(TestSetup.TestBaseDir),
            "TestSetup.TestBaseDir should exist as a directory");
    }

    [Fact]
    public async Task CreateGroup_DoesNotTouchRealOrgFile()
    {
        var realOrgFile = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".polypilot", "organization.json");

        // Use a unique sentinel group name to detect if the test write leaks to the real file
        var sentinelGroupName = $"IsolationTest-{Guid.NewGuid():N}";

        // Create a service and do something that triggers a write
        var services = new ServiceCollection();
        var svc = new CopilotService(
            new StubChatDatabase(), new StubServerManager(),
            new StubWsBridgeClient(), new RepoManager(),
            services.BuildServiceProvider(), new StubDemoService());
        svc.CreateGroup(sentinelGroupName);

        // Wait for the 2s debounce timer to fire
        await Task.Delay(3000);

        // Verify the write went to the test directory instead
        var testOrgFile = Path.Combine(TestSetup.TestBaseDir, "organization.json");
        Assert.True(File.Exists(testOrgFile),
            $"Organization file should have been written to test dir: {testOrgFile}");
        var testOrgContent = await File.ReadAllTextAsync(testOrgFile);
        // Note: we don't assert sentinelGroupName in testOrgContent because parallel tests
        // also write to the shared TestSetup.TestBaseDir and may have overwritten it.
        // The key isolation invariant is: (a) the file exists in test dir, and (b) the
        // sentinel does NOT appear in the real file.
        _ = testOrgContent; // read to confirm the file is readable

        // Verify the sentinel group did NOT leak into the real file.
        // We check content rather than timestamps because the running app also
        // writes to the real file during normal operation.
        if (File.Exists(realOrgFile))
        {
            var realContent = await File.ReadAllTextAsync(realOrgFile);
            Assert.DoesNotContain(sentinelGroupName, realContent);
        }
    }
}
