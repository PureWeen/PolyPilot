using Xunit;

namespace PolyPilot.Tests;

/// <summary>
/// Validates that the pre-Blazor loading screen in index.html uses a styled
/// loading indicator instead of unstyled plain text, and that app.css contains
/// matching styles so the loading screen blends with the app theme.
/// </summary>
public class LoadingScreenTests
{
    private static string? _repoRoot;
    private static string GetRepoRoot()
    {
        if (_repoRoot != null) return _repoRoot;
        var dir = AppContext.BaseDirectory;
        while (dir != null && !File.Exists(Path.Combine(dir, "PolyPilot.slnx")))
            dir = Directory.GetParent(dir)?.FullName;
        return _repoRoot = dir ?? throw new InvalidOperationException("Could not find repo root");
    }

    [Fact]
    public void IndexHtml_AppDiv_DoesNotContainPlainLoadingText()
    {
        var html = File.ReadAllText(Path.Combine(GetRepoRoot(), "PolyPilot", "wwwroot", "index.html"));

        // The #app div must NOT contain bare unstyled text like "Loading..."
        Assert.DoesNotContain(">Loading...</", html);
        Assert.DoesNotContain(">Launching</", html);
        Assert.DoesNotContain(">Launching…</", html);
    }

    [Fact]
    public void IndexHtml_AppDiv_ContainsStyledLoadingIndicator()
    {
        var html = File.ReadAllText(Path.Combine(GetRepoRoot(), "PolyPilot", "wwwroot", "index.html"));

        // Must have a styled loading container inside the #app div
        Assert.Contains("app-loading", html);
        Assert.Contains("app-loading-logo", html);
    }

    [Fact]
    public void AppCss_ContainsLoadingScreenStyles()
    {
        var css = File.ReadAllText(Path.Combine(GetRepoRoot(), "PolyPilot", "wwwroot", "app.css"));

        // CSS must style the loading screen with centering and dark background
        Assert.Contains("#app > .app-loading", css);
        Assert.Contains("height: 100vh", css);
        Assert.Contains("app-loading-logo", css);
    }
}
