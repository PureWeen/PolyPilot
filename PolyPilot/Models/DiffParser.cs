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
            var line = lines[i];

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
            if (line.StartsWith("---") || line.StartsWith("+++") || line.StartsWith("index "))
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
