using System.Text.Json;
using PolyPilot.Models;

namespace PolyPilot.Services;

public partial class CopilotService
{
    private static string? GetGitBranch(string? directory)
    {
        if (string.IsNullOrEmpty(directory)) return null;
        try
        {
            var headFile = FindGitHead(directory);
            if (headFile == null) return null;
            var head = File.ReadAllText(headFile).Trim();
            return head.StartsWith("ref: refs/heads/")
                ? head["ref: refs/heads/".Length..]
                : head.Length >= 8 ? head[..8] : head; // detached HEAD — show short SHA
        }
        catch { return null; }
    }

    private static string? FindGitHead(string dir)
    {
        var d = new DirectoryInfo(dir);
        while (d != null)
        {
            var head = Path.Combine(d.FullName, ".git", "HEAD");
            if (File.Exists(head)) return head;
            d = d.Parent;
        }
        return null;
    }

    private string? GetSessionWorkingDirectory(string sessionId)
    {
        try
        {
            var eventsFile = Path.Combine(SessionStatePath, sessionId, "events.jsonl");
            if (!File.Exists(eventsFile)) return null;
            // Read only enough lines to find session.start
            foreach (var line in File.ReadLines(eventsFile).Take(5))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                if (!root.TryGetProperty("type", out var t) || t.GetString() != "session.start") continue;
                if (root.TryGetProperty("data", out var data))
                {
                    if (data.TryGetProperty("context", out var ctx) &&
                        ctx.TryGetProperty("cwd", out var cwd))
                        return cwd.GetString();
                    if (data.TryGetProperty("workingDirectory", out var wd))
                        return wd.GetString();
                }
            }
        }
        catch { }
        return null;
    }

    private string? GetSessionModelFromDisk(string sessionId)
    {
        try
        {
            var eventsFile = Path.Combine(SessionStatePath, sessionId, "events.jsonl");
            if (!File.Exists(eventsFile)) return null;
            foreach (var line in File.ReadLines(eventsFile).Take(5))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                if (!root.TryGetProperty("type", out var t) || t.GetString() != "session.start") continue;
                if (root.TryGetProperty("data", out var data) &&
                    data.TryGetProperty("selectedModel", out var model))
                {
                    var modelStr = model.GetString();
                    // Normalize display names to slugs
                    return string.IsNullOrEmpty(modelStr) ? null : Models.ModelHelper.NormalizeToSlug(modelStr);
                }
            }
        }
        catch { }
        return null;
    }

    /// <summary>
    /// Check if a session was still processing when the app last closed.
    /// Returns false if the events file is stale (not modified recently),
    /// preventing sessions from being incorrectly marked as processing
    /// after long app restarts.
    /// </summary>
    internal bool IsSessionStillProcessing(string sessionId) =>
        IsSessionStillProcessing(sessionId, SessionStatePath);

    /// <summary>
    /// Testable overload that accepts a custom base path.
    /// </summary>
    internal bool IsSessionStillProcessing(string sessionId, string basePath)
    {
        var eventsFile = Path.Combine(basePath, sessionId, "events.jsonl");
        if (!File.Exists(eventsFile)) return false;

        try
        {
            // Staleness check: if the file hasn't been modified recently,
            // the CLI finished processing long ago — don't mark as still active.
            var lastWrite = File.GetLastWriteTimeUtc(eventsFile);
            var staleness = (DateTime.UtcNow - lastWrite).TotalSeconds;
            if (staleness > WatchdogToolExecutionTimeoutSeconds)
            {
                Debug($"[RESTORE] events.jsonl for '{sessionId}' is stale " +
                      $"({staleness:F0}s old > {WatchdogToolExecutionTimeoutSeconds}s threshold), " +
                      $"treating session as idle");
                return false;
            }

            string? lastLine = null;
            foreach (var line in File.ReadLines(eventsFile))
            {
                if (!string.IsNullOrWhiteSpace(line))
                    lastLine = line;
            }
            if (lastLine == null) return false;

            using var doc = JsonDocument.Parse(lastLine);
            var type = doc.RootElement.GetProperty("type").GetString();
            
            var activeEvents = new[] { 
                "assistant.turn_start", "tool.execution_start", 
                "tool.execution_progress", "assistant.message_delta",
                "assistant.reasoning", "assistant.reasoning_delta",
                "assistant.intent"
            };
            return activeEvents.Contains(type);
        }
        catch { return false; }
    }

    /// <summary>
    /// Reads the last non-empty line of events.jsonl and returns its "type" field.
    /// Used by the watchdog to detect session.shutdown without parsing the full file.
    /// Returns null if the file doesn't exist, is empty, or can't be parsed.
    /// Uses a tail-read (last 4KB) to avoid O(N) full-file scan on large sessions.
    /// </summary>
    internal static string? GetLastEventType(string eventsFilePath)
    {
        try
        {
            if (!File.Exists(eventsFilePath)) return null;

            // Read only the tail of the file (last 4KB is plenty for the last JSON line)
            const int tailBytes = 4096;
            using var fs = new FileStream(eventsFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (fs.Length == 0) return null;

            var offset = Math.Max(0, fs.Length - tailBytes);
            fs.Seek(offset, SeekOrigin.Begin);
            using var reader = new StreamReader(fs);
            string? lastLine = null;
            while (reader.ReadLine() is { } line)
            {
                if (!string.IsNullOrWhiteSpace(line))
                    lastLine = line;
            }
            if (lastLine == null) return null;

            using var doc = System.Text.Json.JsonDocument.Parse(lastLine);
            return doc.RootElement.GetProperty("type").GetString();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Checks whether events.jsonl shows a session that was interrupted mid-tool-execution.
    /// Returns true if the last non-control events are tool.execution_start without matching
    /// tool.execution_complete, AND a session.shutdown occurred after them (proving the tools
    /// were interrupted by a crash, not just running slowly).
    /// Used by EnsureSessionConnectedAsync to send an abort on resume.
    /// </summary>
    internal bool HasInterruptedToolExecution(string sessionId) =>
        HasInterruptedToolExecution(sessionId, SessionStatePath);

    /// <summary>Testable overload that accepts a custom base path.</summary>
    internal bool HasInterruptedToolExecution(string sessionId, string basePath)
    {
        var eventsFile = Path.Combine(basePath, sessionId, "events.jsonl");
        if (!File.Exists(eventsFile)) return false;

        try
        {
            // Stream through events.jsonl keeping only the last 30 non-empty lines
            // (avoids loading the full file into memory for large sessions).
            // Then scan backwards for the pattern: tool.execution_start with no
            // matching tool.execution_complete — indicating a crash mid-tool-execution.
            var buffer = new Queue<string>(31);
            foreach (var line in File.ReadLines(eventsFile))
            {
                if (!string.IsNullOrWhiteSpace(line))
                {
                    buffer.Enqueue(line);
                    if (buffer.Count > 30)
                        buffer.Dequeue();
                }
            }
            var recentLines = buffer.ToList();

            // Walk backwards from the end, skipping session.resume/shutdown control events.
            // IMPORTANT: Because we scan backwards, we see tool.execution_complete BEFORE
            // its matching tool.execution_start. So we count completions first, then consume
            // them when we hit a start. Any start without a matching completion is interrupted.
            var unmatchedStarts = 0;
            var pendingCompletions = 0;
            var sawShutdown = false;
            var sawOnlyControlEvents = true; // Only saw resume/shutdown so far

            for (int i = recentLines.Count - 1; i >= 0; i--)
            {
                using var doc = System.Text.Json.JsonDocument.Parse(recentLines[i]);
                var type = doc.RootElement.GetProperty("type").GetString();

                if (type == "session.resume")
                    continue; // skip resume markers

                if (type == "session.shutdown")
                {
                    sawShutdown = true;
                    continue;
                }

                // We've hit a non-control event
                sawOnlyControlEvents = false;

                if (type == "tool.execution_complete")
                {
                    // Scanning backwards: completions come first. Count them so
                    // we can match them against starts found further back.
                    pendingCompletions++;
                    continue;
                }

                if (type == "tool.execution_start")
                {
                    if (pendingCompletions > 0)
                        pendingCompletions--; // matched with a completion above
                    else
                        unmatchedStarts++; // no matching completion — interrupted
                    continue;
                }

                // Hit a non-tool, non-control event — stop scanning
                break;
            }

            // Interrupted tools detected if:
            // 1. There are unmatched tool.execution_start events, AND
            // 2. Either a session.shutdown confirms the crash, OR the tool starts
            //    are the very last non-control events (force-kill scenario)
            var result = unmatchedStarts > 0 && (sawShutdown || !sawOnlyControlEvents);
            if (result)
            {
                Debug($"[RESUME-ABORT] events.jsonl for '{sessionId}' has {unmatchedStarts} interrupted tool(s) " +
                      $"(shutdown={sawShutdown}, force-kill={!sawShutdown})");
            }
            return result;
        }
        catch (Exception ex)
        {
            Debug($"[RESUME-ABORT] Failed to check events.jsonl for '{sessionId}': {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// During session restore, determines whether the events.jsonl file shows recent server activity
    /// and whether the last event was a tool event. Used to pre-seed watchdog flags so that
    /// the 30s quiescence timeout is bypassed for sessions that were genuinely active before restart.
    /// </summary>
    internal (bool isRecentlyActive, bool hadToolActivity) GetEventsFileRestoreHints(string sessionId) =>
        GetEventsFileRestoreHints(sessionId, SessionStatePath);

    /// <summary>
    /// Testable overload that accepts a custom base path.
    /// </summary>
    internal (bool isRecentlyActive, bool hadToolActivity) GetEventsFileRestoreHints(string sessionId, string basePath)
    {
        var eventsFile = Path.Combine(basePath, sessionId, "events.jsonl");
        if (!File.Exists(eventsFile)) return (false, false);

        var isRecentlyActive = false;
        try
        {
            var lastWrite = File.GetLastWriteTimeUtc(eventsFile);
            var fileAge = (DateTime.UtcNow - lastWrite).TotalSeconds;
            isRecentlyActive = fileAge < WatchdogToolExecutionTimeoutSeconds;

            if (!isRecentlyActive) return (false, false);

            string? lastLine = null;
            foreach (var line in File.ReadLines(eventsFile))
            {
                if (!string.IsNullOrWhiteSpace(line))
                    lastLine = line;
            }
            if (lastLine == null) return (isRecentlyActive, false);

            using var doc = JsonDocument.Parse(lastLine);
            var type = doc.RootElement.GetProperty("type").GetString();
            var hadToolActivity = type is "tool.execution_start" or "tool.execution_progress";

            return (isRecentlyActive, hadToolActivity);
        }
        catch { return (isRecentlyActive, false); }
    }

    /// <summary>
    /// Get the last tool name and assistant message from events.jsonl for status display
    /// </summary>
    private (string? lastTool, string? lastContent) GetLastSessionActivity(string sessionId)
    {
        var eventsFile = Path.Combine(SessionStatePath, sessionId, "events.jsonl");
        if (!File.Exists(eventsFile)) return (null, null);

        try
        {
            string? lastTool = null;
            string? lastContent = null;

            foreach (var line in File.ReadLines(eventsFile))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                var type = root.GetProperty("type").GetString();

                if (type == "tool.execution_start" && root.TryGetProperty("data", out var toolData))
                {
                    if (toolData.TryGetProperty("toolName", out var tn))
                        lastTool = tn.GetString();
                }
                else if (type == "assistant.message" && root.TryGetProperty("data", out var msgData))
                {
                    if (msgData.TryGetProperty("content", out var content))
                    {
                        var c = content.GetString();
                        if (!string.IsNullOrEmpty(c))
                            lastContent = c;
                    }
                }
            }
            return (lastTool, lastContent);
        }
        catch { return (null, null); }
    }

    /// <summary>
    /// Get a compact event log summary from events.jsonl for display in the Log popup.
    /// Returns a list of (timestamp, eventType, detail) tuples.
    /// </summary>
    public List<(string Timestamp, string EventType, string Detail)> GetSessionEventLogSummary(string? sessionId)
    {
        if (string.IsNullOrEmpty(sessionId)) return new();
        var eventsFile = Path.Combine(SessionStatePath, sessionId, "events.jsonl");
        return ParseEventLogFile(eventsFile);
    }

    /// <summary>
    /// Testable overload that reads event log from a specific file path.
    /// </summary>
    internal static List<(string Timestamp, string EventType, string Detail)> ParseEventLogFile(string eventsFilePath)
    {
        var result = new List<(string, string, string)>();
        try
        {
            if (!File.Exists(eventsFilePath)) return result;
            foreach (var line in File.ReadLines(eventsFilePath))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    using var doc = JsonDocument.Parse(line);
                    var root = doc.RootElement;
                    var type = root.TryGetProperty("type", out var t) ? t.GetString() ?? "" : "";
                    var ts = root.TryGetProperty("timestamp", out var tsEl) ? tsEl.GetString() ?? "" : "";
                    var tsDisplay = DateTime.TryParse(ts, out var dt) ? dt.ToString("HH:mm:ss") : ts;
                    var detail = GetEventDetail(type, root);
                    result.Add((tsDisplay, type, detail));
                }
                catch { /* skip malformed lines */ }
            }
            if (result.Count > 500)
                result = result.GetRange(result.Count - 500, 500);
        }
        catch { /* file read error */ }
        return result;
    }

    internal static string GetEventDetail(string type, JsonElement root)
    {
        if (!root.TryGetProperty("data", out var data)) return "";
        try
        {
            return type switch
            {
                "user.message" => Truncate(data.TryGetProperty("content", out var c) ? c.GetString() ?? "" : "", 80),
                "assistant.message" => Truncate(data.TryGetProperty("content", out var c) ? c.GetString() ?? "" : "", 80),
                "assistant.message_delta" => "",
                "tool.execution_start" => data.TryGetProperty("toolName", out var tn) ? tn.GetString() ?? "" : "",
                "tool.execution_complete" => data.TryGetProperty("toolName", out var tn) ? tn.GetString() ?? "" : "",
                "session.start" => data.TryGetProperty("context", out var ctx) && ctx.TryGetProperty("cwd", out var cwd) ? cwd.GetString() ?? "" : "",
                "assistant.intent" => Truncate(data.TryGetProperty("intent", out var i) ? i.GetString() ?? "" : "", 80),
                "session.error" => Truncate(data.TryGetProperty("message", out var m) ? m.GetString() ?? "" : "", 80),
                _ => ""
            };
        }
        catch { return ""; }
    }

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s[..max] + "…";

    /// <summary>
    /// Observes a fire-and-forget <see cref="Task"/> so that any faulted exception
    /// is logged instead of surfacing as <see cref="TaskScheduler.UnobservedTaskException"/>.
    /// All ChatDatabase write-through calls use this wrapper.
    /// </summary>
    internal static void SafeFireAndForget(Task task, string context = "")
    {
        if (task.IsCompletedSuccessfully) return;
        task.ContinueWith(static (t, state) =>
        {
            System.Diagnostics.Debug.WriteLine(
                $"[SafeFireAndForget] {state}: {t.Exception?.GetBaseException().Message}");
        }, context, TaskContinuationOptions.OnlyOnFaulted);
    }

    /// <summary>
    /// Returns true if the exception indicates a broken connection
    /// (JSON-RPC lost, socket closed, transport error, etc.).
    /// Used by CreateSessionAsync retry logic and session restore.
    /// </summary>
    internal static bool IsConnectionError(Exception ex)
    {
        var msg = ex.Message;
        if (ex is System.IO.IOException or System.Net.Sockets.SocketException or ObjectDisposedException
            || msg.Contains("connection refused", StringComparison.OrdinalIgnoreCase)
            || msg.Contains("connection reset", StringComparison.OrdinalIgnoreCase)
            || msg.Contains("connection was closed", StringComparison.OrdinalIgnoreCase)
            || msg.Contains("connection lost", StringComparison.OrdinalIgnoreCase)
            || msg.Contains("transport connection", StringComparison.OrdinalIgnoreCase)
            || msg.Contains("transport is closed", StringComparison.OrdinalIgnoreCase)
            || msg.Contains("JSON-RPC connection", StringComparison.OrdinalIgnoreCase)
            || msg.Contains("not connected", StringComparison.OrdinalIgnoreCase))
            return true;
        // Stale process handle: the CLI server process died and the SDK's
        // StartCliServerAsync hit Process.HasExited on a dead handle.
        if (IsProcessError(ex))
            return true;
        // Auth failures on the persistent server: the server is TCP-alive but can't
        // process requests because the GitHub token expired. Treat as connection error
        // so the existing recovery paths (server restart + client recreate) kick in.
        if (IsAuthError(ex))
            return true;
        // Walk the full exception chain, including all AggregateException inner exceptions
        if (ex is AggregateException agg)
            return agg.InnerExceptions.Any(IsConnectionError);
        return ex.InnerException != null && IsConnectionError(ex.InnerException);
    }

    /// <summary>
    /// Returns true if the exception indicates the CLI server process is dead
    /// (e.g., Process.HasExited throws because the Process handle was never started
    /// or has been disposed). This happens when the SDK tries to monitor a stale process.
    /// </summary>
    internal static bool IsProcessError(Exception ex)
    {
        if (ex is InvalidOperationException && ex.Message.Contains("No process is associated", StringComparison.OrdinalIgnoreCase))
            return true;
        if (ex is AggregateException agg)
            return agg.InnerExceptions.Any(IsProcessError);
        return ex.InnerException != null && IsProcessError(ex.InnerException);
    }

    /// <summary>
    /// Returns true if the exception indicates an authentication or authorization failure.
    /// When the Copilot CLI's GitHub auth token expires or becomes invalid, the persistent
    /// server stays running (TCP check passes) but all session operations fail silently —
    /// no SessionErrorEvent is sent, the session just hangs until the watchdog kills it.
    /// Detecting these errors allows the app to restart the server (re-authenticating).
    /// </summary>
    internal static bool IsAuthError(Exception ex)
    {
        var msg = ex.Message;
        if (msg.Contains("unauthorized", StringComparison.OrdinalIgnoreCase)
            || msg.Contains("not authenticated", StringComparison.OrdinalIgnoreCase)
            || msg.Contains("authentication failed", StringComparison.OrdinalIgnoreCase)
            || msg.Contains("authentication required", StringComparison.OrdinalIgnoreCase)
            || msg.Contains("token expired", StringComparison.OrdinalIgnoreCase)
            || msg.Contains("token is invalid", StringComparison.OrdinalIgnoreCase)
            || msg.Contains("invalid token", StringComparison.OrdinalIgnoreCase)
            || msg.Contains("auth token", StringComparison.OrdinalIgnoreCase)
            || msg.Contains("403 forbidden", StringComparison.OrdinalIgnoreCase)
            || msg.Contains("HTTP 401", StringComparison.OrdinalIgnoreCase)
            || msg.Contains("not authorized", StringComparison.OrdinalIgnoreCase)
            || msg.Contains("bad credentials", StringComparison.OrdinalIgnoreCase)
            || msg.Contains("login required", StringComparison.OrdinalIgnoreCase))
            return true;
        if (ex is AggregateException agg)
            return agg.InnerExceptions.Any(IsAuthError);
        return ex.InnerException != null && IsAuthError(ex.InnerException);
    }

    /// <summary>
    /// Load conversation history from events.jsonl asynchronously with proper file sharing.
    /// Uses FileShare.ReadWrite to avoid contention with concurrent SDK writes during
    /// premature idle recovery scenarios.
    /// </summary>
    private async Task<List<ChatMessage>> LoadHistoryFromDiskAsync(string sessionId)
    {
        var history = new List<ChatMessage>();
        var eventsFile = Path.Combine(SessionStatePath, sessionId, "events.jsonl");
        
        if (!File.Exists(eventsFile))
            return history;

        try
        {
            // Track tool calls by ID so we can update them when complete
            var toolCallMessages = new Dictionary<string, ChatMessage>();

            // Open file with FileShare.ReadWrite to allow concurrent reads/writes
            using var stream = new FileStream(
                eventsFile,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite);
            using var reader = new StreamReader(stream);

            string? line;
            while ((line = await reader.ReadLineAsync().ConfigureAwait(false)) != null)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                
                if (!root.TryGetProperty("type", out var typeEl)) continue;
                var type = typeEl.GetString();
                
                if (!root.TryGetProperty("data", out var data)) continue;
                var timestamp = DateTime.Now;
                if (root.TryGetProperty("timestamp", out var tsEl))
                    DateTime.TryParse(tsEl.GetString(), out timestamp);

                switch (type)
                {
                    case "user.message":
                    {
                        if (data.TryGetProperty("content", out var userContent))
                        {
                            var msgContent = userContent.GetString();
                            if (!string.IsNullOrEmpty(msgContent))
                            {
                                var msg = ChatMessage.UserMessage(msgContent);
                                msg.Timestamp = timestamp;
                                history.Add(msg);
                            }
                        }
                        break;
                    }

                    case "assistant.message":
                    {
                        // Add reasoning if present
                        if (data.TryGetProperty("reasoningText", out var reasoningEl))
                        {
                            var reasoning = reasoningEl.GetString();
                            if (!string.IsNullOrEmpty(reasoning))
                            {
                                var msg = ChatMessage.ReasoningMessage("restored");
                                msg.Content = reasoning;
                                msg.IsComplete = true;
                                msg.IsCollapsed = true;
                                msg.Timestamp = timestamp;
                                history.Add(msg);
                            }
                        }

                        // Add assistant text content (skip if only tool requests with no text)
                        if (data.TryGetProperty("content", out var assistantContent))
                        {
                            var msgContent = assistantContent.GetString()?.Trim();
                            if (!string.IsNullOrEmpty(msgContent))
                            {
                                var msg = ChatMessage.AssistantMessage(msgContent);
                                msg.Timestamp = timestamp;
                                history.Add(msg);
                            }
                        }
                        break;
                    }

                    case "tool.execution_start":
                    {
                        var toolName = data.TryGetProperty("toolName", out var tn) ? tn.GetString() ?? "" : "";
                        var toolCallId = data.TryGetProperty("toolCallId", out var tc) ? tc.GetString() : null;
                        
                        // Skip report_intent — it's noise in history
                        if (toolName == "report_intent") break;

                        // Extract tool input if available
                        string? inputStr = null;
                        if (data.TryGetProperty("input", out var inputEl))
                            inputStr = inputEl.ToString();
                        else if (data.TryGetProperty("arguments", out var argsEl))
                            inputStr = argsEl.ToString();

                        var msg = ChatMessage.ToolCallMessage(toolName, toolCallId, inputStr);
                        msg.Timestamp = timestamp;
                        history.Add(msg);
                        if (toolCallId != null)
                            toolCallMessages[toolCallId] = msg;
                        break;
                    }

                    case "tool.execution_complete":
                    {
                        var toolCallId = data.TryGetProperty("toolCallId", out var tc) ? tc.GetString() : null;
                        if (toolCallId != null && toolCallMessages.TryGetValue(toolCallId, out var msg))
                        {
                            msg.IsComplete = true;
                            msg.IsSuccess = data.TryGetProperty("success", out var s) && s.GetBoolean();
                            msg.IsCollapsed = true;

                            if (data.TryGetProperty("result", out var result))
                            {
                                // Prefer detailedContent, fall back to content
                                var resultContent = result.TryGetProperty("detailedContent", out var dc) ? dc.GetString() : null;
                                if (string.IsNullOrEmpty(resultContent) && result.TryGetProperty("content", out var c))
                                    resultContent = c.GetString();
                                msg.Content = resultContent ?? "";
                            }
                        }
                        break;
                    }
                }
            }
        }
        catch
        {
            // Ignore parse errors, return what we have
        }

        return history;
    }

    /// <summary>
    /// Synchronous wrapper for LoadHistoryFromDiskAsync for callers that can't await.
    /// This is a temporary bridge; callers should be updated to use async version.
    /// </summary>
    private List<ChatMessage> LoadHistoryFromDisk(string sessionId)
    {
        // For synchronous contexts, use blocking wait on the async version
        // This is not ideal but maintains backward compatibility during transition
        try
        {
            return LoadHistoryFromDiskAsync(sessionId).GetAwaiter().GetResult();
        }
        catch
        {
            return new List<ChatMessage>();
        }
    }

    /// <summary>
    /// Load session history from the best available source: events.jsonl or chat_history.db.
    /// The SDK's event file writer can break after server-side session cleanup + re-resume,
    /// causing events to flow in-memory but never persist to events.jsonl ("dead event stream").
    /// ChatDatabase is written fire-and-forget on every message and survives this failure mode.
    /// 
    /// Strategy: compare the latest user message timestamp from each source. Whichever has
    /// the most recent user interaction wins entirely — no merging, no risk of duplicates.
    /// Returns (history, fromDatabase) — fromDatabase=true means events.jsonl was stale.
    /// </summary>
    private async Task<(List<ChatMessage> History, bool FromDatabase)> LoadBestHistoryAsync(string sessionId)
    {
        var eventsHistory = await LoadHistoryFromDiskAsync(sessionId);

        List<ChatMessage> dbHistory;
        try
        {
            dbHistory = await _chatDb.GetAllMessagesAsync(sessionId);
        }
        catch
        {
            return (eventsHistory, false);
        }

        if (dbHistory.Count == 0)
            return (eventsHistory, false);

        if (eventsHistory.Count == 0)
            return (dbHistory, true);

        // Compare the latest user message timestamp from each source.
        // Whichever has the most recent user interaction is the better source.
        var eventsLatestUser = eventsHistory
            .Where(m => m.MessageType == ChatMessageType.User)
            .MaxBy(m => m.Timestamp)?.Timestamp ?? DateTime.MinValue;

        var dbLatestUser = dbHistory
            .Where(m => m.MessageType == ChatMessageType.User)
            .MaxBy(m => m.Timestamp)?.Timestamp ?? DateTime.MinValue;

        if (dbLatestUser > eventsLatestUser && (dbLatestUser - eventsLatestUser).TotalSeconds > 5)
        {
            Debug($"[HISTORY-RECOVERY] ChatDatabase has newer messages (DB latest={dbLatestUser:u}, events latest={eventsLatestUser:u}) for session {sessionId} — using DB");
            return (dbHistory, true);
        }

        return (eventsHistory, false);
    }

    // Dock badge for completed sessions
    private int _badgeCount;

    private void IncrementBadge()
    {
#if MACCATALYST || IOS
        _badgeCount++;
        MainThread.BeginInvokeOnMainThread(() =>
        {
            try
            {
                if (OperatingSystem.IsIOSVersionAtLeast(16) || OperatingSystem.IsMacCatalystVersionAtLeast(16))
                    UserNotifications.UNUserNotificationCenter.Current.SetBadgeCount(_badgeCount, null);
            }
            catch { }
        });
#endif
    }

    public void ClearBadge()
    {
#if MACCATALYST || IOS
        _badgeCount = 0;
        MainThread.BeginInvokeOnMainThread(() =>
        {
            try
            {
                if (OperatingSystem.IsIOSVersionAtLeast(16) || OperatingSystem.IsMacCatalystVersionAtLeast(16))
                    UserNotifications.UNUserNotificationCenter.Current.SetBadgeCount(0, null);
            }
            catch { }
        });
#endif
    }

    private async Task FetchAvailableModelsAsync()
    {
        try
        {
            if (_client == null) return;
            var modelList = await _client.ListModelsAsync();
            if (modelList != null && modelList.Count > 0)
            {
                var models = modelList
                    .Where(m => !string.IsNullOrEmpty(m.Name))
                    .Select(m => m.Name!)
                    .OrderBy(m => m)
                    .ToList();
                if (models.Count > 0)
                {
                    AvailableModels = models;
                    Debug($"Loaded {models.Count} models from SDK");
                    OnStateChanged?.Invoke();
                }
            }
        }
        catch (Exception ex)
        {
            Debug($"Failed to fetch models: {ex.Message}");
        }
    }

    private async Task FetchGitHubUserInfoAsync()
    {
        try
        {
            using var process = new System.Diagnostics.Process();
            process.StartInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "gh",
                Arguments = "api user --jq \"{login: .login, avatar_url: .avatar_url}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            process.Start();
            var output = await process.StandardOutput.ReadToEndAsync();
            await process.WaitForExitAsync();
            if (process.ExitCode == 0 && !string.IsNullOrWhiteSpace(output))
            {
                using var doc = JsonDocument.Parse(output);
                GitHubLogin = doc.RootElement.GetProperty("login").GetString();
                GitHubAvatarUrl = doc.RootElement.GetProperty("avatar_url").GetString();
                Debug($"GitHub user: {GitHubLogin}");
                InvokeOnUI(() => OnStateChanged?.Invoke());
            }
        }
        catch (Exception ex)
        {
            Debug($"Failed to fetch GitHub user info: {ex.Message}");
        }
    }
}
