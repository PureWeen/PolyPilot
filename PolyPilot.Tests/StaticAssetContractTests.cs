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
}
