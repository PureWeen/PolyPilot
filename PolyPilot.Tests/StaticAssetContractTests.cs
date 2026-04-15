using System.Text.RegularExpressions;

namespace PolyPilot.Tests;

public class StaticAssetContractTests
{
    private static string GetRepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null && !File.Exists(Path.Combine(dir, "PolyPilot.slnx")))
            dir = Directory.GetParent(dir)?.FullName;

        return dir ?? throw new DirectoryNotFoundException("Could not find repo root (PolyPilot.slnx not found)");
    }

    private static string IndexHtmlPath =>
        Path.Combine(GetRepoRoot(), "PolyPilot", "wwwroot", "index.html");

    private static string CodeMirrorBundlePath =>
        Path.Combine(GetRepoRoot(), "PolyPilot", "wwwroot", "lib", "codemirror", "codemirror-bundle.js");

    private static string DiffViewPath =>
        Path.Combine(GetRepoRoot(), "PolyPilot", "Components", "DiffView.razor");

    private static string ExpandedSessionViewPath =>
        Path.Combine(GetRepoRoot(), "PolyPilot", "Components", "ExpandedSessionView.razor");

    [Fact]
    public void IndexHtml_LoadsLocalCodeMirrorBundleWithoutFragileIntegrityAttributes()
    {
        var html = File.ReadAllText(IndexHtmlPath);
        var match = Regex.Match(
            html,
            @"<script\s+src=""lib/codemirror/codemirror-bundle\.js""[^>]*>",
            RegexOptions.IgnoreCase);

        Assert.True(match.Success, "Could not find the local CodeMirror bundle script tag in wwwroot/index.html.");
        Assert.DoesNotContain("integrity=", match.Value, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("crossorigin=", match.Value, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CodeMirrorBundle_ExposesRequiredDiffEditorInteropSurface()
    {
        Assert.True(File.Exists(CodeMirrorBundlePath), "The CodeMirror bundle must be present for the diff editor to work.");

        var js = File.ReadAllText(CodeMirrorBundlePath);

        Assert.Contains("window.PolyPilotCodeMirror", js, StringComparison.Ordinal);
        Assert.Contains("createMergeView", js, StringComparison.Ordinal);
        Assert.Contains("dispose", js, StringComparison.Ordinal);
        Assert.Contains("openSearch", js, StringComparison.Ordinal);
    }

    [Fact]
    public void DiffViewMarkup_ContainsCoreReviewEditorControls()
    {
        var markup = File.ReadAllText(DiffViewPath);

        Assert.Contains("role=\"tablist\"", markup, StringComparison.Ordinal);
        Assert.Contains("title=\"Editor view with syntax highlighting\"", markup, StringComparison.Ordinal);
        Assert.Contains("Click any line number to send review feedback to chat.", markup, StringComparison.Ordinal);
        Assert.Contains("Send to chat", markup, StringComparison.Ordinal);
        Assert.Contains("PolyPilotCodeMirror.createMergeView", markup, StringComparison.Ordinal);
    }

    [Fact]
    public void ExpandedSessionView_WiresPrReviewPanelIntoDiffViewCommentFlow()
    {
        var markup = File.ReadAllText(ExpandedSessionViewPath);

        Assert.Contains("<aside class=\"review-panel\"", markup, StringComparison.Ordinal);
        Assert.Contains("<DiffView RawDiff=\"@_prDiffContent\"", markup, StringComparison.Ordinal);
        Assert.Contains("OnCommentRequested=\"HandleDiffCommentAsync\"", markup, StringComparison.Ordinal);
    }
}
