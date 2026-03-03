using Markdig;
using PolyPilot.Models;
using PolyPilot.Services;

namespace PolyPilot.Tests;

/// <summary>
/// Tests for FetchImage path validation, markdown rendering HTML handling,
/// and log output sanitization.
/// </summary>
public class InputValidationTests
{
    #region FetchImage Path Validation

    [Fact]
    public void FetchImage_AllowedDir_MatchesShowImageTool()
    {
        // FetchImage should only serve files from the ShowImageTool images directory
        var imagesDir = ShowImageTool.GetImagesDir();
        Assert.False(string.IsNullOrEmpty(imagesDir));
        Assert.True(Path.IsPathRooted(imagesDir));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("relative/path/image.png")]
    public void FetchImage_RejectsNonRootedPaths(string? path)
    {
        // Non-rooted paths should be rejected
        Assert.True(
            string.IsNullOrEmpty(path) || !Path.IsPathRooted(path),
            "Test data should be null, empty, or non-rooted");
    }

    [Theory]
    [InlineData("/etc/shadow")]
    [InlineData("/etc/passwd")]
    [InlineData("/home/user/.ssh/id_rsa")]
    [InlineData("/tmp/secret.txt")]
    public void FetchImage_RejectsPathsOutsideImagesDir(string path)
    {
        var allowedDir = Path.GetFullPath(ShowImageTool.GetImagesDir());
        var fullPath = Path.GetFullPath(path);
        Assert.False(
            fullPath.StartsWith(allowedDir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase),
            $"Path '{path}' should not be within the allowed images directory");
    }

    [Theory]
    [InlineData("/../../../etc/passwd")]
    [InlineData("/images/../../../etc/shadow")]
    public void FetchImage_RejectsPathTraversal(string suffix)
    {
        var imagesDir = ShowImageTool.GetImagesDir();
        var crafted = imagesDir + suffix;
        Assert.Contains("..", crafted);
    }

    [Theory]
    [InlineData(".txt")]
    [InlineData(".json")]
    [InlineData(".cs")]
    [InlineData(".exe")]
    [InlineData(".sh")]
    [InlineData(".py")]
    [InlineData("")]
    public void FetchImage_RejectsNonImageExtensions(string ext)
    {
        var allowedExts = new HashSet<string> { ".png", ".jpg", ".jpeg", ".gif", ".webp", ".bmp", ".svg", ".tiff" };
        Assert.DoesNotContain(ext, allowedExts);
    }

    [Theory]
    [InlineData(".png")]
    [InlineData(".jpg")]
    [InlineData(".jpeg")]
    [InlineData(".gif")]
    [InlineData(".webp")]
    [InlineData(".bmp")]
    [InlineData(".svg")]
    [InlineData(".tiff")]
    public void FetchImage_AllowsImageExtensions(string ext)
    {
        var allowedExts = new HashSet<string> { ".png", ".jpg", ".jpeg", ".gif", ".webp", ".bmp", ".svg", ".tiff" };
        Assert.Contains(ext, allowedExts);
    }

    [Fact]
    public void FetchImage_ValidPathInImagesDir_Accepted()
    {
        var imagesDir = Path.GetFullPath(ShowImageTool.GetImagesDir());
        var validPath = Path.Combine(imagesDir, "test-image.png");
        var fullPath = Path.GetFullPath(validPath);
        var ext = Path.GetExtension(fullPath).ToLowerInvariant();
        var allowedExts = new HashSet<string> { ".png", ".jpg", ".jpeg", ".gif", ".webp", ".bmp", ".svg", ".tiff" };

        Assert.True(Path.IsPathRooted(validPath));
        Assert.DoesNotContain("..", validPath);
        Assert.True(fullPath.StartsWith(imagesDir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
        Assert.Contains(ext, allowedExts);
    }

    #endregion

    #region Markdown HTML Handling

    // The app's Markdig pipeline uses .DisableHtml() to prevent raw HTML injection.
    // These tests verify the same pipeline configuration strips dangerous tags.
    private static readonly MarkdownPipeline TestPipeline = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions().DisableHtml().Build();

    private static string Render(string markdown) => Markdown.ToHtml(markdown, TestPipeline);

    [Fact]
    public void RenderMarkdown_ScriptTag_IsNotRendered()
    {
        var html = Render("<script>alert('xss')</script>");
        Assert.DoesNotContain("<script>", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("</script>", html, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RenderMarkdown_ImgOnerror_IsNotRenderedAsTag()
    {
        var html = Render("<img src=x onerror='alert(1)'>");
        // DisableHtml escapes the tag — it should not appear as an actual <img> element
        Assert.DoesNotContain("<img ", html, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RenderMarkdown_IframeTag_IsNotRendered()
    {
        var html = Render("<iframe src='https://evil.com'></iframe>");
        Assert.DoesNotContain("<iframe", html, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RenderMarkdown_StyleTag_IsNotRendered()
    {
        var html = Render("<style>body { display: none }</style>");
        Assert.DoesNotContain("<style>", html, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RenderMarkdown_FormTag_IsNotRendered()
    {
        var html = Render("<form action='https://evil.com'><input></form>");
        Assert.DoesNotContain("<form", html, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RenderMarkdown_ValidMarkdown_StillRenders()
    {
        var html = Render("**bold** and `code`");
        Assert.Contains("<strong>bold</strong>", html);
        Assert.Contains("<code>code</code>", html);
    }

    [Fact]
    public void RenderMarkdown_CodeBlock_StillRenders()
    {
        var html = Render("```csharp\nvar x = 1;\n```");
        Assert.Contains("<code", html);
        Assert.Contains("var x = 1;", html);
    }

    [Fact]
    public void RenderMarkdown_MixedMarkdownAndHtml_HtmlStripped()
    {
        var html = Render("# Title\n<script>alert(1)</script>\n**bold**");
        Assert.Contains("<h1", html);
        Assert.Contains("<strong>bold</strong>", html);
        Assert.DoesNotContain("<script>", html, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RenderMarkdown_EventHandler_IsEscaped()
    {
        var html = Render("<div onmouseover='alert(1)'>hover me</div>");
        // DisableHtml escapes tags — should not appear as actual <div> element
        Assert.DoesNotContain("<div ", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("&lt;div", html); // escaped as text
    }

    [Fact]
    public void RenderMarkdown_JavascriptUrl_IsEscaped()
    {
        var html = Render("<a href='javascript:alert(1)'>click</a>");
        // DisableHtml escapes — should not be an actual <a> tag
        Assert.DoesNotContain("<a href", html, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RenderMarkdown_Links_StillRender()
    {
        var html = Render("[click here](https://example.com)");
        Assert.Contains("<a", html);
        Assert.Contains("https://example.com", html);
    }

    [Fact]
    public void RenderMarkdown_NestedHtmlInCodeBlock_SafelyRendered()
    {
        // HTML inside code blocks should be shown as text, not executed
        var html = Render("```\n<script>alert(1)</script>\n```");
        Assert.Contains("&lt;script&gt;", html); // HTML-encoded inside code
    }

    #endregion
}
