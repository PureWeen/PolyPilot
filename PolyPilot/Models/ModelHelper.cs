using System.Text.Json;

namespace PolyPilot.Models;

/// <summary>
/// Normalizes model strings between display names and SDK slugs.
/// The SDK expects slugs like "claude-opus-4.6" but various sources
/// (CLI session events, persisted UI state) may use display names 
/// like "Claude Opus 4.6" or "Claude Opus 4.6 (fast mode)".
/// </summary>
public static class ModelHelper
{
    public static IReadOnlyList<string> FallbackModels { get; } = new[]
    {
        "claude-opus-4.6",
        "claude-opus-4.6-1m",
        "claude-opus-4.6-fast",
        "claude-opus-4.5",
        "claude-sonnet-4.5",
        "claude-sonnet-4",
        "claude-haiku-4.5",
        "gpt-5.4",
        "gpt-5.4-mini",
        "gpt-5.3-codex",
        "gpt-5.2",
        "gpt-5.2-codex",
        "gpt-5.1",
        "gpt-5.1-codex",
        "gpt-5.1-codex-max",
        "gpt-5.1-codex-mini",
        "gpt-5",
        "gpt-5-mini",
        "gpt-4.1",
        "gemini-3-pro-preview",
    };

    /// <summary>
    /// Normalize any model string to its canonical slug form.
    /// Handles display names like "Claude Opus 4.5", "GPT-5.1-Codex", 
    /// "Gemini 3 Pro (Preview)", and already-correct slugs.
    /// </summary>
    public static string NormalizeToSlug(string? model)
    {
        if (string.IsNullOrWhiteSpace(model))
            return "";

        var trimmed = model.Trim();

        // Already a slug (lowercase with hyphens, no spaces)
        if (trimmed == trimmed.ToLowerInvariant() && !trimmed.Contains(' '))
            return trimmed;

        // Strip parenthetical suffixes like "(Preview)", "(fast mode)", "(high)"
        // Handle multiple parenthetical groups, e.g. "(1M Context)(Internal Only)"
        var parenIndex = trimmed.IndexOf('(');
        var baseName = parenIndex > 0 ? trimmed[..parenIndex].Trim() : trimmed;
        // Collect all parenthetical content for suffix matching
        var parenContent = parenIndex > 0 ? trimmed[parenIndex..] : null;

        // Lowercase and replace spaces with hyphens
        var slug = baseName.ToLowerInvariant().Replace(' ', '-');

        // Fix common patterns: "claude-opus" not "claude--opus", etc.
        while (slug.Contains("--"))
            slug = slug.Replace("--", "-");

        // Handle parenthetical content that's part of the model name
        if (!string.IsNullOrEmpty(parenContent))
        {
            var normalizedParen = parenContent.Trim('(', ')').ToLowerInvariant().Replace(' ', '-');
            // Known suffix patterns that are part of the slug
            if (normalizedParen.StartsWith("preview"))
                slug += "-preview";
            else if (normalizedParen.StartsWith("fast-mode") || normalizedParen.StartsWith("fast"))
                slug += "-fast";
            else if (normalizedParen.StartsWith("1m"))
                slug += "-1m";
        }

        return slug;
    }

    /// <summary>
    /// Returns true if the model string looks like a display name rather than a slug.
    /// </summary>
    public static bool IsDisplayName(string? model)
    {
        if (string.IsNullOrWhiteSpace(model)) return false;
        // Display names have uppercase letters or spaces
        return model.Any(char.IsUpper) || model.Contains(' ');
    }

    /// <summary>
    /// Converts a model slug to a human-friendly display name.
    /// If displayNames dictionary is provided and contains the slug, uses the SDK's display name.
    /// Otherwise falls back to algorithmic prettification.
    /// </summary>
    public static string PrettifyModel(string modelId, IReadOnlyDictionary<string, string>? displayNames = null)
    {
        if (string.IsNullOrEmpty(modelId)) return modelId;

        // If we have an SDK-provided display name, prefer it
        if (displayNames != null && displayNames.TryGetValue(modelId, out var sdkName))
            return sdkName;

        // Algorithmic fallback
        var display = modelId
            .Replace("claude-", "Claude ")
            .Replace("gpt-", "GPT-")
            .Replace("gemini-", "Gemini ");
        display = display.Replace("-", " ");
        display = display.Replace("GPT ", "GPT-");
        return string.Join(' ', display.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(s =>
            s.Length > 0 ? char.ToUpper(s[0]) + s[1..] : s));
    }

    internal static string? ExtractLatestModelFromEvents(IEnumerable<string> lines)
    {
        string? latestModel = null;

        foreach (var line in lines)
        {
            var candidate = TryExtractModelFromEventLine(line);
            if (!string.IsNullOrEmpty(candidate))
                latestModel = candidate;
        }

        return latestModel;
    }

    internal static string? TryExtractModelFromEventLine(string? line)
    {
        if (string.IsNullOrWhiteSpace(line))
            return null;

        try
        {
            using var doc = JsonDocument.Parse(line);
            return ExtractModelFromElement(doc.RootElement);
        }
        catch
        {
            return null;
        }
    }

    private static string? ExtractModelFromElement(JsonElement element)
    {
        if (TryGetNormalizedModel(element, out var model))
            return model;

        if (element.ValueKind == JsonValueKind.Object &&
            element.TryGetProperty("data", out var data) &&
            TryGetNormalizedModel(data, out model))
        {
            return model;
        }

        return null;
    }

    private static bool TryGetNormalizedModel(JsonElement element, out string? model)
    {
        model = null;

        if (element.ValueKind != JsonValueKind.Object)
            return false;

        foreach (var property in element.EnumerateObject())
        {
            if (!property.Name.Equals("selectedModel", StringComparison.OrdinalIgnoreCase) &&
                !property.Name.Equals("newModel", StringComparison.OrdinalIgnoreCase) &&
                !property.Name.Equals("model", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (property.Value.ValueKind != JsonValueKind.String)
                continue;

            var normalized = NormalizeToSlug(property.Value.GetString());
            if (string.IsNullOrEmpty(normalized))
                continue;

            model = normalized;
            return true;
        }

        return false;
    }
}
