using System.Text;

namespace PolyPilot.Models;

public class DiffFile
{
    public string FileName { get; set; } = "";
    public string? OldFileName { get; set; }
    public bool IsNew { get; set; }
    public bool IsDeleted { get; set; }
    public bool IsRenamed { get; set; }
    public List<DiffHunk> Hunks { get; set; } = new();
}

public class DiffHunk
{
    public int OldStart { get; set; }
    public int NewStart { get; set; }
    public string? Header { get; set; }
    public List<DiffLine> Lines { get; set; } = new();
}

public enum DiffLineType { Context, Added, Removed }

public class DiffLine
{
    public DiffLineType Type { get; set; }
    public string Content { get; set; } = "";
    public int? OldLineNo { get; set; }
    public int? NewLineNo { get; set; }
}

public static class DiffParser
{
    public static bool IsPlainTextViewTool(string? toolName) =>
        string.Equals(toolName, "view", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(toolName, "read", StringComparison.OrdinalIgnoreCase);

    public static bool ShouldRenderDiffView(string? text, string? toolName)
    {
        if (TryExtractNumberedViewOutput(text, out _))
            return false;

        // `view`/Read tool results should stay as plain file reads even if the
        // content happens to contain unified-diff markers.
        if (IsPlainTextViewTool(toolName))
            return false;

        return LooksLikeUnifiedDiff(text);
    }

    public static bool TryExtractNumberedViewOutput(string? text, out string plainText)
    {
        plainText = "";
        if (!LooksLikeUnifiedDiff(text))
            return false;

        var files = Parse(text!);
        if (files.Count == 0)
            return false;

        // The broken "Read" payloads are synthetic no-op self-diffs that contain
        // only context lines (no real additions/removals). Real diffs should keep
        // using DiffView.
        var allLines = files.SelectMany(f => f.Hunks).SelectMany(h => h.Lines).ToList();
        if (allLines.Count == 0 || allLines.Any(l => l.Type != DiffLineType.Context))
            return false;

        var sb = new StringBuilder();
        foreach (var file in files)
        {
            foreach (var hunk in file.Hunks)
            {
                foreach (var line in hunk.Lines)
                {
                    var lineNo = line.NewLineNo ?? line.OldLineNo;
                    if (lineNo.HasValue)
                        sb.Append(lineNo.Value).Append(". ");

                    sb.AppendLine(line.Content);
                }
            }
        }

        plainText = sb.ToString().TrimEnd();
        return !string.IsNullOrWhiteSpace(plainText);
    }

    public static bool LooksLikeUnifiedDiff(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;

        var hasOldFileMarker =
            text.StartsWith("--- ", StringComparison.Ordinal) ||
            text.Contains("\n--- ", StringComparison.Ordinal) ||
            text.Contains("\r\n--- ", StringComparison.Ordinal);
        var hasNewFileMarker =
            text.StartsWith("+++ ", StringComparison.Ordinal) ||
            text.Contains("\n+++ ", StringComparison.Ordinal) ||
            text.Contains("\r\n+++ ", StringComparison.Ordinal);
        var hasHunkMarker =
            text.StartsWith("@@", StringComparison.Ordinal) ||
            text.Contains("\n@@", StringComparison.Ordinal) ||
            text.Contains("\r\n@@", StringComparison.Ordinal);

        return hasOldFileMarker && hasNewFileMarker && hasHunkMarker;
    }

    public static List<DiffFile> Parse(string unifiedDiff)
    {
        var files = new List<DiffFile>();
        if (string.IsNullOrWhiteSpace(unifiedDiff)) return files;

        var lines = unifiedDiff.Split('\n');
        DiffFile? current = null;
        DiffHunk? hunk = null;
        int oldLine = 0, newLine = 0;

        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i].TrimEnd('\r');

            if (line.StartsWith("diff --git"))
            {
                current = new DiffFile();
                files.Add(current);
                hunk = null;
                // Extract filename from "diff --git a/path b/path"
                var parts = line.Split(" b/", 2);
                if (parts.Length == 2)
                    current.FileName = parts[1];
                continue;
            }

            // Handle standard unified diffs (no "diff --git" prefix) and treat
            // each fresh ---/+++ pair followed by a hunk header as a file boundary.
            if (line.StartsWith("--- ", StringComparison.Ordinal) &&
                i + 2 < lines.Length)
            {
                var nextLine = lines[i + 1].TrimEnd('\r');
                var afterHeader = lines[i + 2].TrimEnd('\r');
                if (nextLine.StartsWith("+++ ", StringComparison.Ordinal) &&
                    afterHeader.StartsWith("@@", StringComparison.Ordinal))
                {
                    if (current == null || current.Hunks.Count > 0)
                    {
                        current = new DiffFile();
                        files.Add(current);
                    }

                    hunk = null;

                    var oldName = line[4..].Trim();
                    var newName = nextLine[4..].Trim();
                    if (oldName.StartsWith("a/")) oldName = oldName[2..];
                    if (newName.StartsWith("b/")) newName = newName[2..];

                    if (newName == "/dev/null")
                    {
                        current.IsDeleted = true;
                        current.FileName = oldName;
                    }
                    else if (oldName == "/dev/null")
                    {
                        current.IsNew = true;
                        current.FileName = newName;
                    }
                    else
                    {
                        current.FileName = newName;
                        if (oldName != newName)
                            current.OldFileName = oldName;
                    }

                    i++; // skip the +++ line
                    continue;
                }
            }

            if (current == null) continue;

            if (line.StartsWith("new file"))
            {
                current.IsNew = true;
                continue;
            }
            if (line.StartsWith("deleted file"))
            {
                current.IsDeleted = true;
                continue;
            }
            if (line.StartsWith("rename from"))
            {
                current.IsRenamed = true;
                current.OldFileName = line[12..];
                continue;
            }
            if (line.StartsWith("rename to"))
            {
                current.FileName = line[10..];
                continue;
            }
            if (line.StartsWith("index ", StringComparison.Ordinal))
                continue;

            if (line.StartsWith("@@"))
            {
                hunk = ParseHunkHeader(line);
                current.Hunks.Add(hunk);
                oldLine = hunk.OldStart;
                newLine = hunk.NewStart;
                continue;
            }

            if (hunk == null) continue;

            if (line.StartsWith("-"))
            {
                hunk.Lines.Add(new DiffLine
                {
                    Type = DiffLineType.Removed,
                    Content = line.Length > 1 ? line[1..] : "",
                    OldLineNo = oldLine++
                });
            }
            else if (line.StartsWith("+"))
            {
                hunk.Lines.Add(new DiffLine
                {
                    Type = DiffLineType.Added,
                    Content = line.Length > 1 ? line[1..] : "",
                    NewLineNo = newLine++
                });
            }
            else if (line.StartsWith(" ") || line == "")
            {
                var content = line.Length > 1 ? line[1..] : (line == " " ? "" : line);
                hunk.Lines.Add(new DiffLine
                {
                    Type = DiffLineType.Context,
                    Content = content,
                    OldLineNo = oldLine++,
                    NewLineNo = newLine++
                });
            }
        }

        return files;
    }

    private static DiffHunk ParseHunkHeader(string line)
    {
        // @@ -oldStart,oldCount +newStart,newCount @@ optional header
        var hunk = new DiffHunk();
        var atEnd = line.IndexOf("@@", 2);
        if (atEnd > 0)
        {
            var range = line[3..atEnd].Trim();
            hunk.Header = line.Length > atEnd + 3 ? line[(atEnd + 3)..] : null;

            var parts = range.Split(' ');
            foreach (var p in parts)
            {
                if (p.StartsWith("-"))
                {
                    var nums = p[1..].Split(',');
                    if (int.TryParse(nums[0], out var s)) hunk.OldStart = s;
                }
                else if (p.StartsWith("+"))
                {
                    var nums = p[1..].Split(',');
                    if (int.TryParse(nums[0], out var s)) hunk.NewStart = s;
                }
            }
        }
        return hunk;
    }

    /// <summary>
    /// Pairs removed/added lines side-by-side for 2-pane rendering.
    /// Returns rows where each row has (left, right) — either or both may be null.
    /// </summary>
    public static List<(DiffLine? Left, DiffLine? Right)> PairLines(DiffHunk hunk)
    {
        var rows = new List<(DiffLine? Left, DiffLine? Right)>();
        var lines = hunk.Lines;
        int i = 0;

        while (i < lines.Count)
        {
            if (lines[i].Type == DiffLineType.Context)
            {
                rows.Add((lines[i], lines[i]));
                i++;
            }
            else
            {
                // Collect consecutive removed, then added
                var removed = new List<DiffLine>();
                while (i < lines.Count && lines[i].Type == DiffLineType.Removed)
                    removed.Add(lines[i++]);
                var added = new List<DiffLine>();
                while (i < lines.Count && lines[i].Type == DiffLineType.Added)
                    added.Add(lines[i++]);

                int max = Math.Max(removed.Count, added.Count);
                for (int j = 0; j < max; j++)
                {
                    rows.Add((
                        j < removed.Count ? removed[j] : null,
                        j < added.Count ? added[j] : null
                    ));
                }
            }
        }

        return rows;
    }
}
