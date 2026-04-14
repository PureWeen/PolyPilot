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
    public void DiffFile_MetadataProperties_ReportStatusAndCounts()
    {
        var file = new DiffFile
        {
            FileName = "src/example.cs",
            Hunks = new List<DiffHunk>
            {
                new()
                {
                    Lines = new List<DiffLine>
                    {
                        new() { Type = DiffLineType.Context, Content = "keep" },
                        new() { Type = DiffLineType.Added, Content = "add 1" },
                        new() { Type = DiffLineType.Added, Content = "add 2" },
                        new() { Type = DiffLineType.Removed, Content = "remove 1" },
                    }
                }
            }
        };

        Assert.Equal(2, file.AddedLineCount);
        Assert.Equal(1, file.RemovedLineCount);
        Assert.Equal("MOD", file.StatusLabel);
        Assert.Equal("modified", file.StatusCssClass);
        Assert.Equal("src/example.cs", file.DisplayName);
    }

    [Fact]
    public void DiffFile_DisplayName_RenamedFile_ShowsOldAndNewPaths()
    {
        var file = new DiffFile
        {
            FileName = "src/new-name.cs",
            OldFileName = "src/old-name.cs",
            IsRenamed = true
        };

        Assert.Equal("REN", file.StatusLabel);
        Assert.Equal("renamed", file.StatusCssClass);
        Assert.Equal("src/old-name.cs → src/new-name.cs", file.DisplayName);
    }

    [Fact]
    public void DiffViewState_ResetSelectionAndModes_TracksGeneration()
    {
        var files = new List<DiffFile>
        {
            new() { FileName = "a.cs" },
            new() { FileName = "b.cs" }
        };
        var state = new DiffViewState();

        state.Reset(files);
        state.SetViewMode(1, DiffViewMode.Editor);
        var firstGeneration = state.Generation;

        Assert.Equal(0, state.SelectedFileIndex);
        Assert.Equal(DiffViewMode.Editor, state.GetViewMode(1));
        Assert.True(state.SelectFile(1, files.Count));
        Assert.Equal(1, state.SelectedFileIndex);

        state.Reset(files);

        Assert.Equal(firstGeneration + 1, state.Generation);
        Assert.Equal(0, state.SelectedFileIndex);
        Assert.Equal(DiffViewMode.Table, state.GetViewMode(1));
        Assert.False(state.IsFilePickerCollapsed);
    }

    [Fact]
    public void DiffLineCommentRequest_ToPrompt_FormatsReviewMessage()
    {
        var request = new DiffLineCommentRequest("src/ReviewPanel.cs", 42, "Please simplify this branch", "original");

        Assert.Equal(
            "On file src/ReviewPanel.cs, line 42 (original): Please simplify this branch",
            request.ToPrompt());
    }

    [Fact]
    public void Parse_BinaryDiffLikeOutput_CapturesFileMetadataWithoutHunks()
    {
        var diff = """
            diff --git a/assets/logo.png b/assets/logo.png
            index e69de29..1b2c3d4 100644
            Binary files a/assets/logo.png and b/assets/logo.png differ
            """;

        var files = DiffParser.Parse(diff);

        Assert.Single(files);
        Assert.Equal("assets/logo.png", files[0].FileName);
        Assert.Empty(files[0].Hunks);
        Assert.Equal(0, files[0].AddedLineCount);
        Assert.Equal(0, files[0].RemovedLineCount);
    }

    [Fact]
    public void Parse_LargeDiff_AggregatesCountsAcrossHunks()
    {
        var removedLines = string.Join('\n', Enumerable.Range(1, 12).Select(i => $"-old {i}"));
        var addedLines = string.Join('\n', Enumerable.Range(1, 15).Select(i => $"+new {i}"));
        var diff = $$"""
            diff --git a/huge.cs b/huge.cs
            --- a/huge.cs
            +++ b/huge.cs
            @@ -1,12 +1,15 @@
            {{removedLines}}
            {{addedLines}}
            """;

        var files = DiffParser.Parse(diff);

        Assert.Single(files);
        Assert.Equal(12, files[0].RemovedLineCount);
        Assert.Equal(15, files[0].AddedLineCount);
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

    [Fact]
    public void ReconstructOriginal_ReturnsContextAndRemovedLines()
    {
        var diff = """
            diff --git a/test.cs b/test.cs
            --- a/test.cs
            +++ b/test.cs
            @@ -1,4 +1,4 @@
             using System;
            -Console.WriteLine("Hello");
            +Console.WriteLine("World");
             return 0;
            """;
        var files = DiffParser.Parse(diff);
        var original = DiffParser.ReconstructOriginal(files[0]);

        Assert.Contains("using System;", original);
        Assert.Contains("Console.WriteLine(\"Hello\")", original);
        Assert.DoesNotContain("Console.WriteLine(\"World\")", original);
        Assert.Contains("return 0;", original);
    }

    [Fact]
    public void ReconstructModified_ReturnsContextAndAddedLines()
    {
        var diff = """
            diff --git a/test.cs b/test.cs
            --- a/test.cs
            +++ b/test.cs
            @@ -1,4 +1,4 @@
             using System;
            -Console.WriteLine("Hello");
            +Console.WriteLine("World");
             return 0;
            """;
        var files = DiffParser.Parse(diff);
        var modified = DiffParser.ReconstructModified(files[0]);

        Assert.Contains("using System;", modified);
        Assert.DoesNotContain("Console.WriteLine(\"Hello\")", modified);
        Assert.Contains("Console.WriteLine(\"World\")", modified);
        Assert.Contains("return 0;", modified);
    }

    [Fact]
    public void ReconstructOriginalAndModified_NewFile_ModifiedHasAllLines()
    {
        var diff = """
            diff --git a/new.txt b/new.txt
            new file mode 100644
            --- /dev/null
            +++ b/new.txt
            @@ -0,0 +1,3 @@
            +line 1
            +line 2
            +line 3
            """;
        var files = DiffParser.Parse(diff);
        var original = DiffParser.ReconstructOriginal(files[0]);
        var modified = DiffParser.ReconstructModified(files[0]);

        Assert.Equal("", original);
        Assert.Contains("line 1", modified);
        Assert.Contains("line 2", modified);
        Assert.Contains("line 3", modified);
    }

    [Fact]
    public void ReconstructOriginalAndModified_DeletedFile_OriginalHasAllLines()
    {
        var diff = """
            diff --git a/old.txt b/old.txt
            deleted file mode 100644
            --- a/old.txt
            +++ /dev/null
            @@ -1,3 +0,0 @@
            -line A
            -line B
            -line C
            """;
        var files = DiffParser.Parse(diff);
        var original = DiffParser.ReconstructOriginal(files[0]);
        var modified = DiffParser.ReconstructModified(files[0]);

        Assert.Contains("line A", original);
        Assert.Contains("line B", original);
        Assert.Contains("line C", original);
        Assert.Equal("", modified);
    }

    [Fact]
    public void ReconstructOriginalAndModified_EmptyHunks_ReturnsEmpty()
    {
        var file = new DiffFile { FileName = "empty.txt", Hunks = new List<DiffHunk>() };
        var original = DiffParser.ReconstructOriginal(file);
        var modified = DiffParser.ReconstructModified(file);

        Assert.Equal("", original);
        Assert.Equal("", modified);
    }

    [Fact]
    public void ReconstructOriginalAndModified_MultipleHunks_CombinesAll()
    {
        var diff = """
            diff --git a/multi.cs b/multi.cs
            --- a/multi.cs
            +++ b/multi.cs
            @@ -1,3 +1,3 @@
             using System;
            -using Old;
            +using New;
             class A {}
            @@ -10,3 +10,4 @@
             void Foo() {
            -    Bar();
            +    Baz();
            +    Qux();
             }
            """;
        var files = DiffParser.Parse(diff);
        var original = DiffParser.ReconstructOriginal(files[0]);
        var modified = DiffParser.ReconstructModified(files[0]);

        // Original should have both hunks' context+removed lines
        Assert.Contains("using Old;", original);
        Assert.Contains("Bar();", original);
        Assert.DoesNotContain("using New;", original);
        Assert.DoesNotContain("Baz();", original);
        Assert.DoesNotContain("Qux();", original);

        // Modified should have both hunks' context+added lines
        Assert.Contains("using New;", modified);
        Assert.Contains("Baz();", modified);
        Assert.Contains("Qux();", modified);
        Assert.DoesNotContain("using Old;", modified);
        Assert.DoesNotContain("Bar();", modified);
    }

    [Fact]
    public void ReconstructOriginalAndModified_ContextOnly_BothIdentical()
    {
        // A diff with only context lines (no real changes) — e.g., a self-diff view
        var file = new DiffFile
        {
            FileName = "ctx.txt",
            Hunks = new List<DiffHunk>
            {
                new()
                {
                    Lines = new List<DiffLine>
                    {
                        new() { Type = DiffLineType.Context, Content = "line 1" },
                        new() { Type = DiffLineType.Context, Content = "line 2" },
                        new() { Type = DiffLineType.Context, Content = "line 3" },
                    }
                }
            }
        };
        var original = DiffParser.ReconstructOriginal(file);
        var modified = DiffParser.ReconstructModified(file);

        Assert.Equal(original, modified);
        Assert.Contains("line 1", original);
        Assert.Contains("line 3", original);
    }

    // ========== INTER-HUNK GAP PLACEHOLDER TESTS ==========

    [Fact]
    public void ReconstructOriginal_MultipleHunks_InsertsGapPlaceholders()
    {
        // Hunks at lines 1-3 and 10-12 with a gap of lines 4-9
        var diff = """
            diff --git a/gap.cs b/gap.cs
            --- a/gap.cs
            +++ b/gap.cs
            @@ -1,3 +1,3 @@
             line1
            -old2
            +new2
             line3
            @@ -10,3 +10,3 @@
             line10
            -old11
            +new11
             line12
            """;
        var files = DiffParser.Parse(diff);
        var original = DiffParser.ReconstructOriginal(files[0]);
        var lines = original.Split('\n');

        // Line 1 should be "line1", line 2 "old2", line 3 "line3"
        Assert.Equal("line1", lines[0]);
        Assert.Equal("old2", lines[1]);
        Assert.Equal("line3", lines[2]);
        // Lines 4-9 should be empty gap placeholders
        for (int i = 3; i < 9; i++)
        {
            Assert.Equal("", lines[i]);
        }
        // Line 10 should be "line10"
        Assert.Equal("line10", lines[9]);
        Assert.Equal("old11", lines[10]);
        Assert.Equal("line12", lines[11]);
    }

    [Fact]
    public void ReconstructModified_MultipleHunks_InsertsGapPlaceholders()
    {
        var diff = """
            diff --git a/gap.cs b/gap.cs
            --- a/gap.cs
            +++ b/gap.cs
            @@ -1,3 +1,3 @@
             line1
            -old2
            +new2
             line3
            @@ -10,3 +10,3 @@
             line10
            -old11
            +new11
             line12
            """;
        var files = DiffParser.Parse(diff);
        var modified = DiffParser.ReconstructModified(files[0]);
        var lines = modified.Split('\n');

        Assert.Equal("line1", lines[0]);
        Assert.Equal("new2", lines[1]);
        Assert.Equal("line3", lines[2]);
        // Lines 4-9 should be empty gap placeholders
        for (int i = 3; i < 9; i++)
        {
            Assert.Equal("", lines[i]);
        }
        Assert.Equal("line10", lines[9]);
        Assert.Equal("new11", lines[10]);
        Assert.Equal("line12", lines[11]);
    }

    [Fact]
    public void ReconstructOriginal_SingleHunkAtLineOne_NoLeadingGap()
    {
        var diff = """
            diff --git a/f.cs b/f.cs
            --- a/f.cs
            +++ b/f.cs
            @@ -1,2 +1,2 @@
            -old
            +new
             keep
            """;
        var files = DiffParser.Parse(diff);
        var original = DiffParser.ReconstructOriginal(files[0]);
        var lines = original.Split('\n');

        Assert.Equal("old", lines[0]);
        Assert.Equal("keep", lines[1]);
    }

    [Fact]
    public void ReconstructOriginal_HunkStartingAtLargeOffset_InsertsCorrectGap()
    {
        // Hunk starts at line 100 — should have 99 empty lines before it
        var diff = """
            diff --git a/big.cs b/big.cs
            --- a/big.cs
            +++ b/big.cs
            @@ -100,2 +100,2 @@
            -old at 100
            +new at 100
             line 101
            """;
        var files = DiffParser.Parse(diff);
        var original = DiffParser.ReconstructOriginal(files[0]);
        var lines = original.Split('\n');

        // Lines 1-99 are gap placeholders
        Assert.Equal(101, lines.Length);
        for (int i = 0; i < 99; i++)
        {
            Assert.Equal("", lines[i]);
        }
        Assert.Equal("old at 100", lines[99]);
        Assert.Equal("line 101", lines[100]);
    }

    // ========== PAIRLINES TESTS ==========

    [Fact]
    public void PairLines_ContextOnly_AllPairedSameOnBothSides()
    {
        var hunk = new DiffHunk
        {
            Lines = new List<DiffLine>
            {
                new() { Type = DiffLineType.Context, Content = "a", OldLineNo = 1, NewLineNo = 1 },
                new() { Type = DiffLineType.Context, Content = "b", OldLineNo = 2, NewLineNo = 2 },
            }
        };
        var rows = DiffParser.PairLines(hunk);

        Assert.Equal(2, rows.Count);
        Assert.Same(rows[0].Left, rows[0].Right);
        Assert.Same(rows[1].Left, rows[1].Right);
    }

    [Fact]
    public void PairLines_MatchedRemoveAndAdd_PairedSideBySide()
    {
        var hunk = new DiffHunk
        {
            Lines = new List<DiffLine>
            {
                new() { Type = DiffLineType.Removed, Content = "old", OldLineNo = 1 },
                new() { Type = DiffLineType.Added, Content = "new", NewLineNo = 1 },
            }
        };
        var rows = DiffParser.PairLines(hunk);

        Assert.Single(rows);
        Assert.Equal("old", rows[0].Left!.Content);
        Assert.Equal("new", rows[0].Right!.Content);
    }

    [Fact]
    public void PairLines_MoreRemovedThanAdded_ExtraRemovedGetNullRight()
    {
        var hunk = new DiffHunk
        {
            Lines = new List<DiffLine>
            {
                new() { Type = DiffLineType.Removed, Content = "a", OldLineNo = 1 },
                new() { Type = DiffLineType.Removed, Content = "b", OldLineNo = 2 },
                new() { Type = DiffLineType.Removed, Content = "c", OldLineNo = 3 },
                new() { Type = DiffLineType.Added, Content = "x", NewLineNo = 1 },
            }
        };
        var rows = DiffParser.PairLines(hunk);

        Assert.Equal(3, rows.Count);
        Assert.Equal("a", rows[0].Left!.Content);
        Assert.Equal("x", rows[0].Right!.Content);
        Assert.Equal("b", rows[1].Left!.Content);
        Assert.Null(rows[1].Right);
        Assert.Equal("c", rows[2].Left!.Content);
        Assert.Null(rows[2].Right);
    }

    [Fact]
    public void PairLines_MoreAddedThanRemoved_ExtraAddedGetNullLeft()
    {
        var hunk = new DiffHunk
        {
            Lines = new List<DiffLine>
            {
                new() { Type = DiffLineType.Removed, Content = "old", OldLineNo = 1 },
                new() { Type = DiffLineType.Added, Content = "new1", NewLineNo = 1 },
                new() { Type = DiffLineType.Added, Content = "new2", NewLineNo = 2 },
                new() { Type = DiffLineType.Added, Content = "new3", NewLineNo = 3 },
            }
        };
        var rows = DiffParser.PairLines(hunk);

        Assert.Equal(3, rows.Count);
        Assert.Equal("old", rows[0].Left!.Content);
        Assert.Equal("new1", rows[0].Right!.Content);
        Assert.Null(rows[1].Left);
        Assert.Equal("new2", rows[1].Right!.Content);
        Assert.Null(rows[2].Left);
        Assert.Equal("new3", rows[2].Right!.Content);
    }

    [Fact]
    public void PairLines_PureAdditions_AllNullLeft()
    {
        var hunk = new DiffHunk
        {
            Lines = new List<DiffLine>
            {
                new() { Type = DiffLineType.Added, Content = "new1", NewLineNo = 1 },
                new() { Type = DiffLineType.Added, Content = "new2", NewLineNo = 2 },
            }
        };
        var rows = DiffParser.PairLines(hunk);

        Assert.Equal(2, rows.Count);
        Assert.Null(rows[0].Left);
        Assert.Equal("new1", rows[0].Right!.Content);
        Assert.Null(rows[1].Left);
        Assert.Equal("new2", rows[1].Right!.Content);
    }

    [Fact]
    public void PairLines_PureDeletions_AllNullRight()
    {
        var hunk = new DiffHunk
        {
            Lines = new List<DiffLine>
            {
                new() { Type = DiffLineType.Removed, Content = "old1", OldLineNo = 1 },
                new() { Type = DiffLineType.Removed, Content = "old2", OldLineNo = 2 },
            }
        };
        var rows = DiffParser.PairLines(hunk);

        Assert.Equal(2, rows.Count);
        Assert.Equal("old1", rows[0].Left!.Content);
        Assert.Null(rows[0].Right);
        Assert.Equal("old2", rows[1].Left!.Content);
        Assert.Null(rows[1].Right);
    }

    [Fact]
    public void PairLines_MixedContextAndChanges_CorrectInterleaving()
    {
        var hunk = new DiffHunk
        {
            Lines = new List<DiffLine>
            {
                new() { Type = DiffLineType.Context, Content = "ctx1", OldLineNo = 1, NewLineNo = 1 },
                new() { Type = DiffLineType.Removed, Content = "old", OldLineNo = 2 },
                new() { Type = DiffLineType.Added, Content = "new", NewLineNo = 2 },
                new() { Type = DiffLineType.Context, Content = "ctx2", OldLineNo = 3, NewLineNo = 3 },
            }
        };
        var rows = DiffParser.PairLines(hunk);

        Assert.Equal(3, rows.Count);
        // First: context
        Assert.Equal("ctx1", rows[0].Left!.Content);
        Assert.Same(rows[0].Left, rows[0].Right);
        // Second: change pair
        Assert.Equal("old", rows[1].Left!.Content);
        Assert.Equal("new", rows[1].Right!.Content);
        // Third: context
        Assert.Equal("ctx2", rows[2].Left!.Content);
        Assert.Same(rows[2].Left, rows[2].Right);
    }

    [Fact]
    public void PairLines_EmptyHunk_ReturnsEmpty()
    {
        var hunk = new DiffHunk { Lines = new List<DiffLine>() };
        Assert.Empty(DiffParser.PairLines(hunk));
    }

    [Fact]
    public void PairLines_MultipleChangeBlocks_EachPairedIndependently()
    {
        // context, remove+add, context, remove+add
        var hunk = new DiffHunk
        {
            Lines = new List<DiffLine>
            {
                new() { Type = DiffLineType.Context, Content = "a", OldLineNo = 1, NewLineNo = 1 },
                new() { Type = DiffLineType.Removed, Content = "old1", OldLineNo = 2 },
                new() { Type = DiffLineType.Added, Content = "new1", NewLineNo = 2 },
                new() { Type = DiffLineType.Context, Content = "b", OldLineNo = 3, NewLineNo = 3 },
                new() { Type = DiffLineType.Removed, Content = "old2", OldLineNo = 4 },
                new() { Type = DiffLineType.Added, Content = "new2", NewLineNo = 4 },
            }
        };
        var rows = DiffParser.PairLines(hunk);

        Assert.Equal(4, rows.Count);
        Assert.Equal("a", rows[0].Left!.Content);
        Assert.Equal("old1", rows[1].Left!.Content);
        Assert.Equal("new1", rows[1].Right!.Content);
        Assert.Equal("b", rows[2].Left!.Content);
        Assert.Equal("old2", rows[3].Left!.Content);
        Assert.Equal("new2", rows[3].Right!.Content);
    }

    // ========== MULTI-FILE MIXED TYPE TESTS ==========

    [Fact]
    public void Parse_MixedFileTypes_NewDeletedModifiedRenamed()
    {
        var diff = """
            diff --git a/modified.cs b/modified.cs
            --- a/modified.cs
            +++ b/modified.cs
            @@ -1,2 +1,2 @@
            -old
            +new
             keep
            diff --git a/brand_new.cs b/brand_new.cs
            new file mode 100644
            --- /dev/null
            +++ b/brand_new.cs
            @@ -0,0 +1,1 @@
            +hello
            diff --git a/doomed.cs b/doomed.cs
            deleted file mode 100644
            --- a/doomed.cs
            +++ /dev/null
            @@ -1,1 +0,0 @@
            -goodbye
            diff --git a/old_name.cs b/new_name.cs
            rename from old_name.cs
            rename to new_name.cs
            """;
        var files = DiffParser.Parse(diff);

        Assert.Equal(4, files.Count);

        Assert.Equal("modified.cs", files[0].FileName);
        Assert.False(files[0].IsNew);
        Assert.False(files[0].IsDeleted);
        Assert.False(files[0].IsRenamed);

        Assert.Equal("brand_new.cs", files[1].FileName);
        Assert.True(files[1].IsNew);

        Assert.Equal("doomed.cs", files[2].FileName);
        Assert.True(files[2].IsDeleted);

        Assert.Equal("new_name.cs", files[3].FileName);
        Assert.True(files[3].IsRenamed);
        Assert.Equal("old_name.cs", files[3].OldFileName);
    }

    // ========== EDGE CASES ==========

    [Fact]
    public void Parse_CRLFLineEndings_ParsesCorrectly()
    {
        var diff = "diff --git a/f.cs b/f.cs\r\n--- a/f.cs\r\n+++ b/f.cs\r\n@@ -1,2 +1,2 @@\r\n-old\r\n+new\r\n keep\r\n";
        var files = DiffParser.Parse(diff);

        Assert.Single(files);
        // Parser produces 3 meaningful lines: removed, added, context
        // (trailing empty line from split is also parsed as context)
        Assert.True(files[0].Hunks[0].Lines.Count >= 3);
        Assert.Equal("old", files[0].Hunks[0].Lines[0].Content);
        Assert.Equal("new", files[0].Hunks[0].Lines[1].Content);
        Assert.Equal("keep", files[0].Hunks[0].Lines[2].Content);
    }

    [Fact]
    public void Parse_EmptyRemovedLine_ContentIsEmptyString()
    {
        var diff = """
            diff --git a/f.txt b/f.txt
            --- a/f.txt
            +++ b/f.txt
            @@ -1,2 +1,1 @@
            -
             keep
            """;
        var files = DiffParser.Parse(diff);
        var removed = files[0].Hunks[0].Lines[0];

        Assert.Equal(DiffLineType.Removed, removed.Type);
        Assert.Equal("", removed.Content);
    }

    [Fact]
    public void Parse_EmptyAddedLine_ContentIsEmptyString()
    {
        var diff = """
            diff --git a/f.txt b/f.txt
            --- a/f.txt
            +++ b/f.txt
            @@ -1,1 +1,2 @@
             keep
            +
            """;
        var files = DiffParser.Parse(diff);
        var added = files[0].Hunks[0].Lines[1];

        Assert.Equal(DiffLineType.Added, added.Type);
        Assert.Equal("", added.Content);
    }

    [Fact]
    public void Parse_HunkHeaderWithoutFunctionName()
    {
        var diff = """
            diff --git a/f.cs b/f.cs
            --- a/f.cs
            +++ b/f.cs
            @@ -1,2 +1,2 @@
            -old
            +new
            """;
        var files = DiffParser.Parse(diff);
        Assert.Null(files[0].Hunks[0].Header);
    }

    [Fact]
    public void Parse_HunkHeaderWithFunctionName()
    {
        var diff = """
            diff --git a/f.cs b/f.cs
            --- a/f.cs
            +++ b/f.cs
            @@ -10,5 +10,5 @@ public void MyMethod()
            -old
            +new
            """;
        var files = DiffParser.Parse(diff);
        Assert.Equal("public void MyMethod()", files[0].Hunks[0].Header);
    }

    [Fact]
    public void Parse_LineNumbersAreSequential()
    {
        var diff = """
            diff --git a/f.cs b/f.cs
            --- a/f.cs
            +++ b/f.cs
            @@ -5,4 +5,4 @@
             ctx5
            -old6
            +new6
             ctx7
            """;
        var files = DiffParser.Parse(diff);
        var lines = files[0].Hunks[0].Lines;

        // Context line at old 5, new 5
        Assert.Equal(5, lines[0].OldLineNo);
        Assert.Equal(5, lines[0].NewLineNo);
        // Removed at old 6
        Assert.Equal(6, lines[1].OldLineNo);
        Assert.Null(lines[1].NewLineNo);
        // Added at new 6
        Assert.Null(lines[2].OldLineNo);
        Assert.Equal(6, lines[2].NewLineNo);
        // Context at old 7, new 7
        Assert.Equal(7, lines[3].OldLineNo);
        Assert.Equal(7, lines[3].NewLineNo);
    }

    [Fact]
    public void Parse_LineNumbersWithInsertions_CorrectOffset()
    {
        // When lines are added, new line numbers advance faster than old
        var diff = """
            diff --git a/f.cs b/f.cs
            --- a/f.cs
            +++ b/f.cs
            @@ -1,2 +1,4 @@
             keep
            +added1
            +added2
             keep2
            """;
        var files = DiffParser.Parse(diff);
        var lines = files[0].Hunks[0].Lines;

        Assert.Equal(1, lines[0].OldLineNo); // keep: old=1
        Assert.Equal(1, lines[0].NewLineNo); // keep: new=1
        Assert.Equal(2, lines[1].NewLineNo); // added1: new=2
        Assert.Equal(3, lines[2].NewLineNo); // added2: new=3
        Assert.Equal(2, lines[3].OldLineNo); // keep2: old=2
        Assert.Equal(4, lines[3].NewLineNo); // keep2: new=4
    }

    [Fact]
    public void Parse_MultipleHunks_LineNumbersResetPerHunk()
    {
        var diff = """
            diff --git a/f.cs b/f.cs
            --- a/f.cs
            +++ b/f.cs
            @@ -1,2 +1,2 @@
            -old1
            +new1
             ctx
            @@ -20,2 +20,2 @@
            -old20
            +new20
             ctx20
            """;
        var files = DiffParser.Parse(diff);

        // First hunk starts at line 1
        Assert.Equal(1, files[0].Hunks[0].Lines[0].OldLineNo);
        // Second hunk starts at line 20
        Assert.Equal(20, files[0].Hunks[1].Lines[0].OldLineNo);
    }

    // ========== SHOULDRENDERDIFFVIEW INTEGRATION ==========

    [Fact]
    public void ShouldRenderDiffView_ReadTool_ReturnsFalse()
    {
        var diff = "--- a/file.txt\n+++ b/file.txt\n@@ -1 +1 @@\n-old\n+new\n";
        Assert.False(DiffParser.ShouldRenderDiffView(diff, "Read"));
    }

    [Fact]
    public void ShouldRenderDiffView_NullToolName_UsesUnifiedDiffDetection()
    {
        var diff = "--- a/file.txt\n+++ b/file.txt\n@@ -1 +1 @@\n-old\n+new\n";
        Assert.True(DiffParser.ShouldRenderDiffView(diff, null));
    }

    [Fact]
    public void ShouldRenderDiffView_PlainText_ReturnsFalse()
    {
        Assert.False(DiffParser.ShouldRenderDiffView("hello world", "bash"));
    }

    [Fact]
    public void ShouldRenderDiffView_EmptyString_ReturnsFalse()
    {
        Assert.False(DiffParser.ShouldRenderDiffView("", null));
        Assert.False(DiffParser.ShouldRenderDiffView(null, null));
    }

    // ========== ISPLAINTEXTVIEWTOOL ==========

    [Fact]
    public void IsPlainTextViewTool_CaseInsensitive()
    {
        Assert.True(DiffParser.IsPlainTextViewTool("View"));
        Assert.True(DiffParser.IsPlainTextViewTool("VIEW"));
        Assert.True(DiffParser.IsPlainTextViewTool("view"));
        Assert.True(DiffParser.IsPlainTextViewTool("Read"));
        Assert.True(DiffParser.IsPlainTextViewTool("READ"));
        Assert.True(DiffParser.IsPlainTextViewTool("read"));
    }

    [Fact]
    public void IsPlainTextViewTool_OtherTools_ReturnsFalse()
    {
        Assert.False(DiffParser.IsPlainTextViewTool("bash"));
        Assert.False(DiffParser.IsPlainTextViewTool("edit"));
        Assert.False(DiffParser.IsPlainTextViewTool(null));
        Assert.False(DiffParser.IsPlainTextViewTool(""));
    }

    // ========== RECONSTRUCTION ROUND-TRIP INTEGRITY ==========

    [Fact]
    public void ReconstructOriginalAndModified_SingleLineChange_ExactContent()
    {
        var diff = """
            diff --git a/f.txt b/f.txt
            --- a/f.txt
            +++ b/f.txt
            @@ -1,1 +1,1 @@
            -hello world
            +goodbye world
            """;
        var files = DiffParser.Parse(diff);
        var original = DiffParser.ReconstructOriginal(files[0]);
        var modified = DiffParser.ReconstructModified(files[0]);

        Assert.Equal("hello world", original);
        Assert.Equal("goodbye world", modified);
    }

    [Fact]
    public void ReconstructOriginal_MultiHunk_GapLineCount()
    {
        // First hunk at line 1 (3 lines), second hunk at line 50 (2 lines)
        // Gap should be lines 4-49 = 46 empty lines
        var file = new DiffFile
        {
            FileName = "test.cs",
            Hunks = new List<DiffHunk>
            {
                new()
                {
                    OldStart = 1,
                    NewStart = 1,
                    Lines = new List<DiffLine>
                    {
                        new() { Type = DiffLineType.Context, Content = "line1" },
                        new() { Type = DiffLineType.Removed, Content = "old2" },
                        new() { Type = DiffLineType.Context, Content = "line3" },
                    }
                },
                new()
                {
                    OldStart = 50,
                    NewStart = 50,
                    Lines = new List<DiffLine>
                    {
                        new() { Type = DiffLineType.Context, Content = "line50" },
                        new() { Type = DiffLineType.Removed, Content = "old51" },
                    }
                }
            }
        };

        var original = DiffParser.ReconstructOriginal(file);
        var lines = original.Split('\n');

        // Lines: 1=line1, 2=old2, 3=line3, 4-49=empty, 50=line50, 51=old51
        Assert.Equal("line1", lines[0]);
        Assert.Equal("old2", lines[1]);
        Assert.Equal("line3", lines[2]);
        // Gap: lines[3] through lines[48] should be empty
        for (int i = 3; i < 49; i++)
            Assert.Equal("", lines[i]);
        Assert.Equal("line50", lines[49]);
        Assert.Equal("old51", lines[50]);
    }

    [Fact]
    public void ReconstructModified_WithAddedLines_GapCountAdjusted()
    {
        // When lines are added in hunk 1, the second hunk's NewStart is higher
        var file = new DiffFile
        {
            FileName = "test.cs",
            Hunks = new List<DiffHunk>
            {
                new()
                {
                    OldStart = 1,
                    NewStart = 1,
                    Lines = new List<DiffLine>
                    {
                        new() { Type = DiffLineType.Context, Content = "a" },
                        new() { Type = DiffLineType.Added, Content = "new_line" },
                    }
                },
                new()
                {
                    OldStart = 10,
                    NewStart = 11, // offset by +1 due to addition
                    Lines = new List<DiffLine>
                    {
                        new() { Type = DiffLineType.Context, Content = "b" },
                    }
                }
            }
        };

        var modified = DiffParser.ReconstructModified(file);
        var lines = modified.Split('\n');

        Assert.Equal("a", lines[0]);
        Assert.Equal("new_line", lines[1]);
        // Gap: lines[2] through lines[9] (newStart=11, currentLine after hunk1=3, gap=11-3=8)
        for (int i = 2; i < 10; i++)
            Assert.Equal("", lines[i]);
        Assert.Equal("b", lines[10]);
    }

    // ========== REAL-WORLD DIFF PATTERNS ==========

    [Fact]
    public void Parse_RealWorldGitDiff_WithBinaryFileNotice()
    {
        // Git sometimes shows "Binary files differ" — parser should handle gracefully
        var diff = """
            diff --git a/image.png b/image.png
            index abc..def 100644
            Binary files a/image.png and b/image.png differ
            diff --git a/code.cs b/code.cs
            --- a/code.cs
            +++ b/code.cs
            @@ -1,1 +1,1 @@
            -old
            +new
            """;
        var files = DiffParser.Parse(diff);

        // The binary file should be parsed as a file entry with no hunks
        Assert.Equal(2, files.Count);
        Assert.Equal("image.png", files[0].FileName);
        Assert.Empty(files[0].Hunks);
        Assert.Equal("code.cs", files[1].FileName);
        Assert.Single(files[1].Hunks);
    }

    [Fact]
    public void Parse_DiffWithIndexLine_SkipsIt()
    {
        var diff = """
            diff --git a/f.cs b/f.cs
            index abc1234..def5678 100644
            --- a/f.cs
            +++ b/f.cs
            @@ -1,1 +1,1 @@
            -old
            +new
            """;
        var files = DiffParser.Parse(diff);
        Assert.Single(files);
        Assert.Single(files[0].Hunks);
        Assert.Equal(2, files[0].Hunks[0].Lines.Count);
    }

    [Fact]
    public void Parse_ThreeHunks_AllParsed()
    {
        var diff = """
            diff --git a/f.cs b/f.cs
            --- a/f.cs
            +++ b/f.cs
            @@ -1,2 +1,2 @@
            -a
            +A
             x
            @@ -50,2 +50,2 @@
            -b
            +B
             y
            @@ -100,2 +100,2 @@
            -c
            +C
             z
            """;
        var files = DiffParser.Parse(diff);
        Assert.Single(files);
        Assert.Equal(3, files[0].Hunks.Count);
        Assert.Equal(1, files[0].Hunks[0].OldStart);
        Assert.Equal(50, files[0].Hunks[1].OldStart);
        Assert.Equal(100, files[0].Hunks[2].OldStart);
    }

    [Fact]
    public void Parse_HunkWithOnlyCount_NoComma()
    {
        // @@ -1 +1 @@ — no comma means count=1 (implied)
        var diff = """
            diff --git a/f.cs b/f.cs
            --- a/f.cs
            +++ b/f.cs
            @@ -1 +1 @@
            -old
            +new
            """;
        var files = DiffParser.Parse(diff);
        Assert.Equal(1, files[0].Hunks[0].OldStart);
        Assert.Equal(1, files[0].Hunks[0].NewStart);
    }

    // ========== TRYEXTRACTNUMBEREDVIEWOUTPUT EDGE CASES ==========

    [Fact]
    public void TryExtractNumberedViewOutput_PlainText_ReturnsFalse()
    {
        Assert.False(DiffParser.TryExtractNumberedViewOutput("hello world", out _));
    }

    [Fact]
    public void TryExtractNumberedViewOutput_DiffWithRealChanges_ReturnsFalse()
    {
        var diff = """
            diff --git a/f.txt b/f.txt
            --- a/f.txt
            +++ b/f.txt
            @@ -1,2 +1,2 @@
            -old
            +new
             keep
            """;
        Assert.False(DiffParser.TryExtractNumberedViewOutput(diff, out _));
    }

    [Fact]
    public void TryExtractNumberedViewOutput_ContextOnlyDiff_ReturnsNumberedText()
    {
        var diff = """
            diff --git a/f.txt b/f.txt
            --- a/f.txt
            +++ b/f.txt
            @@ -1,2 +1,2 @@
             first
             second
            """;
        var ok = DiffParser.TryExtractNumberedViewOutput(diff, out var text);
        Assert.True(ok);
        Assert.Contains("1. first", text);
        Assert.Contains("2. second", text);
    }

    // ========== RECONSTRUCT WITH FILE CONTENT (gap-fill) ==========

    [Fact]
    public void ReconstructModified_WithFileLines_FillsGapsWithRealContent()
    {
        var diff = """
            diff --git a/gap.cs b/gap.cs
            --- a/gap.cs
            +++ b/gap.cs
            @@ -1,3 +1,3 @@
             line1
            -old2
            +new2
             line3
            @@ -10,3 +10,3 @@
             line10
            -old11
            +new11
             line12
            """;
        var files = DiffParser.Parse(diff);

        // Simulate actual file content (12 lines)
        var fileLines = new[]
        {
            "line1", "new2", "line3",
            "real4", "real5", "real6", "real7", "real8", "real9",
            "line10", "new11", "line12"
        };

        var modified = DiffParser.ReconstructModified(files[0], fileLines);
        var lines = modified.Split('\n');

        // Hunk lines should come from the diff
        Assert.Equal("line1", lines[0]);
        Assert.Equal("new2", lines[1]);
        Assert.Equal("line3", lines[2]);
        // Gap lines should come from the file (not blank)
        Assert.Equal("real4", lines[3]);
        Assert.Equal("real5", lines[4]);
        Assert.Equal("real6", lines[5]);
        Assert.Equal("real7", lines[6]);
        Assert.Equal("real8", lines[7]);
        Assert.Equal("real9", lines[8]);
        // Second hunk
        Assert.Equal("line10", lines[9]);
        Assert.Equal("new11", lines[10]);
        Assert.Equal("line12", lines[11]);
    }

    [Fact]
    public void ReconstructOriginal_WithFileLines_FillsGapsWithRealContent()
    {
        var diff = """
            diff --git a/gap.cs b/gap.cs
            --- a/gap.cs
            +++ b/gap.cs
            @@ -1,3 +1,3 @@
             line1
            -old2
            +new2
             line3
            @@ -10,3 +10,3 @@
             line10
            -old11
            +new11
             line12
            """;
        var files = DiffParser.Parse(diff);

        // Original file content (before changes)
        var fileLines = new[]
        {
            "line1", "old2", "line3",
            "real4", "real5", "real6", "real7", "real8", "real9",
            "line10", "old11", "line12"
        };

        var original = DiffParser.ReconstructOriginal(files[0], fileLines);
        var lines = original.Split('\n');

        Assert.Equal("line1", lines[0]);
        Assert.Equal("old2", lines[1]);
        Assert.Equal("line3", lines[2]);
        // Gap lines filled from file
        Assert.Equal("real4", lines[3]);
        Assert.Equal("real5", lines[4]);
        Assert.Equal("real6", lines[5]);
        Assert.Equal("real7", lines[6]);
        Assert.Equal("real8", lines[7]);
        Assert.Equal("real9", lines[8]);
        Assert.Equal("line10", lines[9]);
        Assert.Equal("old11", lines[10]);
        Assert.Equal("line12", lines[11]);
    }

    [Fact]
    public void ReconstructModified_WithFileLines_AppendsTrailingLines()
    {
        // Diff only covers lines 1-3, but file has 6 lines total
        var diff = """
            diff --git a/f.cs b/f.cs
            --- a/f.cs
            +++ b/f.cs
            @@ -1,3 +1,3 @@
             line1
            -old2
            +new2
             line3
            """;
        var files = DiffParser.Parse(diff);

        var fileLines = new[] { "line1", "new2", "line3", "line4", "line5", "line6" };

        var modified = DiffParser.ReconstructModified(files[0], fileLines);
        var lines = modified.Split('\n');

        Assert.Equal(6, lines.Length);
        Assert.Equal("line4", lines[3]);
        Assert.Equal("line5", lines[4]);
        Assert.Equal("line6", lines[5]);
    }

    [Fact]
    public void ReconstructModified_WithoutFileLines_GapsAreBlank()
    {
        // Backward compatibility: calling without fileLines still produces blank gaps
        var diff = """
            diff --git a/gap.cs b/gap.cs
            --- a/gap.cs
            +++ b/gap.cs
            @@ -1,3 +1,3 @@
             line1
            -old2
            +new2
             line3
            @@ -10,3 +10,3 @@
             line10
            -old11
            +new11
             line12
            """;
        var files = DiffParser.Parse(diff);

        var modified = DiffParser.ReconstructModified(files[0]); // no fileLines
        var lines = modified.Split('\n');

        // Gap lines should still be blank (backward compat)
        for (int i = 3; i < 9; i++)
            Assert.Equal("", lines[i]);
    }

    // ========== CACHED LINE COUNTS ==========

    [Fact]
    public void DiffFile_AddedAndRemovedLineCounts_AreCached()
    {
        var file = new DiffFile
        {
            FileName = "test.cs",
            Hunks = new List<DiffHunk>
            {
                new()
                {
                    OldStart = 1, NewStart = 1,
                    Lines = new List<DiffLine>
                    {
                        new() { Type = DiffLineType.Removed, Content = "old" },
                        new() { Type = DiffLineType.Added, Content = "new1" },
                        new() { Type = DiffLineType.Added, Content = "new2" },
                        new() { Type = DiffLineType.Context, Content = "ctx" },
                    }
                }
            }
        };

        // First access computes
        Assert.Equal(2, file.AddedLineCount);
        Assert.Equal(1, file.RemovedLineCount);

        // Second access returns same cached values
        Assert.Equal(2, file.AddedLineCount);
        Assert.Equal(1, file.RemovedLineCount);
    }

    // ========== PATH TRAVERSAL GUARD ==========

    [Fact]
    public void PathTraversal_SiblingDirectory_IsBlocked()
    {
        // Verify that a sibling directory like "projectEvil" doesn't pass
        // StartsWith("C:\project") without trailing separator
        var workDir = Path.Combine(Path.GetTempPath(), "testproject");
        var siblingPath = "..\\testprojectEvil\\secret.txt";

        var filePath = Path.GetFullPath(Path.Combine(workDir, siblingPath));
        var normalizedWorkDir = Path.GetFullPath(workDir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                                + Path.DirectorySeparatorChar;

        Assert.False(filePath.StartsWith(normalizedWorkDir, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void PathTraversal_ValidSubpath_IsAllowed()
    {
        var workDir = Path.Combine(Path.GetTempPath(), "testproject");
        var validPath = "src\\Models\\User.cs";

        var filePath = Path.GetFullPath(Path.Combine(workDir, validPath));
        var normalizedWorkDir = Path.GetFullPath(workDir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                                + Path.DirectorySeparatorChar;

        Assert.True(filePath.StartsWith(normalizedWorkDir, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void PathTraversal_DotDotEscape_IsBlocked()
    {
        var workDir = Path.Combine(Path.GetTempPath(), "testproject");
        var escapePath = "..\\..\\etc\\passwd";

        var filePath = Path.GetFullPath(Path.Combine(workDir, escapePath));
        var normalizedWorkDir = Path.GetFullPath(workDir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                                + Path.DirectorySeparatorChar;

        Assert.False(filePath.StartsWith(normalizedWorkDir, StringComparison.OrdinalIgnoreCase));
    }
}
