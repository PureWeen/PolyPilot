namespace PolyPilot.Models;

/// <summary>
/// Normalizes model strings between display names and SDK slugs.
/// The SDK expects slugs like "claude-opus-4.6" but various sources
/// (CLI session events, persisted UI state) may use display names 
/// like "Claude Opus 4.6" or "Claude Opus 4.6 (fast mode)".
/// </summary>
public static class ModelHelper
{
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
        var parenIndex = trimmed.IndexOf('(');
        var baseName = parenIndex > 0 ? trimmed[..parenIndex].Trim() : trimmed;
        var parenContent = parenIndex > 0 ? trimmed[parenIndex..].Trim('(', ')', ' ') : null;

        // Lowercase and replace spaces with hyphens
        var slug = baseName.ToLowerInvariant().Replace(' ', '-');

        // Fix common patterns: "claude-opus" not "claude--opus", etc.
        while (slug.Contains("--"))
            slug = slug.Replace("--", "-");

        // Handle parenthetical content that's part of the model name
        if (!string.IsNullOrEmpty(parenContent))
        {
            var normalizedParen = parenContent.ToLowerInvariant().Replace(' ', '-');
            // Known suffix patterns that are part of the slug
            if (normalizedParen == "preview")
                slug += "-preview";
            else if (normalizedParen == "fast-mode" || normalizedParen == "fast")
                slug += "-fast";
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
}
