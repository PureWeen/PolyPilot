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

        // Check pending map first (covers the race window before InvokeOnUI fires)
        var reasoningMsg = state.PendingReasoningMessages.GetValueOrDefault(normalizedReasoningId)
            ?? FindReasoningMessage(state.Info, normalizedReasoningId);
        var isNew = false;
        if (reasoningMsg == null)
        {
            reasoningMsg = ChatMessage.ReasoningMessage(normalizedReasoningId);
            // Register in pending map BEFORE posting to UI thread — this prevents
            // rapid consecutive deltas from creating duplicates
            state.PendingReasoningMessages[normalizedReasoningId] = reasoningMsg;
            // Must add to History on UI thread to avoid concurrent List<T> mutation
            InvokeOnUI(() =>
            {
                state.Info.History.Add(reasoningMsg);
                state.Info.MessageCount = state.Info.History.Count;
                // Remove from pending — now findable via History search
                state.PendingReasoningMessages.TryRemove(normalizedReasoningId, out _);
            });
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
        Volatile.Write(ref state.HasReceivedEventsSinceResume, true);
        // Don't reset the watchdog timer for pure metrics/info events (SessionUsageInfoEvent,
        // AssistantUsageEvent). These are informational only and don't indicate actual turn
        // progress. If the SDK enters a state where it sends repeated usage info events
        // without ever completing the turn (e.g., FailedDelegation), the watchdog must still
        // fire based on lack of real progress events.
        if (evt is not SessionUsageInfoEvent and not AssistantUsageEvent)
        {
            Interlocked.Exchange(ref state.LastEventAtTicks, DateTime.UtcNow.Ticks);
            state.Info.LastUpdatedAt = DateTime.Now;
        }
        var sessionName = state.Info.Name;
        var isCurrentState = _sessions.TryGetValue(sessionName, out var current) && ReferenceEquals(current, state);

        // Log critical lifecycle events and detect orphaned handlers
        if (evt is SessionIdleEvent or AssistantTurnEndEvent or SessionErrorEvent)
        {
            Debug($"[EVT] '{sessionName}' received {evt.GetType().Name} " +
                  $"(IsProcessing={state.Info.IsProcessing}, isCurrentState={isCurrentState}, " +
                  $"thread={Environment.CurrentManagedThreadId})");
        }

        // Warn if receiving events on an orphaned (replaced) state object.
        // After the generation-carry fix, stale callbacks on orphaned state would have
        // matching generations and could incorrectly complete the new turn. Gate all
        // terminal/mutating events to only fire on the current (live) state.
        if (!isCurrentState)
        {
            Debug($"[EVT-WARN] '{sessionName}' event {evt.GetType().Name} delivered to ORPHANED state " +
                  $"(not in _sessions). This handler should have been detached.");
            // Block ALL events from orphaned state — stale deltas, tool events, and
            // terminal events can all produce ghost mutations on shared Info.History.
            return;
        }

        void Invoke(Action action)
        {
            if (_syncContext != null)
            {
                _syncContext.Post(_ =>
                {
                    try { action(); }
                    catch (Exception ex)
                    {
                        Debug($"[EVT-ERR] '{sessionName}' SyncContext.Post callback threw: {ex}");
                    }
                }, null);
            }
            else
            {
                try { action(); }
                catch (Exception ex)
                {
                    Debug($"[EVT-ERR] '{sessionName}' inline callback threw: {ex}");
                }
            }
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
                Interlocked.Increment(ref state.ActiveToolCallCount);
                Volatile.Write(ref state.HasUsedToolsThisTurn, true);
                if (state.Info.ProcessingPhase < 3)
                {
                    state.Info.ProcessingPhase = 3; // Working
                    Invoke(() => OnStateChanged?.Invoke());
                }
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

                    // Flush any accumulated assistant text before adding tool message,
                    // then add the tool message — all on the UI thread to avoid
                    // concurrent writes to List<ChatMessage> (History).
                    if (startToolName == ShowImageTool.ToolName)
                    {
                        var imgPlaceholder = ChatMessage.ToolCallMessage(startToolName, startCallId, toolInput);
                        Invoke(() =>
                        {
                            FlushCurrentResponse(state);
                            state.Info.History.Add(imgPlaceholder);
                            OnToolStarted?.Invoke(sessionName, startToolName, startCallId, toolInput);
                        });
                    }
                    else
                    {
                        var toolMsg = ChatMessage.ToolCallMessage(startToolName, startCallId, toolInput);
                        Invoke(() =>
                        {
                            FlushCurrentResponse(state);
                            state.Info.History.Add(toolMsg);
                            OnToolStarted?.Invoke(sessionName, startToolName, startCallId, toolInput);
                            OnActivity?.Invoke(sessionName, $"🔧 Running {startToolName}...");
                        });
                    }
                }
                else if (state.CurrentResponse.Length > 0)
                {
                    // Separate text blocks around filtered tools so they don't run together
                    state.CurrentResponse.Append("\n\n");
                }
                break;

            case ToolExecutionCompleteEvent toolDone:
                if (toolDone.Data == null) break;
                Interlocked.Decrement(ref state.ActiveToolCallCount);
                Interlocked.Increment(ref state.Info._toolCallCount);
                var completeCallId = toolDone.Data.ToolCallId ?? "";
                var completeToolName = toolDone.Data?.GetType().GetProperty("ToolName")?.GetValue(toolDone.Data)?.ToString();
                var resultStr = FormatToolResult(toolDone.Data!.Result);
                var hasError = toolDone.Data.Error != null;
                var errorStr = toolDone.Data.Error?.ToString();
                var isPermissionDenial = IsPermissionDenialText(resultStr) || IsPermissionDenialText(errorStr);

                // Track permission denials via sliding window (3 of last 5 tool results)
                // This handles cases where an occasional OK tool resets a strict consecutive counter
                if (isPermissionDenial || !hasError)
                {
                    var denialCount = state.Info.RecordToolResult(isPermissionDenial);
                    if (!isPermissionDenial && !hasError)
                        Interlocked.Increment(ref state.SuccessfulToolCountThisTurn);
                    if (isPermissionDenial && denialCount == 3)
                    {
                        Invoke(() =>
                        {
                            state.Info.History.Add(ChatMessage.SystemMessage(
                                "⚠️ Permission errors detected. Attempting to reconnect session..."));
                            OnStateChanged?.Invoke();
                            _ = TryRecoverPermissionAsync(state, sessionName);
                        });
                    }
                }

                // Skip filtered tools
                if (completeToolName != null && FilteredTools.Contains(completeToolName))
                    break;
                if (resultStr == "Intent logged")
                    break;

                // Update the matching tool message in history
                var histToolMsg = state.Info.History.LastOrDefault(m => m.ToolCallId == completeCallId);
                if (histToolMsg != null)
                {
                    var effectiveToolName = completeToolName ?? histToolMsg.ToolName;
                    if (effectiveToolName == ShowImageTool.ToolName && !hasError)
                    {
                        // Convert tool call placeholder into an Image message
                        (string? imgPath, string? imgCaption) = ShowImageTool.ParseResult(resultStr);
                        histToolMsg.MessageType = ChatMessageType.Image;
                        histToolMsg.ImagePath = imgPath;
                        histToolMsg.Caption = imgCaption;
                        histToolMsg.IsComplete = true;
                        histToolMsg.IsSuccess = true;
                        histToolMsg.Content = resultStr;
                    }
                    else
                    {
                        histToolMsg.IsComplete = true;
                        histToolMsg.IsSuccess = !hasError;
                        histToolMsg.Content = resultStr;
                    }
                }

                Invoke(() =>
                {
                    if (isPermissionDenial)
                        OnStateChanged?.Invoke();
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
                // Cancel any pending TurnEnd→Idle fallback — another agent round is starting
                CancelTurnEndFallback(state);
                state.HasReceivedDeltasThisTurn = false;
                var phaseAdvancedToThinking = state.Info.ProcessingPhase < 2;
                if (phaseAdvancedToThinking) state.Info.ProcessingPhase = 2; // Thinking
                Interlocked.Exchange(ref state.ActiveToolCallCount, 0);
                Invoke(() =>
                {
                    OnTurnStart?.Invoke(sessionName);
                    OnActivity?.Invoke(sessionName, "🤔 Thinking...");
                    if (phaseAdvancedToThinking) OnStateChanged?.Invoke();
                });
                break;

            case AssistantTurnEndEvent:
                try { CompleteReasoningMessages(state, sessionName); }
                catch (Exception ex)
                {
                    Debug($"[EVT-ERR] '{sessionName}' CompleteReasoningMessages threw in TurnEnd: {ex}");
                }
                // Schedule a delayed CompleteResponse in case SessionIdleEvent never arrives (SDK bug #299).
                // Cancelled by AssistantTurnStartEvent (another round starting) or SessionIdleEvent (normal path).
                {
                    var turnEndGen = Interlocked.Read(ref state.ProcessingGeneration);
                    var idleFallbackCts = new CancellationTokenSource();
                    // Capture token BEFORE publishing so CancelTurnEndFallback on another thread
                    // cannot dispose the CTS before we read .Token (benign but fragile otherwise).
                    var fallbackToken = idleFallbackCts.Token;
                    // Cancel any previous fallback and install the new one atomically
                    var prevCts = Interlocked.Exchange(ref state.TurnEndIdleCts, idleFallbackCts);
                    prevCts?.Cancel();
                    prevCts?.Dispose();
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            await Task.Delay(TurnEndIdleFallbackMs, fallbackToken);
                            if (fallbackToken.IsCancellationRequested) return;
                            // Guard: if tools are still active, a TurnStart is coming — skip.
                            if (Interlocked.CompareExchange(ref state.ActiveToolCallCount, 0, 0) > 0)
                            {
                                Debug($"[IDLE-FALLBACK] '{sessionName}' skipped — tools still active");
                                return;
                            }
                            // Guard: if tools were used this turn, the LLM may still be reasoning
                            // between tool rounds (TurnEnd → thinking → TurnStart, >4s).
                            // HasUsedToolsThisTurn stays true across all sub-turns and is only
                            // cleared at CompleteResponse/abort.
                            if (Volatile.Read(ref state.HasUsedToolsThisTurn))
                            {
                                Debug($"[IDLE-FALLBACK] '{sessionName}' skipped — tools were used this turn, TurnStart likely coming");
                                return;
                            }
                            Debug($"[IDLE-FALLBACK] '{sessionName}' SessionIdleEvent not received {TurnEndIdleFallbackMs}ms after TurnEnd — firing CompleteResponse");
                            Invoke(() => CompleteResponse(state, turnEndGen));
                        }
                        catch (OperationCanceledException) { /* expected on cancellation */ }
                        catch (Exception ex) { Debug($"[IDLE-FALLBACK] '{sessionName}' unexpected error: {ex}"); }
                    });
                }
                Invoke(() =>
                {
                    // Flush any accumulated assistant text to history/DB at end of each sub-turn.
                    // Without this, content in CurrentResponse is lost if the app restarts between
                    // turn_end and session.idle (which triggers CompleteResponse).
                    // Must run on UI thread to avoid racing with History list reads.
                    FlushCurrentResponse(state);
                    OnTurnEnd?.Invoke(sessionName);
                    OnActivity?.Invoke(sessionName, "");
                });
                break;

            case SessionIdleEvent:
                // Cancel the TurnEnd→Idle fallback — normal SessionIdleEvent arrived
                CancelTurnEndFallback(state);
                try { CompleteReasoningMessages(state, sessionName); }
                catch (Exception ex)
                {
                    Debug($"[EVT-ERR] '{sessionName}' CompleteReasoningMessages threw before CompleteResponse: {ex}");
                }
                // Capture the generation at the time the IDLE event arrives (on the SDK thread).
                // CompleteResponse will verify this matches the current generation to avoid
                // completing a turn that was superseded by a new SendPromptAsync call.
                var idleGeneration = Interlocked.Read(ref state.ProcessingGeneration);
                Invoke(() =>
                {
                    Debug($"[IDLE] '{sessionName}' CompleteResponse dispatched " +
                          $"(syncCtx={(_syncContext != null ? "UI" : "inline")}, " +
                          $"IsProcessing={state.Info.IsProcessing}, gen={idleGeneration}/{Interlocked.Read(ref state.ProcessingGeneration)}, " +
                          $"thread={Environment.CurrentManagedThreadId})");
                    CompleteResponse(state, idleGeneration);
                });
                // Refresh git branch — agent may have switched branches
                state.Info.GitBranch = GetGitBranch(state.Info.WorkingDirectory);
                // Send notification when agent finishes
                _ = Task.Run(async () =>
                {
                    try
                    {
                        var currentSettings = ConnectionSettings.Load();
                        if (!currentSettings.EnableSessionNotifications) return;
                        if (currentSettings.MuteWorkerNotifications && IsWorkerInMultiAgentGroup(sessionName)) return;
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
                Invoke(() => { if (!IsRestoring) SaveActiveSessionsToDisk(); });
                break;

            case SessionUsageInfoEvent usageInfo:
                if (state.Info.ProcessingPhase < 1) state.Info.ProcessingPhase = 1; // Server acknowledged
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
                var errMsg = Models.ErrorMessageHelper.HumanizeMessage(err.Data?.Message ?? "Unknown error");
                CancelProcessingWatchdog(state);
                CancelTurnEndFallback(state);
                Interlocked.Exchange(ref state.ActiveToolCallCount, 0);
                state.HasUsedToolsThisTurn = false;
                Interlocked.Exchange(ref state.SuccessfulToolCountThisTurn, 0);
                InvokeOnUI(() =>
                {
                    OnError?.Invoke(sessionName, errMsg);
                    // Flush any accumulated partial response before clearing the accumulator
                    FlushCurrentResponse(state);
                    state.FlushedResponse.Clear();
                    state.PendingReasoningMessages.Clear();
                    Debug($"[ERROR] '{sessionName}' SessionErrorEvent cleared IsProcessing (error={errMsg})");
                    // Clear IsProcessing BEFORE completing the TCS — if the continuation runs
                    // synchronously (e.g., orchestrator retry logic), the next SendPromptAsync
                    // call must see IsProcessing=false or it throws "already processing".
                    // (Matches CompleteResponse ordering per INV-O3)
                    state.Info.IsProcessing = false;
                    state.Info.IsResumed = false;
                    Interlocked.Exchange(ref state.SendingFlag, 0); // Release atomic send lock (INV-1)
                    if (state.Info.ProcessingStartedAt is { } errStarted)
                        state.Info.TotalApiTimeSeconds += (DateTime.UtcNow - errStarted).TotalSeconds;
                    state.Info.ProcessingStartedAt = null;
                    state.Info.ToolCallCount = 0;
                    state.Info.ProcessingPhase = 0;
                    state.Info.ClearPermissionDenials();
                    // Complete TCS AFTER state cleanup (INV-O3: state must be ready for retry)
                    state.ResponseCompletion?.TrySetException(new Exception(errMsg));
                    // Fire completion notification so orchestrator loops are unblocked
                    OnSessionComplete?.Invoke(sessionName, $"[Error] {errMsg}");
                    OnStateChanged?.Invoke();
                });
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
        if (string.IsNullOrWhiteSpace(text)) return;
        
        // Dedup guard: if this exact text was already flushed (e.g., SDK replayed events
        // after resume and content was re-appended to CurrentResponse), don't duplicate.
        var lastAssistant = state.Info.History.LastOrDefault(m => 
            m.Role == "assistant" && m.MessageType != ChatMessageType.ToolCall);
        if (lastAssistant?.Content == text)
        {
            Debug($"[DEDUP] FlushCurrentResponse skipped duplicate content ({text.Length} chars) for session '{state.Info.Name}'");
            state.CurrentResponse.Clear();
            state.HasReceivedDeltasThisTurn = false;
            return;
        }
        
        var msg = new ChatMessage("assistant", text, DateTime.Now) { Model = state.Info.Model };
        state.Info.History.Add(msg);
        state.Info.MessageCount = state.Info.History.Count;
        
        if (!string.IsNullOrEmpty(state.Info.SessionId))
            _ = _chatDb.AddMessageAsync(state.Info.SessionId, msg);
        
        // Track code suggestions from accumulated response segment
        _usageStats?.TrackCodeSuggestion(text);
        
        // Accumulate flushed text so CompleteResponse can include it in the TCS result.
        // Without this, orchestrator dispatch gets "" because TurnEnd flush clears
        // CurrentResponse before SessionIdle fires CompleteResponse.
        if (state.FlushedResponse.Length > 0)
            state.FlushedResponse.Append("\n\n");
        state.FlushedResponse.Append(text);
        
        state.CurrentResponse.Clear();
        state.HasReceivedDeltasThisTurn = false;
    }

    /// <summary>
    /// Completes the current response for a session. The <paramref name="expectedGeneration"/>
    /// parameter prevents a stale IDLE callback from completing a different turn than the one
    /// that produced it. Pass <c>null</c> to skip the generation check (e.g. from error paths
    /// or the watchdog where we always want to force-complete).
    /// </summary>
    private void CompleteResponse(SessionState state, long? expectedGeneration = null)
    {
        if (!state.Info.IsProcessing)
        {
            // Still flush any accumulated content — delta events may have arrived
            // after IsProcessing was cleared prematurely by watchdog/error handler.
            if (state.CurrentResponse.Length > 0)
            {
                Debug($"[COMPLETE] '{state.Info.Name}' IsProcessing already false but flushing " +
                      $"{state.CurrentResponse.Length} chars of accumulated content");
                FlushCurrentResponse(state);
                OnStateChanged?.Invoke();
            }
            else
            {
                Debug($"[COMPLETE] '{state.Info.Name}' CompleteResponse skipped — IsProcessing already false");
            }
            return; // Already completed (e.g. timeout)
        }

        // Guard against the SEND/COMPLETE race: if a new SendPromptAsync incremented the
        // generation between when SessionIdleEvent was received and when this callback
        // executes on the UI thread, this IDLE belongs to the OLD turn — skip it.
        if (expectedGeneration.HasValue)
        {
            var currentGen = Interlocked.Read(ref state.ProcessingGeneration);
            if (expectedGeneration.Value != currentGen)
            {
                Debug($"[COMPLETE] '{state.Info.Name}' CompleteResponse skipped — generation mismatch " +
                      $"(idle={expectedGeneration.Value}, current={currentGen}). A new SEND superseded this turn.");
                return;
            }
        }
        
        Debug($"[COMPLETE] '{state.Info.Name}' CompleteResponse executing " +
              $"(responseLen={state.CurrentResponse.Length}, flushedLen={state.FlushedResponse.Length}, thread={Environment.CurrentManagedThreadId})");
        
        CancelProcessingWatchdog(state);
        // Also cancel any pending TurnEnd→Idle fallback — CompleteResponse is now executing
        CancelTurnEndFallback(state);
        Interlocked.Exchange(ref state.ActiveToolCallCount, 0);
        state.HasUsedToolsThisTurn = false;
        Interlocked.Exchange(ref state.SuccessfulToolCountThisTurn, 0);
        state.Info.IsResumed = false; // Clear after first successful turn
        var response = state.CurrentResponse.ToString();
        if (!string.IsNullOrWhiteSpace(response))
        {
            var msg = new ChatMessage("assistant", response, DateTime.Now) { Model = state.Info.Model };
            state.Info.History.Add(msg);
            state.Info.MessageCount = state.Info.History.Count;
            // If user is viewing this session, keep it read
            if (state.Info.Name == _activeSessionName)
                state.Info.LastReadMessageCount = state.Info.History.Count;

            // Write-through to DB
            if (!string.IsNullOrEmpty(state.Info.SessionId))
                _ = _chatDb.AddMessageAsync(state.Info.SessionId, msg);
            
            // Track code suggestions from final response segment
            _usageStats?.TrackCodeSuggestion(response);
        }
        // Build full turn response for TCS: include text flushed mid-turn (e.g., on TurnEnd)
        // plus any remaining text in CurrentResponse. Without this, orchestrator dispatch
        // gets "" because FlushCurrentResponse on TurnEnd clears CurrentResponse before
        // SessionIdle fires CompleteResponse.
        var fullResponse = state.FlushedResponse.Length > 0
            ? (string.IsNullOrEmpty(response)
                ? state.FlushedResponse.ToString()
                : state.FlushedResponse + "\n\n" + response)
            : response;
        // Track one message per completed turn regardless of trailing text
        _usageStats?.TrackMessage();
        // Reset permission recovery attempts on successful turn completion
        _permissionRecoveryAttempts.TryRemove(state.Info.Name, out _);
        // Clear IsProcessing BEFORE completing the TCS — if the continuation runs
        // synchronously (e.g., in orchestrator reflection loops), the next SendPromptAsync
        // call must see IsProcessing=false or it throws "already processing".
        state.CurrentResponse.Clear();
        state.FlushedResponse.Clear();
        state.PendingReasoningMessages.Clear();
        // Accumulate API time before clearing ProcessingStartedAt
        if (state.Info.ProcessingStartedAt is { } started)
        {
            state.Info.TotalApiTimeSeconds += (DateTime.UtcNow - started).TotalSeconds;
            state.Info.PremiumRequestsUsed++;
        }
        state.Info.IsProcessing = false;
        state.Info.IsResumed = false; // After first successful completion, use normal watchdog timeouts
        Interlocked.Exchange(ref state.SendingFlag, 0); // Release atomic send lock
        state.Info.ProcessingStartedAt = null;
        state.Info.ToolCallCount = 0;
        state.Info.ProcessingPhase = 0;
        state.Info.ClearPermissionDenials();
        state.Info.LastUpdatedAt = DateTime.Now;
        state.ResponseCompletion?.TrySetResult(fullResponse);
        
        // Fire completion notification BEFORE OnStateChanged — this ensures
        // HandleComplete populates completedSessions before RefreshState checks the
        // throttle (completedSessions.Count > 0 bypasses throttle).
        var summary = fullResponse.Length > 0 ? (fullResponse.Length > 100 ? fullResponse[..100] + "..." : fullResponse) : "";
        OnSessionComplete?.Invoke(state.Info.Name, summary);
        OnStateChanged?.Invoke();

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
                // Use evaluator session if available, otherwise fall back to self-evaluation
                if (!string.IsNullOrEmpty(cycle.EvaluatorSessionName) && _sessions.ContainsKey(cycle.EvaluatorSessionName))
                {
                    Debug($"[EVAL] Taking evaluator path for '{state.Info.Name}', evaluator='{cycle.EvaluatorSessionName}'");
                    // Async evaluator path — dispatch evaluation in background
                    var sessionName = state.Info.Name;
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            await EvaluateAndAdvanceAsync(sessionName, response);
                        }
                        catch (Exception ex)
                        {
                            Debug($"Evaluator failed for '{sessionName}': {ex.Message}. Falling back to self-evaluation.");
                            _syncContext?.Post(_ => FallbackAdvance(sessionName, response), null);
                        }
                    });
                }
                else
                {
                    Debug($"[EVAL] Taking FALLBACK path for '{state.Info.Name}', evaluatorName='{cycle.EvaluatorSessionName}', inSessions={(!string.IsNullOrEmpty(cycle.EvaluatorSessionName) && _sessions.ContainsKey(cycle.EvaluatorSessionName))}");
                    // Fallback: self-evaluation via sentinel detection
                    FallbackAdvance(state.Info.Name, response);
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
            lock (_imageQueueLock)
            {
                if (_queuedImagePaths.TryGetValue(state.Info.Name, out var imageQueue) && imageQueue.Count > 0)
                {
                    nextImagePaths = imageQueue[0];
                    imageQueue.RemoveAt(0);
                    if (imageQueue.Count == 0)
                        _queuedImagePaths.TryRemove(state.Info.Name, out _);
                }
            }
            // Retrieve any queued agent mode for this message
            string? nextAgentMode = null;
            lock (_imageQueueLock)
            {
                if (_queuedAgentModes.TryGetValue(state.Info.Name, out var modeQueue) && modeQueue.Count > 0)
                {
                    nextAgentMode = modeQueue[0];
                    modeQueue.RemoveAt(0);
                    if (modeQueue.Count == 0)
                        _queuedAgentModes.TryRemove(state.Info.Name, out _);
                }
            }

            var skipHistory = state.Info.ReflectionCycle is { IsActive: true } &&
                              ReflectionCycle.IsReflectionFollowUpPrompt(nextPrompt);

            // Check if the dequeued message is for an orchestrator session — if so,
            // route through the multi-agent dispatch pipeline instead of direct send.
            
            // If we are restoring, the global ReconcileOrganization() hasn't run yet.
            // We must force an additive-only update so this session's metadata exists.
            if (IsRestoring) ReconcileOrganization(allowPruning: false);
            
            var orchGroupId = GetOrchestratorGroupId(state.Info.Name);

            // Use Task.Run to dispatch on a clean stack frame, avoiding reentrancy
            // issues where CompleteResponse hasn't fully unwound yet.
            _ = Task.Run(async () =>
            {
                try
                {
                    // Small delay to let the current turn fully complete
                    await Task.Delay(100);
                    if (_syncContext != null)
                    {
                        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                        _syncContext.Post(async _ =>
                        {
                            try
                            {
                                if (orchGroupId != null && nextImagePaths is null or { Count: 0 })
                                {
                                    Debug($"[DISPATCH] Queue drain routing to multi-agent pipeline: session='{state.Info.Name}', group='{orchGroupId}'");
                                    await SendToMultiAgentGroupAsync(orchGroupId, nextPrompt);
                                }
                                else
                                {
                                    await SendPromptAsync(state.Info.Name, nextPrompt, imagePaths: nextImagePaths, skipHistoryMessage: skipHistory, agentMode: nextAgentMode);
                                }
                                tcs.TrySetResult();
                            }
                            catch (Exception ex)
                            {
                                tcs.TrySetException(ex);
                            }
                        }, null);
                        await tcs.Task;
                    }
                    else
                    {
                        if (orchGroupId != null && nextImagePaths is null or { Count: 0 })
                        {
                            Debug($"[DISPATCH] Queue drain routing to multi-agent pipeline: session='{state.Info.Name}', group='{orchGroupId}'");
                            await SendToMultiAgentGroupAsync(orchGroupId, nextPrompt);
                        }
                        else
                        {
                            await SendPromptAsync(state.Info.Name, nextPrompt, imagePaths: nextImagePaths, skipHistoryMessage: skipHistory, agentMode: nextAgentMode);
                        }
                    }
                }
                catch (Exception ex)
                {
                    Debug($"Failed to send queued message: {ex.Message}");
                    InvokeOnUI(() =>
                    {
                        state.Info.MessageQueue.Insert(0, nextPrompt);
                        if (nextImagePaths != null)
                        {
                            lock (_imageQueueLock)
                            {
                                var images = _queuedImagePaths.GetOrAdd(state.Info.Name, _ => new List<List<string>>());
                                images.Insert(0, nextImagePaths);
                            }
                        }
                        // Re-queue the agent mode too (always re-insert to maintain alignment)
                        lock (_imageQueueLock)
                        {
                            if (_queuedAgentModes.TryGetValue(state.Info.Name, out var existingModes))
                            {
                                existingModes.Insert(0, nextAgentMode);
                            }
                            else if (nextAgentMode != null)
                            {
                                var modes = _queuedAgentModes.GetOrAdd(state.Info.Name, _ => new List<string?>());
                                modes.Insert(0, nextAgentMode);
                            }
                        }
                    });
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

    /// <summary>
    /// Sends the worker's response to the evaluator session and advances the cycle based on the result.
    /// Runs on a background thread; posts UI updates back to sync context.
    /// </summary>
    private async Task EvaluateAndAdvanceAsync(string workerSessionName, string workerResponse)
    {
        if (!_sessions.TryGetValue(workerSessionName, out var workerState))
            return;

        var cycle = workerState.Info.ReflectionCycle;
        if (cycle == null || !cycle.IsActive || string.IsNullOrEmpty(cycle.EvaluatorSessionName))
        {
            _syncContext?.Post(_ => FallbackAdvance(workerSessionName, workerResponse), null);
            return;
        }

        var evaluatorName = cycle.EvaluatorSessionName;
        if (!_sessions.TryGetValue(evaluatorName, out var evalState))
        {
            Debug($"Evaluator session '{evaluatorName}' not found. Falling back to self-evaluation.");
            _syncContext?.Post(_ => FallbackAdvance(workerSessionName, workerResponse), null);
            return;
        }

        // Build evaluation prompt and send to evaluator with a timeout
        var evalPrompt = cycle.BuildEvaluatorPrompt(workerResponse);
        Debug($"Sending to evaluator '{evaluatorName}' for cycle on '{workerSessionName}' (iteration {cycle.CurrentIteration + 1})");

        bool evaluatorPassed = false;
        string? evaluatorFeedback = null;

        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

            // Wait for evaluator to not be processing
            while (evalState.Info.IsProcessing && !cts.Token.IsCancellationRequested)
                await Task.Delay(200, cts.Token);

            evalState.ResponseCompletion = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            await SendPromptAsync(evaluatorName, evalPrompt, cancellationToken: cts.Token, skipHistoryMessage: true);

            // Wait for the evaluator response
            var evalResponse = await evalState.ResponseCompletion.Task.WaitAsync(cts.Token);

            Debug($"Evaluator response for '{workerSessionName}': {(evalResponse.Length > 100 ? evalResponse[..100] + "..." : evalResponse)}");

            var (pass, feedback) = ReflectionCycle.ParseEvaluatorResponse(evalResponse);
            evaluatorPassed = pass;
            evaluatorFeedback = feedback;
            Debug($"[EVAL] Parsed result for '{workerSessionName}': pass={pass}, feedback='{feedback}'");
        }
        catch (OperationCanceledException)
        {
            Debug($"Evaluator timed out for '{workerSessionName}'. Falling back to self-evaluation.");
            _syncContext?.Post(_ => FallbackAdvance(workerSessionName, workerResponse), null);
            return;
        }
        catch (Exception ex)
        {
            Debug($"Evaluator error for '{workerSessionName}': {ex.Message}. Falling back to self-evaluation.");
            _syncContext?.Post(_ => FallbackAdvance(workerSessionName, workerResponse), null);
            return;
        }

        // Post the advance back to the UI thread
        // Capture cycle reference to detect if user restarted the cycle while evaluator was running
        var originalCycle = cycle;
        _syncContext?.Post(_ =>
        {
            if (!_sessions.TryGetValue(workerSessionName, out var state)) return;
            var c = state.Info.ReflectionCycle;
            // Verify this is still the same cycle instance (user may have stopped/restarted)
            if (c == null || !c.IsActive || !ReferenceEquals(c, originalCycle)) return;

            var shouldContinue = c.AdvanceWithEvaluation(workerResponse, evaluatorPassed, evaluatorFeedback);
            HandleReflectionAdvanceResult(state, workerResponse, shouldContinue, evaluatorFeedback);
        }, null);
    }

    /// <summary>
    /// Fallback: advances the cycle using sentinel-based self-evaluation.
    /// Must be called on the UI thread.
    /// </summary>
    private void FallbackAdvance(string sessionName, string response)
    {
        if (!_sessions.TryGetValue(sessionName, out var state)) return;
        var cycle = state.Info.ReflectionCycle;
        if (cycle == null || !cycle.IsActive) return;

        var goalMet = cycle.IsGoalMet(response);
        Debug($"[EVAL] FallbackAdvance for '{sessionName}': sentinel detected={goalMet}, response length={response.Length}");
        var shouldContinue = cycle.Advance(response);
        HandleReflectionAdvanceResult(state, response, shouldContinue, null);
    }

    /// <summary>
    /// Common logic after cycle advance: handles stall warnings, context warnings,
    /// follow-up enqueueing, and completion messages.
    /// </summary>
    private void HandleReflectionAdvanceResult(SessionState state, string response, bool shouldContinue, string? evaluatorFeedback)
    {
        var cycle = state.Info.ReflectionCycle!;

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

            // Use evaluator feedback to build the follow-up prompt (or fall back to self-eval prompt)
            string followUp;
            if (!string.IsNullOrEmpty(evaluatorFeedback))
            {
                followUp = cycle.BuildFollowUpFromEvaluator(evaluatorFeedback);
                Debug($"Reflection cycle iteration {cycle.CurrentIteration}/{cycle.MaxIterations} for '{state.Info.Name}' — evaluator feedback: {evaluatorFeedback}");
            }
            else
            {
                followUp = cycle.BuildFollowUpPrompt(response);
                Debug($"Reflection cycle iteration {cycle.CurrentIteration}/{cycle.MaxIterations} for '{state.Info.Name}' (self-eval fallback)");
            }

            var reflectionMsg = ChatMessage.ReflectionMessage(cycle.BuildFollowUpStatus());
            state.Info.History.Add(reflectionMsg);
            state.Info.MessageCount = state.Info.History.Count;
            if (!string.IsNullOrEmpty(state.Info.SessionId))
                _ = _chatDb.AddMessageAsync(state.Info.SessionId, reflectionMsg);

            // Show evaluator feedback in chat if available
            if (!string.IsNullOrEmpty(evaluatorFeedback))
            {
                var feedbackMsg = ChatMessage.SystemMessage($"🔍 Evaluator: {evaluatorFeedback}");
                state.Info.History.Add(feedbackMsg);
                state.Info.MessageCount = state.Info.History.Count;
                if (!string.IsNullOrEmpty(state.Info.SessionId))
                    _ = _chatDb.AddMessageAsync(state.Info.SessionId, feedbackMsg);
            }

            // Keep queue FIFO so user steering messages queued during this turn run first.
            state.Info.MessageQueue.Add(followUp);
            OnStateChanged?.Invoke();

            // If the session is idle (evaluator ran asynchronously after CompleteResponse),
            // dispatch the queued message immediately.
            if (!state.Info.IsProcessing && state.Info.MessageQueue.Count > 0)
            {
                var nextPrompt = state.Info.MessageQueue[0];
                state.Info.MessageQueue.RemoveAt(0);

                // Consume any queued agent mode to keep alignment
                string? nextAgentMode2 = null;
                lock (_imageQueueLock)
                {
                    if (_queuedAgentModes.TryGetValue(state.Info.Name, out var modeQueue2) && modeQueue2.Count > 0)
                    {
                        nextAgentMode2 = modeQueue2[0];
                        modeQueue2.RemoveAt(0);
                        if (modeQueue2.Count == 0)
                            _queuedAgentModes.TryRemove(state.Info.Name, out _);
                    }
                }

                var skipHistory = state.Info.ReflectionCycle is { IsActive: true } &&
                                  ReflectionCycle.IsReflectionFollowUpPrompt(nextPrompt);

                _ = Task.Run(async () =>
                {
                    try
                    {
                        await Task.Delay(100);
                        if (_syncContext != null)
                        {
                            var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                            _syncContext.Post(async _ =>
                            {
                                try
                                {
                                    await SendPromptAsync(state.Info.Name, nextPrompt, skipHistoryMessage: skipHistory, agentMode: nextAgentMode2);
                                    tcs.TrySetResult();
                                }
                                catch (Exception ex)
                                {
                                    Debug($"Error dispatching evaluator follow-up: {ex.Message}");
                                    tcs.TrySetException(ex);
                                }
                            }, null);
                            await tcs.Task;
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug($"Error dispatching queued message after evaluation: {ex.Message}");
                    }
                });
            }
        }
        else if (!cycle.IsActive)
        {
            var reason = cycle.GoalMet ? "goal met" : cycle.IsStalled ? "stalled" : "max iterations reached";
            Debug($"Reflection cycle ended for '{state.Info.Name}': {reason}");

            // Show evaluator verdict when cycle ends
            if (cycle.GoalMet && !string.IsNullOrEmpty(cycle.EvaluatorSessionName))
            {
                var passMsg = ChatMessage.SystemMessage("🔍 Evaluator: **PASS** — goal achieved");
                state.Info.History.Add(passMsg);
                state.Info.MessageCount = state.Info.History.Count;
                if (!string.IsNullOrEmpty(state.Info.SessionId))
                    _ = _chatDb.AddMessageAsync(state.Info.SessionId, passMsg);
            }
            else if (!string.IsNullOrEmpty(evaluatorFeedback))
            {
                var feedbackMsg = ChatMessage.SystemMessage($"🔍 Evaluator: {evaluatorFeedback}");
                state.Info.History.Add(feedbackMsg);
                state.Info.MessageCount = state.Info.History.Count;
                if (!string.IsNullOrEmpty(state.Info.SessionId))
                    _ = _chatDb.AddMessageAsync(state.Info.SessionId, feedbackMsg);
            }

            var completionMsg = ChatMessage.SystemMessage(cycle.BuildCompletionSummary());
            state.Info.History.Add(completionMsg);
            state.Info.MessageCount = state.Info.History.Count;
            if (!string.IsNullOrEmpty(state.Info.SessionId))
                _ = _chatDb.AddMessageAsync(state.Info.SessionId, completionMsg);

            // Clean up evaluator session
            if (!string.IsNullOrEmpty(cycle.EvaluatorSessionName))
            {
                var evalName = cycle.EvaluatorSessionName;
                _ = Task.Run(async () =>
                {
                    try { await CloseSessionAsync(evalName); }
                    catch (Exception ex) { Debug($"Error closing evaluator session: {ex.Message}"); }
                });
            }

            OnStateChanged?.Invoke();
        }
    }

    // -- Processing watchdog: detects stuck sessions when server dies mid-turn --

    /// <summary>Interval between watchdog checks in seconds.</summary>
    internal const int WatchdogCheckIntervalSeconds = 15;
    /// <summary>If no SDK events arrive for this many seconds (and no tool is running), the session is considered stuck.</summary>
    internal const int WatchdogInactivityTimeoutSeconds = 120;
    /// <summary>If no SDK events arrive for this many seconds while a tool is actively executing, the session is considered stuck.
    /// This is much longer because legitimate tool executions (e.g., running UI tests, long builds) can take many minutes.</summary>
    internal const int WatchdogToolExecutionTimeoutSeconds = 600;
    /// <summary>If a resumed session receives zero SDK events for this many seconds, it was likely already
    /// finished when the app restarted. Short enough that users don't have to click Stop, long enough
    /// for the SDK to start streaming if the turn is genuinely still active.</summary>
    internal const int WatchdogResumeQuiescenceTimeoutSeconds = 30;
    /// <summary>Absolute maximum processing time in seconds. Even if events keep arriving,
    /// no single turn should run longer than this. This is a safety net for scenarios where
    /// non-progress events (like repeated SessionUsageInfoEvent) keep arriving without a terminal event.</summary>
    internal const int WatchdogMaxProcessingTimeSeconds = 3600; // 60 minutes

    /// <summary>
    /// Milliseconds to wait after AssistantTurnEndEvent before firing CompleteResponse
    /// as a fallback, in case SessionIdleEvent never arrives (SDK bug #299).
    /// </summary>
    internal const int TurnEndIdleFallbackMs = 4000;

    private static void CancelProcessingWatchdog(SessionState state)
    {
        if (state.ProcessingWatchdog != null)
        {
            state.ProcessingWatchdog.Cancel();
            state.ProcessingWatchdog.Dispose();
            state.ProcessingWatchdog = null;
        }
    }

    /// <summary>
    /// Cancels and disposes any pending TurnEnd→Idle fallback timer on the state.
    /// Mirrors the CancelProcessingWatchdog pattern: Cancel + Dispose to avoid
    /// kernel timer queue resource leaks over many tool-call cycles.
    /// </summary>
    private static void CancelTurnEndFallback(SessionState state)
    {
        var prev = Interlocked.Exchange(ref state.TurnEndIdleCts, null);
        prev?.Cancel();
        prev?.Dispose();
    }

    /// <summary>
    /// Returns true if the text indicates a permission denial from the Copilot SDK.
    /// Matches both the human-readable "Permission denied" message and the SDK's internal
    /// error codes (e.g. "denied-no-approval-rule-and-could-not-request-from-user").
    /// This is more robust than a single string match and handles future SDK message variants.
    /// </summary>
    internal static bool IsPermissionDenialText(string? text)
    {
        if (string.IsNullOrEmpty(text)) return false;
        return text.Contains("Permission denied", StringComparison.OrdinalIgnoreCase)
            || text.Contains("denied-no-approval-rule", StringComparison.OrdinalIgnoreCase)
            || text.Contains("could not request permission", StringComparison.OrdinalIgnoreCase);
    }

    private void StartProcessingWatchdog(SessionState state, string sessionName)
    {
        CancelProcessingWatchdog(state);
        // Always seed from DateTime.UtcNow. Do NOT pass events.jsonl file time here —
        // that would make elapsed = (file age) + check interval, causing the 30s quiescence
        // timeout to fire on the first watchdog check for any file > ~15s old.
        // This is the exact regression pattern from PR #148 (short timeout killing active sessions).
        Interlocked.Exchange(ref state.LastEventAtTicks, DateTime.UtcNow.Ticks);
        state.ProcessingWatchdog = new CancellationTokenSource();
        var ct = state.ProcessingWatchdog.Token;
        _ = RunProcessingWatchdogAsync(state, sessionName, ct);
    }

    private async Task RunProcessingWatchdogAsync(SessionState state, string sessionName, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested && state.Info.IsProcessing)
            {
                await Task.Delay(TimeSpan.FromSeconds(WatchdogCheckIntervalSeconds), ct);

                if (!state.Info.IsProcessing) break;

                var lastEventTicks = Interlocked.Read(ref state.LastEventAtTicks);
                var elapsed = (DateTime.UtcNow - new DateTime(lastEventTicks)).TotalSeconds;
                var hasActiveTool = Interlocked.CompareExchange(ref state.ActiveToolCallCount, 0, 0) > 0;

                // After events have started flowing on a resumed session, clear IsResumed
                // so the watchdog transitions from the long 600s timeout to the shorter 120s.
                // Guard: don't clear if tools are active or have been used this turn — between
                // tool rounds, ActiveToolCallCount returns to 0 when AssistantTurnStartEvent
                // resets it, but the model may still be reasoning about the next tool call.
                // HasUsedToolsThisTurn persists across rounds and prevents premature downgrade.
                if (state.Info.IsResumed && Volatile.Read(ref state.HasReceivedEventsSinceResume)
                    && !hasActiveTool && !Volatile.Read(ref state.HasUsedToolsThisTurn))
                {
                    Debug($"[WATCHDOG] '{sessionName}' clearing IsResumed — events have arrived since resume with no tool activity");
                    InvokeOnUI(() => state.Info.IsResumed = false);
                }
                // Use the longer tool-execution timeout if:
                // 1. A tool call is actively running (hasActiveTool), OR
                // 2. This is a resumed session that was mid-turn (agent sessions routinely
                //    have 2-3 min gaps between events while the model reasons), OR
                // 3. Tools have been executed this turn (HasUsedToolsThisTurn) — even between
                //    tool rounds when ActiveToolCallCount is 0, the model may spend minutes
                //    thinking about what tool to call next, OR
                // 4. Session is in a multi-agent group — workers doing text-heavy tasks
                //    (e.g., PR reviews) can take 2-4 min without tool calls. The orchestration
                //    loop has its own 10-minute CancelAfter timeout per worker. Cached at
                //    send time on UI thread to avoid cross-thread List<T> access.
                var isMultiAgentSession = Volatile.Read(ref state.IsMultiAgentSession);
                var hasReceivedEvents = Volatile.Read(ref state.HasReceivedEventsSinceResume);
                var hasUsedTools = Volatile.Read(ref state.HasUsedToolsThisTurn);

                // Resumed session that has received ZERO events since restart — the turn likely
                // completed before the app restarted. Use a short 30s quiescence timeout so the
                // user doesn't have to click Stop. If events start flowing, HasReceivedEventsSinceResume
                // goes true and we fall through to the normal timeout tiers.
                var useResumeQuiescence = state.Info.IsResumed && !hasReceivedEvents && !hasActiveTool && !hasUsedTools;

                var useToolTimeout = hasActiveTool || (state.Info.IsResumed && !useResumeQuiescence) || hasUsedTools || isMultiAgentSession;
                var effectiveTimeout = useResumeQuiescence
                    ? WatchdogResumeQuiescenceTimeoutSeconds
                    : useToolTimeout
                        ? WatchdogToolExecutionTimeoutSeconds
                        : WatchdogInactivityTimeoutSeconds;

                // Safety net: check absolute max processing time, but only if events have also
                // gone stale. If events are still flowing (elapsed < effectiveTimeout), the session
                // is actively working — don't kill it just because the turn is long-running.
                // This prevents premature kills on CI/agent sessions that legitimately work for hours.
                // The cap still catches "zombie" sessions where non-progress events (e.g., repeated
                // SessionUsageInfoEvent with FailedDelegation) keep the inactivity timer happy.
                var startedAt = state.Info.ProcessingStartedAt;
                var totalProcessingSeconds = startedAt.HasValue
                    ? (DateTime.UtcNow - startedAt.Value).TotalSeconds
                    : 0;

                if (elapsed >= effectiveTimeout)
                {
                    var exceededMaxTime = totalProcessingSeconds >= WatchdogMaxProcessingTimeSeconds;
                    var timeoutDisplay = exceededMaxTime
                        ? $"{WatchdogMaxProcessingTimeSeconds / 60} minute(s) total processing time"
                        : effectiveTimeout >= 60
                            ? $"{effectiveTimeout / 60} minute(s)"
                            : $"{effectiveTimeout} seconds";
                    Debug(exceededMaxTime
                        ? $"Session '{sessionName}' watchdog: total processing time {totalProcessingSeconds:F0}s exceeded max {WatchdogMaxProcessingTimeSeconds}s, clearing stuck processing state"
                        : $"Session '{sessionName}' watchdog: no events for {elapsed:F0}s " +
                          $"(timeout={effectiveTimeout}s, hasActiveTool={hasActiveTool}, isResumed={state.Info.IsResumed}, hasUsedTools={state.HasUsedToolsThisTurn}, multiAgent={isMultiAgentSession}), clearing stuck processing state");
                    // Capture generation before posting — same guard pattern as CompleteResponse.
                    // Prevents a stale watchdog callback from killing a new turn if the user
                    // aborts + resends between the Post() and the callback execution.
                    var watchdogGeneration = Interlocked.Read(ref state.ProcessingGeneration);
                    // Marshal all state mutations to the UI thread to avoid
                    // racing with CompleteResponse / HandleSessionEvent.
                    InvokeOnUI(() =>
                    {
                        if (!state.Info.IsProcessing) return; // Already completed
                        var currentGen = Interlocked.Read(ref state.ProcessingGeneration);
                        if (watchdogGeneration != currentGen)
                        {
                            Debug($"Session '{sessionName}' watchdog callback skipped — generation mismatch " +
                                  $"(watchdog={watchdogGeneration}, current={currentGen}). A new SEND superseded this turn.");
                            return;
                        }
                        CancelProcessingWatchdog(state);
                        Interlocked.Exchange(ref state.ActiveToolCallCount, 0);
                        state.HasUsedToolsThisTurn = false;
                        Interlocked.Exchange(ref state.SuccessfulToolCountThisTurn, 0);
                        // Cancel any pending TurnEnd→Idle fallback
                        CancelTurnEndFallback(state);
                        state.Info.IsResumed = false;
                        // Flush any accumulated partial response before clearing processing state
                        FlushCurrentResponse(state);
                        Debug($"[WATCHDOG] '{sessionName}' IsProcessing=false — watchdog timeout after {totalProcessingSeconds:F0}s total, elapsed={elapsed:F0}s, exceededMaxTime={exceededMaxTime}");
                        state.Info.IsProcessing = false;
                        Interlocked.Exchange(ref state.SendingFlag, 0);
                        if (state.Info.ProcessingStartedAt is { } wdStarted)
                            state.Info.TotalApiTimeSeconds += (DateTime.UtcNow - wdStarted).TotalSeconds;
                        state.Info.ProcessingStartedAt = null;
                        state.Info.ToolCallCount = 0;
                        state.Info.ProcessingPhase = 0;
                        state.Info.ClearPermissionDenials(); // INV-1: clear on all termination paths
                        state.Info.History.Add(ChatMessage.SystemMessage(
                            "⚠️ Session appears stuck — no response received. You can try sending your message again."));
                        var watchdogResponse = state.FlushedResponse.ToString();
                        state.FlushedResponse.Clear();
                        state.PendingReasoningMessages.Clear();
                        state.ResponseCompletion?.TrySetResult(watchdogResponse);
                        // Fire completion notification so orchestrator loops are unblocked (INV-O4)
                        OnSessionComplete?.Invoke(sessionName, "[Watchdog] timeout");
                        OnError?.Invoke(sessionName, $"Session appears stuck — no events received for over {timeoutDisplay}.");
                        OnStateChanged?.Invoke();
                    });
                    break;
                }
            }
        }
        catch (OperationCanceledException) { /* Normal cancellation when response completes */ }
        catch (Exception ex) { Debug($"Watchdog error for '{sessionName}': {ex.Message}"); }
    }

    /// <summary>
    /// Attempts to recover a session whose permission callback binding was lost.
    /// Disposes the broken session and resumes it with a fresh AutoApprovePermissions callback.
    /// </summary>
    // Guard against infinite recovery loops (permission denied → recover → resend → denied again)
    private readonly ConcurrentDictionary<string, int> _permissionRecoveryAttempts = new();
    private readonly ConcurrentDictionary<string, bool> _recoveryInProgress = new();

    /// <summary>
    /// Clears all processing state so the session isn't stuck in "Thinking..." after a failed recovery.
    /// Must be called on the UI thread. Resolves the pending TCS, clears SendingFlag, and resets
    /// all 9 companion fields per INV-1.
    /// </summary>
    private void ClearProcessingStateForRecoveryFailure(SessionState state, string sessionName)
    {
        // Clear SendingFlag early so we don't block new sends
        Interlocked.Exchange(ref state.SendingFlag, 0);
        state.Info.ClearPermissionDenials();
        // Cancel any pending TurnEnd→Idle fallback
        CancelTurnEndFallback(state);
        if (state.Info.IsProcessing)
        {
            FlushCurrentResponse(state);
            state.CurrentResponse.Clear();
            state.FlushedResponse.Clear();
            state.PendingReasoningMessages.Clear();
            if (state.Info.ProcessingStartedAt is { } started)
                state.Info.TotalApiTimeSeconds += (DateTime.UtcNow - started).TotalSeconds;
            state.Info.IsProcessing = false;
            state.Info.IsResumed = false;
            state.HasUsedToolsThisTurn = false;
            Interlocked.Exchange(ref state.SuccessfulToolCountThisTurn, 0);
            Interlocked.Exchange(ref state.ActiveToolCallCount, 0);
            state.Info.ProcessingStartedAt = null;
            state.Info.ToolCallCount = 0;
            state.Info.ProcessingPhase = 0;
            // Complete TCS AFTER state cleanup (INV-O3: state must be ready for retry)
            state.ResponseCompletion?.TrySetCanceled();
            // Fire completion notification so orchestrator loops are unblocked (INV-O4)
            OnSessionComplete?.Invoke(sessionName, "[Recovery] failed");
        }
        else
        {
            // Even if not processing, still complete TCS if pending and notify listeners
            state.ResponseCompletion?.TrySetCanceled();
            // Fire completion notification even when not processing — ensures bridge clients
            // don't remain stuck if they were waiting on this session (INV-O4)
            OnSessionComplete?.Invoke(sessionName, "[Recovery] idle session reset");
        }
    }

    private async Task TryRecoverPermissionAsync(SessionState state, string sessionName)
    {
        // Guard against concurrent recovery for the same session (auto + manual can race)
        if (!_recoveryInProgress.TryAdd(sessionName, true))
        {
            Debug($"[PERMISSION-RECOVER] Recovery already in progress for '{sessionName}'");
            return;
        }
        try
        {
            if (_client == null || state.Info.SessionId == null)
            {
                ClearProcessingStateForRecoveryFailure(state, sessionName);
                return;
            }

            // Limit recovery attempts per session to prevent infinite loops
            var attempts = _permissionRecoveryAttempts.AddOrUpdate(sessionName, 1, (_, v) => v + 1);
            if (attempts > 2)
            {
                Debug($"[PERMISSION-RECOVER] Max recovery attempts reached for '{sessionName}'");
                ClearProcessingStateForRecoveryFailure(state, sessionName);
                state.Info.History.Add(ChatMessage.SystemMessage(
                    "⚠️ Multiple recovery attempts failed. Use Settings → Save & Reconnect to fully restart the service."));
                OnStateChanged?.Invoke();
                return;
            }

            Debug($"[PERMISSION-RECOVER] Attempting session reconnect for '{sessionName}'");

            // Dispose the old session
            try { await state.Session.DisposeAsync(); } catch { }

            // Resume with fresh permission callback
            var resumeModel = Models.ModelHelper.NormalizeToSlug(state.Info.Model);
            var resumeConfig = new ResumeSessionConfig
            {
                Tools = new List<Microsoft.Extensions.AI.AIFunction> { ShowImageTool.CreateFunction() },
                OnPermissionRequest = AutoApprovePermissions
            };
            if (!string.IsNullOrEmpty(resumeModel))
                resumeConfig.Model = resumeModel;
            if (!string.IsNullOrEmpty(state.Info.WorkingDirectory))
                resumeConfig.WorkingDirectory = state.Info.WorkingDirectory;

            var newSession = await _client.ResumeSessionAsync(state.Info.SessionId, resumeConfig);

            // Cancel old watchdog AND TurnEnd fallback BEFORE creating new state
            CancelProcessingWatchdog(state);
            CancelTurnEndFallback(state);

            // Bug B fix: Cancel the old ResponseCompletion TCS so the original
            // SendPromptAsync awaiter doesn't hang forever.
            state.ResponseCompletion?.TrySetCanceled();

            // Create new state preserving Info
            var newState = new SessionState
            {
                Session = newSession,
                Info = state.Info
            };
            newState.ResponseCompletion = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            Interlocked.Exchange(ref newState.ProcessingGeneration,
                Interlocked.Read(ref state.ProcessingGeneration));
            newState.HasUsedToolsThisTurn = state.HasUsedToolsThisTurn;
            newState.IsMultiAgentSession = state.IsMultiAgentSession;
            // Transfer ActiveToolCallCount to prevent negative counts from in-flight completions.
            // Don't transfer SendingFlag — we need it at 0 for the resend below.
            Interlocked.Exchange(ref newState.ActiveToolCallCount,
                Interlocked.Exchange(ref state.ActiveToolCallCount, 0));
            Interlocked.Exchange(ref state.SendingFlag, 0);
            // Clear stale tool flag so watchdog uses normal timeout if resend is skipped
            newState.HasUsedToolsThisTurn = false;

            // Replace in sessions dictionary BEFORE registering event handler
            // so HandleSessionEvent's isCurrentState check passes for the new state.
            _sessions[sessionName] = newState;
            newSession.On(evt => HandleSessionEvent(newState, evt));

            // Bug A fix: Clear IsProcessing + all 9 companion fields so SendPromptAsync
            // doesn't throw "already processing". Must run on UI thread (INV-2).
            // History read also done on UI thread to avoid concurrent List<T> mutation.
            string? lastPrompt = null;
            bool hadSuccessfulTools = false;

            // Use a TaskCompletionSource to wait for the UI-thread cleanup to complete
            // before attempting the resend.
            var cleanupDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            InvokeOnUI(() =>
            {
                // Read History on UI thread where it's safe (List<T> not thread-safe)
                var lastUserMsg = state.Info.History.LastOrDefault(m => m.Role == "user");
                lastPrompt = lastUserMsg?.OriginalContent ?? lastUserMsg?.Content;

                // Check if any tools succeeded this turn — if so, skip auto-resend to avoid
                // re-executing side-effectful work (issue #298)
                hadSuccessfulTools = Volatile.Read(ref state.SuccessfulToolCountThisTurn) > 0;

                state.Info.ClearPermissionDenials();
                // INV-1: Flush partial response to History before discarding buffers
                FlushCurrentResponse(state);
                state.CurrentResponse.Clear();
                state.FlushedResponse.Clear();
                state.PendingReasoningMessages.Clear();
                if (state.Info.ProcessingStartedAt is { } started)
                    state.Info.TotalApiTimeSeconds += (DateTime.UtcNow - started).TotalSeconds;
                state.Info.IsProcessing = false;
                state.Info.IsResumed = false;
                state.HasUsedToolsThisTurn = false;
                Interlocked.Exchange(ref state.SuccessfulToolCountThisTurn, 0);
                Interlocked.Exchange(ref state.ActiveToolCallCount, 0);
                state.Info.ProcessingStartedAt = null;
                state.Info.ToolCallCount = 0;
                state.Info.ProcessingPhase = 0;
                Debug($"[PERMISSION-RECOVER] '{sessionName}' cleared processing state");
                if (hadSuccessfulTools)
                {
                    state.Info.History.Add(ChatMessage.SystemMessage(
                        "✅ Session reconnected. Auto-resend skipped — some tools had already completed. Send your message again when ready."));
                }
                else if (!string.IsNullOrEmpty(lastPrompt))
                {
                    var preview = lastPrompt.Length > 50 ? lastPrompt[..50] + "…" : lastPrompt;
                    state.Info.History.Add(ChatMessage.SystemMessage($"⟳ Resending: \"{preview}\""));
                }
                else
                {
                    state.Info.History.Add(ChatMessage.SystemMessage("✅ Session reconnected."));
                }
                OnActivity?.Invoke(sessionName, "✅ Session reconnected");
                OnStateChanged?.Invoke();
                cleanupDone.TrySetResult();
            });

            // Wait for UI-thread cleanup before resending.
            // Since TryRecoverPermissionAsync runs on UI thread (via Invoke), the
            // continuation stays on UI thread — satisfying INV-2 for SendPromptAsync.
            await cleanupDone.Task;

            // Resend the last prompt so the agent picks up where it left off.
            // Skipped if tools already completed this turn to avoid re-executing work.
            // IsProcessing is now false and SendingFlag is 0, so SendPromptAsync will succeed.
            if (!hadSuccessfulTools && !string.IsNullOrEmpty(lastPrompt))
            {
                Debug($"[PERMISSION-RECOVER-RESEND] '{sessionName}' resending last prompt");
                try
                {
                    await SendPromptAsync(sessionName, lastPrompt, skipHistoryMessage: true);
                }
                catch (Exception sendEx)
                {
                    Debug($"[PERMISSION-RECOVER-RESEND] Failed for '{sessionName}': {sendEx.Message}");
                }
            }
            else if (hadSuccessfulTools)
            {
                Debug($"[PERMISSION-RECOVER-RESEND] '{sessionName}' skipping resend — tools had already completed this turn");
            }
        }
        catch (Exception ex)
        {
            Debug($"[PERMISSION-RECOVER] Failed for '{sessionName}': {ex.Message}");
            ClearProcessingStateForRecoveryFailure(state, sessionName);
            state.Info.History.Add(ChatMessage.SystemMessage(
                "⚠️ Auto-recovery failed. Use the Reconnect button in Settings → Save & Reconnect to restore permissions."));
            OnStateChanged?.Invoke();
        }
        finally
        {
            _recoveryInProgress.TryRemove(sessionName, out _);
        }
    }

    /// <summary>
    /// Public entry point for per-session permission recovery.
    /// Looks up the session by name and attempts to reconnect it with a fresh permission callback.
    /// </summary>
    public async Task RecoverSessionAsync(string sessionName)
    {
        if (_sessions.TryGetValue(sessionName, out var state))
        {
            await TryRecoverPermissionAsync(state, sessionName);
        }
    }
}
