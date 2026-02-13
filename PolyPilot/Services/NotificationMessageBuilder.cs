using PolyPilot.Models;

namespace PolyPilot.Services;

/// <summary>
/// Builds context-aware notification messages for session events.
/// </summary>
public static class NotificationMessageBuilder
{
    public static (string Title, string Body) BuildMessage(AttentionNeededPayload payload)
    {
        var title = payload.SessionName;
        var body = payload.Reason switch
        {
            AttentionReason.Completed => BuildCompletedMessage(payload.Summary),
            AttentionReason.Error => BuildErrorMessage(payload.Summary),
            AttentionReason.NeedsInteraction => BuildInteractionMessage(payload.Summary),
            AttentionReason.ReadyForMore => BuildReadyMessage(payload.Summary),
            _ => payload.Summary
        };
        
        return (title, body);
    }

    private static string BuildCompletedMessage(string summary)
    {
        if (string.IsNullOrEmpty(summary))
            return "✓ Response complete";
        return $"✓ {Truncate(summary, 80)}";
    }

    private static string BuildErrorMessage(string summary)
    {
        if (string.IsNullOrEmpty(summary))
            return "⚠ An error occurred";
        return $"⚠ {Truncate(summary, 80)}";
    }

    private static string BuildInteractionMessage(string summary)
    {
        if (string.IsNullOrEmpty(summary))
            return "🔔 Needs your input";
        return $"🔔 {Truncate(summary, 80)}";
    }

    private static string BuildReadyMessage(string summary)
    {
        if (string.IsNullOrEmpty(summary))
            return "📋 Ready for your next prompt";
        return $"📋 {Truncate(summary, 80)}";
    }

    private static string Truncate(string text, int maxLength)
    {
        if (string.IsNullOrEmpty(text)) return "";
        text = text.Replace("\n", " ").Replace("\r", "").Trim();
        return text.Length <= maxLength ? text : text[..(maxLength - 1)] + "…";
    }
}
