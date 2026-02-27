using PolyPilot.Services;

namespace PolyPilot.Tests;

public class GitHubReferenceLinkerTests
{
    // --- Fully-qualified references (owner/repo#123) ---

    [Fact]
    public void QualifiedRef_LinkedWithoutRepoContext()
    {
        var html = "<p>See PureWeen/PolyPilot#42 for details</p>";
        var result = GitHubReferenceLinker.LinkifyReferences(html);
        Assert.Contains("href=\"https://github.com/PureWeen/PolyPilot/issues/42\"", result);
        Assert.Contains("target=\"_blank\"", result);
        Assert.Contains(">PureWeen/PolyPilot#42</a>", result);
    }

    [Fact]
    public void MultipleQualifiedRefs_AllLinked()
    {
        var html = "<p>See owner/repo#1 and other/project#999</p>";
        var result = GitHubReferenceLinker.LinkifyReferences(html);
        Assert.Contains("href=\"https://github.com/owner/repo/issues/1\"", result);
        Assert.Contains("href=\"https://github.com/other/project/issues/999\"", result);
    }

    // --- Bare references (#123) ---

    [Fact]
    public void BareRef_NotLinkedWithoutRepoUrl()
    {
        var html = "<p>Fix for #123</p>";
        var result = GitHubReferenceLinker.LinkifyReferences(html);
        Assert.DoesNotContain("href=", result);
        Assert.Contains("#123", result);
    }

    [Fact]
    public void BareRef_LinkedWithRepoUrl()
    {
        var html = "<p>Fix for #123</p>";
        var result = GitHubReferenceLinker.LinkifyReferences(html, "https://github.com/PureWeen/PolyPilot");
        Assert.Contains("href=\"https://github.com/PureWeen/PolyPilot/issues/123\"", result);
        Assert.Contains(">#123</a>", result);
    }

    [Fact]
    public void BareRef_WorksWithDotGitUrl()
    {
        var html = "<p>#456</p>";
        var result = GitHubReferenceLinker.LinkifyReferences(html, "https://github.com/owner/repo.git");
        Assert.Contains("href=\"https://github.com/owner/repo/issues/456\"", result);
    }

    [Fact]
    public void BareRef_WorksWithSshUrl()
    {
        var html = "<p>#789</p>";
        var result = GitHubReferenceLinker.LinkifyReferences(html, "git@github.com:owner/repo.git");
        Assert.Contains("href=\"https://github.com/owner/repo/issues/789\"", result);
    }

    // --- Skip tags (content inside <a>, <code>, <pre> should NOT be linked) ---

    [Fact]
    public void SkipsExistingLinks()
    {
        var html = "<p><a href=\"https://example.com\">#123</a></p>";
        var result = GitHubReferenceLinker.LinkifyReferences(html, "https://github.com/owner/repo");
        // The #123 inside the existing <a> should not be double-linked
        Assert.DoesNotContain("href=\"https://github.com/owner/repo/issues/123\"", result);
    }

    [Fact]
    public void SkipsCodeBlocks()
    {
        var html = "<code>#123</code>";
        var result = GitHubReferenceLinker.LinkifyReferences(html, "https://github.com/owner/repo");
        Assert.DoesNotContain("href=", result);
    }

    [Fact]
    public void SkipsPreBlocks()
    {
        var html = "<pre>owner/repo#42</pre>";
        var result = GitHubReferenceLinker.LinkifyReferences(html);
        Assert.DoesNotContain("href=", result);
    }

    [Fact]
    public void SkipsNestedCodeInParagraph()
    {
        var html = "<p>Use <code>#123</code> in your code, but see #456 for context</p>";
        var result = GitHubReferenceLinker.LinkifyReferences(html, "https://github.com/owner/repo");
        // #123 in code should NOT be linked
        Assert.Contains("<code>#123</code>", result);
        // #456 in text should be linked
        Assert.Contains("href=\"https://github.com/owner/repo/issues/456\"", result);
    }

    // --- HTML entity safety ---

    [Fact]
    public void SkipsHtmlEntities()
    {
        // &#123; is the HTML entity for {
        var html = "<p>Use &#123; for braces</p>";
        var result = GitHubReferenceLinker.LinkifyReferences(html, "https://github.com/owner/repo");
        // Should NOT linkify the 123 from &#123;
        Assert.DoesNotContain("href=", result);
    }

    [Fact]
    public void SkipsUrlFragments()
    {
        var html = "<p>Navigate to /path#123</p>";
        var result = GitHubReferenceLinker.LinkifyReferences(html, "https://github.com/owner/repo");
        // /path#123 looks like a URL fragment, not a GitHub ref
        Assert.DoesNotContain("href=", result);
    }

    // --- Mixed content ---

    [Fact]
    public void MixedQualifiedAndBareRefs()
    {
        var html = "<p>See PureWeen/PolyPilot#42 and also #100</p>";
        var result = GitHubReferenceLinker.LinkifyReferences(html, "https://github.com/other/repo");
        // Qualified ref links to the specified repo
        Assert.Contains("href=\"https://github.com/PureWeen/PolyPilot/issues/42\"", result);
        // Bare ref links to the context repo
        Assert.Contains("href=\"https://github.com/other/repo/issues/100\"", result);
    }

    [Fact]
    public void TextBeforeAndAfterRefsPreserved()
    {
        var html = "<p>This is about #42 and more stuff</p>";
        var result = GitHubReferenceLinker.LinkifyReferences(html, "https://github.com/owner/repo");
        Assert.Contains("This is about ", result);
        Assert.Contains(" and more stuff", result);
    }

    // --- Edge cases ---

    [Fact]
    public void EmptyStringReturnsEmpty()
    {
        Assert.Equal("", GitHubReferenceLinker.LinkifyReferences(""));
    }

    [Fact]
    public void NullStringReturnsNull()
    {
        Assert.Null(GitHubReferenceLinker.LinkifyReferences(null!));
    }

    [Fact]
    public void NoRefsReturnsOriginal()
    {
        var html = "<p>No references here</p>";
        Assert.Equal(html, GitHubReferenceLinker.LinkifyReferences(html));
    }

    [Fact]
    public void PlainTextWithoutHtml()
    {
        var text = "Fix #42 please";
        var result = GitHubReferenceLinker.LinkifyReferences(text, "https://github.com/owner/repo");
        Assert.Contains("href=\"https://github.com/owner/repo/issues/42\"", result);
    }

    // --- ExtractOwnerRepo tests ---

    [Fact]
    public void ExtractOwnerRepo_HttpsUrl()
    {
        Assert.Equal("PureWeen/PolyPilot", GitHubReferenceLinker.ExtractOwnerRepo("https://github.com/PureWeen/PolyPilot"));
    }

    [Fact]
    public void ExtractOwnerRepo_HttpsUrlWithGit()
    {
        Assert.Equal("owner/repo", GitHubReferenceLinker.ExtractOwnerRepo("https://github.com/owner/repo.git"));
    }

    [Fact]
    public void ExtractOwnerRepo_SshUrl()
    {
        Assert.Equal("owner/repo", GitHubReferenceLinker.ExtractOwnerRepo("git@github.com:owner/repo.git"));
    }

    [Fact]
    public void ExtractOwnerRepo_SshUrlNoGit()
    {
        Assert.Equal("owner/repo", GitHubReferenceLinker.ExtractOwnerRepo("git@github.com:owner/repo"));
    }

    [Fact]
    public void ExtractOwnerRepo_NullReturnsNull()
    {
        Assert.Null(GitHubReferenceLinker.ExtractOwnerRepo(null));
    }

    [Fact]
    public void ExtractOwnerRepo_EmptyReturnsNull()
    {
        Assert.Null(GitHubReferenceLinker.ExtractOwnerRepo(""));
    }

    // --- Markdown-rendered HTML patterns ---

    [Fact]
    public void MarkdownRenderedParagraph_RefsLinked()
    {
        // Simulates what Markdig produces for "Fix for #123"
        var html = "<p>Fix for #123</p>\n";
        var result = GitHubReferenceLinker.LinkifyReferences(html, "https://github.com/owner/repo");
        Assert.Contains("href=\"https://github.com/owner/repo/issues/123\"", result);
    }

    [Fact]
    public void MarkdownRenderedList_RefsLinked()
    {
        var html = "<ul>\n<li>Fixed #10</li>\n<li>Closed #20</li>\n</ul>\n";
        var result = GitHubReferenceLinker.LinkifyReferences(html, "https://github.com/owner/repo");
        Assert.Contains("href=\"https://github.com/owner/repo/issues/10\"", result);
        Assert.Contains("href=\"https://github.com/owner/repo/issues/20\"", result);
    }

    [Fact]
    public void DoesNotLinkInsideExistingMarkdownLinks()
    {
        // Markdig converts [text](url) to <a href="url">text</a>
        var html = "<p>See <a href=\"https://github.com/owner/repo/pull/42\">#42</a></p>";
        var result = GitHubReferenceLinker.LinkifyReferences(html, "https://github.com/owner/repo");
        // #42 inside the existing <a> should NOT be re-linked
        var hrefCount = result.Split("href=").Length - 1;
        Assert.Equal(1, hrefCount);
    }

    [Fact]
    public void RefInInlineCode_NotLinked()
    {
        // Markdig converts `#123` to <code>#123</code>
        var html = "<p>Use <code>#123</code> for the issue</p>";
        var result = GitHubReferenceLinker.LinkifyReferences(html, "https://github.com/owner/repo");
        Assert.Contains("<code>#123</code>", result);
        Assert.DoesNotContain("href=\"https://github.com/owner/repo/issues/123\"", result);
    }

    [Fact]
    public void QualifiedRef_OwnerWithDotsAndDashes()
    {
        var html = "<p>my-org.name/my-repo.js#42</p>";
        var result = GitHubReferenceLinker.LinkifyReferences(html);
        Assert.Contains("href=\"https://github.com/my-org.name/my-repo.js/issues/42\"", result);
    }

    [Fact]
    public void DoubleDotInOwnerRepo_NotLinked()
    {
        // Avoid matching file paths like ../../file#123
        var html = "<p>See ../repo#42</p>";
        var result = GitHubReferenceLinker.LinkifyReferences(html);
        Assert.DoesNotContain("href=", result);
    }
}
