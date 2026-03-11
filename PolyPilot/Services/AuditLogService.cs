using System.Text.RegularExpressions;
using PolyPilot.Models;

namespace PolyPilot.Services;

/// <summary>
/// Structured audit logging for security-sensitive operations (codespace connections,
/// SSH handshakes, tunnel setup, token acquisition). Writes JSON Lines to daily
/// rotated files in ~/.polypilot/audit_logs/. Thread-safe via SemaphoreSlim.
///
/// Security: All tokens, keys, and passwords are sanitized before writing.
/// Error handling: Logging failures never propagate — they fall back to Console.Error.
/// </summary>
public sealed class AuditLogService : IDisposable
{
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private const int RetentionDays = 30;
    private const string LogDirName = "audit_logs";

    // Instance-level override (for testing or custom paths)
    private readonly string? _instanceLogDir;

    // Lazy directory resolution matching CopilotService pattern
    private static string? _auditLogDir;
    private static string DefaultAuditLogDir => _auditLogDir ??= ComputeAuditLogDir();

    // Effective log dir: instance override takes priority
    private string AuditLogDir => _instanceLogDir ?? DefaultAuditLogDir;

    // For testing: allows overriding the default log directory
    internal static void SetLogDirForTesting(string dir) => _auditLogDir = dir;
    internal static void ResetLogDir() => _auditLogDir = null;

    /// <summary>
    /// Creates an AuditLogService. Optionally provide a log directory (for testing).
    /// When null, uses the default ~/.polypilot/audit_logs/ path.
    /// </summary>
    public AuditLogService(string? logDir = null)
    {
        _instanceLogDir = logDir;
    }

    private static string ComputeAuditLogDir()
    {
        try
        {
#if IOS || ANDROID
            return Path.Combine(FileSystem.AppDataDirectory, ".polypilot", LogDirName);
#else
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (string.IsNullOrEmpty(home))
                home = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return Path.Combine(home, ".polypilot", LogDirName);
#endif
        }
        catch
        {
            return Path.Combine(Path.GetTempPath(), ".polypilot", LogDirName);
        }
    }

    // ── Core write ──────────────────────────────────────────────────────────

    /// <summary>
    /// Writes an audit entry as a single JSON line to the current day's log file.
    /// Never throws — logging failures are swallowed and reported to Console.Error.
    /// </summary>
    public async Task WriteEntryAsync(AuditLogEntry entry)
    {
        try
        {
            var dir = AuditLogDir;
            Directory.CreateDirectory(dir);

            var filePath = Path.Combine(dir, $"audit_{DateTime.UtcNow:yyyy-MM-dd}.jsonl");
            var line = entry.ToJsonLine() + Environment.NewLine;

            await _writeLock.WaitAsync().ConfigureAwait(false);
            try
            {
                await File.AppendAllTextAsync(filePath, line).ConfigureAwait(false);
            }
            finally
            {
                _writeLock.Release();
            }
        }
        catch (Exception ex)
        {
            // Audit logging must never crash the app
            Console.Error.WriteLine($"[AuditLog] Failed to write entry: {ex.Message}");
        }
    }

    // ── Retention cleanup ───────────────────────────────────────────────────

    /// <summary>
    /// Deletes audit log files older than 30 days. Called once on startup.
    /// </summary>
    public void PurgeOldLogs()
    {
        try
        {
            var dir = AuditLogDir;
            if (!Directory.Exists(dir)) return;

            var cutoff = DateTime.UtcNow.AddDays(-RetentionDays);
            foreach (var file in Directory.GetFiles(dir, "audit_*.jsonl"))
            {
                try
                {
                    // Parse date from filename (audit_YYYY-MM-DD.jsonl) instead of
                    // filesystem timestamps — GetCreationTimeUtc is unreliable on Linux
                    // where it falls back to mtime (always "now" for appended files).
                    var name = Path.GetFileNameWithoutExtension(file);
                    if (name.Length >= 16 // "audit_YYYY-MM-DD"
                        && DateTime.TryParseExact(name[6..], "yyyy-MM-dd", null,
                            System.Globalization.DateTimeStyles.None, out var fileDate)
                        && fileDate < cutoff.Date)
                    {
                        File.Delete(file);
                    }
                }
                catch { /* best-effort cleanup */ }
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[AuditLog] Failed to purge old logs: {ex.Message}");
        }
    }

    // ── Sanitization helpers ────────────────────────────────────────────────

    /// <summary>
    /// Truncates a secret to at most <paramref name="visibleChars"/> characters
    /// with a "[redacted]" suffix. Returns "[none]" for null/empty values.
    /// </summary>
    public static string SanitizeSecret(string? value, int visibleChars = 8)
    {
        if (string.IsNullOrEmpty(value)) return "[none]";
        if (value.Length <= visibleChars) return "[redacted]";
        return value[..visibleChars] + "[redacted]";
    }

    /// <summary>
    /// Removes file system paths that may reveal the user's home directory
    /// and strips potential token-like strings from error messages.
    /// </summary>
    public static string SanitizeErrorMessage(string? message)
    {
        if (string.IsNullOrEmpty(message)) return "[empty]";

        // Replace home directory paths
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrEmpty(home))
            message = message.Replace(home, "~");

        // Redact strings that look like tokens (40+ hex chars or JWT-like patterns)
        message = Regex.Replace(message, @"ghp_[A-Za-z0-9]{36,}", "ghp_[redacted]");
        message = Regex.Replace(message, @"gho_[A-Za-z0-9]{36,}", "gho_[redacted]");
        message = Regex.Replace(message, @"github_pat_[A-Za-z0-9_]{40,}", "github_pat_[redacted]");
        message = Regex.Replace(message, @"eyJ[A-Za-z0-9_-]{20,}\.[A-Za-z0-9_-]{20,}", "jwt_[redacted]");

        return message;
    }

    // ── Typed event methods ─────────────────────────────────────────────────

    // Event 1: Codespace connection initiated
    public Task LogCodespaceConnectionInitiated(string codespaceName, string? sessionId, int remotePort)
    {
        return WriteEntryAsync(new AuditLogEntry
        {
            EventType = AuditEventTypes.CodespaceConnectionInitiated,
            SessionId = sessionId,
            Details = new()
            {
                ["codespace_name"] = codespaceName,
                ["remote_port"] = remotePort,
                ["device_info"] = $"{Environment.OSVersion.Platform}/{Environment.OSVersion.Version}"
            }
        });
    }

    // Event 2: SSH handshake success
    public Task LogSshHandshakeSuccess(string codespaceName, string? sessionId, int localPort, bool isSshTunnel)
    {
        return WriteEntryAsync(new AuditLogEntry
        {
            EventType = AuditEventTypes.CodespaceSshHandshakeSuccess,
            SessionId = sessionId,
            Details = new()
            {
                ["codespace_name"] = codespaceName,
                ["local_port"] = localPort,
                ["tunnel_type"] = isSshTunnel ? "ssh" : "port_forward",
                ["device_info"] = $"{Environment.OSVersion.Platform}/{Environment.OSVersion.Version}"
            }
        });
    }

    // Event 3: SSH handshake failure
    public Task LogSshHandshakeFailure(string codespaceName, string? sessionId, string failureReason, int retryCount = 0)
    {
        return WriteEntryAsync(new AuditLogEntry
        {
            EventType = AuditEventTypes.CodespaceSshHandshakeFailure,
            SessionId = sessionId,
            Details = new()
            {
                ["codespace_name"] = codespaceName,
                ["failure_reason"] = SanitizeErrorMessage(failureReason),
                ["retry_count"] = retryCount
            }
        });
    }

    // Event 4: Copilot headless started successfully
    public Task LogCopilotHeadlessStart(string codespaceName, string? sessionId, int remotePort)
    {
        return WriteEntryAsync(new AuditLogEntry
        {
            EventType = AuditEventTypes.CopilotHeadlessStart,
            SessionId = sessionId,
            Details = new()
            {
                ["codespace_name"] = codespaceName,
                ["remote_port"] = remotePort
            }
        });
    }

    // Event 5: Copilot headless failed to start
    public Task LogCopilotHeadlessFailure(string codespaceName, string? sessionId, string errorMessage)
    {
        return WriteEntryAsync(new AuditLogEntry
        {
            EventType = AuditEventTypes.CopilotHeadlessFailure,
            SessionId = sessionId,
            Details = new()
            {
                ["codespace_name"] = codespaceName,
                ["error_message"] = SanitizeErrorMessage(errorMessage)
            }
        });
    }

    // Event 5b: Copilot headless status indeterminate (SSH worked, but startup
    // probe was inconclusive — tunnel probe will determine actual status)
    public Task LogCopilotHeadlessIndeterminate(string codespaceName, string? sessionId, string probeResult)
    {
        return WriteEntryAsync(new AuditLogEntry
        {
            EventType = AuditEventTypes.CopilotHeadlessIndeterminate,
            SessionId = sessionId,
            Details = new()
            {
                ["codespace_name"] = codespaceName,
                ["probe_result"] = probeResult,
                ["determined_by"] = "tunnel_probe"
            }
        });
    }

    // Event 6: DevTunnel token acquired
    public Task LogDevtunnelTokenAcquired(string? sessionId, string? tunnelId, int tokenLength)
    {
        return WriteEntryAsync(new AuditLogEntry
        {
            EventType = AuditEventTypes.DevtunnelTokenAcquired,
            SessionId = sessionId,
            Details = new()
            {
                ["tunnel_id"] = SanitizeSecret(tunnelId),
                ["token_length"] = tokenLength
            }
        });
    }

    // Event 7: DevTunnel connection established
    public Task LogDevtunnelConnectionEstablished(string? sessionId, string? tunnelId, string? tunnelUrl, long latencyMs)
    {
        return WriteEntryAsync(new AuditLogEntry
        {
            EventType = AuditEventTypes.DevtunnelConnectionEstablished,
            SessionId = sessionId,
            Details = new()
            {
                ["tunnel_id"] = SanitizeSecret(tunnelId),
                ["tunnel_url"] = tunnelUrl,
                ["connection_latency_ms"] = latencyMs
            }
        });
    }

    // Event 8: DevTunnel connection failed
    public Task LogDevtunnelConnectionFailed(string? sessionId, string? tunnelId, string failureReason)
    {
        return WriteEntryAsync(new AuditLogEntry
        {
            EventType = AuditEventTypes.DevtunnelConnectionFailed,
            SessionId = sessionId,
            Details = new()
            {
                ["tunnel_id"] = SanitizeSecret(tunnelId),
                ["failure_reason"] = SanitizeErrorMessage(failureReason)
            }
        });
    }

    // Event 9: Session closed
    public Task LogSessionClosed(string? sessionId, double durationSeconds, bool cleanClose, string? closeReason)
    {
        return WriteEntryAsync(new AuditLogEntry
        {
            EventType = AuditEventTypes.SessionClosed,
            SessionId = sessionId,
            Details = new()
            {
                ["duration_seconds"] = Math.Round(durationSeconds, 1),
                ["clean_close"] = cleanClose,
                ["close_reason"] = closeReason
            }
        });
    }

    // Event 10: Session error
    public Task LogSessionError(string? sessionId, string errorCategory, string errorMessage, string? stackTrace = null)
    {
        return WriteEntryAsync(new AuditLogEntry
        {
            EventType = AuditEventTypes.SessionError,
            SessionId = sessionId,
            Details = new()
            {
                ["error_category"] = errorCategory,
                ["error_message"] = SanitizeErrorMessage(errorMessage),
                ["stack_trace"] = stackTrace != null ? SanitizeErrorMessage(stackTrace) : null
            }
        });
    }

    public void Dispose()
    {
        _writeLock.Dispose();
    }
}
