using PolyPilot.Models;

namespace PolyPilot.Tests;

public class PlatformHelperTests
{
    [Fact]
    public void IsDesktop_OnTestHost_IsTrue()
    {
        // Tests run on desktop (no IOS/ANDROID defines).
        // On non-mobile platforms (including Linux), IsDesktop is true.
        Assert.True(PlatformHelper.IsDesktop);
    }

    [Fact]
    public void IsMobile_OnTestHost_IsFalse()
    {
        Assert.False(PlatformHelper.IsMobile);
    }

    [Fact]
    public void AvailableModes_OnNonDesktop_IncludesRemote()
    {
        // When IsDesktop is false (test host), Remote is always available;
        // DEBUG builds also include Demo mode.
        if (!PlatformHelper.IsDesktop)
        {
            Assert.Contains(ConnectionMode.Remote, PlatformHelper.AvailableModes);
            Assert.DoesNotContain(ConnectionMode.Embedded, PlatformHelper.AvailableModes);
            Assert.DoesNotContain(ConnectionMode.Persistent, PlatformHelper.AvailableModes);
#if DEBUG
            Assert.Equal(2, PlatformHelper.AvailableModes.Length);
            Assert.Contains(ConnectionMode.Demo, PlatformHelper.AvailableModes);
#else
            Assert.Single(PlatformHelper.AvailableModes);
#endif
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

    [Fact]
    public void ShellEscape_PlainText_WrapsSingleQuotes()
    {
        Assert.Equal("'hello'", PlatformHelper.ShellEscape("hello"));
    }

    [Fact]
    public void ShellEscape_SingleQuote_EscapedCorrectly()
    {
        // Bash single-quote escaping: close quote, double-quote the apostrophe, reopen quote
        Assert.Equal("'it'\"'\"'s a test'", PlatformHelper.ShellEscape("it's a test"));
    }

    [Fact]
    public void ShellEscape_SpecialChars_NotExpanded()
    {
        // Dollar signs, backticks, semicolons, pipes — all neutralized inside single quotes
        Assert.Equal("'$HOME;rm -rf /|`whoami`'", PlatformHelper.ShellEscape("$HOME;rm -rf /|`whoami`"));
    }

    [Fact]
    public void ShellEscape_EmptyString_ReturnsEmptyQuotes()
    {
        Assert.Equal("''", PlatformHelper.ShellEscape(""));
    }

    [Fact]
    public void ShellEscape_MultipleSingleQuotes_AllEscaped()
    {
        Assert.Equal("''\"'\"''\"'\"''", PlatformHelper.ShellEscape("''"));
    }

    // --- GetShellCommand tests ---

    [Fact]
    public void GetShellCommand_ReturnsValidTuple()
    {
        var (fileName, arguments) = PlatformHelper.GetShellCommand("echo hello");
        Assert.False(string.IsNullOrEmpty(fileName));
        Assert.False(string.IsNullOrEmpty(arguments));
    }

    [Fact]
    public void GetShellCommand_OnWindows_UsesCmdExe()
    {
        if (!OperatingSystem.IsWindows()) return;
        var (fileName, arguments) = PlatformHelper.GetShellCommand("echo hello");
        Assert.Equal("cmd.exe", fileName);
        Assert.Equal("/c \"echo hello\"", arguments);
    }

    [Fact]
    public void GetShellCommand_OnWindows_DoesNotUseBash()
    {
        if (!OperatingSystem.IsWindows()) return;
        var (fileName, _) = PlatformHelper.GetShellCommand("code .");
        Assert.DoesNotContain("bash", fileName, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("/bin/", fileName);
    }

    [Fact]
    public void GetShellCommand_OnNonWindows_UsesBash()
    {
        if (OperatingSystem.IsWindows()) return;
        var (fileName, arguments) = PlatformHelper.GetShellCommand("echo hello");
        Assert.Equal("/bin/bash", fileName);
        Assert.StartsWith("-c ", arguments);
    }

    [Fact]
    public void GetShellCommand_OnNonWindows_EscapesBackslashes()
    {
        if (OperatingSystem.IsWindows()) return;
        var (_, arguments) = PlatformHelper.GetShellCommand("echo \\n");
        Assert.Contains("\\\\n", arguments);
    }

    [Fact]
    public void GetShellCommand_OnNonWindows_EscapesQuotes()
    {
        if (OperatingSystem.IsWindows()) return;
        var (_, arguments) = PlatformHelper.GetShellCommand("echo \"hi\"");
        Assert.Contains("\\\"hi\\\"", arguments);
    }

    [Fact]
    public void GetShellCommand_CommandPreserved()
    {
        var (_, arguments) = PlatformHelper.GetShellCommand("code .");
        Assert.Contains("code .", arguments);
    }

    // --- BuildVSCodeRemoteFolderUri tests ---

    [Fact]
    public void BuildVSCodeRemoteFolderUri_RemoteMode_UnixPath()
    {
        var result = PlatformHelper.BuildVSCodeRemoteFolderUri(true, "DEV-SERVER", "/home/user/project");
        Assert.Equal("vscode-remote://tunnel+DEV-SERVER/home/user/project", result);
    }

    [Fact]
    public void BuildVSCodeRemoteFolderUri_RemoteMode_WindowsPath()
    {
        var result = PlatformHelper.BuildVSCodeRemoteFolderUri(true, "DEV-SERVER", @"C:\Users\dev\project");
        Assert.Equal("vscode-remote://tunnel+DEV-SERVER/C:/Users/dev/project", result);
    }

    [Fact]
    public void BuildVSCodeRemoteFolderUri_NotRemoteMode_ReturnsNull()
    {
        var result = PlatformHelper.BuildVSCodeRemoteFolderUri(false, "DEV-SERVER", "/home/user/project");
        Assert.Null(result);
    }

    [Fact]
    public void BuildVSCodeRemoteFolderUri_NullMachineName_ReturnsNull()
    {
        var result = PlatformHelper.BuildVSCodeRemoteFolderUri(true, null, "/home/user/project");
        Assert.Null(result);
    }

    [Fact]
    public void BuildVSCodeRemoteFolderUri_EmptyMachineName_ReturnsNull()
    {
        var result = PlatformHelper.BuildVSCodeRemoteFolderUri(true, "", "/home/user/project");
        Assert.Null(result);
    }

    [Fact]
    public void BuildVSCodeRemoteFolderUri_NullPath_ReturnsNull()
    {
        var result = PlatformHelper.BuildVSCodeRemoteFolderUri(true, "DEV-SERVER", null);
        Assert.Null(result);
    }

    [Fact]
    public void BuildVSCodeRemoteFolderUri_LowercaseMachineName_Preserved()
    {
        var result = PlatformHelper.BuildVSCodeRemoteFolderUri(true, "my-laptop", "/tmp/work");
        Assert.Equal("vscode-remote://tunnel+my-laptop/tmp/work", result);
    }

    [Fact]
    public void BuildVSCodeRemoteFolderUri_UncPath_ReturnsNull()
    {
        var result = PlatformHelper.BuildVSCodeRemoteFolderUri(true, "DEV-SERVER", @"\\fileserver\share\project");
        Assert.Null(result);
    }

    [Fact]
    public void BuildVSCodeRemoteFolderUri_PathWithSpaces_UriEncoded()
    {
        var result = PlatformHelper.BuildVSCodeRemoteFolderUri(true, "DEV-SERVER", "/home/user/my project");
        Assert.Equal("vscode-remote://tunnel+DEV-SERVER/home/user/my%20project", result);
    }

    [Fact]
    public void BuildVSCodeRemoteFolderUri_WindowsPathWithSpaces_UriEncoded()
    {
        var result = PlatformHelper.BuildVSCodeRemoteFolderUri(true, "DEV-SERVER", @"C:\Users\dev user\my project");
        Assert.Equal("vscode-remote://tunnel+DEV-SERVER/C:/Users/dev%20user/my%20project", result);
    }
}
