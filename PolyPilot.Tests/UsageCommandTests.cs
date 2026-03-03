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
    public void UsageCommand_NoUsageInfo_ShowsTokensOnly()
    {
        var session = new AgentSessionInfo
        {
            Name = "test",
            Model = "gpt-4.1",
            TotalInputTokens = 0,
            TotalOutputTokens = 0,
        };

        var text = FormatUsageOutput(session, null);

        Assert.Contains("**Input tokens:** 0", text);
        Assert.Contains("**Output tokens:** 0", text);
        Assert.DoesNotContain("Model", text);
        Assert.DoesNotContain("Quota", text);
    }

    /// <summary>
    /// Mirrors the formatting logic from Dashboard.razor HandleSlashCommand /usage case.
    /// Kept in sync to validate output format.
    /// </summary>
    private static string FormatUsageOutput(AgentSessionInfo session, SessionUsageInfo? usageInfo)
    {
        var usageLines = new System.Text.StringBuilder();
        var ic = System.Globalization.CultureInfo.InvariantCulture;
        usageLines.AppendLine("**Session Usage**");
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
}
