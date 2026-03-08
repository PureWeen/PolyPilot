using PolyPilot.Models;
using PolyPilot.Services;

namespace PolyPilot.Tests;

/// <summary>
/// Tests for FetchImage path validation, markdown rendering HTML handling,
/// and log output sanitization.
/// </summary>
public class InputValidationTests
{
    #region FetchImage Path Validation (calls production ValidateImagePath)

    [Fact]
    public void ValidateImagePath_NullPath_ReturnsError()
    {
        Assert.Equal("Invalid path", WsBridgeServer.ValidateImagePath(null));
    }

    [Fact]
    public void ValidateImagePath_EmptyPath_ReturnsError()
    {
        Assert.Equal("Invalid path", WsBridgeServer.ValidateImagePath(""));
    }

    [Fact]
    public void ValidateImagePath_RelativePath_ReturnsError()
    {
        Assert.Equal("Invalid path", WsBridgeServer.ValidateImagePath("relative/path/image.png"));
    }

    [Theory]
    [InlineData("/etc/shadow")]
    [InlineData("/etc/passwd")]
    [InlineData("/home/user/.ssh/id_rsa")]
    [InlineData("/tmp/secret.txt")]
    public void ValidateImagePath_OutsideImagesDir_ReturnsNotAllowed(string path)
    {
        Assert.Equal("Path not allowed", WsBridgeServer.ValidateImagePath(path));
    }

    [Theory]
    [InlineData("/../../../etc/passwd")]
    [InlineData("/../../etc/shadow")]
    [InlineData("/../secret.txt")]
    public void ValidateImagePath_TraversalAttempt_ReturnsNotAllowed(string suffix)
    {
        var crafted = ShowImageTool.GetImagesDir() + suffix;
        // GetFullPath canonicalizes away the ".." — result lands outside images dir
        Assert.Equal("Path not allowed", WsBridgeServer.ValidateImagePath(crafted));
    }

    [Theory]
    [InlineData(".txt")]
    [InlineData(".json")]
    [InlineData(".cs")]
    [InlineData(".exe")]
    [InlineData(".sh")]
    [InlineData(".py")]
    public void ValidateImagePath_NonImageExtension_ReturnsUnsupported(string ext)
    {
        var path = Path.Combine(ShowImageTool.GetImagesDir(), "file" + ext);
        Assert.Equal("Unsupported file type", WsBridgeServer.ValidateImagePath(path));
    }

    [Fact]
    public void ValidateImagePath_NoExtension_ReturnsUnsupported()
    {
        var path = Path.Combine(ShowImageTool.GetImagesDir(), "noext");
        Assert.Equal("Unsupported file type", WsBridgeServer.ValidateImagePath(path));
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
    public void ValidateImagePath_ValidImageInImagesDir_ReturnsNull(string ext)
    {
        var path = Path.Combine(ShowImageTool.GetImagesDir(), "test" + ext);
        Assert.Null(WsBridgeServer.ValidateImagePath(path));
    }

    [Fact]
    public void ValidateImagePath_ImagesDirItself_ReturnsNotAllowed()
    {
        // Requesting the directory itself should not be allowed
        Assert.Equal("Path not allowed", WsBridgeServer.ValidateImagePath(ShowImageTool.GetImagesDir()));
    }

    [Fact]
    public void ValidateImagePath_SubdirectoryImage_IsAllowed()
    {
        var path = Path.Combine(ShowImageTool.GetImagesDir(), "subdir", "test.png");
        Assert.Null(WsBridgeServer.ValidateImagePath(path));
    }

    [Fact]
    public void ValidateImagePath_OutOverload_ReturnsResolvedPath()
    {
        var path = Path.Combine(ShowImageTool.GetImagesDir(), "test.png");
        var error = WsBridgeServer.ValidateImagePath(path, out var resolvedPath);
        Assert.Null(error);
        Assert.Equal(Path.GetFullPath(path), resolvedPath);
    }

    [Fact]
    public void ValidateImagePath_OutOverload_ErrorReturnsEmpty()
    {
        var error = WsBridgeServer.ValidateImagePath("/etc/passwd", out var resolvedPath);
        Assert.NotNull(error);
        Assert.Equal(string.Empty, resolvedPath);
    }

    [Fact]
    public void ValidateImagePath_SymlinkOutsideImagesDir_ReturnsNotAllowed()
    {
        var imagesDir = ShowImageTool.GetImagesDir();
        Directory.CreateDirectory(imagesDir);
        var linkPath = Path.Combine(imagesDir, "evil-link.png");
        try
        {
            File.CreateSymbolicLink(linkPath, "/etc/passwd");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Symlink creation requires elevated privileges on Windows — skip
            return;
        }
        try
        {
            Assert.Equal("Path not allowed", WsBridgeServer.ValidateImagePath(linkPath));
        }
        finally
        {
            if (File.Exists(linkPath)) File.Delete(linkPath);
        }
    }

    [Fact]
    public void ValidateImagePath_DirectorySymlinkBypass_ReturnsNotAllowed()
    {
        var imagesDir = ShowImageTool.GetImagesDir();
        Directory.CreateDirectory(imagesDir);
        var symlinkDir = Path.Combine(imagesDir, "evil-subdir");
        try
        {
            // Create a directory symlink inside images/ pointing to /etc/
            Directory.CreateSymbolicLink(symlinkDir, "/etc");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Symlink creation requires elevated privileges on Windows — skip
            return;
        }
        try
        {
            var attackPath = Path.Combine(symlinkDir, "passwd");
            Assert.Equal("Path not allowed", WsBridgeServer.ValidateImagePath(attackPath));
        }
        finally
        {
            if (Directory.Exists(symlinkDir)) Directory.Delete(symlinkDir);
        }
    }

    #endregion

    #region Markdown HTML Handling

    // Tests use the production MarkdownRenderer.Pipeline (shared with ChatMessageList)
    private static string Render(string markdown) => MarkdownRenderer.ToHtml(markdown);

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

    #region Markdown URL Sanitization

    [Theory]
    [InlineData("[click](javascript:alert(1))")]
    [InlineData("[click](javascript:void(document.location='http://evil.com'))")]
    [InlineData("[click](JAVASCRIPT:alert(1))")]
    [InlineData("[click](Javascript:alert(1))")]
    public void RenderMarkdown_JavascriptLinkScheme_IsBlocked(string input)
    {
        var html = Render(input);
        Assert.DoesNotContain("javascript:", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("x-blocked:", html);
    }

    [Theory]
    [InlineData("![img](javascript:alert(1))")]
    public void RenderMarkdown_JavascriptImageScheme_IsBlocked(string input)
    {
        var html = Render(input);
        Assert.DoesNotContain("javascript:", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("x-blocked:", html);
    }

    [Theory]
    [InlineData("[click](vbscript:MsgBox(1))")]
    [InlineData("[click](VBSCRIPT:MsgBox(1))")]
    public void RenderMarkdown_VbscriptLinkScheme_IsBlocked(string input)
    {
        var html = Render(input);
        Assert.DoesNotContain("vbscript:", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("x-blocked:", html);
    }

    [Theory]
    [InlineData("[click](data:text/html,<script>alert(1)</script>)")]
    public void RenderMarkdown_DataLinkScheme_IsBlocked(string input)
    {
        var html = Render(input);
        Assert.DoesNotContain("data:text/html", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("x-blocked:", html);
    }

    [Fact]
    public void RenderMarkdown_SafeLinks_StillWork()
    {
        Assert.Contains("https://example.com", Render("[click](https://example.com)"));
        Assert.Contains("http://example.com", Render("[click](http://example.com)"));
        Assert.Contains("mailto:user@example.com", Render("[click](mailto:user@example.com)"));
    }

    [Fact]
    public void SanitizeUrls_DirectCall_WorksCorrectly()
    {
        Assert.Equal(
            @"<a href=""x-blocked:alert(1)"">",
            MarkdownRenderer.SanitizeUrls(@"<a href=""javascript:alert(1)"">"));
        Assert.Equal(
            @"<img src=""x-blocked:alert(1)"" />",
            MarkdownRenderer.SanitizeUrls(@"<img src=""javascript:alert(1)"" />"));
        // Safe URLs are untouched
        Assert.Equal(
            @"<a href=""https://safe.com"">",
            MarkdownRenderer.SanitizeUrls(@"<a href=""https://safe.com"">"));
    }

    #endregion

    #region Additional Edge Cases

    [Theory]
    [InlineData(".PNG")]
    [InlineData(".Jpg")]
    [InlineData(".JPEG")]
    public void ValidateImagePath_CaseInsensitiveExtension_IsAllowed(string ext)
    {
        var path = Path.Combine(ShowImageTool.GetImagesDir(), "test" + ext);
        Assert.Null(WsBridgeServer.ValidateImagePath(path));
    }

    [Theory]
    [InlineData("image.png.exe")]
    [InlineData("image.jpg.txt")]
    [InlineData("image.exe.png.sh")]
    public void ValidateImagePath_DoubleExtension_ChecksLastExtension(string filename)
    {
        var path = Path.Combine(ShowImageTool.GetImagesDir(), filename);
        var result = WsBridgeServer.ValidateImagePath(path);
        var ext = Path.GetExtension(filename).ToLowerInvariant();
        if (ext == ".png" || ext == ".jpg")
            Assert.Null(result);
        else
            Assert.Equal("Unsupported file type", result);
    }

    [Fact]
    public void ValidateImagePath_SpacesInFilename_IsAllowed()
    {
        var path = Path.Combine(ShowImageTool.GetImagesDir(), "my image file.png");
        Assert.Null(WsBridgeServer.ValidateImagePath(path));
    }

    #endregion
}
