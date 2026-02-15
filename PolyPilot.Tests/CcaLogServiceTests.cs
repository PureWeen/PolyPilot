using PolyPilot.Models;
using PolyPilot.Services;

namespace PolyPilot.Tests;

public class CcaLogServiceTests
{
    [Fact]
    public void ParseAgentConversation_StripsBoilerplate()
    {
        var log = string.Join("\n", Enumerable.Range(0, 180).Select(i =>
            $"2026-02-15T16:15:47.{i:D7}Z boilerplate line {i}")) +
            "\n2026-02-15T16:16:01.4175709Z Processing requests...\n" +
            "2026-02-15T16:16:13.6839997Z copilot: I'll start by exploring.\n" +
            "2026-02-15T16:16:20.0250471Z function:\n" +
            "2026-02-15T16:16:20.0254876Z   name: glob\n";

        var result = CcaLogService.ParseAgentConversation(log);

        Assert.Contains("Processing requests", result);
        Assert.Contains("copilot: I'll start by exploring.", result);
        Assert.Contains("name: glob", result);
        // Boilerplate should be stripped
        Assert.DoesNotContain("boilerplate line 0", result);
        Assert.DoesNotContain("boilerplate line 100", result);
    }

    [Fact]
    public void ParseAgentConversation_StripsTimestamps()
    {
        var log = "2026-02-15T16:16:01.4175709Z Processing requests...\n" +
                  "2026-02-15T16:16:13.6839997Z copilot: Hello world\n";

        var result = CcaLogService.ParseAgentConversation(log);

        Assert.Contains("Processing requests...", result);
        Assert.Contains("copilot: Hello world", result);
        Assert.DoesNotContain("2026-02-15T", result);
    }

    [Fact]
    public void ParseAgentConversation_SkipsGitHubActionsGroupMarkers()
    {
        var log = "2026-02-15T16:16:01.0Z Processing requests...\n" +
                  "2026-02-15T16:16:02.0Z ##[group]Run some command\n" +
                  "2026-02-15T16:16:03.0Z copilot: Working on it\n" +
                  "2026-02-15T16:16:04.0Z ##[endgroup]\n" +
                  "2026-02-15T16:16:05.0Z copilot: Done\n";

        var result = CcaLogService.ParseAgentConversation(log);

        Assert.Contains("copilot: Working on it", result);
        Assert.Contains("copilot: Done", result);
        Assert.DoesNotContain("##[group]", result);
        Assert.DoesNotContain("##[endgroup]", result);
    }

    [Fact]
    public void ParseAgentConversation_SkipsFirewallNoise()
    {
        var log = "2026-02-15T16:16:01.0Z Processing requests...\n" +
                  "2026-02-15T16:16:02.0Z   - kind: http-rule\n" +
                  "2026-02-15T16:16:03.0Z     url: { domain: crl3.digicert.com }\n" +
                  "2026-02-15T16:16:04.0Z copilot: Starting work\n";

        var result = CcaLogService.ParseAgentConversation(log);

        Assert.Contains("copilot: Starting work", result);
        Assert.DoesNotContain("kind: http-rule", result);
        Assert.DoesNotContain("digicert.com", result);
    }

    [Fact]
    public void ParseAgentConversation_TruncatesLongLines()
    {
        var longLine = new string('x', 3000);
        var log = "2026-02-15T16:16:01.0Z Processing requests...\n" +
                  $"2026-02-15T16:16:02.0Z {longLine}\n" +
                  "2026-02-15T16:16:03.0Z copilot: After long line\n";

        var result = CcaLogService.ParseAgentConversation(log);

        Assert.Contains("[... truncated]", result);
        Assert.Contains("copilot: After long line", result);
        // Should not contain the full 3000-char line
        Assert.True(result.Split('\n').All(l => l.Length <= 2020));
    }

    [Fact]
    public void ParseAgentConversation_StopsAtCleanup()
    {
        var log = "2026-02-15T16:16:01.0Z Processing requests...\n" +
                  "2026-02-15T16:16:02.0Z copilot: Working\n" +
                  "2026-02-15T16:25:19.6701184Z ##[group]Run echo \"Cleaning up...\"\n" +
                  "2026-02-15T16:25:20.0Z cleanup stuff\n";

        var result = CcaLogService.ParseAgentConversation(log);

        Assert.Contains("copilot: Working", result);
        Assert.DoesNotContain("cleanup stuff", result);
    }

    [Fact]
    public void ParseAgentConversation_EmptyLog_ReturnsEmpty()
    {
        Assert.Equal("", CcaLogService.ParseAgentConversation(""));
        Assert.Equal("", CcaLogService.ParseAgentConversation(null!));
    }

    [Fact]
    public void ParseAgentConversation_NoRelevantContent_ReturnsEmpty()
    {
        var log = "2026-02-15T16:15:47.0Z Runner setup\n" +
                  "2026-02-15T16:15:48.0Z More boilerplate\n";

        var result = CcaLogService.ParseAgentConversation(log);

        Assert.Equal("", result.Trim());
    }

    [Fact]
    public void ParseAgentConversation_PreservesAgentConversationStructure()
    {
        var log = "2026-02-15T16:16:01.0Z Solving problem: abc from owner/repo@main\n" +
                  "2026-02-15T16:16:01.1Z Problem statement:\n" +
                  "2026-02-15T16:16:01.2Z \n" +
                  "2026-02-15T16:16:01.3Z Fix the login bug\n" +
                  "2026-02-15T16:16:13.0Z copilot: I'll fix the login bug.\n" +
                  "2026-02-15T16:16:20.0Z function:\n" +
                  "2026-02-15T16:16:20.1Z   name: grep\n" +
                  "2026-02-15T16:16:20.2Z   args:\n" +
                  "2026-02-15T16:16:20.3Z     pattern: login\n" +
                  "2026-02-15T16:16:20.4Z   result: |\n" +
                  "2026-02-15T16:16:20.5Z     src/auth.ts:42:function login()\n";

        var result = CcaLogService.ParseAgentConversation(log);

        Assert.Contains("Solving problem:", result);
        Assert.Contains("Fix the login bug", result);
        Assert.Contains("copilot: I'll fix the login bug.", result);
        Assert.Contains("name: grep", result);
        Assert.Contains("src/auth.ts:42:function login()", result);
    }

    // --- AgentSessionInfo CCA fields ---

    [Fact]
    public void AgentSessionInfo_CcaFields_DefaultToNull()
    {
        var info = new AgentSessionInfo { Name = "test", Model = "gpt-4" };
        Assert.Null(info.CcaRunId);
        Assert.Null(info.CcaPrNumber);
        Assert.Null(info.CcaBranch);
    }

    [Fact]
    public void AgentSessionInfo_CcaFields_CanBeSet()
    {
        var info = new AgentSessionInfo
        {
            Name = "CCA: PR #116",
            Model = "gpt-4",
            CcaRunId = 22038922298,
            CcaPrNumber = 116,
            CcaBranch = "copilot/add-plan-mode-preview-option"
        };
        Assert.Equal(22038922298, info.CcaRunId);
        Assert.Equal(116, info.CcaPrNumber);
        Assert.Equal("copilot/add-plan-mode-preview-option", info.CcaBranch);
    }
}
