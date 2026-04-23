using System.Globalization;

namespace PolyPilot.Models;

/// <summary>
/// Helper methods for displaying quota information in the UI.
/// </summary>
public static class QuotaDisplayHelper
{
    /// <summary>Threshold below which quota is considered critically low.</summary>
    public const int CriticalThresholdPercent = 20;

    /// <summary>Threshold below which quota is considered low but not critical.</summary>
    public const int WarningThresholdPercent = 50;

    /// <summary>
    /// Determines the visual severity level for a given remaining percentage.
    /// </summary>
    public static QuotaLevel GetQuotaLevel(int remainingPercentage) =>
        remainingPercentage switch
        {
            <= CriticalThresholdPercent => QuotaLevel.Critical,
            <= WarningThresholdPercent => QuotaLevel.Warning,
            _ => QuotaLevel.Normal,
        };

    /// <summary>
    /// Computes a human-friendly "Resets in X days" string from a reset date.
    /// Returns null if the date cannot be parsed.
    /// </summary>
    public static string? FormatResetCountdown(string? resetDate)
    {
        if (string.IsNullOrWhiteSpace(resetDate))
            return null;

        if (!DateTime.TryParse(resetDate, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed))
            return null;

        var days = (int)Math.Ceiling((parsed.ToUniversalTime() - DateTime.UtcNow).TotalDays);
        return days switch
        {
            < 0 => "Reset overdue",
            0 => "Resets today",
            1 => "Resets tomorrow",
            _ => $"Resets in {days} days",
        };
    }

    /// <summary>
    /// Formats the quota as a compact summary string (e.g., "258/300 · 86%").
    /// </summary>
    public static string FormatCompactSummary(Services.QuotaInfo quota)
    {
        if (quota.IsUnlimited)
            return "Unlimited";

        var remaining = quota.EntitlementRequests - quota.UsedRequests;
        return $"{remaining}/{quota.EntitlementRequests} remaining · {quota.RemainingPercentage}%";
    }

    /// <summary>
    /// Returns the CSS class suffix for the quota level (e.g., "critical", "warning", "normal").
    /// </summary>
    public static string GetCssClass(QuotaLevel level) =>
        level switch
        {
            QuotaLevel.Critical => "critical",
            QuotaLevel.Warning => "warning",
            _ => "normal",
        };
}

public enum QuotaLevel
{
    Normal,
    Warning,
    Critical,
}
