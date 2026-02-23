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
    public void Parse_SpecialHtmlCharacters_PreservedInContent()
    {
        // Regression: DiffView was double-encoding HTML entities because
        // it called HtmlEncode() before passing to Blazor's @() which
        // encodes again. Verify the parser preserves raw characters.
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

        // Content must contain raw <, >, ", & — no HTML encoding at parse time
        Assert.Equal("<div class=\"container\">", lines[0].Content);
        Assert.Equal("    <span title=\"old\">old &amp; value</span>", lines[1].Content);
        Assert.Equal("    <span title=\"new\">new &amp; value</span>", lines[2].Content);
        Assert.Equal("</div>", lines[3].Content);
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
