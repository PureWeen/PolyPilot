using System.Text;
using System.Text.RegularExpressions;

namespace PolyPilot.Services;

/// <summary>
/// Converts GitHub issue/PR references in HTML to clickable links.
/// Handles both fully-qualified (owner/repo#123) and bare (#123) references.
/// </summary>
public static class GitHubReferenceLinker
{
    // Allowlist for owner/repo path segments — prevents HTML attribute injection
    private static readonly Regex SafeOwnerRepoRegex = new(
        @"^[A-Za-z0-9._-]+/[A-Za-z0-9._-]+$",
        RegexOptions.Compiled);

    // Tags whose content should NOT be processed
    private static readonly HashSet<string> SkipTags = new(StringComparer.OrdinalIgnoreCase)
        { "a", "code", "pre", "script", "style" };

    // Matches an HTML tag (opening, closing, or self-closing)
    private static readonly Regex TagRegex = new(
        @"<(/?)(\w+)([^>]*)>",
        RegexOptions.Compiled);

    /// <summary>
    /// Converts GitHub references in HTML to clickable links.
    /// </summary>
    /// <param name="html">HTML string to process.</param>
    /// <param name="repoUrl">
    /// Optional GitHub repo URL (e.g., "https://github.com/owner/repo") for resolving bare #123 refs.
    /// When null, only fully-qualified owner/repo#123 refs are linked.
    /// </param>
    public static string LinkifyReferences(string html, string? repoUrl = null)
    {
        if (string.IsNullOrEmpty(html)) return html;

        var ownerRepo = ExtractOwnerRepo(repoUrl);
        var sb = new StringBuilder(html.Length + 64);
        var skipDepth = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        int pos = 0;

        foreach (Match tagMatch in TagRegex.Matches(html))
        {
            // Process text before this tag
            if (tagMatch.Index > pos)
            {
                var text = html.Substring(pos, tagMatch.Index - pos);
                if (IsInsideSkipTag(skipDepth))
                    sb.Append(text);
                else
                    sb.Append(LinkifyText(text, ownerRepo));
            }

            // Append the tag itself
            sb.Append(tagMatch.Value);

            // Track skip tag depth
            var tagName = tagMatch.Groups[2].Value;
            if (SkipTags.Contains(tagName))
            {
                var isClosing = tagMatch.Groups[1].Value == "/";
                var isSelfClosing = tagMatch.Groups[3].Value.TrimEnd().EndsWith("/");

                if (!isClosing && !isSelfClosing)
                {
                    skipDepth.TryGetValue(tagName, out var depth);
                    skipDepth[tagName] = depth + 1;
                }
                else if (isClosing)
                {
                    skipDepth.TryGetValue(tagName, out var depth);
                    if (depth > 0) skipDepth[tagName] = depth - 1;
                }
            }

            pos = tagMatch.Index + tagMatch.Length;
        }

        // Process remaining text after last tag
        if (pos < html.Length)
        {
            var text = html.Substring(pos);
            if (IsInsideSkipTag(skipDepth))
                sb.Append(text);
            else
                sb.Append(LinkifyText(text, ownerRepo));
        }

        return sb.ToString();
    }

    // Combined regex: qualified ref first (higher priority), then bare ref.
    // Using alternation ensures each position is matched at most once.
    private static readonly Regex CombinedRefRegex = new(
        @"([A-Za-z0-9][A-Za-z0-9._-]*/[A-Za-z0-9][A-Za-z0-9._-]*)#(\d+)|#(\d+)",
        RegexOptions.Compiled);

    /// <summary>
    /// Linkifies GitHub references in a plain text segment (not inside skip tags).
    /// Uses a single-pass regex to avoid re-matching refs inside already-created links.
    /// </summary>
    internal static string LinkifyText(string text, string? ownerRepo)
    {
        return CombinedRefRegex.Replace(text, match =>
        {
            var startIdx = match.Index;

            // Skip if preceded by & (HTML entities like &#123;)
            if (startIdx > 0 && text[startIdx - 1] == '&')
                return match.Value;

            if (match.Groups[1].Success)
            {
                // Qualified ref: owner/repo#123
                var refOwnerRepo = match.Groups[1].Value;
                var number = match.Groups[2].Value;

                // Don't match file paths with double dots or trailing dots
                if (refOwnerRepo.Contains("..") || refOwnerRepo.EndsWith("."))
                    return match.Value;

                var url = $"https://github.com/{refOwnerRepo}/issues/{number}";
                return $"<a href=\"{url}\" target=\"_blank\" rel=\"noopener\">{match.Value}</a>";
            }
            else if (match.Groups[3].Success && ownerRepo != null)
            {
                // Bare ref: #123
                var number = match.Groups[3].Value;

                // Skip if preceded by a word character or / (URL fragments, file paths)
                if (startIdx > 0 && (char.IsLetterOrDigit(text[startIdx - 1]) || text[startIdx - 1] == '/' || text[startIdx - 1] == '.'))
                    return match.Value;

                var url = $"https://github.com/{ownerRepo}/issues/{number}";
                return $"<a href=\"{url}\" target=\"_blank\" rel=\"noopener\">{match.Value}</a>";
            }

            return match.Value;
        });
    }

    /// <summary>
    /// Extracts "owner/repo" from a GitHub URL.
    /// Handles formats: https://github.com/owner/repo, https://github.com/owner/repo.git
    /// </summary>
    internal static string? ExtractOwnerRepo(string? repoUrl)
    {
        if (string.IsNullOrEmpty(repoUrl)) return null;

        try
        {
            // Handle git@ SSH URLs
            if (repoUrl.StartsWith("git@", StringComparison.OrdinalIgnoreCase))
            {
                var colonIdx = repoUrl.IndexOf(':');
                if (colonIdx > 0)
                {
                    var path = repoUrl.Substring(colonIdx + 1);
                    path = path.TrimEnd('/');
                    if (path.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
                        path = path[..^4];
                    if (!SafeOwnerRepoRegex.IsMatch(path)) return null;
                    return path;
                }
            }

            // Handle HTTPS URLs
            if (Uri.TryCreate(repoUrl, UriKind.Absolute, out var uri))
            {
                var path = uri.AbsolutePath.Trim('/');
                if (path.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
                    path = path[..^4];

                var parts = path.Split('/');
                if (parts.Length >= 2)
                {
                    var result = $"{parts[0]}/{parts[1]}";
                    if (!SafeOwnerRepoRegex.IsMatch(result)) return null;
                    return result;
                }
            }
        }
        catch { /* Malformed URL — return null */ }

        return null;
    }

    private static bool IsInsideSkipTag(Dictionary<string, int> skipDepth)
    {
        foreach (var kvp in skipDepth)
            if (kvp.Value > 0) return true;
        return false;
    }
}
