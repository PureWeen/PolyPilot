using System.Text.RegularExpressions;
using Xunit;

namespace PolyPilot.Tests;

/// <summary>
/// Verifies that the "Fix a Bug / Add Feature" panel in SessionSidebar
/// auto-closes after successfully launching Copilot, showing the success
/// message only briefly before closing the dialog.
/// </summary>
public class FixItPanelAutoCloseTests
{
    private static string GetRepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null && !File.Exists(Path.Combine(dir, "PolyPilot.slnx")))
            dir = Directory.GetParent(dir)?.FullName;
        return dir ?? throw new DirectoryNotFoundException("Could not find repo root");
    }

    private string ReadSessionSidebar()
    {
        var file = Path.Combine(GetRepoRoot(), "PolyPilot", "Components", "Layout", "SessionSidebar.razor");
        return File.ReadAllText(file);
    }

    [Fact]
    public void AutoClosePanelDelayMs_IsBetween5And10Seconds()
    {
        // The auto-close delay constant should be between 5000-10000ms (5-10 seconds)
        var content = ReadSessionSidebar();
        var match = Regex.Match(content, @"AutoClosePanelDelayMs\s*=\s*(\d+)");
        Assert.True(match.Success, "AutoClosePanelDelayMs constant not found in SessionSidebar.razor");
        var delayMs = int.Parse(match.Groups[1].Value);
        Assert.InRange(delayMs, 5000, 10000);
    }

    [Fact]
    public void LaunchFixIt_SuccessPath_AutoClosesPanel()
    {
        // After setting the success status, there should be a Task.Delay + CloseFooterPanel
        var content = ReadSessionSidebar();
        Assert.Contains("Copilot launched in new terminal", content);
        Assert.Contains("CloseFooterPanel", content);
        Assert.Contains("AutoClosePanelDelayMs", content);
    }

    [Fact]
    public void LaunchFixIt_SuccessPath_HasDelayBeforeClose()
    {
        // Verify the auto-close uses Task.Delay (not immediate close)
        var content = ReadSessionSidebar();
        Assert.Contains("Task.Delay(AutoClosePanelDelayMs)", content);
    }

    [Fact]
    public void LaunchFixIt_SuccessPath_GuardsAgainstStaleClose()
    {
        // The auto-close should only fire if the success message is still showing
        // (guards against closing if user navigated away or an error replaced the status)
        var content = ReadSessionSidebar();
        Assert.Matches(@"footerStatus\s*==\s*""✓ Copilot launched in new terminal""", content);
    }

    [Fact]
    public void LaunchFixIt_SuccessPath_UsesInvokeAsync()
    {
        // UI state changes must be marshaled to the Blazor render thread via InvokeAsync
        var content = ReadSessionSidebar();
        // The deferred close should use InvokeAsync to safely update UI state
        Assert.Contains("InvokeAsync", content);
    }
}
