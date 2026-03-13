using System.Text.RegularExpressions;

namespace PolyPilot.Tests;

/// <summary>
/// Sweeping enforcement tests that scan ALL source CSS and Razor files
/// to ensure font-size values use the type-scale system (var(--type-*))
/// and font-family values use var(--font-mono), var(--font-base), or inherit.
///
/// Any new hardcoded font-size added anywhere will immediately fail these tests.
/// Intentional exceptions are documented in explicit allowlists.
/// </summary>
public class FontSizingEnforcementTests
{
    private static string GetRepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null && !File.Exists(Path.Combine(dir, "PolyPilot.slnx")))
            dir = Directory.GetParent(dir)?.FullName;
        return dir ?? throw new DirectoryNotFoundException("Could not find repo root (PolyPilot.slnx not found)");
    }

    private static string ProjectRoot => Path.Combine(GetRepoRoot(), "PolyPilot");

    /// <summary>
    /// Gets all source CSS files (excludes build artifacts, vendor libraries, and generated bundles).
    /// </summary>
    private static IEnumerable<string> GetSourceCssFiles()
    {
        var dirs = new[]
        {
            Path.Combine(ProjectRoot, "wwwroot"),
            Path.Combine(ProjectRoot, "Components"),
        };

        foreach (var dir in dirs)
        {
            if (!Directory.Exists(dir)) continue;
            foreach (var file in Directory.EnumerateFiles(dir, "*.css", SearchOption.AllDirectories))
            {
                var name = Path.GetFileName(file);
                // Skip build artifacts, generated bundles, and vendor libraries
                if (name.Contains(".styles.css") ||
                    name.Contains(".bundle.scp.css") ||
                    name.Contains(".rz.scp.css") ||
                    name.StartsWith("bootstrap", StringComparison.OrdinalIgnoreCase))
                    continue;
                yield return file;
            }
        }
    }

    /// <summary>
    /// Gets all source Razor files.
    /// </summary>
    private static IEnumerable<string> GetSourceRazorFiles()
    {
        var dir = Path.Combine(ProjectRoot, "Components");
        if (!Directory.Exists(dir)) yield break;
        foreach (var file in Directory.EnumerateFiles(dir, "*.razor", SearchOption.AllDirectories))
            yield return file;
    }

    // ── Allowlist: intentional non-var(--type-*) font-size values ──────────

    /// <summary>
    /// Allowlist for CSS font-size values that intentionally don't use var(--type-*).
    /// Each entry: (filename, value-regex, reason).
    /// </summary>
    private static readonly (string File, string ValuePattern, string Reason)[] CssFontSizeAllowlist =
    {
        // Icons that scale relative to parent text — em is correct here
        ("ChatMessageList.razor.css", @"^0\.6em$", "Icon (.action-chevron) scales with parent text"),
        ("ChatMessageList.razor.css", @"^0\.65em$", "Icon (.toggle-icon) scales with parent text"),
        ("ChatMessageList.razor.css", @"^0\.85em$", "Inline code (.markdown-body code) — standard CSS convention"),
        ("provider-session.css", @"^1\.2em$", "Provider icon scales with context"),

        // Decorative elements beyond the type-scale range
        ("Settings.razor.css", @"^2rem$", "Decorative mode-icon — beyond type-scale range"),

        // Worker child items scale relative to parent — em is correct here
        ("SessionListItem.razor.css", @"^0\.85em$", "Worker child items scale relative to parent text"),
    };

    /// <summary>
    /// Allowlist for Razor inline style font-size values that intentionally don't use var(--type-*).
    /// </summary>
    private static readonly (string File, string ValuePattern, string Reason)[] RazorFontSizeAllowlist =
    {
        // Currently empty — all inline styles should use var(--type-*)
    };

    /// <summary>
    /// Allowlist for font-family values that don't use var(--font-*) or inherit.
    /// </summary>
    private static readonly (string File, string ValuePattern, string Reason)[] FontFamilyAllowlist =
    {
        // Currently empty — all font-family declarations should use variables or inherit
    };

    // ── Sweeping CSS font-size test ───────────────────────────────────────────

    [Fact]
    public void AllCssFiles_FontSizes_UseTypeScaleOrAllowlisted()
    {
        var violations = new List<string>();
        var fontSizePattern = new Regex(@"font-size\s*:\s*([^;!]+?)(?:\s*!important)?\s*;", RegexOptions.IgnoreCase);

        foreach (var file in GetSourceCssFiles())
        {
            var fileName = Path.GetFileName(file);
            var content = File.ReadAllText(file);
            var lines = content.Split('\n');

            for (int i = 0; i < lines.Length; i++)
            {
                foreach (Match match in fontSizePattern.Matches(lines[i]))
                {
                    var value = match.Groups[1].Value.Trim();

                    // Already using type-scale variable — good
                    if (value.Contains("var(--type-"))
                        continue;

                    // Using the root app-font-size variable — this is the scaling mechanism itself
                    if (value.Contains("var(--app-font-size"))
                        continue;

                    // Inheriting from parent — valid
                    if (value.Equals("inherit", StringComparison.OrdinalIgnoreCase))
                        continue;

                    // Check allowlist
                    bool allowed = CssFontSizeAllowlist.Any(a =>
                        fileName.Equals(a.File, StringComparison.OrdinalIgnoreCase) &&
                        Regex.IsMatch(value, a.ValuePattern));

                    if (!allowed)
                    {
                        violations.Add($"  {fileName}:{i + 1} — font-size: {value}");
                    }
                }
            }
        }

        Assert.True(violations.Count == 0,
            $"Found {violations.Count} CSS font-size value(s) not using var(--type-*) and not in the allowlist.\n" +
            $"Either replace with a var(--type-*) variable, or add to the allowlist in FontSizingEnforcementTests.cs with a reason.\n\n" +
            string.Join("\n", violations));
    }

    // ── Razor inline style font-size test ─────────────────────────────────────

    [Fact]
    public void AllRazorFiles_InlineFontSizes_UseTypeScaleOrAllowlisted()
    {
        var violations = new List<string>();
        // Match style="..." attributes containing font-size
        var styleAttrPattern = new Regex(@"style\s*=\s*""([^""]*font-size[^""]*)""", RegexOptions.IgnoreCase);
        var fontSizeInStyle = new Regex(@"font-size\s*:\s*([^;""]+)", RegexOptions.IgnoreCase);

        foreach (var file in GetSourceRazorFiles())
        {
            var fileName = Path.GetFileName(file);
            var lines = File.ReadAllLines(file);

            for (int i = 0; i < lines.Length; i++)
            {
                foreach (Match styleMatch in styleAttrPattern.Matches(lines[i]))
                {
                    var styleContent = styleMatch.Groups[1].Value;
                    foreach (Match fsMatch in fontSizeInStyle.Matches(styleContent))
                    {
                        var value = fsMatch.Groups[1].Value.Trim().TrimEnd(';', '"');

                        if (value.Contains("var(--type-"))
                            continue;

                        bool allowed = RazorFontSizeAllowlist.Any(a =>
                            fileName.Equals(a.File, StringComparison.OrdinalIgnoreCase) &&
                            Regex.IsMatch(value, a.ValuePattern));

                        if (!allowed)
                        {
                            violations.Add($"  {fileName}:{i + 1} — inline font-size: {value}");
                        }
                    }
                }
            }
        }

        Assert.True(violations.Count == 0,
            $"Found {violations.Count} Razor inline font-size value(s) not using var(--type-*).\n" +
            $"Either replace with a var(--type-*) variable, or add to the allowlist in FontSizingEnforcementTests.cs with a reason.\n\n" +
            string.Join("\n", violations));
    }

    // ── Font-family enforcement test ──────────────────────────────────────────

    [Fact]
    public void AllCssFiles_FontFamilies_UseVariablesOrInherit()
    {
        var violations = new List<string>();
        var fontFamilyPattern = new Regex(@"font-family\s*:\s*([^;]+);", RegexOptions.IgnoreCase);

        foreach (var file in GetSourceCssFiles())
        {
            var fileName = Path.GetFileName(file);
            var content = File.ReadAllText(file);
            var lines = content.Split('\n');

            for (int i = 0; i < lines.Length; i++)
            {
                foreach (Match match in fontFamilyPattern.Matches(lines[i]))
                {
                    var value = match.Groups[1].Value.Trim();

                    // Allowed: var(--font-mono), var(--font-base), inherit
                    if (value.Contains("var(--font-mono") ||
                        value.Contains("var(--font-base") ||
                        value.Equals("inherit", StringComparison.OrdinalIgnoreCase))
                        continue;

                    bool allowed = FontFamilyAllowlist.Any(a =>
                        fileName.Equals(a.File, StringComparison.OrdinalIgnoreCase) &&
                        Regex.IsMatch(value, a.ValuePattern));

                    if (!allowed)
                    {
                        violations.Add($"  {fileName}:{i + 1} — font-family: {value}");
                    }
                }
            }
        }

        Assert.True(violations.Count == 0,
            $"Found {violations.Count} CSS font-family value(s) not using var(--font-mono), var(--font-base), or inherit.\n" +
            $"Either replace with a CSS variable, or add to the allowlist in FontSizingEnforcementTests.cs with a reason.\n\n" +
            string.Join("\n", violations));
    }
}
