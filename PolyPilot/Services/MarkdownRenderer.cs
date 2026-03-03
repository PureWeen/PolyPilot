using Markdig;

namespace PolyPilot.Services;

/// <summary>
/// Shared markdown pipeline configuration used by ChatMessageList and tests.
/// </summary>
internal static class MarkdownRenderer
{
    internal static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions().DisableHtml().Build();

    internal static string ToHtml(string markdown) => Markdown.ToHtml(markdown, Pipeline);
}
