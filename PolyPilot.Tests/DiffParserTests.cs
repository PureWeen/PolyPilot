using PolyPilot.Models;

namespace PolyPilot.Tests;

public class DiffParserTests
{
    [Fact]
    public void Parse_EmptyInput_ReturnsEmptyList()
    {
        Assert.Empty(DiffParser.Parse(""));
        Assert.Empty(DiffParser.Parse(null!));
        Assert.Empty(DiffParser.Parse("   "));
    }

    [Fact]
    public void LooksLikeUnifiedDiff_ValidUnifiedDiff_ReturnsTrue()
    {
        var diff = """
            diff --git a/src/file.cs b/src/file.cs
            index abc..def 100644
            --- a/src/file.cs
            +++ b/src/file.cs
            @@ -1,2 +1,2 @@
            -old
            +new
            """;

        Assert.True(DiffParser.LooksLikeUnifiedDiff(diff));
    }

    [Fact]
    public void LooksLikeUnifiedDiff_PlainText_ReturnsFalse()
    {
        var output = """
            Updated /tmp/file.txt successfully
            2 lines changed
            """;

        Assert.False(DiffParser.LooksLikeUnifiedDiff(output));
    }

    [Fact]
    public void Parse_StandardDiff_ExtractsFileName()
    {
        var diff = """
            diff --git a/src/file.cs b/src/file.cs
            index abc..def 100644
            --- a/src/file.cs
            +++ b/src/file.cs
            @@ -1,3 +1,4 @@
             line1
            +added
             line2
             line3
            """;
        var files = DiffParser.Parse(diff);
        Assert.Single(files);
        Assert.Equal("src/file.cs", files[0].FileName);
    }

    [Fact]
    public void Parse_NewFile_SetsIsNew()
    {
        var diff = """
            diff --git a/new.txt b/new.txt
            new file mode 100644
            --- /dev/null
            +++ b/new.txt
            @@ -0,0 +1,2 @@
            +hello
            +world
            """;
        var files = DiffParser.Parse(diff);
        Assert.Single(files);
        Assert.True(files[0].IsNew);
    }

    [Fact]
    public void Parse_DeletedFile_SetsIsDeleted()
    {
        var diff = """
            diff --git a/old.txt b/old.txt
            deleted file mode 100644
            --- a/old.txt
            +++ /dev/null
            @@ -1,2 +0,0 @@
            -hello
            -world
            """;
        var files = DiffParser.Parse(diff);
        Assert.Single(files);
        Assert.True(files[0].IsDeleted);
    }

    [Fact]
    public void Parse_RenamedFile_SetsOldAndNewNames()
    {
        var diff = """
            diff --git a/old.cs b/new.cs
            rename from old.cs
            rename to new.cs
            """;
        var files = DiffParser.Parse(diff);
        Assert.Single(files);
        Assert.True(files[0].IsRenamed);
        Assert.Equal("old.cs", files[0].OldFileName);
        Assert.Equal("new.cs", files[0].FileName);
    }

    [Fact]
    public void Parse_HunkHeader_ExtractsLineNumbers()
    {
        var diff = """
            diff --git a/f.cs b/f.cs
            --- a/f.cs
            +++ b/f.cs
            @@ -10,5 +12,7 @@ class Foo
             context
            """;
        var files = DiffParser.Parse(diff);
        var hunk = files[0].Hunks[0];
        Assert.Equal(10, hunk.OldStart);
        Assert.Equal(12, hunk.NewStart);
        Assert.Equal("class Foo", hunk.Header);
    }

    [Fact]
    public void Parse_AddedAndRemovedLines_TracksLineNumbers()
    {
        var diff = """
            diff --git a/f.cs b/f.cs
            --- a/f.cs
            +++ b/f.cs
            @@ -1,3 +1,3 @@
             same
            -old
            +new
             same2
            """;
        var files = DiffParser.Parse(diff);
        var lines = files[0].Hunks[0].Lines;

        Assert.Equal(4, lines.Count);
        Assert.Equal(DiffLineType.Context, lines[0].Type);
        Assert.Equal(DiffLineType.Removed, lines[1].Type);
        Assert.Equal(2, lines[1].OldLineNo);  // after context line at 1
        Assert.Equal(DiffLineType.Added, lines[2].Type);
        Assert.Equal(2, lines[2].NewLineNo);  // after context line at 1
        Assert.Equal(DiffLineType.Context, lines[3].Type);
    }

    [Fact]
    public void Parse_MultipleFiles_ParsesAll()
    {
        var diff = """
            diff --git a/a.cs b/a.cs
            --- a/a.cs
            +++ b/a.cs
            @@ -1 +1 @@
            -old
            +new
            diff --git a/b.cs b/b.cs
            --- a/b.cs
            +++ b/b.cs
            @@ -1 +1 @@
            -x
            +y
            """;
        var files = DiffParser.Parse(diff);
        Assert.Equal(2, files.Count);
        Assert.Equal("a.cs", files[0].FileName);
        Assert.Equal("b.cs", files[1].FileName);
    }

    [Fact]
    public void Parse_StandardUnifiedDiff_MultipleFiles_ParsesAll()
    {
        var diff = """
            --- a/a.cs
            +++ b/a.cs
            @@ -1 +1 @@
            -old
            +new
            --- a/b.cs
            +++ b/b.cs
            @@ -1 +1 @@
            -x
            +y
            """;

        var files = DiffParser.Parse(diff);

        Assert.Equal(2, files.Count);
        Assert.Equal("a.cs", files[0].FileName);
        Assert.Equal("b.cs", files[1].FileName);
        Assert.Single(files[0].Hunks);
        Assert.Single(files[1].Hunks);
    }

    [Fact]
    public void Parse_HunkLinesThatLookLikeFileHeaders_ArePreserved()
    {
        var diff = """
            diff --git a/script.sh b/script.sh
            --- a/script.sh
            +++ b/script.sh
            @@ -1,3 +1,3 @@
            ---- old flag
            ++++ new flag
             keep
            """;

        var files = DiffParser.Parse(diff);
        var lines = files[0].Hunks[0].Lines;

        Assert.Equal(3, lines.Count);
        Assert.Equal(DiffLineType.Removed, lines[0].Type);
        Assert.Equal("--- old flag", lines[0].Content);
        Assert.Equal(DiffLineType.Added, lines[1].Type);
        Assert.Equal("+++ new flag", lines[1].Content);
    }

    [Fact]
    public void Parse_SpecialHtmlCharacters_PreservedInContent()
    {
        // Verify the parser preserves raw HTML characters as-is.
        // DiffView relies on Blazor's @() auto-encoding, so the parser
        // must never pre-encode content.
        var diff = """
            diff --git a/template.html b/template.html
            --- a/template.html
            +++ b/template.html
            @@ -1,3 +1,3 @@
             <div class="container">
            -    <span title="old">old &amp; value</span>
            +    <span title="new">new &amp; value</span>
             </div>
            """;
        var files = DiffParser.Parse(diff);
        var lines = files[0].Hunks[0].Lines;

        // Parser must pass through <, >, ", & verbatim — DiffView's @() handles encoding
        Assert.Equal("<div class=\"container\">", lines[0].Content);
        Assert.Equal("    <span title=\"old\">old &amp; value</span>", lines[1].Content);
        Assert.Equal("    <span title=\"new\">new &amp; value</span>", lines[2].Content);
        Assert.Equal("</div>", lines[3].Content);
    }

    [Fact]
    public void Parse_StandardUnifiedDiff_WithoutGitPrefix()
    {
        // Standard `diff -u` output has no "diff --git" line
        var diff = "--- a/file1.txt\n+++ b/file2.txt\n@@ -1,3 +1,3 @@\n context\n-old line\n+new line\n context2";
        var files = DiffParser.Parse(diff);
        Assert.Single(files);
        Assert.Equal("file2.txt", files[0].FileName);
        Assert.Single(files[0].Hunks);
        Assert.Equal(4, files[0].Hunks[0].Lines.Count);
    }

    [Fact]
    public void Parse_StandardUnifiedDiff_WithoutPathPrefix()
    {
        // Some tools produce --- /path/file without a/ or b/ prefix
        var diff = "--- /tmp/old.txt\n+++ /tmp/new.txt\n@@ -1,2 +1,2 @@\n-old\n+new\n";
        var files = DiffParser.Parse(diff);
        Assert.Single(files);
        Assert.Equal("/tmp/new.txt", files[0].FileName);
    }

    [Fact]
    public void LooksLikeUnifiedDiff_StandardDiff_ReturnsTrue()
    {
        var diff = "--- a/file.txt\n+++ b/file.txt\n@@ -1 +1 @@\n-old\n+new\n";
        Assert.True(DiffParser.LooksLikeUnifiedDiff(diff));
    }

    [Fact]
    public void ShouldRenderDiffView_ViewTool_ReturnsFalse()
    {
        var diff = "--- a/file.txt\n+++ b/file.txt\n@@ -1 +1 @@\n-old\n+new\n";
        Assert.False(DiffParser.ShouldRenderDiffView(diff, "view"));
    }

    [Fact]
    public void ShouldRenderDiffView_NonViewTool_UsesUnifiedDiffDetection()
    {
        var diff = "--- a/file.txt\n+++ b/file.txt\n@@ -1 +1 @@\n-old\n+new\n";
        Assert.True(DiffParser.ShouldRenderDiffView(diff, "bash"));
    }

    [Fact]
    public void TryExtractNumberedViewOutput_SyntheticReadDiff_ReturnsPlainNumberedText()
    {
        var diff = """
            diff --git a/README.md b/README.md
            index 0000000..0000000 100644
            --- a/README.md
            +++ b/README.md
            @@ -1,3 +1,3 @@
             <p align="center">
               <img src="logo.png">
             </p>
            """;

        var ok = DiffParser.TryExtractNumberedViewOutput(diff, out var text);

        Assert.True(ok);
        Assert.Contains("1. <p align=\"center\">", text);
        Assert.Contains("2.   <img src=\"logo.png\">", text);
        Assert.Contains("3. </p>", text);
    }

    [Fact]
    public void TryExtractNumberedViewOutput_RealDiffWithChanges_ReturnsFalse()
    {
        var diff = """
            diff --git a/file.txt b/file.txt
            index abc123..def456 100644
            --- a/file.txt
            +++ b/file.txt
            @@ -1,2 +1,2 @@
            -old
            +new
             keep
            """;

        var ok = DiffParser.TryExtractNumberedViewOutput(diff, out _);

        Assert.False(ok);
    }

    [Fact]
    public void Parse_MalformedDiffLikeMarkersSeparated_ReturnsEmptyWhileLookingDiffLike()
    {
        var diff = """
            --- a/file.txt
            not actually a diff body
            +++ b/file.txt
            @@ -1 +1 @@
            """;

        Assert.True(DiffParser.LooksLikeUnifiedDiff(diff));
        Assert.Empty(DiffParser.Parse(diff));
        Assert.False(DiffParser.TryExtractNumberedViewOutput(diff, out _));
    }

    [Fact]
    public void Parse_AngleBracketsInCode_NotEncoded()
    {
        // Verify generic type parameters with <> are preserved as-is
        var diff = """
            diff --git a/f.cs b/f.cs
            --- a/f.cs
            +++ b/f.cs
            @@ -1,2 +1,2 @@
            -List<string> items = new List<string>();
            +Dictionary<string, int> items = new Dictionary<string, int>();
            """;
        var files = DiffParser.Parse(diff);
        var lines = files[0].Hunks[0].Lines;

        Assert.Equal("List<string> items = new List<string>();", lines[0].Content);
        Assert.Equal("Dictionary<string, int> items = new Dictionary<string, int>();", lines[1].Content);
    }
}
