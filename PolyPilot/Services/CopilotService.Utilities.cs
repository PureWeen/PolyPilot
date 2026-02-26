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
    /// Load conversation history from events.jsonl
    /// </summary>
    private List<ChatMessage> LoadHistoryFromDisk(string sessionId)
    {
        var history = new List<ChatMessage>();
        var eventsFile = Path.Combine(SessionStatePath, sessionId, "events.jsonl");
        
        if (!File.Exists(eventsFile))
            return history;

        try
        {
            // Track tool calls by ID so we can update them when complete
            var toolCallMessages = new Dictionary<string, ChatMessage>();

            foreach (var line in File.ReadLines(eventsFile))
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
