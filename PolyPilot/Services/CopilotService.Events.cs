using System.Text;
using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using PolyPilot.Models;
using GitHub.Copilot.SDK;

namespace PolyPilot.Services;

public partial class CopilotService
{
    private static readonly HashSet<string> FilteredTools = new() { "report_intent", "skill", "store_memory" };
    private readonly ConcurrentDictionary<string, byte> _loggedUnhandledSessionEvents = new(StringComparer.Ordinal);

    private enum EventVisibility
    {
        Ignore,
        TimelineOnly,
        ChatVisible
    }

    private static readonly IReadOnlyDictionary<string, EventVisibility> SdkEventMatrix = new Dictionary<string, EventVisibility>(StringComparer.Ordinal)
    {
        // Core chat projection
        ["UserMessageEvent"] = EventVisibility.ChatVisible,
        [nameof(AssistantTurnStartEvent)] = EventVisibility.ChatVisible,
        [nameof(AssistantReasoningEvent)] = EventVisibility.ChatVisible,
        [nameof(AssistantReasoningDeltaEvent)] = EventVisibility.ChatVisible,
        [nameof(AssistantMessageDeltaEvent)] = EventVisibility.ChatVisible,
        [nameof(AssistantMessageEvent)] = EventVisibility.ChatVisible,
        [nameof(ToolExecutionStartEvent)] = EventVisibility.ChatVisible,
        [nameof(ToolExecutionProgressEvent)] = EventVisibility.ChatVisible,
        [nameof(ToolExecutionCompleteEvent)] = EventVisibility.ChatVisible,
        [nameof(AssistantIntentEvent)] = EventVisibility.ChatVisible,
        [nameof(AssistantTurnEndEvent)] = EventVisibility.ChatVisible,
        [nameof(SessionIdleEvent)] = EventVisibility.ChatVisible,
        [nameof(SessionErrorEvent)] = EventVisibility.ChatVisible,
        ["SystemMessageEvent"] = EventVisibility.ChatVisible,
        ["ToolExecutionPartialResultEvent"] = EventVisibility.ChatVisible,
        ["AbortEvent"] = EventVisibility.ChatVisible,

        // Session state / metadata timeline
        [nameof(SessionStartEvent)] = EventVisibility.TimelineOnly,
        [nameof(SessionModelChangeEvent)] = EventVisibility.TimelineOnly,
        [nameof(SessionUsageInfoEvent)] = EventVisibility.TimelineOnly,
        [nameof(AssistantUsageEvent)] = EventVisibility.TimelineOnly,
        ["SessionInfoEvent"] = EventVisibility.TimelineOnly,
        ["SessionResumeEvent"] = EventVisibility.TimelineOnly,
        ["SessionHandoffEvent"] = EventVisibility.TimelineOnly,
        ["SessionShutdownEvent"] = EventVisibility.TimelineOnly,
        ["SessionSnapshotRewindEvent"] = EventVisibility.TimelineOnly,
        ["SessionTruncationEvent"] = EventVisibility.TimelineOnly,
        ["SessionCompactionStartEvent"] = EventVisibility.TimelineOnly,
        ["SessionCompactionCompleteEvent"] = EventVisibility.TimelineOnly,
        ["PendingMessagesModifiedEvent"] = EventVisibility.TimelineOnly,
        ["ToolUserRequestedEvent"] = EventVisibility.TimelineOnly,
        ["SkillInvokedEvent"] = EventVisibility.TimelineOnly,
        ["SubagentSelectedEvent"] = EventVisibility.TimelineOnly,
        ["SubagentStartedEvent"] = EventVisibility.TimelineOnly,
        ["SubagentCompletedEvent"] = EventVisibility.TimelineOnly,
        ["SubagentFailedEvent"] = EventVisibility.TimelineOnly,

        // Currently noisy internal events
        ["SessionLifecycleEvent"] = EventVisibility.Ignore,
        ["HookStartEvent"] = EventVisibility.Ignore,
        ["HookEndEvent"] = EventVisibility.Ignore,
    };

    private static EventVisibility ClassifySessionEvent(SessionEvent evt)
    {
        var eventTypeName = evt.GetType().Name;
        return SdkEventMatrix.TryGetValue(eventTypeName, out var classification)
            ? classification
            : EventVisibility.TimelineOnly;
    }

    private void LogUnhandledSessionEvent(string sessionName, SessionEvent evt)
    {
        var eventTypeName = evt.GetType().Name;
        if (!_loggedUnhandledSessionEvents.TryAdd(eventTypeName, 0)) return;
        var classification = ClassifySessionEvent(evt);
        Debug($"[EventMatrix] Unhandled {eventTypeName} ({classification}) for '{sessionName}'");
    }

    private static ChatMessage? FindReasoningMessage(AgentSessionInfo info, string reasoningId)
    {
        // Exact ID match first, then most recent incomplete reasoning message.
        if (!string.IsNullOrEmpty(reasoningId))
        {
            var exact = info.History.LastOrDefault(m =>
                m.MessageType == ChatMessageType.Reasoning &&
                string.Equals(m.ReasoningId, reasoningId, StringComparison.Ordinal));
            if (exact != null) return exact;
        }

        return info.History.LastOrDefault(m =>
            m.MessageType == ChatMessageType.Reasoning &&
            !m.IsComplete);
    }

    private static string ResolveReasoningId(AgentSessionInfo info, string? reasoningId)
    {
        if (!string.IsNullOrWhiteSpace(reasoningId)) return reasoningId;

        var existing = info.History.LastOrDefault(m =>
            m.MessageType == ChatMessageType.Reasoning &&
            !m.IsComplete &&
            !string.IsNullOrEmpty(m.ReasoningId));
        return existing?.ReasoningId ?? $"reasoning-{Guid.NewGuid():N}";
    }

    private static void MergeReasoningContent(ChatMessage message, string content, bool isDelta)
    {
        if (string.IsNullOrEmpty(content)) return;

        if (isDelta)
        {
            message.Content += content;
            return;
        }

        // AssistantReasoningEvent can arrive as full snapshots or chunks depending on SDK version.
        if (string.IsNullOrEmpty(message.Content) ||
            content.Length >= message.Content.Length ||
            content.StartsWith(message.Content, StringComparison.Ordinal))
        {
            message.Content = content;
        }
        else if (!message.Content.EndsWith(content, StringComparison.Ordinal))
        {
            message.Content += content;
        }
    }

    private void ApplyReasoningUpdate(SessionState state, string sessionName, string? reasoningId, string? content, bool isDelta)
    {
        if (string.IsNullOrEmpty(content)) return;

        var normalizedReasoningId = ResolveReasoningId(state.Info, reasoningId);
        var reasoningMsg = FindReasoningMessage(state.Info, normalizedReasoningId);
        var isNew = false;
        if (reasoningMsg == null)
        {
            reasoningMsg = ChatMessage.ReasoningMessage(normalizedReasoningId);
            state.Info.History.Add(reasoningMsg);
            state.Info.MessageCount = state.Info.History.Count;
            isNew = true;
        }

        reasoningMsg.ReasoningId = normalizedReasoningId;
        reasoningMsg.IsComplete = false;
        reasoningMsg.IsCollapsed = false;
        reasoningMsg.Timestamp = DateTime.Now;
        MergeReasoningContent(reasoningMsg, content, isDelta);
        state.Info.LastUpdatedAt = DateTime.Now;

        if (!string.IsNullOrEmpty(state.Info.SessionId))
        {
            if (isNew)
                _ = _chatDb.AddMessageAsync(state.Info.SessionId, reasoningMsg);
            else
                _ = _chatDb.UpdateReasoningContentAsync(state.Info.SessionId, normalizedReasoningId, reasoningMsg.Content, false);
        }

        InvokeOnUI(() => OnReasoningReceived?.Invoke(sessionName, normalizedReasoningId, content));
    }

    private void CompleteReasoningMessages(SessionState state, string sessionName)
    {
        var openReasoningMessages = state.Info.History
            .Where(m => m.MessageType == ChatMessageType.Reasoning && !m.IsComplete)
            .ToList();
        if (openReasoningMessages.Count == 0) return;

        var completedIds = new List<string>();
        foreach (var msg in openReasoningMessages)
        {
            msg.IsComplete = true;
            msg.IsCollapsed = true;
            msg.Timestamp = DateTime.Now;
            if (!string.IsNullOrEmpty(msg.ReasoningId))
            {
                completedIds.Add(msg.ReasoningId);
                if (!string.IsNullOrEmpty(state.Info.SessionId))
                    _ = _chatDb.UpdateReasoningContentAsync(state.Info.SessionId, msg.ReasoningId, msg.Content, true);
            }
        }

        state.Info.LastUpdatedAt = DateTime.Now;
        InvokeOnUI(() =>
        {
            foreach (var reasoningId in completedIds)
                OnReasoningComplete?.Invoke(sessionName, reasoningId);
        });
    }

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
                ApplyReasoningUpdate(state, sessionName, reasoning.Data.ReasoningId, reasoning.Data.Content, isDelta: false);
                break;

            case AssistantReasoningDeltaEvent reasoningDelta:
                ApplyReasoningUpdate(state, sessionName, reasoningDelta.Data.ReasoningId, reasoningDelta.Data.DeltaContent, isDelta: true);
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
                CompleteReasoningMessages(state, sessionName);
                Invoke(() =>
                {
                    OnTurnEnd?.Invoke(sessionName);
                    OnActivity?.Invoke(sessionName, "");
                });
                break;

            case SessionIdleEvent:
                CompleteReasoningMessages(state, sessionName);
                Invoke(() => CompleteResponse(state));
                // Refresh git branch — agent may have switched branches
                state.Info.GitBranch = GetGitBranch(state.Info.WorkingDirectory);
                // Send notification when agent finishes
                _ = Task.Run(async () =>
                {
                    try
                    {
                        var currentSettings = ConnectionSettings.Load();
                        if (!currentSettings.EnableSessionNotifications) return;
                        var notifService = _serviceProvider?.GetService<INotificationManagerService>();
                        if (notifService == null || !notifService.HasPermission) return;
                        var lastMsg = state.Info.History.LastOrDefault(m => m.Role == "assistant");
                        var body = BuildNotificationBody(lastMsg?.Content, state.Info.History.Count);
                        await notifService.SendNotificationAsync(
                            $"✓ {sessionName}",
                            body,
                            state.Info.SessionId);
                    }
                    catch { }
                });
                break;

            case SessionStartEvent start:
                state.Info.SessionId = start.Data.SessionId;
                Debug($"Session ID assigned: {start.Data.SessionId}");
                var startModel = start.Data?.GetType().GetProperty("SelectedModel")?.GetValue(start.Data)?.ToString();
                if (!string.IsNullOrEmpty(startModel))
                {
                    var normalizedStartModel = Models.ModelHelper.NormalizeToSlug(startModel);
                    state.Info.Model = normalizedStartModel;
                    Debug($"Session model from start event: {startModel} → {normalizedStartModel}");
                }
                if (!IsRestoring) SaveActiveSessionsToDisk();
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
                {
                    var normalizedUModel = Models.ModelHelper.NormalizeToSlug(uModel);
                    Debug($"[UsageInfo] Updating model from event: {state.Info.Model} -> {normalizedUModel}");
                    state.Info.Model = normalizedUModel;
                }
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
                    state.Info.Model = Models.ModelHelper.NormalizeToSlug(aModel);
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
                    newModel = Models.ModelHelper.NormalizeToSlug(newModel);
                    state.Info.Model = newModel;
                    Debug($"Session '{sessionName}' model changed to: {newModel}");
                    Invoke(() => OnUsageInfoChanged?.Invoke(sessionName, new SessionUsageInfo(newModel, null, null, null, null)));
                    Invoke(() => OnStateChanged?.Invoke());
                }
                break;
                
            default:
                LogUnhandledSessionEvent(sessionName, evt);
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
            // If user is viewing this session, keep it read
            if (state.Info.Name == _activeSessionName)
                state.Info.LastReadMessageCount = state.Info.History.Count;

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

        // Reflection cycle: evaluate response and enqueue follow-up if goal not yet met
        var cycle = state.Info.ReflectionCycle;
        if (cycle != null && cycle.IsActive)
        {
            if (state.SkipReflectionEvaluationOnce)
            {
                state.SkipReflectionEvaluationOnce = false;
                Debug($"Reflection cycle for '{state.Info.Name}' will begin after queued goal prompt.");
            }
            else if (!string.IsNullOrEmpty(response))
            {
                var shouldContinue = cycle.Advance(response);

                if (cycle.ShouldWarnOnStall)
                {
                    var pct = cycle.LastSimilarity;
                    var stallWarning = ChatMessage.SystemMessage($"⚠️ Potential stall — {pct:P0} similarity with previous response. If the next response is also repetitive, the cycle will stop.");
                    state.Info.History.Add(stallWarning);
                    state.Info.MessageCount = state.Info.History.Count;
                    if (!string.IsNullOrEmpty(state.Info.SessionId))
                        _ = _chatDb.AddMessageAsync(state.Info.SessionId, stallWarning);
                }

                if (shouldContinue)
                {
                    // Context usage warning during reflection
                    if (state.Info.ContextTokenLimit.HasValue && state.Info.ContextTokenLimit.Value > 0 
                        && state.Info.ContextCurrentTokens.HasValue && state.Info.ContextCurrentTokens.Value > 0)
                    {
                        var ctxPct = (double)state.Info.ContextCurrentTokens.Value / state.Info.ContextTokenLimit.Value;
                        if (ctxPct > 0.9)
                        {
                            var ctxWarning = ChatMessage.SystemMessage($"🔴 Context {ctxPct:P0} full — reflection may lose earlier history. Consider `/reflect stop`.");
                            state.Info.History.Add(ctxWarning);
                            state.Info.MessageCount = state.Info.History.Count;
                            if (!string.IsNullOrEmpty(state.Info.SessionId))
                                _ = _chatDb.AddMessageAsync(state.Info.SessionId, ctxWarning);
                        }
                        else if (ctxPct > 0.7)
                        {
                            var ctxWarning = ChatMessage.SystemMessage($"🟡 Context {ctxPct:P0} used — {cycle.MaxIterations - cycle.CurrentIteration} iterations remaining.");
                            state.Info.History.Add(ctxWarning);
                            state.Info.MessageCount = state.Info.History.Count;
                            if (!string.IsNullOrEmpty(state.Info.SessionId))
                                _ = _chatDb.AddMessageAsync(state.Info.SessionId, ctxWarning);
                        }
                    }

                    var followUp = cycle.BuildFollowUpPrompt(response);
                    Debug($"Reflection cycle iteration {cycle.CurrentIteration}/{cycle.MaxIterations} for '{state.Info.Name}'");

                    var reflectionMsg = ChatMessage.ReflectionMessage(cycle.BuildFollowUpStatus());
                    state.Info.History.Add(reflectionMsg);
                    state.Info.MessageCount = state.Info.History.Count;
                    if (!string.IsNullOrEmpty(state.Info.SessionId))
                        _ = _chatDb.AddMessageAsync(state.Info.SessionId, reflectionMsg);

                    // Keep queue FIFO so user steering messages queued during this turn run first.
                    state.Info.MessageQueue.Add(followUp);
                    OnStateChanged?.Invoke();
                }
                else if (!cycle.IsActive)
                {
                    var reason = cycle.GoalMet ? "goal met" : cycle.IsStalled ? "stalled" : "max iterations reached";
                    Debug($"Reflection cycle ended for '{state.Info.Name}': {reason}");
                    var completionMsg = ChatMessage.SystemMessage(cycle.BuildCompletionSummary());
                    state.Info.History.Add(completionMsg);
                    state.Info.MessageCount = state.Info.History.Count;
                    if (!string.IsNullOrEmpty(state.Info.SessionId))
                        _ = _chatDb.AddMessageAsync(state.Info.SessionId, completionMsg);
                    OnStateChanged?.Invoke();
                }
            }
        }

        // Auto-dispatch next queued message — send immediately on the current
        // synchronization context to prevent other actors from racing for the session.
        if (state.Info.MessageQueue.Count > 0)
        {
            var nextPrompt = state.Info.MessageQueue[0];
            state.Info.MessageQueue.RemoveAt(0);
            // Retrieve any queued image paths for this message
            List<string>? nextImagePaths = null;
            if (_queuedImagePaths.TryGetValue(state.Info.Name, out var imageQueue) && imageQueue.Count > 0)
            {
                nextImagePaths = imageQueue[0];
                imageQueue.RemoveAt(0);
                if (imageQueue.Count == 0)
                    _queuedImagePaths.TryRemove(state.Info.Name, out _);
            }

            var skipHistory = state.Info.ReflectionCycle is { IsActive: true } &&
                              ReflectionCycle.IsReflectionFollowUpPrompt(nextPrompt);
            _ = SendPromptAsync(state.Info.Name, nextPrompt, imagePaths: nextImagePaths, skipHistoryMessage: skipHistory)
                .ContinueWith(t =>
                {
                    if (t.IsFaulted)
                    {
                        Debug($"Failed to send queued message: {t.Exception?.InnerException?.Message}");
                        // Re-queue on failure so it retries on next CompleteResponse
                        state.Info.MessageQueue.Insert(0, nextPrompt);
                        if (nextImagePaths != null)
                        {
                            var images = _queuedImagePaths.GetOrAdd(state.Info.Name, _ => new List<List<string>>());
                            images.Insert(0, nextImagePaths);
                        }
                    }
                });
        }
    }

    private static string BuildNotificationBody(string? content, int messageCount)
    {
        if (string.IsNullOrWhiteSpace(content))
            return $"Agent finished · {messageCount} messages";

        // Strip markdown formatting for cleaner notification text
        var text = content
            .Replace("**", "").Replace("__", "")
            .Replace("```", "").Replace("`", "")
            .Replace("###", "").Replace("##", "").Replace("#", "")
            .Replace("\r", "");

        // Get first non-empty line as summary
        var firstLine = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault(l => l.Length > 5 && !l.StartsWith("---") && !l.StartsWith("- ["));

        if (string.IsNullOrEmpty(firstLine))
            return $"Agent finished · {messageCount} messages";

        if (firstLine.Length > 120)
            firstLine = firstLine[..117] + "…";

        return firstLine;
    }
}
