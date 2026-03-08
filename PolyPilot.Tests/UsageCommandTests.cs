using PolyPilot.Models;
using PolyPilot.Services;

namespace PolyPilot.Tests;

/// <summary>
/// Tests for the /usage slash command output formatting.
/// Since the command lives in a Razor component, these tests validate
/// the data models and formatting logic that the command depends on.
/// </summary>
public class UsageCommandTests
{
    [Fact]
    public void SessionUsageInfo_BasicTokens_Populated()
    {
        var info = new SessionUsageInfo("gpt-4.1", 500, 8000, 1200, 300);

        Assert.Equal("gpt-4.1", info.Model);
        Assert.Equal(500, info.CurrentTokens);
        Assert.Equal(8000, info.TokenLimit);
        Assert.Equal(1200, info.InputTokens);
        Assert.Equal(300, info.OutputTokens);
        Assert.Null(info.PremiumQuota);
    }

    [Fact]
    public void SessionUsageInfo_WithQuota_Populated()
    {
        var quota = new QuotaInfo(false, 300, 42, 86, "2026-04-01");
        var info = new SessionUsageInfo("gpt-4.1", null, null, 100, 50, quota);

        Assert.NotNull(info.PremiumQuota);
        Assert.False(info.PremiumQuota!.IsUnlimited);
        Assert.Equal(300, info.PremiumQuota.EntitlementRequests);
        Assert.Equal(42, info.PremiumQuota.UsedRequests);
        Assert.Equal(86, info.PremiumQuota.RemainingPercentage);
        Assert.Equal("2026-04-01", info.PremiumQuota.ResetDate);
    }

    [Fact]
    public void SessionUsageInfo_UnlimitedQuota()
    {
        var quota = new QuotaInfo(true, 0, 0, 100, null);
        var info = new SessionUsageInfo(null, null, null, null, null, quota);

        Assert.True(info.PremiumQuota!.IsUnlimited);
    }

    [Fact]
    public void AgentSessionInfo_TokenAccumulation()
    {
        var session = new AgentSessionInfo { Name = "test", Model = "gpt-4.1" };
        Assert.Equal(0, session.TotalInputTokens);
        Assert.Equal(0, session.TotalOutputTokens);
        Assert.Null(session.ContextCurrentTokens);
        Assert.Null(session.ContextTokenLimit);

        session.TotalInputTokens += 500;
        session.TotalOutputTokens += 200;
        session.ContextCurrentTokens = 700;
        session.ContextTokenLimit = 8000;

        Assert.Equal(500, session.TotalInputTokens);
        Assert.Equal(200, session.TotalOutputTokens);
        Assert.Equal(700, session.ContextCurrentTokens);
        Assert.Equal(8000, session.ContextTokenLimit);
    }

    [Fact]
    public void AgentSessionInfo_PremiumRequestsUsed_DefaultsToZero()
    {
        var session = new AgentSessionInfo { Name = "test", Model = "gpt-4.1" };
        Assert.Equal(0, session.PremiumRequestsUsed);
    }

    [Fact]
    public void AgentSessionInfo_TotalApiTimeSeconds_DefaultsToZero()
    {
        var session = new AgentSessionInfo { Name = "test", Model = "gpt-4.1" };
        Assert.Equal(0.0, session.TotalApiTimeSeconds);
    }

    [Fact]
    public void AgentSessionInfo_PremiumRequestsUsed_Accumulates()
    {
        var session = new AgentSessionInfo { Name = "test", Model = "gpt-4.1" };
        session.PremiumRequestsUsed++;
        session.PremiumRequestsUsed++;
        session.PremiumRequestsUsed++;
        Assert.Equal(3, session.PremiumRequestsUsed);
    }

    [Fact]
    public void AgentSessionInfo_TotalApiTimeSeconds_Accumulates()
    {
        var session = new AgentSessionInfo { Name = "test", Model = "gpt-4.1" };
        session.TotalApiTimeSeconds += 5.5;
        session.TotalApiTimeSeconds += 3.2;
        Assert.Equal(8.7, session.TotalApiTimeSeconds, 1);
    }

    [Fact]
    public void UsageCommand_FormatsBasicTokens()
    {
        // Simulate the formatting logic from /usage command
        var session = new AgentSessionInfo
        {
            Name = "test",
            Model = "gpt-4.1",
            TotalInputTokens = 1234,
            TotalOutputTokens = 567,
        };

        var text = FormatUsageOutput(session, null);

        Assert.Contains("**Session Usage**", text);
        Assert.Contains("**Total usage est:** 0 Premium requests", text);
        Assert.Contains("**API time spent:** 0s", text);
        Assert.Contains("**Total session time:**", text);
        Assert.Contains("**Input tokens:** 1,234", text);
        Assert.Contains("**Output tokens:** 567", text);
        Assert.DoesNotContain("Context window", text);
        Assert.DoesNotContain("Premium Quota", text);
    }

    [Fact]
    public void UsageCommand_FormatsContextWindow()
    {
        var session = new AgentSessionInfo
        {
            Name = "test",
            Model = "gpt-4.1",
            TotalInputTokens = 0,
            TotalOutputTokens = 0,
            ContextCurrentTokens = 4500,
            ContextTokenLimit = 128000,
        };

        var text = FormatUsageOutput(session, null);

        Assert.Contains("**Context window:** 4,500 / 128,000", text);
    }

    [Fact]
    public void UsageCommand_FormatsModelAndQuota()
    {
        var session = new AgentSessionInfo
        {
            Name = "test",
            Model = "gpt-4.1",
            TotalInputTokens = 100,
            TotalOutputTokens = 50,
        };
        var quota = new QuotaInfo(false, 300, 42, 86, "2026-04-01");
        var usageInfo = new SessionUsageInfo("gpt-4.1", null, null, 100, 50, quota);

        var text = FormatUsageOutput(session, usageInfo);

        Assert.Contains("**Model:** gpt-4.1", text);
        Assert.Contains("**Premium Quota**", text);
        Assert.Contains("**Used:** 42 / 300", text);
        Assert.Contains("**Remaining:** 86%", text);
        Assert.Contains("**Resets:** 2026-04-01", text);
    }

    [Fact]
    public void UsageCommand_FormatsUnlimitedQuota()
    {
        var session = new AgentSessionInfo
        {
            Name = "test",
            Model = "gpt-4.1",
            TotalInputTokens = 0,
            TotalOutputTokens = 0,
        };
        var quota = new QuotaInfo(true, 0, 0, 100, null);
        var usageInfo = new SessionUsageInfo("claude-sonnet-4", null, null, null, null, quota);

        var text = FormatUsageOutput(session, usageInfo);

        Assert.Contains("**Model:** claude-sonnet-4", text);
        Assert.Contains("Unlimited entitlement", text);
        Assert.DoesNotContain("**Used:**", text);
        Assert.DoesNotContain("**Resets:**", text);
    }

    [Fact]
    public void UsageCommand_NoUsageInfo_ShowsCliStyleFields()
    {
        var session = new AgentSessionInfo
        {
            Name = "test",
            Model = "gpt-4.1",
            TotalInputTokens = 0,
            TotalOutputTokens = 0,
        };

        var text = FormatUsageOutput(session, null);

        Assert.Contains("**Total usage est:** 0 Premium requests", text);
        Assert.Contains("**API time spent:** 0s", text);
        Assert.Contains("**Total session time:**", text);
        Assert.Contains("**Input tokens:** 0", text);
        Assert.Contains("**Output tokens:** 0", text);
        Assert.DoesNotContain("Model", text);
        Assert.DoesNotContain("Quota", text);
    }

    [Fact]
    public void UsageCommand_ShowsPremiumRequests()
    {
        var session = new AgentSessionInfo
        {
            Name = "test",
            Model = "gpt-4.1",
            TotalInputTokens = 500,
            TotalOutputTokens = 200,
            PremiumRequestsUsed = 5,
        };

        var text = FormatUsageOutput(session, null);

        Assert.Contains("**Total usage est:** 5 Premium requests", text);
    }

    [Fact]
    public void UsageCommand_ShowsApiTime()
    {
        var session = new AgentSessionInfo
        {
            Name = "test",
            Model = "gpt-4.1",
            TotalInputTokens = 0,
            TotalOutputTokens = 0,
            TotalApiTimeSeconds = 45.0,
        };

        var text = FormatUsageOutput(session, null);

        Assert.Contains("**API time spent:** 45s", text);
    }

    [Fact]
    public void UsageCommand_ShowsApiTimeMinutes()
    {
        var session = new AgentSessionInfo
        {
            Name = "test",
            Model = "gpt-4.1",
            TotalInputTokens = 0,
            TotalOutputTokens = 0,
            TotalApiTimeSeconds = 125.0,
        };

        var text = FormatUsageOutput(session, null);

        Assert.Contains("**API time spent:** 2m 5s", text);
    }

    [Fact]
    public void FormatDurationSeconds_UnderMinute()
    {
        Assert.Equal("0s", FormatDuration(0));
        Assert.Equal("5s", FormatDuration(5.3));
        Assert.Equal("59s", FormatDuration(59.9));
    }

    [Fact]
    public void FormatDurationSeconds_MinutesAndSeconds()
    {
        Assert.Equal("1m", FormatDuration(60));
        Assert.Equal("1m 30s", FormatDuration(90));
        Assert.Equal("2m 5s", FormatDuration(125));
    }

    [Fact]
    public void FormatDurationSeconds_LargeDuration()
    {
        Assert.Equal("60m", FormatDuration(3600));
        Assert.Equal("61m 1s", FormatDuration(3661));
    }

    /// <summary>
    /// Mirrors the formatting logic from Dashboard.razor HandleSlashCommand /usage case.
    /// Kept in sync to validate output format.
    /// </summary>
    private static string FormatUsageOutput(AgentSessionInfo session, SessionUsageInfo? usageInfo)
    {
        var usageLines = new System.Text.StringBuilder();
        var ic = System.Globalization.CultureInfo.InvariantCulture;
        var sessionTime = (DateTime.UtcNow - session.CreatedAt.ToUniversalTime()).TotalSeconds;
        usageLines.AppendLine("**Session Usage**");
        usageLines.AppendLine($"- **Total usage est:** {session.PremiumRequestsUsed} Premium requests");
        usageLines.AppendLine($"- **API time spent:** {FormatDuration(session.TotalApiTimeSeconds)}");
        usageLines.AppendLine($"- **Total session time:** {FormatDuration(sessionTime)}");
        usageLines.AppendLine($"- **Input tokens:** {session.TotalInputTokens.ToString("N0", ic)}");
        usageLines.AppendLine($"- **Output tokens:** {session.TotalOutputTokens.ToString("N0", ic)}");
        if (session.ContextCurrentTokens.HasValue || session.ContextTokenLimit.HasValue)
        {
            var ctx = session.ContextCurrentTokens?.ToString("N0", ic) ?? "—";
            var lim = session.ContextTokenLimit?.ToString("N0", ic) ?? "—";
            usageLines.AppendLine($"- **Context window:** {ctx} / {lim}");
        }
        if (usageInfo != null)
        {
            if (!string.IsNullOrEmpty(usageInfo.Model))
                usageLines.AppendLine($"- **Model:** {usageInfo.Model}");
            if (usageInfo.PremiumQuota is { } quota)
            {
                usageLines.AppendLine();
                usageLines.AppendLine("**Premium Quota**");
                if (quota.IsUnlimited)
                {
                    usageLines.AppendLine("- Unlimited entitlement");
                }
                else
                {
                    usageLines.AppendLine($"- **Used:** {quota.UsedRequests.ToString("N0", ic)} / {quota.EntitlementRequests.ToString("N0", ic)}");
                    usageLines.AppendLine($"- **Remaining:** {quota.RemainingPercentage}%");
                }
                if (!string.IsNullOrEmpty(quota.ResetDate))
                    usageLines.AppendLine($"- **Resets:** {quota.ResetDate}");
            }
        }
        return usageLines.ToString().TrimEnd();
    }

    private static string FormatDuration(double totalSeconds)
    {
        if (totalSeconds < 60) return $"{(int)totalSeconds}s";
        var minutes = (int)(totalSeconds / 60);
        var seconds = (int)(totalSeconds % 60);
        return seconds > 0 ? $"{minutes}m {seconds}s" : $"{minutes}m";
    }
}
