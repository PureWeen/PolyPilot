using PolyPilot.Models;
using PolyPilot.Services;

namespace PolyPilot.Tests;

/// <summary>
/// Tests for QuotaDisplayHelper: level classification, reset countdown,
/// compact summary, and CSS class mapping.
/// </summary>
public class QuotaDisplayTests
{
    // ─── GetQuotaLevel ───

    [Theory]
    [InlineData(100, QuotaLevel.Normal)]
    [InlineData(80, QuotaLevel.Normal)]
    [InlineData(51, QuotaLevel.Normal)]
    [InlineData(50, QuotaLevel.Warning)]
    [InlineData(35, QuotaLevel.Warning)]
    [InlineData(21, QuotaLevel.Warning)]
    [InlineData(20, QuotaLevel.Critical)]
    [InlineData(10, QuotaLevel.Critical)]
    [InlineData(1, QuotaLevel.Critical)]
    [InlineData(0, QuotaLevel.Critical)]
    public void GetQuotaLevel_ReturnsCorrectLevel(int remaining, QuotaLevel expected)
    {
        Assert.Equal(expected, QuotaDisplayHelper.GetQuotaLevel(remaining));
    }

    // ─── GetCssClass ───

    [Fact]
    public void GetCssClass_MapsLevelsToCssStrings()
    {
        Assert.Equal("normal", QuotaDisplayHelper.GetCssClass(QuotaLevel.Normal));
        Assert.Equal("warning", QuotaDisplayHelper.GetCssClass(QuotaLevel.Warning));
        Assert.Equal("critical", QuotaDisplayHelper.GetCssClass(QuotaLevel.Critical));
    }

    // ─── FormatResetCountdown ───

    [Fact]
    public void FormatResetCountdown_NullOrEmpty_ReturnsNull()
    {
        Assert.Null(QuotaDisplayHelper.FormatResetCountdown(null));
        Assert.Null(QuotaDisplayHelper.FormatResetCountdown(""));
        Assert.Null(QuotaDisplayHelper.FormatResetCountdown("  "));
    }

    [Fact]
    public void FormatResetCountdown_InvalidDate_ReturnsNull()
    {
        Assert.Null(QuotaDisplayHelper.FormatResetCountdown("not-a-date"));
    }

    [Fact]
    public void FormatResetCountdown_FutureDate_ShowsDays()
    {
        var futureDate = DateTime.UtcNow.AddDays(5).ToString("yyyy-MM-dd");
        var result = QuotaDisplayHelper.FormatResetCountdown(futureDate);
        Assert.NotNull(result);
        Assert.Contains("Resets in", result);
        Assert.Contains("days", result);
    }

    [Fact]
    public void FormatResetCountdown_Tomorrow_ShowsTomorrow()
    {
        var tomorrow = DateTime.UtcNow.AddHours(25).ToString("yyyy-MM-dd");
        var result = QuotaDisplayHelper.FormatResetCountdown(tomorrow);
        Assert.NotNull(result);
        // Could be "Resets tomorrow" or "Resets in 2 days" depending on time of day
        Assert.StartsWith("Resets", result);
    }

    [Fact]
    public void FormatResetCountdown_PastDate_ReturnsOverdue()
    {
        var pastDate = DateTime.UtcNow.AddDays(-3).ToString("yyyy-MM-dd");
        var result = QuotaDisplayHelper.FormatResetCountdown(pastDate);
        Assert.Equal("Reset overdue", result);
    }

    // ─── FormatCompactSummary ───

    [Fact]
    public void FormatCompactSummary_Unlimited_ReturnsUnlimited()
    {
        var quota = new QuotaInfo(true, 0, 0, 100, null);
        Assert.Equal("Unlimited", QuotaDisplayHelper.FormatCompactSummary(quota));
    }

    [Fact]
    public void FormatCompactSummary_Limited_ShowsRemainingAndPercent()
    {
        var quota = new QuotaInfo(false, 300, 42, 86, "2026-05-01");
        var summary = QuotaDisplayHelper.FormatCompactSummary(quota);
        Assert.Contains("258/300 remaining", summary);
        Assert.Contains("86%", summary);
    }

    [Fact]
    public void FormatCompactSummary_ZeroRemaining()
    {
        var quota = new QuotaInfo(false, 100, 100, 0, "2026-05-01");
        var summary = QuotaDisplayHelper.FormatCompactSummary(quota);
        Assert.Contains("0/100 remaining", summary);
        Assert.Contains("0%", summary);
    }

    // ─── Threshold constants ───

    [Fact]
    public void ThresholdConstants_AreReasonable()
    {
        Assert.Equal(20, QuotaDisplayHelper.CriticalThresholdPercent);
        Assert.Equal(50, QuotaDisplayHelper.WarningThresholdPercent);
        Assert.True(QuotaDisplayHelper.CriticalThresholdPercent < QuotaDisplayHelper.WarningThresholdPercent);
    }

    // ─── QuotaInfo warning scenario ───

    [Fact]
    public void QuotaInfo_BelowCritical_TriggersWarning()
    {
        var quota = new QuotaInfo(false, 300, 255, 15, "2026-05-01");
        var level = QuotaDisplayHelper.GetQuotaLevel(quota.RemainingPercentage);
        Assert.Equal(QuotaLevel.Critical, level);
    }

    [Fact]
    public void QuotaInfo_Unlimited_NeverTriggersWarning()
    {
        var quota = new QuotaInfo(true, 0, 0, 100, null);
        var level = QuotaDisplayHelper.GetQuotaLevel(quota.RemainingPercentage);
        Assert.Equal(QuotaLevel.Normal, level);
    }
}
