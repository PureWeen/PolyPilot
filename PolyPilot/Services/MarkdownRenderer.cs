using System.Text.RegularExpressions;
using Markdig;

namespace PolyPilot.Services;

/// <summary>
/// Shared markdown pipeline configuration used by ChatMessageList and tests.
/// DisableHtml strips raw HTML tags, and SanitizeUrls blocks dangerous URL schemes
/// in Markdig-generated links (javascript:, vbscript:, data:).
/// </summary>
internal static partial class MarkdownRenderer
{
    internal static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions().DisableHtml().Build();

    internal static string ToHtml(string markdown) => SanitizeUrls(Markdown.ToHtml(markdown, Pipeline));

    /// <summary>
    /// Replaces dangerous URL schemes in href/src attributes with a safe blocked prefix.
    /// DisableHtml prevents raw HTML tags but Markdig still generates &lt;a href&gt; and
    /// &lt;img src&gt; from markdown link syntax — those need URL sanitization.
    /// </summary>
    internal static string SanitizeUrls(string html)
        => UnsafeUrlSchemeRegex().Replace(html, "${prefix}x-blocked:");

    [GeneratedRegex(@"(?<prefix>(?:href|src)\s*=\s*"")(?:javascript|vbscript|data)\s*:", RegexOptions.IgnoreCase)]
    private static partial Regex UnsafeUrlSchemeRegex();
}
