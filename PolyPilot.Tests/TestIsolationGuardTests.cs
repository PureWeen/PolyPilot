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

        // Snapshot the real file's last-write time (if it exists)
        var beforeTime = File.Exists(realOrgFile)
            ? File.GetLastWriteTimeUtc(realOrgFile)
            : (DateTime?)null;

        // Create a service and do something that triggers a write
        var services = new ServiceCollection();
        var svc = new CopilotService(
            new StubChatDatabase(), new StubServerManager(),
            new StubWsBridgeClient(), new RepoManager(),
            services.BuildServiceProvider(), new StubDemoService());
        svc.CreateGroup("IsolationTest");

        // Wait for the 2s debounce timer to fire
        await Task.Delay(3000);

        // Verify the real file was NOT modified
        if (beforeTime.HasValue)
        {
            var afterTime = File.GetLastWriteTimeUtc(realOrgFile);
            Assert.Equal(beforeTime.Value, afterTime);
        }

        // Verify the write went to the test directory instead
        var testOrgFile = Path.Combine(TestSetup.TestBaseDir, "organization.json");
        Assert.True(File.Exists(testOrgFile),
            $"Organization file should have been written to test dir: {testOrgFile}");
    }
}
