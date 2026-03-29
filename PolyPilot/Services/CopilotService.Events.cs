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

        // External tool requests — classified explicitly to avoid "Unhandled" log spam
        ["ExternalToolRequestedEvent"] = EventVisibility.TimelineOnly,
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
                SafeFireAndForget(_chatDb.AddMessageAsync(state.Info.SessionId, reasoningMsg), "AddMessageAsync");
            else
                SafeFireAndForget(_chatDb.UpdateReasoningContentAsync(state.Info.SessionId, normalizedReasoningId, reasoningMsg.Content, false), "UpdateReasoningContentAsync");
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
                    SafeFireAndForget(_chatDb.UpdateReasoningContentAsync(state.Info.SessionId, msg.ReasoningId, msg.Content, true), "UpdateReasoningContentAsync");
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
        // Skip ALL event processing for orphaned states. When a session reconnects,
        // the old state is marked IsOrphaned=true. The old CopilotSession object may still
        // fire events (replays, stale callbacks) — processing them on the orphaned state
        // would race with the new state and corrupt IsProcessing on the shared Info object.
        if (state.IsOrphaned)
            return;
        
        Volatile.Write(ref state.HasReceivedEventsSinceResume, true);
        // Don't reset the watchdog timer for pure metrics/info events (SessionUsageInfoEvent,
        // AssistantUsageEvent). These are informational only and don't indicate actual turn
        // progress. If the SDK enters a state where it sends repeated usage info events
        // without ever completing the turn (e.g., FailedDelegation), the watchdog must still
        // fire based on lack of real progress events.
        if (evt is not SessionUsageInfoEvent and not AssistantUsageEvent)
        {
            Interlocked.Exchange(ref state.LastEventAtTicks, DateTime.UtcNow.Ticks);
            // Real event arrived — reset the Case A reset counter. This proves the session's
            // JSON-RPC connection is alive, so future Case A resets are legitimate.
            Interlocked.Exchange(ref state.WatchdogCaseAResets, 0);
            Interlocked.Exchange(ref state.WatchdogCaseBResets, 0);
            Interlocked.Exchange(ref state.WatchdogCaseBLastFileSize, 0);
            Interlocked.Exchange(ref state.WatchdogCaseBStaleCount, 0);
            // Clear the reconnect flag — event stream is alive for this session.
            state.IsReconnectedSend = false;
            state.Info.LastUpdatedAt = DateTime.Now;
        }
        // Count every event for zero-idle diagnostics (#299)
        Interlocked.Increment(ref state.EventCountThisTurn);

        var sessionName = state.Info.Name;
        var isCurrentState = _sessions.TryGetValue(sessionName, out var current) && ReferenceEquals(current, state);

        // Log all critical lifecycle events, including TurnStart. TurnStart cancels the
        // TurnEnd→Idle fallback; without logging it, stuck-session forensics cannot see the
        // sub-turn boundary that caused the fallback to be silently cancelled.
        if (evt is SessionIdleEvent or AssistantTurnEndEvent or SessionErrorEvent or AssistantTurnStartEvent)
        {
            Debug($"[EVT] '{sessionName}' received {evt.GetType().Name} " +
                  $"(IsProcessing={state.Info.IsProcessing}, isCurrentState={isCurrentState}, " +
                  $"thread={Environment.CurrentManagedThreadId})");
        }
        // Verbose event tracing: log ALL event types when enabled (for zero-idle investigation #299).
        // This reveals the exact last event before silence — was it ToolExecutionComplete? AssistantMessage?
        else if (_currentSettings?.EnableVerboseEventTracing == true)
        {
            Debug($"[EVT-TRACE] '{sessionName}' {evt.GetType().Name} " +
                  $"(eventCount={state.EventCountThisTurn}, thread={Environment.CurrentManagedThreadId})");
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
                state.HasUsedToolsThisTurn = true; // volatile field — no explicit barrier needed
                // Record tool start time and schedule health check
                Interlocked.Exchange(ref state.ToolStartedAtTicks, DateTime.UtcNow.Ticks);
                ScheduleToolHealthCheck(state, sessionName);
                if (state.Info.ProcessingPhase < 3)
                {
                    state.Info.ProcessingPhase = 3; // Working
                    NotifyStateChangedCoalesced();
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
                // Cancel the tool health check timer since tool completed normally
                CancelToolHealthCheck(state);
                Interlocked.Increment(ref state.Info._toolCallCount);
                var completeCallId = toolDone.Data.ToolCallId ?? "";
                var completeToolName = toolDone.Data?.GetType().GetProperty("ToolName")?.GetValue(toolDone.Data)?.ToString();
                var resultStr = FormatToolResult(toolDone.Data!.Result);
                var hasError = toolDone.Data.Error != null;
                // Extract the error message from the structured Error object.
                // Error is a ToolExecutionCompleteDataError with Message/Code properties
                // — its default ToString() returns the type name, not the message text.
                var errorStr = ExtractErrorMessage(toolDone.Data.Error);
                // Only check resultStr for permission text when there IS an error.
                // Successful tool results can contain source code that mentions "Permission denied"
                // (e.g., reading our own permission detection code) — false positive.
                var isPermissionDenial = IsPermissionDenialText(errorStr)
                    || (hasError && IsPermissionDenialText(resultStr));
                var isShellFailure = IsShellEnvironmentFailure(errorStr)
                    || (hasError && IsShellEnvironmentFailure(resultStr));
                var isMcpFailure = IsMcpError(errorStr)
                    || (hasError && IsMcpError(resultStr));

                // Black-box log every permission denial for post-mortem analysis
                if (isPermissionDenial)
                {
                    var errorPreview = errorStr?.Length > 150 ? errorStr[..150] : errorStr;
                    var resultPreview = resultStr?.Length > 150 ? resultStr[..150] : resultStr;
                    Debug($"[PERMISSION-DENY] '{sessionName}' tool='{completeToolName}' " +
                          $"error='{errorPreview}' result='{resultPreview}' hasError={hasError} " +
                          $"errorType={toolDone.Data.Error?.GetType().Name} " +
                          $"(denials={state.Info.PermissionDenialCount + 1}, isMultiAgent={state.IsMultiAgentSession})");
                }

                if (isShellFailure)
                {
                    Debug($"[SHELL-FAILURE] '{sessionName}' tool='{completeToolName}' " +
                          $"error='{errorStr}' (shell errors so far: posix_spawn/environment failure)");
                }

                if (isMcpFailure)
                {
                    Debug($"[MCP-FAILURE] '{sessionName}' tool='{completeToolName}' " +
                          $"error='{errorStr}' (MCP server error detected)");
                }

                // Track permission denials, shell failures, AND MCP failures via sliding window (3 of last 5 tool results)
                // Shell environment failures (posix_spawn) and MCP server failures are treated like
                // permission denials — they indicate the session's process context is broken and needs recovery.
                var isRecoverableError = isPermissionDenial || isShellFailure || isMcpFailure;
                if (isRecoverableError || !hasError)
                {
                    var denialCount = state.Info.RecordToolResult(isRecoverableError);
                    if (!isRecoverableError && !hasError)
                        Interlocked.Increment(ref state.SuccessfulToolCountThisTurn);
                    if (isRecoverableError && denialCount >= 3)
                    {
                        // Trigger recovery on first threshold crossing (denialCount == 3),
                        // not on subsequent denials that stay at 3+ in the window
                        if (denialCount == 3)
                        {
                            var reason = isMcpFailure ? "MCP server error" : isShellFailure ? "Shell environment broken" : "Permission errors";
                            Debug($"[PERMISSION-RECOVER-TRIGGER] '{sessionName}' threshold reached ({denialCount}/5 errors, reason={reason})");
                            Invoke(() =>
                            {
                                var msg = isMcpFailure
                                    ? "⚠️ MCP server errors detected. Attempting to reconnect session with fresh MCP configuration...\n\n" +
                                      "If the issue persists, try `/mcp reload` to create a new session, or check that the MCP server process is running."
                                    : isShellFailure
                                    ? "⚠️ Shell environment broken (posix_spawn failed). Attempting to reconnect session..."
                                    : "⚠️ Permission errors detected. Attempting to reconnect session...";
                                state.Info.History.Add(ChatMessage.SystemMessage(msg));

                                // For shell failures: also set the server health banner so the user
                                // can restart the entire headless server (the root cause is stale
                                // native modules, not a session-level issue)
                                if (isShellFailure && CurrentMode == ConnectionMode.Persistent)
                                {
                                    ServerHealthNotice = "Shell environment broken — the headless server's native modules may be stale (posix_spawn failed). Restart the server to fix.";
                                }

                                OnStateChanged?.Invoke();
                                _ = TryRecoverPermissionAsync(state, sessionName);
                            });
                        }
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
                        // If resultStr is empty but there's an error message, show the error
                        histToolMsg.Content = string.IsNullOrEmpty(resultStr) && hasError ? errorStr ?? "" : resultStr;
                    }
                }

                Invoke(() =>
                {
                    if (isPermissionDenial)
                        NotifyStateChangedCoalesced();
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
                state.FallbackCanceledByTurnStart = true;
                state.HasReceivedDeltasThisTurn = false;
                var phaseAdvancedToThinking = state.Info.ProcessingPhase < 2;
                if (phaseAdvancedToThinking) state.Info.ProcessingPhase = 2; // Thinking
                Interlocked.Exchange(ref state.ActiveToolCallCount, 0);
                // Premature session.idle recovery: the SDK sometimes sends session.idle
                // mid-turn, then continues processing (ghost events). If we receive a
                // TurnStart after IsProcessing was already cleared, re-arm processing
                // so the UI shows the session as active and content is properly captured
                // via the normal CompleteResponse path on the next session.idle.
                if (!state.Info.IsProcessing && isCurrentState && !state.IsOrphaned)
                {
                    Debug($"[EVT-REARM] '{sessionName}' TurnStartEvent arrived after premature session.idle — re-arming IsProcessing");
                    state.PrematureIdleSignal.Set(); // Signal to ExecuteWorkerAsync that TCS result was truncated
                    Invoke(() =>
                    {
                        if (state.IsOrphaned) return;
                        state.Info.IsProcessing = true;
                        state.Info.ProcessingPhase = 2;
                        state.Info.ProcessingStartedAt ??= DateTime.UtcNow;
                        StartProcessingWatchdog(state, sessionName);
                        OnTurnStart?.Invoke(sessionName);
                        OnActivity?.Invoke(sessionName, "🤔 Thinking...");
                        NotifyStateChangedCoalesced();
                    });
                }
                else
                {
                    Invoke(() =>
                    {
                        OnTurnStart?.Invoke(sessionName);
                        OnActivity?.Invoke(sessionName, "🤔 Thinking...");
                        if (phaseAdvancedToThinking) NotifyStateChangedCoalesced();
                    });
                }
                break;

            case AssistantTurnEndEvent:
                Interlocked.Exchange(ref state.TurnEndReceivedAtTicks, DateTime.UtcNow.Ticks);
                try { CompleteReasoningMessages(state, sessionName); }
                catch (Exception ex)
                {
                    Debug($"[EVT-ERR] '{sessionName}' CompleteReasoningMessages threw in TurnEnd: {ex}");
                }
                // Schedule a delayed CompleteResponse in case SessionIdleEvent never arrives (SDK bug #299).
                // Cancelled by AssistantTurnStartEvent (another round starting) or SessionIdleEvent (normal path).
                // If TurnStart previously canceled the fallback, this re-arms it — creating the
                // self-healing loop: TurnEnd → TurnStart cancel → TurnEnd re-arm → fallback fires.
                {
                    if (state.FallbackCanceledByTurnStart)
                    {
                        Debug($"[TURNEND-FALLBACK] '{sessionName}' re-arming fallback timer (was canceled by TurnStart)");
                        state.FallbackCanceledByTurnStart = false;
                    }
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
                            if (state.IsOrphaned) return;
                            // Guard: if tools are still active, a TurnStart is coming — skip.
                            if (Volatile.Read(ref state.ActiveToolCallCount) > 0)
                            {
                                Debug($"[IDLE-FALLBACK] '{sessionName}' skipped — tools still active");
                                return;
                            }
                            // Guard: if tools were used this turn, more agent rounds may follow
                            // (TurnEnd → thinking → TurnStart → more tools). Firing CompleteResponse
                            // here would prematurely end the turn. Skip the fallback entirely and
                            // let the watchdog handle any genuinely stuck sessions.
                            if (state.HasUsedToolsThisTurn)
                            {
                                Debug($"[IDLE-FALLBACK] '{sessionName}' skipped — tools were used this turn (watchdog will handle if stuck)");
                                return;
                            }
                            Debug($"[IDLE-FALLBACK] '{sessionName}' SessionIdleEvent not received {TurnEndIdleFallbackMs}ms after TurnEnd — firing CompleteResponse");
                            CaptureZeroIdleDiagnostics(state, sessionName, toolsUsed: false);
                            InvokeOnUI(() => CompleteResponse(state, turnEndGen));
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

            case SessionIdleEvent idle:
                // Cancel the TurnEnd→Idle fallback — normal SessionIdleEvent arrived
                CancelTurnEndFallback(state);

                // KEY FIX: Check if the server reports active background tasks (sub-agents, shells).
                // session.idle with background tasks means "foreground quiesced, background still running."
                // Do NOT treat this as terminal — flush text and wait for the real idle.
                if (HasActiveBackgroundTasks(idle))
                {
                    state.HasDeferredIdle = true; // Track for watchdog freshness window
                    Debug($"[IDLE-DEFER] '{sessionName}' session.idle received with active background tasks — " +
                          $"deferring completion (IsProcessing={state.Info.IsProcessing}, " +
                          $"response={state.CurrentResponse.Length}+{state.FlushedResponse.Length} chars)");
                    // Flush accumulated text at each idle boundary to prevent content loss
                    Invoke(() =>
                    {
                        if (state.IsOrphaned) return;
                        FlushCurrentResponse(state);
                        NotifyStateChangedCoalesced();
                    });
                    break; // Don't complete — wait for next idle without background tasks
                }

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
                CancelToolHealthCheck(state);
                Interlocked.Exchange(ref state.ActiveToolCallCount, 0);
                state.HasUsedToolsThisTurn = false;
                state.HasDeferredIdle = false;
                Interlocked.Exchange(ref state.SuccessfulToolCountThisTurn, 0);
                Interlocked.Exchange(ref state.ToolHealthStaleChecks, 0);
                Interlocked.Exchange(ref state.EventCountThisTurn, 0);
                Interlocked.Exchange(ref state.TurnEndReceivedAtTicks, 0);
                InvokeOnUI(() =>
                {
                    if (state.IsOrphaned) return;
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
                    state.IsReconnectedSend = false; // INV-1: clear all per-turn flags on termination
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
            SafeFireAndForget(_chatDb.AddMessageAsync(state.Info.SessionId, msg), "AddMessageAsync");
        
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
        
        // Early dispatch: if the orchestrator wrote @worker blocks in an intermediate sub-turn,
        // resolve the TCS now so ParseTaskAssignments can run immediately. Without this, the
        // orchestrator continues doing tool work itself for minutes before dispatch happens.
        if (state.EarlyDispatchOnWorkerBlocks && state.ResponseCompletion != null)
        {
            var flushed = state.FlushedResponse.ToString();
            if (System.Text.RegularExpressions.Regex.IsMatch(flushed, @"@worker:.+?\n[\s\S]+?@end", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            {
                Debug($"[DISPATCH] Early dispatch: @worker blocks detected in flushed text ({flushed.Length} chars) for '{state.Info.Name}'");
                state.EarlyDispatchOnWorkerBlocks = false; // One-shot
                // Build the full response the same way CompleteResponse does
                var remaining = state.CurrentResponse.ToString();
                var fullResponse = string.IsNullOrEmpty(remaining) ? flushed : flushed + "\n\n" + remaining;
                state.FlushedResponse.Clear();
                state.CurrentResponse.Clear();
                state.ResponseCompletion.TrySetResult(fullResponse);
            }
        }
    }

    /// <summary>
    /// Completes the current response for a session. The <paramref name="expectedGeneration"/>
    /// parameter prevents a stale IDLE callback from completing a different turn than the one
    /// that produced it. Pass <c>null</c> to skip the generation check (e.g. from error paths
    /// or the watchdog where we always want to force-complete).
    /// </summary>
    private void CompleteResponse(SessionState state, long? expectedGeneration = null)
    {
        // Belt-and-suspenders: skip if this state was orphaned by a reconnect.
        // Invoke callbacks may have been queued before IsOrphaned was set.
        if (state.IsOrphaned)
        {
            Debug($"[COMPLETE] '{state.Info.Name}' CompleteResponse skipped — state is orphaned (reconnect replaced it)");
            // Complete the TCS so callers (e.g., orchestrator workers) don't hang forever.
            state.ResponseCompletion?.TrySetCanceled();
            return;
        }
        
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
        CancelToolHealthCheck(state);
        Interlocked.Exchange(ref state.ActiveToolCallCount, 0);
        state.HasUsedToolsThisTurn = false;
        state.HasDeferredIdle = false;
        state.IsReconnectedSend = false; // Clear reconnect flag on turn completion (defense-in-depth)
        state.FallbackCanceledByTurnStart = false;
        Interlocked.Exchange(ref state.SuccessfulToolCountThisTurn, 0);
        Interlocked.Exchange(ref state.ToolHealthStaleChecks, 0);
        Interlocked.Exchange(ref state.EventCountThisTurn, 0);
        Interlocked.Exchange(ref state.TurnEndReceivedAtTicks, 0);
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
                SafeFireAndForget(_chatDb.AddMessageAsync(state.Info.SessionId, msg), "AddMessageAsync");
            
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
        state.Info.IsResumed = false;
        Interlocked.Exchange(ref state.SendingFlag, 0); // Release atomic send lock
        state.Info.ConsecutiveStuckCount = 0;
        // A successful completion proves the server is healthy — reset the
        // service-level watchdog timeout counter to prevent false recovery triggers.
        Interlocked.Exchange(ref _consecutiveWatchdogTimeouts, 0);
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
        var nextPrompt = state.Info.MessageQueue.TryDequeue();
        if (nextPrompt != null)
        {
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
                SafeFireAndForget(_chatDb.AddMessageAsync(state.Info.SessionId, stallWarning), "AddMessageAsync");
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
                        SafeFireAndForget(_chatDb.AddMessageAsync(state.Info.SessionId, ctxWarning), "AddMessageAsync");
                }
                else if (ctxPct > 0.7)
                {
                    var ctxWarning = ChatMessage.SystemMessage($"🟡 Context {ctxPct:P0} used — {cycle.MaxIterations - cycle.CurrentIteration} iterations remaining.");
                    state.Info.History.Add(ctxWarning);
                    state.Info.MessageCount = state.Info.History.Count;
                    if (!string.IsNullOrEmpty(state.Info.SessionId))
                        SafeFireAndForget(_chatDb.AddMessageAsync(state.Info.SessionId, ctxWarning), "AddMessageAsync");
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
                SafeFireAndForget(_chatDb.AddMessageAsync(state.Info.SessionId, reflectionMsg), "AddMessageAsync");

            // Show evaluator feedback in chat if available
            if (!string.IsNullOrEmpty(evaluatorFeedback))
            {
                var feedbackMsg = ChatMessage.SystemMessage($"🔍 Evaluator: {evaluatorFeedback}");
                state.Info.History.Add(feedbackMsg);
                state.Info.MessageCount = state.Info.History.Count;
                if (!string.IsNullOrEmpty(state.Info.SessionId))
                    SafeFireAndForget(_chatDb.AddMessageAsync(state.Info.SessionId, feedbackMsg), "AddMessageAsync");
            }

            // Keep queue FIFO so user steering messages queued during this turn run first.
            state.Info.MessageQueue.Add(followUp);
            OnStateChanged?.Invoke();

            // If the session is idle (evaluator ran asynchronously after CompleteResponse),
            // dispatch the queued message immediately.
            if (!state.Info.IsProcessing)
            {
                var nextPrompt2 = state.Info.MessageQueue.TryDequeue();
                if (nextPrompt2 != null)
                {
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
                                      ReflectionCycle.IsReflectionFollowUpPrompt(nextPrompt2);

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
                                        await SendPromptAsync(state.Info.Name, nextPrompt2, skipHistoryMessage: skipHistory, agentMode: nextAgentMode2);
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
                    SafeFireAndForget(_chatDb.AddMessageAsync(state.Info.SessionId, passMsg), "AddMessageAsync");
            }
            else if (!string.IsNullOrEmpty(evaluatorFeedback))
            {
                var feedbackMsg = ChatMessage.SystemMessage($"🔍 Evaluator: {evaluatorFeedback}");
                state.Info.History.Add(feedbackMsg);
                state.Info.MessageCount = state.Info.History.Count;
                if (!string.IsNullOrEmpty(state.Info.SessionId))
                    SafeFireAndForget(_chatDb.AddMessageAsync(state.Info.SessionId, feedbackMsg), "AddMessageAsync");
            }

            var completionMsg = ChatMessage.SystemMessage(cycle.BuildCompletionSummary());
            state.Info.History.Add(completionMsg);
            state.Info.MessageCount = state.Info.History.Count;
            if (!string.IsNullOrEmpty(state.Info.SessionId))
                SafeFireAndForget(_chatDb.AddMessageAsync(state.Info.SessionId, completionMsg), "AddMessageAsync");

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
    /// <summary>After the first Case A reset (tool running + server alive but no events), switch to this
    /// shorter timeout for subsequent checks. This accelerates dead connection detection while still
    /// allowing the first 600s for legitimate long-running tools. Total max stuck time becomes
    /// 600s + (WatchdogMaxToolAliveResets × 60s) ≈ 12 minutes instead of 40 minutes.</summary>
    internal const int WatchdogToolEscalationTimeoutSeconds = 60;
    // Sessions that USED tools but have none actively running — the model may be
    // thinking between tool rounds, but 600s is too long for a likely-dead session.
    internal const int WatchdogUsedToolsIdleTimeoutSeconds = 180;
    /// <summary>If a resumed session receives zero SDK events for this many seconds, it was likely already
    /// finished when the app restarted. Short enough that users don't have to click Stop, long enough
    /// for the SDK to start streaming if the turn is genuinely still active.</summary>
    internal const int WatchdogResumeQuiescenceTimeoutSeconds = 30;
    /// <summary>If a reconnected session (IsReconnectedSend=true) receives zero SDK events for this many
    /// seconds, the event stream is likely dead (SDK event writer broken after client recreation + mass
    /// sibling re-resume). Shorter than the normal 120s so the reconnect triggers sooner and the user
    /// gets a second retry attempt, rather than waiting the full 2 minutes for the watchdog kill.</summary>
    internal const int WatchdogReconnectInactivityTimeoutSeconds = 35;
    /// <summary>Absolute maximum processing time in seconds. Even if events keep arriving,
    /// no single turn should run longer than this. This is a safety net for scenarios where
    /// non-progress events (like repeated SessionUsageInfoEvent) keep arriving without a terminal event.</summary>
    internal const int WatchdogMaxProcessingTimeSeconds = 3600; // 60 minutes

    /// <summary>Maximum number of consecutive Case A resets (tool active + server alive) before
    /// the watchdog assumes the session's JSON-RPC connection is dead and kills it anyway.
    /// The persistent server may still be alive serving other sessions while this specific
    /// session's transport-level connection is broken (ConnectionLostException). Without this cap,
    /// Case A resets LastEventAtTicks indefinitely, and ProcessingStartedAt resets on each app
    /// restart — so neither the inactivity nor the max-time safety net ever fires.
    /// With escalation timeout (60s after first reset), total max stuck time is:
    /// 600s (first) + 2 × 60s (escalation) = 720s ≈ 12 minutes.</summary>
    internal const int WatchdogMaxToolAliveResets = 2;

    /// <summary>Maximum number of consecutive Case B deferrals (tool finished, events.jsonl fresh)
    /// before completing anyway. Unlike Case A (dead connection), Case B sessions are genuinely
    /// active but ActiveToolCallCount drops to 0 between tool rounds. A generous cap allows
    /// long-running tools (e.g., skill-validator) while preventing infinite deferrals when the
    /// CLI finishes but SessionIdleEvent is lost. At 15s check intervals, 40 deferrals ≈ 10 min
    /// of additional time beyond the initial 120s inactivity timeout.</summary>
    internal const int WatchdogMaxCaseBResets = 40;

    /// <summary>Maximum age (in seconds) of events.jsonl for Case B to consider the session still active.
    /// If events.jsonl was modified after the turn started AND within this window, the CLI is still
    /// writing — defer completion. 300s (5 min) is appropriate for interactive sessions.</summary>
    internal const int WatchdogCaseBFreshnessSeconds = 300;

    /// <summary>Extended freshness window for multi-agent sessions. Worker sessions perform long
    /// server-side tool executions (builds, tests, complex code analysis) where the SDK may pause
    /// event delivery for 10+ minutes while the tool runs. The standard 300s window causes premature
    /// force-completion, sending truncated results to the orchestrator (issue #365).
    /// 1800s (30 min) aligns with the 60-min worker execution timeout as the ultimate backstop.</summary>
    internal const int WatchdogMultiAgentCaseBFreshnessSeconds = 1800;

    /// <summary>Maximum number of consecutive Case B deferrals where events.jsonl file size has NOT
    /// grown before the watchdog stops deferring. When the JSON-RPC connection is lost (ConnectionLostException),
    /// events.jsonl stops growing but its modification time stays within the freshness window. Without
    /// this check, multi-agent sessions with 1800s freshness stay stuck for up to 30 minutes. With this
    /// check, dead connections are detected within 3 watchdog cycles (~6 minutes) — 1 baseline cycle to
    /// record the initial file size, then 2 consecutive stale checks. The file size growth check is a
    /// direct signal: if the CLI is actively writing events, the file grows; if dead, it doesn't.</summary>
    internal const int WatchdogCaseBMaxStaleChecks = 2;

    /// <summary>
    /// Milliseconds after a tool starts to perform the first health check. If no events have
    /// arrived since tool start, we verify the connection is still alive. This detects dead
    /// connections within ~30s instead of waiting for the 600s watchdog timeout.
    /// </summary>
    internal const int ToolHealthCheckIntervalMs = 30_000;

    /// <summary>
    /// Maximum milliseconds to wait for SDK SendAsync to complete before treating it as hung.
    /// SendAsync is called with CancellationToken.None (SDK workaround), so we wrap it with
    /// Task.WhenAny to enforce a client-side timeout. If the server takes longer than this
    /// to accept the message (e.g., large context processing, half-open TCP), we throw
    /// TimeoutException which enters the reconnect/error path. Set to 60s — long enough for
    /// legitimate slow starts but short enough to detect hung connections quickly.
    /// </summary>
    internal const int SendAsyncTimeoutMs = 60_000;

    /// <summary>
    /// Milliseconds to wait after AssistantTurnEndEvent before firing CompleteResponse
    /// as a fallback, in case SessionIdleEvent never arrives (SDK bug #299).
    /// </summary>
    internal const int TurnEndIdleFallbackMs = 4000;

    /// <summary>
    /// Additional milliseconds to wait after the initial TurnEnd fallback when tools were used
    /// this turn. After the base 4s wait we know there are no active tools, but the LLM may
    /// be reasoning between rounds (TurnEnd → TurnStart gap can exceed 4s). If no TurnStart
    /// arrives within this additional window, SessionIdleEvent was likely lost — fire CompleteResponse.
    /// Total wait for tool sessions = TurnEndIdleFallbackMs + TurnEndIdleToolFallbackAdditionalMs = 34s.
    /// </summary>
    internal const int TurnEndIdleToolFallbackAdditionalMs = 30_000;

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
    /// Cancels and disposes any pending tool health check timer.
    /// </summary>
    private static void CancelToolHealthCheck(SessionState state)
    {
        var prev = Interlocked.Exchange(ref state.ToolHealthCheckTimer, null);
        prev?.Dispose();
    }

    /// <summary>
    /// Schedules a tool health check to run after ToolHealthCheckIntervalMs.
    /// If the tool completes before the timer fires, the timer is cancelled.
    /// If the timer fires and no events have arrived since tool start, we check
    /// if the connection is still alive and trigger recovery if it's dead.
    /// </summary>
    private void ScheduleToolHealthCheck(SessionState state, string sessionName)
    {
        // Skip in demo/remote mode where we can't probe the local server
        if (IsDemoMode || IsRemoteMode) return;

        var checkGeneration = Interlocked.Read(ref state.ProcessingGeneration);
        var timer = new Timer(_ =>
        {
            try
            {
                // Verify we're still on the same turn
                if (Interlocked.Read(ref state.ProcessingGeneration) != checkGeneration) return;
                if (!state.Info.IsProcessing) return;
                if (state.IsOrphaned) return;

                var activeTools = Volatile.Read(ref state.ActiveToolCallCount);
                if (activeTools <= 0) return; // Tool completed normally

                // Check if any events arrived since tool start
                var lastEventTicks = Interlocked.Read(ref state.LastEventAtTicks);
                var toolStartTicks = Interlocked.Read(ref state.ToolStartedAtTicks);
                var eventsSinceToolStart = lastEventTicks > toolStartTicks;

                if (eventsSinceToolStart)
                {
                    // Events are still flowing - tool is legitimately running
                    // Schedule another check
                    Interlocked.Exchange(ref state.ToolHealthStaleChecks, 0);
                    Debug($"[TOOL-HEALTH] '{sessionName}' events flowing — rescheduling health check");
                    ScheduleToolHealthCheck(state, sessionName);
                    return;
                }

                // No events since tool start. Check if server is alive.
                var serverAlive = _serverManager.IsServerRunning;
                if (!serverAlive)
                {
                    Debug($"[TOOL-HEALTH] '{sessionName}' server is DEAD — triggering immediate recovery");
                    TriggerToolHealthRecovery(state, sessionName, "server not responding");
                    return;
                }

                // Server TCP port is alive, but no events for 30s. The connection might be dead.
                // Increment the stale check counter and check if we should recover.
                var staleChecks = Interlocked.Increment(ref state.ToolHealthStaleChecks);
                if (staleChecks > WatchdogMaxToolAliveResets)
                {
                    Debug($"[TOOL-HEALTH] '{sessionName}' {staleChecks} stale health checks — assuming dead connection");
                    TriggerToolHealthRecovery(state, sessionName, "no events after multiple health checks (connection likely dead)");
                    return;
                }

                Debug($"[TOOL-HEALTH] '{sessionName}' no events for {ToolHealthCheckIntervalMs/1000}s, server alive — " +
                      $"check {staleChecks}/{WatchdogMaxToolAliveResets}, scheduling another check");
                ScheduleToolHealthCheck(state, sessionName);
            }
            catch (Exception ex)
            {
                Debug($"[TOOL-HEALTH] '{sessionName}' check failed: {ex.Message}");
            }
        }, null, Timeout.Infinite, Timeout.Infinite); // Don't start yet — store first to avoid race

        var prev = Interlocked.Exchange(ref state.ToolHealthCheckTimer, timer);
        prev?.Dispose();
        timer.Change(ToolHealthCheckIntervalMs, Timeout.Infinite); // Now start
    }

    /// <summary>
    /// Triggers recovery when tool health check detects a dead connection.
    /// Clears the stuck processing state and notifies the user.
    /// </summary>
    private void TriggerToolHealthRecovery(SessionState state, string sessionName, string reason)
    {
        if (state.IsOrphaned) return;
        CancelToolHealthCheck(state);
        CancelProcessingWatchdog(state);
        CancelTurnEndFallback(state);

        var activeTools = Volatile.Read(ref state.ActiveToolCallCount);
        var recoveryGeneration = Interlocked.Read(ref state.ProcessingGeneration);
        Debug($"[TOOL-HEALTH] '{sessionName}' triggering recovery: {reason} (activeTools={activeTools})");

        InvokeOnUI(() =>
        {
            if (state.IsOrphaned) return;
            if (!state.Info.IsProcessing) return;
            var currentGen = Interlocked.Read(ref state.ProcessingGeneration);
            if (recoveryGeneration != currentGen) return;

            OnError?.Invoke(sessionName, $"Tool execution stuck ({reason}). Session recovered automatically.");

            // Full cleanup mirroring CompleteResponse — missing fields here caused stuck sessions
            Interlocked.Exchange(ref state.ActiveToolCallCount, 0);
            state.HasUsedToolsThisTurn = false;
            state.HasDeferredIdle = false;
            state.FallbackCanceledByTurnStart = false;
            Interlocked.Exchange(ref state.SuccessfulToolCountThisTurn, 0);
            Interlocked.Exchange(ref state.WatchdogCaseAResets, 0);
            Interlocked.Exchange(ref state.ToolHealthStaleChecks, 0);
            Interlocked.Exchange(ref state.EventCountThisTurn, 0);
            Interlocked.Exchange(ref state.TurnEndReceivedAtTicks, 0);

            // Build full response: flushed mid-turn text + remaining current text
            var response = state.CurrentResponse.ToString();
            var fullResponse = state.FlushedResponse.Length > 0
                ? (string.IsNullOrEmpty(response)
                    ? state.FlushedResponse.ToString()
                    : state.FlushedResponse + "\n\n" + response)
                : response;

            state.CurrentResponse.Clear();
            state.FlushedResponse.Clear();
            state.PendingReasoningMessages.Clear();

            state.Info.IsProcessing = false;
            state.Info.IsResumed = false;
            Interlocked.Exchange(ref state.SendingFlag, 0);
            state.Info.ProcessingStartedAt = null;
            state.Info.ToolCallCount = 0;
            state.Info.ProcessingPhase = 0;
            state.Info.ClearPermissionDenials();

            state.ResponseCompletion?.TrySetResult(fullResponse);

            Debug($"[TOOL-HEALTH-COMPLETE] '{sessionName}' recovery finished (responseLen={fullResponse.Length})");

            var summary = fullResponse.Length > 0 ? (fullResponse.Length > 100 ? fullResponse[..100] + "..." : fullResponse) : "";
            OnSessionComplete?.Invoke(sessionName, summary);
            OnStateChanged?.Invoke();
        });
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

    /// <summary>
    /// Detects shell environment failures where the CLI can no longer spawn processes.
    /// This happens when posix_spawn fails (e.g., broken session process context after
    /// prolonged use or server restart). The session needs to be disposed and recreated.
    /// </summary>
    internal static bool IsShellEnvironmentFailure(string? text)
    {
        if (string.IsNullOrEmpty(text)) return false;
        return text.Contains("posix_spawn failed", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Detects MCP server failures where a configured MCP server is unreachable or crashed.
    /// Only "MCP server" and "mcp_server" substrings match unconditionally — these are
    /// unambiguously MCP-specific. All other patterns (ECONNREFUSED, connection refused,
    /// transport error, spawn ENOENT, etc.) require "mcp" context in the text to avoid
    /// false-positives from SSH, DB, Docker, or HTTP errors that would otherwise incorrectly
    /// trigger MCP recovery.
    /// When repeated MCP failures are detected, the session is recreated with fresh MCP configs
    /// so the CLI can re-launch the MCP server processes.
    /// </summary>
    internal static bool IsMcpError(string? text)
    {
        if (string.IsNullOrEmpty(text)) return false;

        // Unambiguously MCP-specific patterns — safe without additional context
        if (text.Contains("MCP server", StringComparison.OrdinalIgnoreCase)
            || text.Contains("mcp_server", StringComparison.OrdinalIgnoreCase))
            return true;

        // All other patterns require "mcp" context to avoid false-positives:
        // - ECONNREFUSED / spawn ENOENT are standard Node.js/libuv errors emitted by any
        //   tool making a TCP connection or spawning a missing binary (Docker, jq, gh, etc.)
        // - The remaining patterns are generic network/process terms
        var hasMcpContext = text.Contains("mcp", StringComparison.OrdinalIgnoreCase);
        return hasMcpContext && (
            text.Contains("ECONNREFUSED", StringComparison.Ordinal)
            || text.Contains("spawn ENOENT", StringComparison.OrdinalIgnoreCase)
            || text.Contains("connection refused", StringComparison.OrdinalIgnoreCase)
            || text.Contains("server disconnected", StringComparison.OrdinalIgnoreCase)
            || text.Contains("transport error", StringComparison.OrdinalIgnoreCase)
            || text.Contains("failed to start", StringComparison.OrdinalIgnoreCase)
            || text.Contains("server process exited", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Extracts a human-readable error message from a ToolExecutionCompleteData.Error object.
    /// The SDK's ToolExecutionCompleteDataError type has Message/Code properties but does NOT
    /// override ToString() — calling ToString() returns the type name, not the message.
    /// This method reads the Message and Code properties via reflection to get the actual text.
    /// </summary>
    internal static string? ExtractErrorMessage(object? error)
    {
        if (error == null) return null;
        try
        {
            // Try to read the Message property (primary error text)
            var msgProp = error.GetType().GetProperty("Message");
            var message = msgProp?.GetValue(error)?.ToString();
            // Also read Code for additional context
            var codeProp = error.GetType().GetProperty("Code");
            var code = codeProp?.GetValue(error)?.ToString();
            if (!string.IsNullOrEmpty(message) && !string.IsNullOrEmpty(code))
                return $"{message} (code: {code})";
            if (!string.IsNullOrEmpty(message))
                return message;
            if (!string.IsNullOrEmpty(code))
                return code;
        }
        catch
        {
            // Reflection failed — fall through to ToString()
        }
        return error.ToString();
    }

    /// <summary>
    /// Check if a SessionIdleEvent reports active background tasks (agents or shells).
    /// When background tasks are active, session.idle means "foreground quiesced, background
    /// still running" — NOT true completion.
    /// </summary>
    internal static bool HasActiveBackgroundTasks(SessionIdleEvent idle)
    {
        var bt = idle.Data?.BackgroundTasks;
        if (bt == null) return false;
        return (bt.Agents is { Length: > 0 }) || (bt.Shells is { Length: > 0 });
    }

    private void StartProcessingWatchdog(SessionState state, string sessionName)
    {
        CancelProcessingWatchdog(state);
        // Always seed from DateTime.UtcNow. Do NOT pass events.jsonl file time here —
        // that would make elapsed = (file age) + check interval, causing the 30s quiescence
        // timeout to fire on the first watchdog check for any file > ~15s old.
        // This is the exact regression pattern from PR #148 (short timeout killing active sessions).
        Interlocked.Exchange(ref state.LastEventAtTicks, DateTime.UtcNow.Ticks);
        Interlocked.Exchange(ref state.WatchdogCaseAResets, 0);
        Interlocked.Exchange(ref state.WatchdogCaseBResets, 0);
        Interlocked.Exchange(ref state.WatchdogCaseBLastFileSize, 0);
        Interlocked.Exchange(ref state.WatchdogCaseBStaleCount, 0);
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
                if (state.IsOrphaned) { Debug($"[WATCHDOG] '{sessionName}' exiting — state is orphaned"); return; }

                var lastEventTicks = Interlocked.Read(ref state.LastEventAtTicks);
                var elapsed = (DateTime.UtcNow - new DateTime(lastEventTicks)).TotalSeconds;
                var hasActiveTool = Volatile.Read(ref state.ActiveToolCallCount) > 0;

                // After events have started flowing on a resumed session, clear IsResumed
                // so the watchdog transitions from the long 600s timeout to the shorter 120s.
                // Guard: don't clear if tools are active or have been used this turn — between
                // tool rounds, ActiveToolCallCount returns to 0 when AssistantTurnStartEvent
                // resets it, but the model may still be reasoning about the next tool call.
                // HasUsedToolsThisTurn persists across rounds and prevents premature downgrade.
                if (state.Info.IsResumed && Volatile.Read(ref state.HasReceivedEventsSinceResume)
                    && !hasActiveTool && !state.HasUsedToolsThisTurn)
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
                //    thinking about what tool to call next.
                //
                // NOTE: isMultiAgentSession alone does NOT extend the timeout.
                // Workers actively streaming text (e.g., PR reviews) generate delta events
                // continuously, so elapsed stays small regardless of timeout tier. The 600s
                // timeout is only needed for actual tool execution silence. Using 600s for
                // pure reasoning sub-turns (no tools) caused stuck-session UX bugs when the
                // SDK dropped terminal events (sdk bug #299 variant): user had to wait 600s
                // instead of the 120s inactivity timeout.
                var isMultiAgentSession = Volatile.Read(ref state.IsMultiAgentSession);
                var hasReceivedEvents = Volatile.Read(ref state.HasReceivedEventsSinceResume);
                var hasUsedTools = state.HasUsedToolsThisTurn;

                // Resumed session that has received ZERO events since restart — the turn likely
                // completed before the app restarted. Use a short 30s quiescence timeout so the
                // user doesn't have to click Stop. If events start flowing, HasReceivedEventsSinceResume
                // goes true and we fall through to the normal timeout tiers.
                var useResumeQuiescence = state.Info.IsResumed && !hasReceivedEvents && !hasActiveTool && !hasUsedTools;

                // Periodic mid-watchdog flush: if content has accumulated in CurrentResponse
                // for longer than the check interval without being moved to History, flush it now.
                // This ensures partial responses are visible in the chat even while IsProcessing=true
                // (e.g., if TurnEnd→Idle fallback hasn't fired yet, or streaming stalled mid-response).
                if (elapsed >= WatchdogCheckIntervalSeconds)
                {
                    // Capture generation before InvokeOnUI — if the user aborts + resends between
                    // this check and the UI dispatch, the generation changes and we must not flush
                    // new-turn content into the old turn's history.
                    var flushGen = Interlocked.Read(ref state.ProcessingGeneration);
                    InvokeOnUI(() =>
                    {
                        if (Interlocked.Read(ref state.ProcessingGeneration) != flushGen) return;
                        if (state.CurrentResponse.Length > 0)
                        {
                            Debug($"[WATCHDOG] '{sessionName}' periodic flush — CurrentResponse has content after {elapsed:F0}s of inactivity");
                            FlushCurrentResponse(state);
                            OnStateChanged?.Invoke();
                        }
                    });
                }

                var useToolTimeout = hasActiveTool || (state.Info.IsResumed && !useResumeQuiescence);
                var useUsedToolsTimeout = !useToolTimeout && hasUsedTools && !hasActiveTool;
                
                // After the first Case A reset (tool running + server alive but no events arrived),
                // switch to the escalation timeout. This allows the first 600s for legitimate long-running
                // tools, but speeds up dead connection detection on subsequent checks.
                var caseAResets = Volatile.Read(ref state.WatchdogCaseAResets);
                var useEscalationTimeout = useToolTimeout && caseAResets > 0;

                // Reconnected sessions without tools get a shorter inactivity timeout so the dead
                // event stream pattern (SDK file writer broken after mass sibling re-resume) is
                // detected in ~35s rather than the full 120s. Once the first real event arrives,
                // IsReconnectedSend is cleared and we fall back to the normal 120s timeout.
                var useReconnectTimeout = state.IsReconnectedSend && !useToolTimeout && !useUsedToolsTimeout && !useResumeQuiescence;

                var effectiveTimeout = useResumeQuiescence
                    ? WatchdogResumeQuiescenceTimeoutSeconds
                    : useEscalationTimeout
                        ? WatchdogToolEscalationTimeoutSeconds
                        : useToolTimeout
                            ? WatchdogToolExecutionTimeoutSeconds
                            : useUsedToolsTimeout
                                ? WatchdogUsedToolsIdleTimeoutSeconds
                                : useReconnectTimeout
                                    ? WatchdogReconnectInactivityTimeoutSeconds
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
                    // Defensive: if ProcessingStartedAt is null while IsProcessing is true,
                    // something is already wrong — treat as exceeded max time so Case A
                    // can't reset the timer indefinitely.
                    var exceededMaxTime = !startedAt.HasValue
                        || totalProcessingSeconds >= WatchdogMaxProcessingTimeSeconds;

                    // Before killing, check what state we're actually in:
                    //
                    // Case A — Tool actively running (ActiveToolCallCount > 0):
                    //   The SDK only fires events at tool start/complete; a long build or test run
                    //   produces zero events while executing. Use server TCP liveness to decide:
                    //   - Server alive → tool is running, reset inactivity timer and keep waiting
                    //   - Server dead → no events will ever arrive, kill with error
                    //   (Skipped for demo/remote where we can't probe the local TCP port)
                    //
                    // Case B — No active tool (ActiveToolCallCount = 0) and tools were used:
                    //   Tool has finished. The LLM has finished (AssistantTurnEndEvent fired).
                    //   SessionIdleEvent was lost (SDK bug #299 or network hiccup).
                    //   The 34s fallback should have caught this, but if not, complete cleanly.
                    //   No error message — the response IS complete, we just missed the terminal event.
                    //
                    // Case D — Dead send (events.jsonl hasn't grown since SendAsync):
                    //   The SDK accepted the message but isn't processing it. This happens when
                    //   a session resumes with interrupted tool execution — the SDK is stuck
                    //   waiting for tool results that will never arrive. An abort clears this.
                    //   Only fires once per generation (WatchdogAbortAttempted).
                    //   Checked AFTER Case A/B so we never abort active-tool or completing sessions.
                    //
                    // Case C — Max time exceeded OR server dead / demo / remote:
                    //   Kill with error (genuine zombie session).
                    if (!exceededMaxTime)
                    {
                        if (hasActiveTool && !IsDemoMode && !IsRemoteMode)
                        {
                            // Case A: check server TCP port + events.jsonl freshness
                            var serverAlive = _serverManager.IsServerRunning;
                            if (serverAlive)
                            {
                                // Check if the CLI is actively writing events for this session.
                                // events.jsonl is written by the CLI process, not our app.
                                // If recently modified → tool is actively running → wait indefinitely.
                                // If stale → connection is likely dead → kill immediately.
                                var eventsFileActive = false;
                                try
                                {
                                    var sessionId = state.Info.SessionId;
                                    if (!string.IsNullOrEmpty(sessionId))
                                    {
                                        var eventsPath = Path.Combine(SessionStatePath, sessionId, "events.jsonl");
                                        if (File.Exists(eventsPath))
                                        {
                                            var lastWrite = File.GetLastWriteTimeUtc(eventsPath);
                                            var fileAge = (DateTime.UtcNow - lastWrite).TotalSeconds;
                                            eventsFileActive = fileAge < 60; // modified within last 60s
                                        }
                                    }
                                }
                                catch { /* filesystem errors → fall through to reset-cap logic */ }

                                if (eventsFileActive)
                                {
                                    // Events file is fresh — tool is actively running. Wait indefinitely.
                                    Debug($"[WATCHDOG] '{sessionName}' tool is running and events.jsonl is fresh — waiting indefinitely " +
                                          $"(elapsed={elapsed:F0}s, totalProcessing={totalProcessingSeconds:F0}s)");
                                    Interlocked.Exchange(ref state.LastEventAtTicks, DateTime.UtcNow.Ticks);
                                    Interlocked.Exchange(ref state.WatchdogCaseAResets, 0); // reset counter since tool is active
                                    continue;
                                }
                                else
                                {
                                    // Events file is stale or missing — connection is likely dead.
                                    // Use the reset-cap as a safety buffer (1 more cycle to confirm).
                                    var resets = Interlocked.Increment(ref state.WatchdogCaseAResets);
                                    if (resets > 1) // Only need 1 confirmation cycle since we have file evidence
                                    {
                                        Debug($"[WATCHDOG] '{sessionName}' events.jsonl stale and reset count {resets} > 1 " +
                                              $"— killing despite server alive (elapsed={elapsed:F0}s, totalProcessing={totalProcessingSeconds:F0}s)");
                                        // fall through to kill
                                    }
                                    else
                                    {
                                        Debug($"[WATCHDOG] '{sessionName}' events.jsonl stale but giving 1 more cycle " +
                                              $"(reset #{resets}, elapsed={elapsed:F0}s, totalProcessing={totalProcessingSeconds:F0}s)");
                                        Interlocked.Exchange(ref state.LastEventAtTicks, DateTime.UtcNow.Ticks);
                                        continue;
                                    }
                                }
                            }
                            else
                            {
                                Debug($"[WATCHDOG] '{sessionName}' tool running but server is not responding — killing stuck session " +
                                      $"(elapsed={elapsed:F0}s, totalProcessing={totalProcessingSeconds:F0}s)");
                            }
                        }
                        else if (!hasActiveTool && (hasUsedTools || (isMultiAgentSession && !IsDemoMode && !IsRemoteMode && _serverManager.IsServerRunning)))
                        {
                            // Case B: tools finished (or multi-agent session with server alive) and
                            // terminal event was lost — complete cleanly without an error message.
                            // For tool-using sessions: tool finished, SessionIdleEvent lost (sdk bug #299).
                            // For multi-agent no-tool sessions: LLM finished generating, SDK dropped
                            // both AssistantTurnEndEvent and SessionIdleEvent. Server liveness check
                            // confirms the session completed normally, not a server crash.
                            //
                            // BUT: ActiveToolCallCount can drop to 0 between tool rounds (the model
                            // is deciding what to do next). If events.jsonl is still being written,
                            // the session is alive — don't complete prematurely.
                            if (!IsDemoMode && !IsRemoteMode && startedAt.HasValue)
                            {
                                // Compare events.jsonl modification time to when this turn started
                                // AND check that it was modified recently (within 5 min). The CLI
                                // writes events.jsonl independently from our SDK handler — during
                                // long tool execution, our handler sees zero events but the CLI may
                                // still be writing. We need BOTH checks because:
                                // - "after turn start" alone stays true forever once any event is written
                                // - "recent" alone could match stale files from a previous turn
                                var caseBEventsActive = false;
                                var freshnessSeconds = (isMultiAgentSession || state.HasDeferredIdle)
                                    ? WatchdogMultiAgentCaseBFreshnessSeconds
                                    : WatchdogCaseBFreshnessSeconds;
                                try
                                {
                                    var sid = state.Info.SessionId;
                                    if (!string.IsNullOrEmpty(sid))
                                    {
                                        var ep = Path.Combine(SessionStatePath, sid, "events.jsonl");
                                        if (File.Exists(ep))
                                        {
                                            var fileInfo = new FileInfo(ep);
                                            var lastWrite = fileInfo.LastWriteTimeUtc;
                                            var currentFileSize = fileInfo.Length;
                                            var age = (DateTime.UtcNow - lastWrite).TotalSeconds;
                                            // File must be: (1) written after this turn started AND
                                            // (2) written within the freshness window (CLI is still active).
                                            // Multi-agent workers get a much longer window because the SDK
                                            // can pause event delivery for 10+ min during long tool runs.
                                            caseBEventsActive = lastWrite > startedAt.Value && age < freshnessSeconds;

                                            // Even if the file looks fresh, check if the server shut down
                                            // the session. session.shutdown is written to events.jsonl when
                                            // the server kills a session (idle timeout, stuck tools, etc.)
                                            // but the client event stream may be dead so we never received it.
                                            // Same pattern as HasInterruptedToolExecution in Utilities.cs.
                                            if (caseBEventsActive)
                                            {
                                                var lastEventType = GetLastEventType(ep);
                                                if (lastEventType == "session.shutdown")
                                                {
                                                    Debug($"[WATCHDOG] '{sessionName}' Case B — events.jsonl ends with session.shutdown, " +
                                                          $"skipping deferral (server killed session)");
                                                    caseBEventsActive = false;
                                                }
                                            }

                                            // File size growth check: if the file hasn't grown since the
                                            // last Case B deferral, the CLI is no longer writing events —
                                            // the session's JSON-RPC connection is likely dead (ConnectionLostException).
                                            // The freshness window (especially 1800s for multi-agent) keeps
                                            // caseBEventsActive=true based on modification time alone, but
                                            // a truly active CLI would be appending new events. After
                                            // WatchdogCaseBMaxStaleChecks consecutive checks with no growth,
                                            // override the freshness signal and force completion.
                                            if (caseBEventsActive)
                                            {
                                                var prevSize = Interlocked.Read(ref state.WatchdogCaseBLastFileSize);
                                                if (prevSize > 0 && currentFileSize <= prevSize)
                                                {
                                                    var staleCount = Interlocked.Increment(ref state.WatchdogCaseBStaleCount);
                                                    if (staleCount >= WatchdogCaseBMaxStaleChecks)
                                                    {
                                                        Debug($"[WATCHDOG] '{sessionName}' Case B — events.jsonl not growing " +
                                                              $"(size={currentFileSize}, staleChecks={staleCount}/{WatchdogCaseBMaxStaleChecks}) " +
                                                              $"— connection likely dead, skipping deferral " +
                                                              $"(elapsed={elapsed:F0}s, totalProcessing={totalProcessingSeconds:F0}s)");
                                                        caseBEventsActive = false;
                                                    }
                                                }
                                                else
                                                {
                                                    // File grew — CLI is actively writing, reset stale counter
                                                    Interlocked.Exchange(ref state.WatchdogCaseBStaleCount, 0);
                                                }
                                                Interlocked.Exchange(ref state.WatchdogCaseBLastFileSize, currentFileSize);
                                            }
                                        }
                                    }
                                }
                                catch { /* filesystem errors → proceed with completion */ }

                                if (caseBEventsActive)
                                {
                                    var caseBResets = Interlocked.Increment(ref state.WatchdogCaseBResets);
                                    if (caseBResets <= WatchdogMaxCaseBResets)
                                    {
                                        Debug($"[WATCHDOG] '{sessionName}' Case B deferred — events.jsonl modified since turn start, session still active " +
                                              $"(elapsed={elapsed:F0}s, totalProcessing={totalProcessingSeconds:F0}s, deferral={caseBResets}/{WatchdogMaxCaseBResets}, " +
                                              $"freshness={freshnessSeconds}s{(isMultiAgentSession ? " [multi-agent]" : "")})");
                                        Interlocked.Exchange(ref state.LastEventAtTicks, DateTime.UtcNow.Ticks);
                                        continue;
                                    }
                                    Debug($"[WATCHDOG] '{sessionName}' Case B deferral cap reached ({caseBResets}/{WatchdogMaxCaseBResets}) — completing despite fresh events.jsonl " +
                                          $"(elapsed={elapsed:F0}s, totalProcessing={totalProcessingSeconds:F0}s, freshness={freshnessSeconds}s{(isMultiAgentSession ? " [multi-agent]" : "")})");
                                }
                            }

                            var watchdogGen = Interlocked.Read(ref state.ProcessingGeneration);
                            Debug($"[WATCHDOG] '{sessionName}' tool finished but SessionIdleEvent never arrived — completing cleanly " +
                                  $"(elapsed={elapsed:F0}s, totalProcessing={totalProcessingSeconds:F0}s)");
                            InvokeOnUI(() =>
                            {
                                if (!state.Info.IsProcessing) return;
                                if (Interlocked.Read(ref state.ProcessingGeneration) != watchdogGen) return;
                                CompleteResponse(state, watchdogGen);
                            });
                            break;
                        }

                        // Case D: Dead send detection — events.jsonl hasn't grown since send.
                        // The SDK accepted SendAsync but is stuck (e.g., pending interrupted tools
                        // not caught at resume time). Try AbortAsync to clear the stuck state.
                        // Placed AFTER Case A/B so we never abort sessions with active tools or
                        // sessions that are completing normally with lost terminal events.
                        // Only fires once per generation (WatchdogAbortAttempted).
                        if (!hasActiveTool && !state.WatchdogAbortAttempted 
                            && Interlocked.Read(ref state.EventsFileSizeAtSend) > 0
                            && !IsDemoMode && !IsRemoteMode && elapsed >= 30)
                        {
                            try
                            {
                                var sid = state.Info.SessionId;
                                if (!string.IsNullOrEmpty(sid))
                                {
                                    var eventsPath = Path.Combine(SessionStatePath, sid, "events.jsonl");
                                    if (File.Exists(eventsPath))
                                    {
                                        var currentSize = new FileInfo(eventsPath).Length;
                                        var sizeAtSend = Interlocked.Read(ref state.EventsFileSizeAtSend);
                                        if (currentSize <= sizeAtSend)
                                        {
                                            // events.jsonl hasn't grown — SDK is not processing.
                                            state.WatchdogAbortAttempted = true;
                                            Debug($"[WATCHDOG-DEAD-SEND] '{sessionName}' events.jsonl unchanged since send " +
                                                  $"(size={currentSize}, sizeAtSend={sizeAtSend}, elapsed={elapsed:F0}s) — sending abort");
                                            try
                                            {
                                                await state.Session.AbortAsync(ct);
                                                Debug($"[WATCHDOG-DEAD-SEND] '{sessionName}' abort sent — resetting watchdog timer");
                                                Interlocked.Exchange(ref state.LastEventAtTicks, DateTime.UtcNow.Ticks);
                                                continue;
                                            }
                                            catch (Exception abortEx)
                                            {
                                                Debug($"[WATCHDOG-DEAD-SEND] '{sessionName}' abort failed: {abortEx.Message} — falling through to normal timeout");
                                            }
                                        }
                                    }
                                }
                            }
                            catch { /* filesystem error — fall through */ }
                        }
                    }

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
                        if (state.IsOrphaned) return; // Reconnect already replaced this state
                        if (!state.Info.IsProcessing) return; // Already completed
                        var currentGen = Interlocked.Read(ref state.ProcessingGeneration);
                        if (watchdogGeneration != currentGen)
                        {
                            Debug($"Session '{sessionName}' watchdog callback skipped — generation mismatch " +
                                  $"(watchdog={watchdogGeneration}, current={currentGen}). A new SEND superseded this turn.");
                            return;
                        }
                        CancelProcessingWatchdog(state);
                        CancelToolHealthCheck(state);
                        Interlocked.Exchange(ref state.ActiveToolCallCount, 0);
                        state.HasUsedToolsThisTurn = false;
                        state.HasDeferredIdle = false;
                        Interlocked.Exchange(ref state.SuccessfulToolCountThisTurn, 0);
                        Interlocked.Exchange(ref state.ToolHealthStaleChecks, 0);
                        Interlocked.Exchange(ref state.EventCountThisTurn, 0);
                        Interlocked.Exchange(ref state.TurnEndReceivedAtTicks, 0);
                        // Cancel any pending TurnEnd→Idle fallback
                        CancelTurnEndFallback(state);
                        state.Info.IsResumed = false;
                        state.IsReconnectedSend = false; // INV-1: clear all per-turn flags on termination
                        // Flush any accumulated partial response before clearing processing state.
                        // Wrapped in try-catch: if flush fails, IsProcessing MUST still be cleared
                        // (otherwise the session is permanently stuck — the watchdog has already exited).
                        try { FlushCurrentResponse(state); }
                        catch (Exception flushEx) { Debug($"[WATCHDOG] '{sessionName}' flush failed during kill: {flushEx.Message}"); }
                        Debug($"[WATCHDOG] '{sessionName}' IsProcessing=false — watchdog timeout after {totalProcessingSeconds:F0}s total, elapsed={elapsed:F0}s, exceededMaxTime={exceededMaxTime}");
                        state.Info.IsProcessing = false;
                        Interlocked.Exchange(ref state.SendingFlag, 0);
                        if (state.Info.ProcessingStartedAt is { } wdStarted)
                            state.Info.TotalApiTimeSeconds += (DateTime.UtcNow - wdStarted).TotalSeconds;
                        state.Info.ProcessingStartedAt = null;
                        state.Info.ToolCallCount = 0;
                        state.Info.ProcessingPhase = 0;
                        state.Info.ClearPermissionDenials(); // INV-1: clear on all termination paths
                        state.Info.ConsecutiveStuckCount++;
                        // Track service-level consecutive watchdog timeouts. When the
                        // persistent server's auth token expires, ALL sessions hang silently.
                        // After WatchdogServerRecoveryThreshold consecutive timeouts across
                        // any sessions, attempt automatic server restart (re-authentication).
                        var serviceTimeouts = Interlocked.Increment(ref _consecutiveWatchdogTimeouts);
                        if (serviceTimeouts >= WatchdogServerRecoveryThreshold
                            && CurrentMode == ConnectionMode.Persistent
                            && !IsDemoMode && !IsRemoteMode)
                        {
                            Debug($"[SERVER-RECOVERY] {serviceTimeouts} consecutive watchdog timeouts — triggering persistent server recovery");
                            _ = Task.Run(async () =>
                            {
                                try { await TryRecoverPersistentServerAsync(); }
                                catch (Exception recoverEx) { Debug($"[SERVER-RECOVERY] Background recovery failed: {recoverEx.Message}"); }
                            });
                        }
                        // Break the positive feedback loop: when a session repeatedly gets stuck,
                        // each "Session appears stuck" system message grows the history, increasing
                        // server context processing time and making the NEXT failure more likely.
                        // After 3+ consecutive timeouts, skip the system message to stop history growth.
                        if (state.Info.ConsecutiveStuckCount < 3)
                        {
                            state.Info.History.Add(ChatMessage.SystemMessage(
                                "⚠️ Session appears stuck — no response received. You can try sending your message again."));
                        }
                        else
                        {
                            // Clear message queue to prevent auto-dispatch from immediately re-sending
                            // into a session that's in a repeated-stuck cycle.
                            state.Info.MessageQueue.Clear();
                        }
                        var watchdogResponse = state.FlushedResponse.ToString();
                        state.FlushedResponse.Clear();
                        state.PendingReasoningMessages.Clear();
                        state.ResponseCompletion?.TrySetResult(watchdogResponse);
                        // Fire completion notification so orchestrator loops are unblocked (INV-O4)
                        OnSessionComplete?.Invoke(sessionName, "[Watchdog] timeout");
                        var stuckCountMsg = state.Info.ConsecutiveStuckCount >= 3
                            ? $"Session has failed to respond {state.Info.ConsecutiveStuckCount} times consecutively. Consider starting a new session — the conversation history ({state.Info.History.Count} messages) may be too large for the server to process."
                            : $"Session appears stuck — no events received for over {timeoutDisplay}.";
                        OnError?.Invoke(sessionName, stuckCountMsg);
                        OnStateChanged?.Invoke();
                    });
                    break;
                }
            }
        }
        catch (OperationCanceledException) { /* Normal cancellation when response completes */ }
        catch (Exception ex)
        {
            // Safety net: if the watchdog crashes for ANY reason, clear IsProcessing to prevent
            // permanently stuck sessions. Without this, any unexpected exception (NRE, state
            // corruption, etc.) leaves the session showing "Sending..." forever with no recovery
            // path — the watchdog is the last line of defense.
            Debug($"[WATCHDOG-CRASH] Watchdog loop error for '{sessionName}': {ex.Message}");
            try
            {
                InvokeOnUI(() =>
                {
                    if (state.IsOrphaned) return;
                    if (!state.Info.IsProcessing) return;
                    Debug($"[WATCHDOG-CRASH] '{sessionName}' clearing IsProcessing after watchdog crash");
                    // Best-effort flush before clearing processing state
                    try { FlushCurrentResponse(state); }
                    catch { /* Flush failure must not prevent IsProcessing cleanup */ }
                    // INV-1: clear IsProcessing and all 9 companion fields
                    state.Info.IsProcessing = false;
                    state.Info.IsResumed = false;
                    Interlocked.Exchange(ref state.SendingFlag, 0);
                    Interlocked.Exchange(ref state.ActiveToolCallCount, 0);
                    state.HasUsedToolsThisTurn = false;
                    state.HasDeferredIdle = false;
                    Interlocked.Exchange(ref state.SuccessfulToolCountThisTurn, 0);
                    Interlocked.Exchange(ref state.ToolHealthStaleChecks, 0);
                    Interlocked.Exchange(ref state.EventCountThisTurn, 0);
                    Interlocked.Exchange(ref state.TurnEndReceivedAtTicks, 0);
                    state.Info.ProcessingStartedAt = null;
                    state.Info.ToolCallCount = 0;
                    state.Info.ProcessingPhase = 0;
                    state.Info.ClearPermissionDenials();
                    state.Info.ConsecutiveStuckCount++;
                    Interlocked.Increment(ref _consecutiveWatchdogTimeouts);
                    var crashResponse = state.FlushedResponse.ToString() + state.CurrentResponse.ToString();
                    state.FlushedResponse.Clear();
                    state.CurrentResponse.Clear();
                    state.PendingReasoningMessages.Clear();
                    state.ResponseCompletion?.TrySetResult(crashResponse);
                    OnSessionComplete?.Invoke(sessionName, "[Watchdog] crash recovery");
                    OnError?.Invoke(sessionName, "Internal error in session monitoring. Try sending your message again.");
                    OnStateChanged?.Invoke();
                });
            }
            catch { /* Best effort — InvokeOnUI itself failed */ }
        }
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
            state.HasDeferredIdle = false;
            Interlocked.Exchange(ref state.SuccessfulToolCountThisTurn, 0);
            Interlocked.Exchange(ref state.ActiveToolCallCount, 0);
            Interlocked.Exchange(ref state.ToolHealthStaleChecks, 0);
            Interlocked.Exchange(ref state.EventCountThisTurn, 0);
            Interlocked.Exchange(ref state.TurnEndReceivedAtTicks, 0);
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
            CancelToolHealthCheck(state);

            // Bug B fix: Cancel the old ResponseCompletion TCS so the original
            // SendPromptAsync awaiter doesn't hang forever.
            state.ResponseCompletion?.TrySetCanceled();

            // Create new state preserving Info
            var oldState = state;
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
            newState.HasDeferredIdle = false;

            // Replace in sessions dictionary BEFORE registering event handler
            // so HandleSessionEvent's isCurrentState check passes for the new state.
            _sessions[sessionName] = newState;
            DisposePrematureIdleSignal(oldState);
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
                state.HasDeferredIdle = false;
                Interlocked.Exchange(ref state.SuccessfulToolCountThisTurn, 0);
                Interlocked.Exchange(ref state.ActiveToolCallCount, 0);
                Interlocked.Exchange(ref state.ToolHealthStaleChecks, 0);
                Interlocked.Exchange(ref state.EventCountThisTurn, 0);
                Interlocked.Exchange(ref state.TurnEndReceivedAtTicks, 0);
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

    // ── Zero-idle capture diagnostics (#299) ────────────────────────────────

    private static string? _zeroIdleCaptureDir;
    private static string ZeroIdleCaptureDir
    {
        get
        {
            lock (_pathLock)
                return _zeroIdleCaptureDir ??= Path.Combine(PolyPilotBaseDir, "zero-idle-captures");
        }
    }

    // For testing: override the capture directory
    internal static void SetCaptureDirForTesting(string dir) => _zeroIdleCaptureDir = dir;
    internal static void ResetCaptureDir() => _zeroIdleCaptureDir = null;

    /// <summary>
    /// Writes a diagnostic capture file when the TurnEnd→Idle fallback fires,
    /// meaning SessionIdleEvent was not received (SDK bug #299).
    /// Includes session state snapshot, event counts, and last events from events.jsonl.
    /// Never throws — capture failures are swallowed to Console.Error.
    /// </summary>
    private void CaptureZeroIdleDiagnostics(SessionState state, string sessionName, bool toolsUsed)
    {
        try
        {
            var captureDir = ZeroIdleCaptureDir;
            Directory.CreateDirectory(captureDir);

            var sessionId = state.Info.SessionId ?? "unknown";
            var now = DateTime.UtcNow;
            var turnEndTicks = Interlocked.Read(ref state.TurnEndReceivedAtTicks);
            var turnEndAge = turnEndTicks > 0
                ? (now - new DateTime(turnEndTicks, DateTimeKind.Utc)).TotalSeconds
                : -1;
            var lastEventAge = (now - new DateTime(Interlocked.Read(ref state.LastEventAtTicks), DateTimeKind.Utc)).TotalSeconds;

            // Read last 50 events from events.jsonl
            var eventsFile = Path.Combine(SessionStatePath, sessionId, "events.jsonl");
            var recentEvents = new List<object>();
            try
            {
                if (File.Exists(eventsFile))
                {
                    var allEvents = ParseEventLogFile(eventsFile);
                    var tail = allEvents.Count > 50 ? allEvents.GetRange(allEvents.Count - 50, 50) : allEvents;
                    foreach (var (ts, type, detail) in tail)
                        recentEvents.Add(new { timestamp = ts, type, detail });
                }
            }
            catch { /* best-effort */ }

            // Count concurrent sessions
            var totalSessions = _sessions.Count;
            var processingSessions = _sessions.Values.Count(s => s.Info.IsProcessing);

            var capture = new Dictionary<string, object?>
            {
                ["capture_timestamp"] = now.ToString("O"),
                ["trigger"] = "IDLE_FALLBACK",
                ["session"] = new Dictionary<string, object?>
                {
                    ["session_id"] = sessionId,
                    ["session_name"] = sessionName,
                    ["model"] = state.Info.Model,
                    ["history_size"] = state.Info.MessageCount,
                    ["is_multi_agent"] = state.IsMultiAgentSession,
                },
                ["processing_state"] = new Dictionary<string, object?>
                {
                    ["is_processing"] = state.Info.IsProcessing,
                    ["processing_phase"] = state.Info.ProcessingPhase,
                    ["active_tool_call_count"] = Volatile.Read(ref state.ActiveToolCallCount),
                    ["has_used_tools_this_turn"] = state.HasUsedToolsThisTurn,
                    ["successful_tool_count"] = state.SuccessfulToolCountThisTurn,
                    ["event_count_this_turn"] = Volatile.Read(ref state.EventCountThisTurn),
                    ["processing_generation"] = Interlocked.Read(ref state.ProcessingGeneration),
                    ["has_received_deltas"] = state.HasReceivedDeltasThisTurn,
                },
                ["timing"] = new Dictionary<string, object?>
                {
                    ["turn_end_age_seconds"] = Math.Round(turnEndAge, 2),
                    ["last_event_age_seconds"] = Math.Round(lastEventAge, 2),
                    ["fallback_tools_used"] = toolsUsed,
                    ["fallback_wait_ms"] = toolsUsed
                        ? TurnEndIdleFallbackMs + TurnEndIdleToolFallbackAdditionalMs
                        : TurnEndIdleFallbackMs,
                },
                ["concurrency"] = new Dictionary<string, object?>
                {
                    ["total_sessions"] = totalSessions,
                    ["processing_sessions"] = processingSessions,
                },
                ["events_jsonl_tail"] = recentEvents,
            };

            var json = System.Text.Json.JsonSerializer.Serialize(capture,
                new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
            var fileName = $"capture_{now:yyyy-MM-ddTHH-mm-ss}_{sessionId[..Math.Min(8, sessionId.Length)]}.json";
            File.WriteAllText(Path.Combine(captureDir, fileName), json);

            Debug($"[ZERO-IDLE] '{sessionName}' capture written: {fileName} " +
                  $"(events={state.EventCountThisTurn}, historySize={state.Info.History.Count}, " +
                  $"turnEndAge={turnEndAge:F1}s, tools={toolsUsed})");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[ZeroIdleCapture] Failed to write capture: {ex.Message}");
        }
    }

    /// <summary>
    /// Deletes old zero-idle capture files, keeping the most recent 100.
    /// Called once on startup. Never throws.
    /// </summary>
    internal static void PurgeOldCaptures(int keepCount = 100)
    {
        try
        {
            var dir = ZeroIdleCaptureDir;
            if (!Directory.Exists(dir)) return;
            var files = Directory.GetFiles(dir, "capture_*.json")
                .OrderByDescending(f => f)
                .Skip(keepCount)
                .ToList();
            foreach (var file in files)
            {
                try { File.Delete(file); } catch { }
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[ZeroIdleCapture] Failed to purge old captures: {ex.Message}");
        }
    }
}
