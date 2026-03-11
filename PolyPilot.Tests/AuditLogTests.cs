using System.Text.Json;
using PolyPilot.Models;
using PolyPilot.Services;

namespace PolyPilot.Tests;

// Disable parallel execution — tests share filesystem
[Collection("AuditLogTests")]
public class AuditLogTests : IDisposable
{
    private readonly string _testDir;
    private readonly AuditLogService _auditLog;

    public AuditLogTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), $"polypilot-audit-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testDir);
        // Pass directory to constructor — no static state needed
        _auditLog = new AuditLogService(_testDir);
    }

    public void Dispose()
    {
        _auditLog.Dispose();
        try { Directory.Delete(_testDir, recursive: true); } catch { }
    }

    // ── File creation and format ────────────────────────────────────────────

    [Fact]
    public async Task WriteEntry_CreatesJsonlFile()
    {
        await _auditLog.WriteEntryAsync(new AuditLogEntry
        {
            EventType = AuditEventTypes.CodespaceConnectionInitiated,
            SessionId = "test-session-1",
            Details = new() { ["codespace_name"] = "my-cs" }
        });

        var files = Directory.GetFiles(_testDir, "audit_*.jsonl");
        Assert.Single(files);

        var lines = await File.ReadAllLinesAsync(files[0]);
        Assert.Single(lines);

        // Verify it's valid JSON
        var doc = JsonDocument.Parse(lines[0]);
        Assert.Equal("CODESPACE_CONNECTION_INITIATED", doc.RootElement.GetProperty("event_type").GetString());
        Assert.Equal("test-session-1", doc.RootElement.GetProperty("session_id").GetString());
    }

    [Fact]
    public async Task WriteEntry_AppendsMultipleLines()
    {
        await _auditLog.WriteEntryAsync(new AuditLogEntry { EventType = "EVENT_1" });
        await _auditLog.WriteEntryAsync(new AuditLogEntry { EventType = "EVENT_2" });
        await _auditLog.WriteEntryAsync(new AuditLogEntry { EventType = "EVENT_3" });

        var files = Directory.GetFiles(_testDir, "audit_*.jsonl");
        var lines = (await File.ReadAllLinesAsync(files[0]))
            .Where(l => !string.IsNullOrWhiteSpace(l)).ToArray();
        Assert.Equal(3, lines.Length);
    }

    [Fact]
    public async Task WriteEntry_IncludesTimestamp()
    {
        var before = DateTime.UtcNow;
        await _auditLog.WriteEntryAsync(new AuditLogEntry { EventType = "TEST" });
        var after = DateTime.UtcNow;

        var files = Directory.GetFiles(_testDir, "audit_*.jsonl");
        var doc = JsonDocument.Parse((await File.ReadAllLinesAsync(files[0]))[0]);
        var timestamp = doc.RootElement.GetProperty("timestamp").GetDateTime();
        Assert.InRange(timestamp, before, after.AddSeconds(1));
    }

    // ── Typed event methods ─────────────────────────────────────────────────

    [Fact]
    public async Task LogCodespaceConnectionInitiated_WritesCorrectFields()
    {
        await _auditLog.LogCodespaceConnectionInitiated("test-codespace", "sess-1", 4321);

        var entry = await ReadLastEntry();
        Assert.Equal("CODESPACE_CONNECTION_INITIATED", entry.EventType);
        Assert.Equal("sess-1", entry.SessionId);
        Assert.Equal("test-codespace", entry.Details["codespace_name"]?.ToString());
    }

    [Fact]
    public async Task LogSshHandshakeSuccess_WritesCorrectFields()
    {
        await _auditLog.LogSshHandshakeSuccess("cs-1", "sess-2", 12345, isSshTunnel: true);

        var entry = await ReadLastEntry();
        Assert.Equal("CODESPACE_SSH_HANDSHAKE_SUCCESS", entry.EventType);
        Assert.Equal("ssh", entry.Details["tunnel_type"]?.ToString());
    }

    [Fact]
    public async Task LogSshHandshakeFailure_SanitizesMessage()
    {
        await _auditLog.LogSshHandshakeFailure("cs-1", null, "Error with token ghp_ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmno");

        var entry = await ReadLastEntry();
        Assert.Equal("CODESPACE_SSH_HANDSHAKE_FAILURE", entry.EventType);
        var reason = entry.Details["failure_reason"]?.ToString()!;
        Assert.DoesNotContain("ghp_ABCD", reason);
        Assert.Contains("ghp_[redacted]", reason);
    }

    [Fact]
    public async Task LogDevtunnelTokenAcquired_TruncatesTunnelId()
    {
        await _auditLog.LogDevtunnelTokenAcquired(null, "abcdefghijklmnop-long-tunnel-id", 256);

        var entry = await ReadLastEntry();
        Assert.Equal("DEVTUNNEL_TOKEN_ACQUIRED", entry.EventType);
        var tunnelId = entry.Details["tunnel_id"]?.ToString()!;
        Assert.StartsWith("abcdefgh", tunnelId);
        Assert.Contains("[redacted]", tunnelId);
    }

    [Fact]
    public async Task LogSessionError_WritesAllFields()
    {
        await _auditLog.LogSessionError("sess-err", "SSH", "Connection refused", "at SomeMethod()");

        var entry = await ReadLastEntry();
        Assert.Equal("SESSION_ERROR", entry.EventType);
        Assert.Equal("SSH", entry.Details["error_category"]?.ToString());
    }

    [Fact]
    public async Task LogCopilotHeadlessIndeterminate_WritesCorrectFields()
    {
        await _auditLog.LogCopilotHeadlessIndeterminate("cs-1", "sess-3", "FAILED");

        var entry = await ReadLastEntry();
        Assert.Equal("COPILOT_HEADLESS_INDETERMINATE", entry.EventType);
        Assert.Equal("cs-1", entry.Details["codespace_name"]?.ToString());
        Assert.Equal("FAILED", entry.Details["probe_result"]?.ToString());
        Assert.Equal("tunnel_probe", entry.Details["determined_by"]?.ToString());
    }

    // ── Sanitization ────────────────────────────────────────────────────────

    [Fact]
    public void SanitizeSecret_TruncatesLongValues()
    {
        Assert.Equal("abcdefgh[redacted]", AuditLogService.SanitizeSecret("abcdefghijklmnop"));
        Assert.Equal("[redacted]", AuditLogService.SanitizeSecret("short"));
        Assert.Equal("[none]", AuditLogService.SanitizeSecret(null));
        Assert.Equal("[none]", AuditLogService.SanitizeSecret(""));
    }

    [Fact]
    public void SanitizeErrorMessage_RedactsTokenPatterns()
    {
        var msg = "Auth failed with token ghp_ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmno";
        var sanitized = AuditLogService.SanitizeErrorMessage(msg);
        Assert.DoesNotContain("ghp_ABCD", sanitized);
        Assert.Contains("ghp_[redacted]", sanitized);
    }

    [Fact]
    public void SanitizeErrorMessage_RedactsJwtTokens()
    {
        var msg = "Token: eyJhbGciOiJSUzI1NiIsInR5cCI6IkpXVCJ9.eyJzdWIiOiIxMjM0NTY3ODkwIn0";
        var sanitized = AuditLogService.SanitizeErrorMessage(msg);
        Assert.DoesNotContain("eyJhbG", sanitized);
        Assert.Contains("jwt_[redacted]", sanitized);
    }

    [Fact]
    public void SanitizeErrorMessage_RedactsGitHubPat()
    {
        var msg = "Error: github_pat_11AABCDEF0123456789abcdefghijklmnop01234567890";
        var sanitized = AuditLogService.SanitizeErrorMessage(msg);
        Assert.Contains("github_pat_[redacted]", sanitized);
    }

    [Fact]
    public void SanitizeErrorMessage_ReplacesHomePath()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrEmpty(home)) return; // Skip if no home dir
        var msg = $"File not found: {home}/.ssh/id_rsa";
        var sanitized = AuditLogService.SanitizeErrorMessage(msg);
        Assert.DoesNotContain(home, sanitized);
        Assert.Contains("~/.ssh/id_rsa", sanitized);
    }

    [Fact]
    public void SanitizeErrorMessage_HandlesNullAndEmpty()
    {
        Assert.Equal("[empty]", AuditLogService.SanitizeErrorMessage(null));
        Assert.Equal("[empty]", AuditLogService.SanitizeErrorMessage(""));
    }

    // ── Thread safety ───────────────────────────────────────────────────────

    [Fact]
    public async Task ConcurrentWrites_NoDataLoss()
    {
        const int taskCount = 10;
        const int entriesPerTask = 20;

        var tasks = Enumerable.Range(0, taskCount).Select(t =>
            Task.Run(async () =>
            {
                for (int i = 0; i < entriesPerTask; i++)
                {
                    await _auditLog.WriteEntryAsync(new AuditLogEntry
                    {
                        EventType = $"CONCURRENT_{t}_{i}",
                        SessionId = $"task-{t}"
                    });
                }
            })
        );

        await Task.WhenAll(tasks);

        var files = Directory.GetFiles(_testDir, "audit_*.jsonl");
        var lines = (await File.ReadAllLinesAsync(files[0]))
            .Where(l => !string.IsNullOrWhiteSpace(l)).ToArray();
        Assert.Equal(taskCount * entriesPerTask, lines.Length);

        // Verify each line is valid JSON
        foreach (var line in lines)
        {
            var doc = JsonDocument.Parse(line);
            Assert.NotNull(doc.RootElement.GetProperty("event_type").GetString());
        }
    }

    // ── Error handling ──────────────────────────────────────────────────────

    [Fact]
    public async Task WriteEntry_DoesNotThrow_OnInvalidPath()
    {
        // Create a service pointing at an impossible path
        using var badSvc = new AuditLogService("/dev/null/impossible/path");

        // Should not throw — errors are swallowed
        await badSvc.WriteEntryAsync(new AuditLogEntry { EventType = "TEST" });
    }

    // ── Retention ───────────────────────────────────────────────────────────

    [Fact]
    public void PurgeOldLogs_DeletesOldFiles()
    {
        // Create an "old" file with a date >30 days ago in the filename
        var oldFile = Path.Combine(_testDir, "audit_2024-01-01.jsonl");
        File.WriteAllText(oldFile, "{\"event_type\":\"OLD\"}\n");

        // Create a "recent" file with a future date in the filename
        var recentFile = Path.Combine(_testDir, "audit_2099-01-01.jsonl");
        File.WriteAllText(recentFile, "{\"event_type\":\"RECENT\"}\n");

        _auditLog.PurgeOldLogs();

        Assert.False(File.Exists(oldFile), "Old file should be deleted (filename date > 30 days)");
        Assert.True(File.Exists(recentFile), "Recent file should be kept");
    }

    [Fact]
    public void PurgeOldLogs_IgnoresFilesWithUnparsableNames()
    {
        var oddFile = Path.Combine(_testDir, "audit_not-a-date.jsonl");
        File.WriteAllText(oddFile, "{\"event_type\":\"ODD\"}\n");

        _auditLog.PurgeOldLogs();

        Assert.True(File.Exists(oddFile), "Files with unparsable names should be kept");
    }

    // ── AuditLogEntry serialization ─────────────────────────────────────────

    [Fact]
    public void AuditLogEntry_ToJsonLine_ProducesValidJson()
    {
        var entry = new AuditLogEntry
        {
            EventType = "TEST_EVENT",
            SessionId = "s-123",
            Details = new() { ["key1"] = "value1", ["key2"] = 42 }
        };

        var json = entry.ToJsonLine();
        var doc = JsonDocument.Parse(json);
        Assert.Equal("TEST_EVENT", doc.RootElement.GetProperty("event_type").GetString());
        Assert.Equal("s-123", doc.RootElement.GetProperty("session_id").GetString());
    }

    [Fact]
    public void AuditEventTypes_AllConstantsDefined()
    {
        Assert.Equal("CODESPACE_CONNECTION_INITIATED", AuditEventTypes.CodespaceConnectionInitiated);
        Assert.Equal("CODESPACE_SSH_HANDSHAKE_SUCCESS", AuditEventTypes.CodespaceSshHandshakeSuccess);
        Assert.Equal("CODESPACE_SSH_HANDSHAKE_FAILURE", AuditEventTypes.CodespaceSshHandshakeFailure);
        Assert.Equal("COPILOT_HEADLESS_START", AuditEventTypes.CopilotHeadlessStart);
        Assert.Equal("COPILOT_HEADLESS_FAILURE", AuditEventTypes.CopilotHeadlessFailure);
        Assert.Equal("COPILOT_HEADLESS_INDETERMINATE", AuditEventTypes.CopilotHeadlessIndeterminate);
        Assert.Equal("DEVTUNNEL_TOKEN_ACQUIRED", AuditEventTypes.DevtunnelTokenAcquired);
        Assert.Equal("DEVTUNNEL_CONNECTION_ESTABLISHED", AuditEventTypes.DevtunnelConnectionEstablished);
        Assert.Equal("DEVTUNNEL_CONNECTION_FAILED", AuditEventTypes.DevtunnelConnectionFailed);
        Assert.Equal("SESSION_CLOSED", AuditEventTypes.SessionClosed);
        Assert.Equal("SESSION_ERROR", AuditEventTypes.SessionError);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private async Task<AuditLogEntry> ReadLastEntry()
    {
        var files = Directory.GetFiles(_testDir, "audit_*.jsonl");
        Assert.Single(files);
        var lines = (await File.ReadAllLinesAsync(files[0]))
            .Where(l => !string.IsNullOrWhiteSpace(l)).ToArray();
        Assert.NotEmpty(lines);
        var json = lines[^1];
        var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var entry = new AuditLogEntry
        {
            EventType = root.GetProperty("event_type").GetString() ?? "",
            SessionId = root.TryGetProperty("session_id", out var sid) ? sid.GetString() : null,
        };
        if (root.TryGetProperty("details", out var details))
        {
            foreach (var prop in details.EnumerateObject())
                entry.Details[prop.Name] = prop.Value.ValueKind == JsonValueKind.String
                    ? prop.Value.GetString()
                    : prop.Value.ToString();
        }
        return entry;
    }
}
