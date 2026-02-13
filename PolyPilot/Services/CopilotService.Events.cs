using System.Text;
using System.Text.Json;
using PolyPilot.Models;
using GitHub.Copilot.SDK;

namespace PolyPilot.Services;

public partial class CopilotService
{
    private static readonly HashSet<string> FilteredTools = new() { "report_intent", "skill", "store_memory" };

    private void HandleSessionEvent(SessionState state, SessionEvent evt)
    {
        state.HasReceivedEventsSinceResume = true;
        var sessionName = state.Info.Name;
        void Invoke(Action action)
        {
            if (_syncContext != null)
                _syncContext.Post(_ => action(), null);
            else
                action();
        }
        
        switch (evt)
        {
            case AssistantReasoningEvent reasoning:
                Invoke(() =>
                {
                    OnReasoningReceived?.Invoke(sessionName, reasoning.Data.ReasoningId ?? "", reasoning.Data.Content ?? "");
                });
                break;

            case AssistantReasoningDeltaEvent reasoningDelta:
                Invoke(() =>
                {
                    OnReasoningReceived?.Invoke(sessionName, reasoningDelta.Data.ReasoningId ?? "", reasoningDelta.Data.DeltaContent ?? "");
                });
                break;

            case AssistantMessageDeltaEvent delta:
                var deltaContent = delta.Data.DeltaContent;
                state.HasReceivedDeltasThisTurn = true;
                state.CurrentResponse.Append(deltaContent);
                Invoke(() => OnContentReceived?.Invoke(sessionName, deltaContent ?? ""));
                break;

            case AssistantMessageEvent msg:
                var msgContent = msg.Data.Content;
                var msgId = msg.Data.MessageId;
                // Deduplicate: SDK fires this event multiple times for resumed sessions
                if (!string.IsNullOrEmpty(msgContent) && !state.HasReceivedDeltasThisTurn && msgId != state.LastMessageId)
                {
                    state.LastMessageId = msgId;
                    state.CurrentResponse.Append(msgContent);
                    state.Info.LastUpdatedAt = DateTime.Now;
                    Invoke(() => OnContentReceived?.Invoke(sessionName, msgContent));
                }
                break;

            case ToolExecutionStartEvent toolStart:
                if (toolStart.Data == null) break;
                var startToolName = toolStart.Data.ToolName ?? "unknown";
                var startCallId = toolStart.Data.ToolCallId ?? "";
                var toolInput = ExtractToolInput(toolStart.Data);
                
                // Track skill invocations
                if (startToolName == "skill")
                {
                    try
                    {
                        var argsObj = toolStart.Data.GetType().GetProperty("Arguments")?.GetValue(toolStart.Data);
                        Console.WriteLine($"[SkillTrack] '{sessionName}' Arguments type: {argsObj?.GetType().Name}, value: {argsObj}");
                        if (argsObj is Dictionary<string, object> args && args.TryGetValue("skill", out var skillValue))
                        {
                            var skillName = skillValue?.ToString();
                            Console.WriteLine($"[SkillTrack] '{sessionName}' ✅ Extracted skill name: {skillName}");
                            state.Info.LastSkillUsed = skillName;
                            Invoke(() => OnStateChanged?.Invoke());
                        }
                        else
                        {
                            Console.WriteLine($"[SkillTrack] '{sessionName}' ❌ Failed to extract skill from args");
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[SkillTrack] '{sessionName}' ⚠️ Exception: {ex.Message}");
                    }
                }
                
                if (!FilteredTools.Contains(startToolName))
                {
                    // Deduplicate: SDK replays events on resume/reconnect — update existing
                    var existingTool = state.Info.History.FirstOrDefault(m => m.ToolCallId == startCallId);
                    if (existingTool != null)
                    {
                        // Update with potentially fresher data
                        if (!string.IsNullOrEmpty(toolInput)) existingTool.ToolInput = toolInput;
                        break;
                    }

                    // Flush any accumulated assistant text before adding tool message
                    FlushCurrentResponse(state);
                    
                    var toolMsg = ChatMessage.ToolCallMessage(startToolName, startCallId, toolInput);
                    state.Info.History.Add(toolMsg);
                    
                    Invoke(() =>
                    {
                        OnToolStarted?.Invoke(sessionName, startToolName, startCallId, toolInput);
                        OnActivity?.Invoke(sessionName, $"🔧 Running {startToolName}...");
                    });
                }
                else if (state.CurrentResponse.Length > 0)
                {
                    // Separate text blocks around filtered tools so they don't run together
                    state.CurrentResponse.Append("\n\n");
                }
                break;

            case ToolExecutionCompleteEvent toolDone:
                if (toolDone.Data == null) break;
                var completeCallId = toolDone.Data.ToolCallId ?? "";
                var completeToolName = toolDone.Data?.GetType().GetProperty("ToolName")?.GetValue(toolDone.Data)?.ToString();
                var resultStr = FormatToolResult(toolDone.Data!.Result);
                var hasError = toolDone.Data.Error != null;

                // Skip filtered tools
                if (completeToolName != null && FilteredTools.Contains(completeToolName))
                    break;
                if (resultStr == "Intent logged")
                    break;

                // Update the matching tool message in history
                var histToolMsg = state.Info.History.LastOrDefault(m => m.ToolCallId == completeCallId);
                if (histToolMsg != null)
                {
                    histToolMsg.IsComplete = true;
                    histToolMsg.IsSuccess = !hasError;
                    histToolMsg.Content = resultStr;
                }

                Invoke(() =>
                {
                    OnToolCompleted?.Invoke(sessionName, completeCallId, resultStr, !hasError);
                    OnActivity?.Invoke(sessionName, hasError ? "❌ Tool failed" : "✅ Tool completed");
                });
                break;

            case ToolExecutionProgressEvent:
                Invoke(() => OnActivity?.Invoke(sessionName, "⚙️ Tool executing..."));
                break;

            case AssistantIntentEvent intent:
                var intentText = intent.Data.Intent ?? "";
                Invoke(() =>
                {
                    OnIntentChanged?.Invoke(sessionName, intentText);
                    OnActivity?.Invoke(sessionName, $"💭 {intentText}");
                });
                break;

            case AssistantTurnStartEvent:
                state.HasReceivedDeltasThisTurn = false;
                Invoke(() =>
                {
                    OnTurnStart?.Invoke(sessionName);
                    OnActivity?.Invoke(sessionName, "🤔 Thinking...");
                });
                break;

            case AssistantTurnEndEvent:
                Invoke(() =>
                {
                    OnTurnEnd?.Invoke(sessionName);
                    OnActivity?.Invoke(sessionName, "");
                });
                break;

            case SessionIdleEvent:
                CompleteResponse(state);
                // Refresh git branch — agent may have switched branches
                state.Info.GitBranch = GetGitBranch(state.Info.WorkingDirectory);
                break;

            case SessionStartEvent start:
                state.Info.SessionId = start.Data.SessionId;
                Debug($"Session ID assigned: {start.Data.SessionId}");
                var startModel = start.Data?.GetType().GetProperty("SelectedModel")?.GetValue(start.Data)?.ToString();
                if (!string.IsNullOrEmpty(startModel))
                {
                    state.Info.Model = startModel;
                    Debug($"Session model from start event: {startModel}");
                }
                SaveActiveSessionsToDisk();
                break;

            case SessionUsageInfoEvent usageInfo:
                var uData = usageInfo.Data;
                if (uData != null)
                {
                    var uProps = string.Join(", ", uData.GetType().GetProperties().Select(p => $"{p.Name}={p.GetValue(uData)}({p.PropertyType.Name})"));
                    Debug($"[UsageInfo] '{sessionName}' all props: {uProps}");
                }
                var uModel = uData?.GetType().GetProperty("Model")?.GetValue(uData)?.ToString();
                var uCurrentTokensRaw = uData?.GetType().GetProperty("CurrentTokens")?.GetValue(uData);
                var uTokenLimitRaw = uData?.GetType().GetProperty("TokenLimit")?.GetValue(uData);
                var uInputTokensRaw = uData?.GetType().GetProperty("InputTokens")?.GetValue(uData);
                var uOutputTokensRaw = uData?.GetType().GetProperty("OutputTokens")?.GetValue(uData);
                Debug($"[UsageInfo] '{sessionName}' raw: CurrentTokens={uCurrentTokensRaw} ({uCurrentTokensRaw?.GetType().Name}), TokenLimit={uTokenLimitRaw} ({uTokenLimitRaw?.GetType().Name}), InputTokens={uInputTokensRaw}, OutputTokens={uOutputTokensRaw}");
                var uCurrentTokens = uCurrentTokensRaw != null ? (int?)Convert.ToInt32(uCurrentTokensRaw) : null;
                var uTokenLimit = uTokenLimitRaw != null ? (int?)Convert.ToInt32(uTokenLimitRaw) : null;
                var uInputTokens = uInputTokensRaw != null ? (int?)Convert.ToInt32(uInputTokensRaw) : null;
                var uOutputTokens = uOutputTokensRaw != null ? (int?)Convert.ToInt32(uOutputTokensRaw) : null;
                if (!string.IsNullOrEmpty(uModel))
                    state.Info.Model = uModel;
                if (uCurrentTokens.HasValue) state.Info.ContextCurrentTokens = uCurrentTokens;
                if (uTokenLimit.HasValue) state.Info.ContextTokenLimit = uTokenLimit;
                if (uInputTokens.HasValue) state.Info.TotalInputTokens += uInputTokens.Value;
                if (uOutputTokens.HasValue) state.Info.TotalOutputTokens += uOutputTokens.Value;
                Invoke(() => OnUsageInfoChanged?.Invoke(sessionName, new SessionUsageInfo(uModel, uCurrentTokens, uTokenLimit, uInputTokens, uOutputTokens)));
                break;

            case AssistantUsageEvent assistantUsage:
                var aData = assistantUsage.Data;
                var aModel = aData?.GetType().GetProperty("Model")?.GetValue(aData)?.ToString();
                var aInputRaw = aData?.GetType().GetProperty("InputTokens")?.GetValue(aData);
                var aOutputRaw = aData?.GetType().GetProperty("OutputTokens")?.GetValue(aData);
                var aInput = aInputRaw != null ? (int?)Convert.ToInt32(aInputRaw) : null;
                var aOutput = aOutputRaw != null ? (int?)Convert.ToInt32(aOutputRaw) : null;
                QuotaInfo? aPremiumQuota = null;
                try
                {
                    var quotaSnapshots = aData?.GetType().GetProperty("QuotaSnapshots")?.GetValue(aData);
                    if (quotaSnapshots is Dictionary<string, object> qs &&
                        qs.TryGetValue("premium_interactions", out var premiumObj) &&
                        premiumObj is System.Text.Json.JsonElement je)
                    {
                        var isUnlimited = je.TryGetProperty("isUnlimitedEntitlement", out var u) && u.GetBoolean();
                        var entitlement = je.TryGetProperty("entitlementRequests", out var e) ? e.GetInt32() : -1;
                        var used = je.TryGetProperty("usedRequests", out var ur) ? ur.GetInt32() : 0;
                        var remaining = je.TryGetProperty("remainingPercentage", out var rp) ? rp.GetInt32() : 100;
                        var resetDate = je.TryGetProperty("resetDate", out var rd) ? rd.GetString() : null;
                        aPremiumQuota = new QuotaInfo(isUnlimited, entitlement, used, remaining, resetDate);
                    }
                }
                catch { }
                if (!string.IsNullOrEmpty(aModel))
                    state.Info.Model = aModel;
                if (aInput.HasValue) state.Info.TotalInputTokens += aInput.Value;
                if (aOutput.HasValue) state.Info.TotalOutputTokens += aOutput.Value;
                if (aInput.HasValue || aOutput.HasValue || aPremiumQuota != null)
                {
                    Invoke(() => OnUsageInfoChanged?.Invoke(sessionName, new SessionUsageInfo(aModel, null, null, aInput, aOutput, aPremiumQuota)));
                }
                break;

            case SessionErrorEvent err:
                var errMsg = err.Data?.Message ?? "Unknown error";
                Invoke(() => OnError?.Invoke(sessionName, errMsg));
                state.ResponseCompletion?.TrySetException(new Exception(errMsg));
                state.Info.IsProcessing = false;
                Invoke(() => OnStateChanged?.Invoke());
                break;

            case SessionModelChangeEvent modelChange:
                var newModel = modelChange.Data?.NewModel;
                if (!string.IsNullOrEmpty(newModel))
                {
                    state.Info.Model = newModel;
                    Debug($"Session '{sessionName}' model changed to: {newModel}");
                    Invoke(() => OnUsageInfoChanged?.Invoke(sessionName, new SessionUsageInfo(newModel, null, null, null, null)));
                    Invoke(() => OnStateChanged?.Invoke());
                }
                break;
                
            default:
                break;
        }
    }

    private static string FormatToolResult(object? result)
    {
        if (result == null) return "";
        if (result is string str) return str;
        try
        {
            var resultType = result.GetType();
            // Prefer DetailedContent (has richer info like file paths) over Content
            foreach (var propName in new[] { "DetailedContent", "detailedContent", "Content", "content", "Message", "message", "Text", "text", "Value", "value" })
            {
                var prop = resultType.GetProperty(propName);
                if (prop != null)
                {
                    var val = prop.GetValue(result)?.ToString();
                    if (!string.IsNullOrEmpty(val)) return val;
                }
            }
            var json = JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true });
            if (json != "{}" && json != "null") return json;
        }
        catch { }
        return result.ToString() ?? "";
    }

    private static string? ExtractToolInput(object? data)
    {
        if (data == null) return null;
        try
        {
            var type = data.GetType();
            // Try common property names for tool input/arguments
            foreach (var propName in new[] { "Input", "Arguments", "Args", "Parameters", "input", "arguments" })
            {
                var prop = type.GetProperty(propName);
                if (prop == null) continue;
                var val = prop.GetValue(data);
                if (val == null) continue;
                if (val is string s && !string.IsNullOrEmpty(s)) return s;
                try
                {
                    var json = JsonSerializer.Serialize(val, new JsonSerializerOptions { WriteIndented = false });
                    if (json != "{}" && json != "null" && json != "\"\"") return json;
                }
                catch { return val.ToString(); }
            }
        }
        catch { }
        return null;
    }

    private void TryAttachImages(MessageOptions options, List<string> imagePaths)
    {
        try
        {
            var attachments = new List<UserMessageDataAttachmentsItem>();
            foreach (var path in imagePaths)
            {
                if (!File.Exists(path)) continue;
                var fileItem = new UserMessageDataAttachmentsItemFile
                {
                    Path = path,
                    DisplayName = System.IO.Path.GetFileName(path)
                };
                attachments.Add(fileItem);
            }

            if (attachments.Count == 0) return;
            options.Attachments = attachments;
            Debug($"Attached {attachments.Count} image(s) via SDK");
        }
        catch (Exception ex)
        {
            Debug($"Failed to attach images via SDK: {ex.Message}");
        }
    }

    /// <summary>Flush accumulated assistant text to history without ending the turn.</summary>
    private void FlushCurrentResponse(SessionState state)
    {
        var text = state.CurrentResponse.ToString();
        if (string.IsNullOrEmpty(text)) return;
        
        var msg = new ChatMessage("assistant", text, DateTime.Now);
        state.Info.History.Add(msg);
        state.Info.MessageCount = state.Info.History.Count;
        
        if (!string.IsNullOrEmpty(state.Info.SessionId))
            _ = _chatDb.AddMessageAsync(state.Info.SessionId, msg);
        
        state.CurrentResponse.Clear();
        state.HasReceivedDeltasThisTurn = false;
    }

    private void CompleteResponse(SessionState state)
    {
        if (!state.Info.IsProcessing) return; // Already completed (e.g. timeout)
        
        var response = state.CurrentResponse.ToString();
        if (!string.IsNullOrEmpty(response))
        {
            var msg = new ChatMessage("assistant", response, DateTime.Now);
            state.Info.History.Add(msg);
            state.Info.MessageCount = state.Info.History.Count;

            // Write-through to DB
            if (!string.IsNullOrEmpty(state.Info.SessionId))
                _ = _chatDb.AddMessageAsync(state.Info.SessionId, msg);
        }
        state.ResponseCompletion?.TrySetResult(response);
        state.CurrentResponse.Clear();
        state.Info.IsProcessing = false;
        state.Info.LastUpdatedAt = DateTime.Now;
        OnStateChanged?.Invoke();
        
        // Fire completion notification
        var summary = response.Length > 100 ? response[..100] + "..." : response;
        OnSessionComplete?.Invoke(state.Info.Name, summary);
        IncrementBadge();

        // Auto-dispatch next queued message
        if (state.Info.MessageQueue.Count > 0)
        {
            var nextPrompt = state.Info.MessageQueue[0];
            state.Info.MessageQueue.RemoveAt(0);
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(500);
                    await SendPromptAsync(state.Info.Name, nextPrompt);
                }
                catch (Exception ex)
                {
                    Debug($"Failed to send queued message: {ex.Message}");
                    OnError?.Invoke(state.Info.Name, $"Queued message failed: {ex.Message}");
                }
            });
        }
    }
}
