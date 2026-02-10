using AutoPilot.App.Models;

namespace AutoPilot.App.Tests;

public class PlatformHelperTests
{
    [Fact]
    public void IsDesktop_OnTestHost_IsTrue()
    {
        // Tests run on desktop (no IOS/ANDROID defines)
        // The #if MACCATALYST || WINDOWS path won't be active either,
        // so IsDesktop will be false on a plain net10.0 test host.
        // This test documents the actual behavior.
        Assert.False(PlatformHelper.IsDesktop);
    }

    [Fact]
    public void IsMobile_OnTestHost_IsFalse()
    {
        Assert.False(PlatformHelper.IsMobile);
    }

    [Fact]
    public void AvailableModes_OnNonDesktop_IsRemoteOnly()
    {
        // When IsDesktop is false (test host), only Remote mode is available
        if (!PlatformHelper.IsDesktop)
        {
            Assert.Single(PlatformHelper.AvailableModes);
            Assert.Equal(ConnectionMode.Remote, PlatformHelper.AvailableModes[0]);
        }
    }

    [Fact]
    public void DefaultMode_OnNonDesktop_IsRemote()
    {
        if (!PlatformHelper.IsDesktop)
        {
            Assert.Equal(ConnectionMode.Remote, PlatformHelper.DefaultMode);
        }
    }

    [Fact]
    public void AvailableModes_OnDesktop_HasAllThreeModes()
    {
        if (PlatformHelper.IsDesktop)
        {
            Assert.Equal(3, PlatformHelper.AvailableModes.Length);
            Assert.Contains(ConnectionMode.Embedded, PlatformHelper.AvailableModes);
            Assert.Contains(ConnectionMode.Persistent, PlatformHelper.AvailableModes);
            Assert.Contains(ConnectionMode.Remote, PlatformHelper.AvailableModes);
        }
    }
}
