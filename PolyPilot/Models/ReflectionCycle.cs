using System.Text.RegularExpressions;

namespace PolyPilot.Models;

/// <summary>
/// Reflection cycle ("reflection loop"): an iterative mechanism where a prompt is sent,
/// the response is evaluated against a goal, and if the goal is not yet met, a refined
/// follow-up prompt is automatically generated and sent. The cycle continues until the
/// goal is satisfied, the model stalls, or the maximum number of iterations is reached.
/// </summary>
public partial class ReflectionCycle
{
    /// <summary>
    /// Sentinel token the model must emit on its own line to signal goal completion.
    /// Deliberately machine-style to avoid false positives in natural prose.
    /// </summary>
    internal const string CompletionSentinel = "[[REFLECTION_COMPLETE]]";

    [GeneratedRegex(@"^\s*\[\[REFLECTION_COMPLETE\]\]\s*$", RegexOptions.Multiline)]
    private static partial Regex CompletionSentinelRegex();

    private static readonly char[] TokenSeparators = [' ', '\n', '\r', '\t'];
    private const string FollowUpHeaderPrefix = "[Reflection cycle — iteration ";

    /// <summary>
    /// The high-level goal or acceptance criteria that the cycle is working toward.
    /// Used to construct evaluation prompts between iterations.
    /// </summary>
    public string Goal { get; set; } = "";

    /// <summary>
    /// Maximum number of iterations before the cycle stops automatically.
    /// Prevents runaway loops. Default is 5.
    /// </summary>
    public int MaxIterations { get; set; } = 5;

    /// <summary>
    /// Current iteration count (0-based, incremented after each response).
    /// </summary>
    public int CurrentIteration { get; set; }

    /// <summary>
    /// Whether the cycle is currently active and should evaluate responses.
    /// </summary>
    public bool IsActive { get; set; }

    /// <summary>
    /// Whether the cycle completed by meeting its goal (vs. hitting max iterations or being cancelled).
    /// </summary>
    public bool GoalMet { get; set; }

    /// <summary>
    /// Whether the cycle was stopped early because progress stalled.
    /// </summary>
    public bool IsStalled { get; set; }

    /// <summary>
    /// Whether the cycle was manually cancelled by the user via StopGroupReflection.
    /// </summary>
    public bool IsCancelled { get; set; }

    /// <summary>
    /// Number of consecutive stalls detected. Exposed for diagnostics and warning UI.
    /// Not serialized — private stall state (_recentHashes, _lastResponse) is not recoverable
    /// from JSON, so persisting this counter would create inconsistent state after restart.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public int ConsecutiveStalls { get; internal set; }

    /// <summary>
    /// Number of consecutive errors in the reflection loop. Separate from ConsecutiveStalls
    /// because stalls and errors have different thresholds and recovery strategies.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public int ConsecutiveErrors { get; internal set; }

    /// <summary>
    /// Optional instructions on how to evaluate whether the goal has been met.
    /// If empty, a default evaluation prompt is constructed from the Goal.
    /// </summary>
    public string EvaluationPrompt { get; set; } = "";

    /// <summary>
    /// True only on the advance where the first stall is detected.
    /// </summary>
    public bool ShouldWarnOnStall { get; private set; }

    /// <summary>
    /// The Jaccard similarity score from the last stall check (0.0–1.0).
    /// Exposed so the UI can show "91% similar to previous response".
    /// </summary>
    public double LastSimilarity { get; private set; }

    /// <summary>
    /// When the cycle was started.
    /// </summary>
    public DateTime? StartedAt { get; set; }

    /// <summary>
    /// When the cycle completed (goal met, stalled, or max iterations).
    /// </summary>
    public DateTime? CompletedAt { get; set; }

    /// <summary>
    /// Whether the cycle is paused (user can inspect without cancelling).
    /// </summary>
    public bool IsPaused { get; set; }

    /// <summary>
    /// Optional: session name of a dedicated evaluator (different from orchestrator/worker).
    /// </summary>
    public string? EvaluatorSessionName { get; set; }

    /// <summary>
    /// The latest feedback from the evaluator (shown in reflection status UI).
    /// </summary>
    public string? EvaluatorFeedback { get; set; }

    /// <summary>
    /// The orchestrator's evaluation from the last iteration (for multi-agent).
    /// </summary>
    public string? LastEvaluation { get; set; }

    /// <summary>
    /// Per-iteration evaluation results for trend tracking.
    /// </summary>
    public List<EvaluationResult> EvaluationHistory { get; set; } = new();

    /// <summary>
    /// Auto-adjustment suggestions surfaced to the user.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public List<string> PendingAdjustments { get; } = new();

    // Stall detection state (not serialized)
    private readonly List<string> _recentResponses = new();
    private string _lastResponse = "";

    /// <summary>
    /// Resets stall detection state. Call when resuming from pause to avoid
    /// false stall detection from pre-pause response history.
    /// </summary>
    public void ResetStallDetection()
    {
        _recentResponses.Clear();
        _lastResponse = "";
        ConsecutiveStalls = 0;
        ConsecutiveErrors = 0;
        ShouldWarnOnStall = false;
    }

    /// <summary>
    /// Constructs the follow-up prompt to send when the cycle determines
    /// the goal has not yet been met after an iteration.
    /// </summary>
    public string BuildFollowUpPrompt(string lastResponse)
    {
        var evaluation = !string.IsNullOrWhiteSpace(EvaluationPrompt)
            ? EvaluationPrompt
            : $"The goal is: {Goal}";

        return $"{FollowUpHeaderPrefix}{CurrentIteration + 1}/{MaxIterations}]\n\n"
             + $"{evaluation}\n\n"
             + "Before continuing, briefly assess what progress was made and what remains.\n\n"
             + "Then continue working toward the goal. Make concrete, incremental progress.\n\n"
             + "IMPORTANT: Only when the goal is genuinely, fully achieved with NO remaining work, "
             + $"emit this exact sentinel on its own line:\n{CompletionSentinel}\n\n"
             + "Do NOT emit the sentinel if there is any remaining work, errors to fix, or uncertainty. "
             + "Partial progress or \"good enough\" is NOT complete.";
    }

    public string BuildFollowUpStatus()
    {
        var truncatedGoal = Goal.Length > 40 ? Goal[..37] + "…" : Goal;
        return System.Text.Json.JsonSerializer.Serialize(new
        {
            iteration = CurrentIteration + 1,
            max = MaxIterations,
            goal = Goal,
            status = "Refining...",
            nextStep = "Evaluating progress",
            summary = $"🔄 Iteration {CurrentIteration + 1}/{MaxIterations} — \"{truncatedGoal}\""
        });
    }

    public static bool IsReflectionFollowUpPrompt(string prompt)
    {
        return !string.IsNullOrWhiteSpace(prompt) &&
               prompt.StartsWith(FollowUpHeaderPrefix, StringComparison.Ordinal);
    }

    /// <summary>
    /// Builds the prompt sent to the independent evaluator session.
    /// The evaluator sees only the goal and the latest worker response (fresh context).
    /// </summary>
    public string BuildEvaluatorPrompt(string workerResponse)
    {
        var truncated = workerResponse.Length > 4000 ? workerResponse[..4000] + "\n[... truncated]" : workerResponse;
        string iterationContext;
        if (CurrentIteration <= 2)
            iterationContext = $"This is iteration {CurrentIteration}/{MaxIterations}. You MUST find at least one flaw. "
                             + "Even if the response is good, push for higher quality — better word choice, stronger imagery, tighter structure, more creativity. "
                             + "Do NOT say PASS on early iterations unless the work is truly exceptional and flawless.";
        else if (CurrentIteration < MaxIterations - 1)
            iterationContext = $"This is iteration {CurrentIteration}/{MaxIterations}. Be demanding but fair — FAIL if there are genuine issues.";
        else
            iterationContext = $"This is the final iteration ({CurrentIteration}/{MaxIterations}). Be lenient — PASS if the core goal is met.";

        return "You are a ruthlessly strict quality evaluator. Your job is to find flaws and push for excellence.\n\n"
             + $"## Goal\n{Goal}\n\n"
             + $"## Worker's Response (Iteration {CurrentIteration}/{MaxIterations})\n{truncated}\n\n"
             + "## Instructions\n"
             + $"{iterationContext}\n\n"
             + "Check EVERY requirement in the goal. For each one, verify it is fully met.\n"
             + "If ANY requirement is missing, incomplete, or poorly executed: FAIL.\n"
             + "Look for: missing requirements, weak execution, lack of specificity, logical errors, room for improvement.\n\n"
             + "- If ALL requirements are genuinely and fully met with high quality: respond with exactly: PASS\n"
             + "- Otherwise: respond with: FAIL: <specific feedback listing what to improve>\n\n"
             + "Respond with PASS or FAIL: on the FIRST LINE. No other format.";
    }

    /// <summary>
    /// Parses the evaluator's response. Returns (pass, feedback).
    /// </summary>
    public static (bool Pass, string? Feedback) ParseEvaluatorResponse(string response)
    {
        if (string.IsNullOrWhiteSpace(response))
            return (false, "Evaluator returned empty response");

        var firstLine = response.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim() ?? "";

        if (firstLine.StartsWith("PASS", StringComparison.OrdinalIgnoreCase))
            return (true, null);

        if (firstLine.StartsWith("FAIL:", StringComparison.OrdinalIgnoreCase))
        {
            var feedback = firstLine["FAIL:".Length..].Trim();
            if (string.IsNullOrEmpty(feedback) && response.Split('\n').Length > 1)
                feedback = string.Join(" ", response.Split('\n').Skip(1)).Trim();
            return (false, string.IsNullOrEmpty(feedback) ? "No specific feedback provided" : feedback);
        }

        // Unknown format — treat as FAIL with the first line as feedback
        return (false, firstLine.Length > 200 ? firstLine[..200] : firstLine);
    }

    /// <summary>
    /// Builds the follow-up prompt for the worker using evaluator feedback.
    /// </summary>
    public string BuildFollowUpFromEvaluator(string feedback)
    {
        return $"{FollowUpHeaderPrefix}{CurrentIteration + 1}/{MaxIterations}]\n\n"
             + $"An independent evaluator reviewed your work against the goal:\n"
             + $"**Goal:** {Goal}\n\n"
             + $"**Evaluator feedback:** {feedback}\n\n"
             + "Address the feedback above and continue working toward the goal.\n\n"
             + $"When the goal is fully achieved, emit: {CompletionSentinel}";
    }

    /// <summary>
    /// Checks whether a response indicates the goal has been met by looking for the
    /// completion sentinel on its own line. Uses a strict regex to avoid false positives.
    /// </summary>
    public bool IsGoalMet(string response)
    {
        if (string.IsNullOrWhiteSpace(response)) return false;
        return CompletionSentinelRegex().IsMatch(response);
    }

    /// <summary>
    /// Checks if the response indicates a stall (repetitive or near-identical to previous).
    /// Uses exact hash matching over a sliding window and Jaccard token similarity.
    /// </summary>
    public bool CheckStall(string response)
    {
        if (string.IsNullOrWhiteSpace(response)) return true;

        bool isStall = false;
        LastSimilarity = 0.0;

        // Exact repetition check over last 5 responses (full string equality, no hash collisions)
        if (_recentResponses.Contains(response))
        {
            isStall = true;
            LastSimilarity = 1.0;
        }

        _recentResponses.Add(response);
        if (_recentResponses.Count > 5) _recentResponses.RemoveAt(0);

        // Jaccard similarity with immediate predecessor
        if (!isStall && !string.IsNullOrEmpty(_lastResponse))
        {
            var prevWords = new HashSet<string>(
                _lastResponse.Split(TokenSeparators, StringSplitOptions.RemoveEmptyEntries));
            var currWords = new HashSet<string>(
                response.Split(TokenSeparators, StringSplitOptions.RemoveEmptyEntries));

            if (prevWords.Count > 0 && currWords.Count > 0)
            {
                var intersection = new HashSet<string>(prevWords);
                intersection.IntersectWith(currWords);
                var union = new HashSet<string>(prevWords);
                union.UnionWith(currWords);

                double similarity = (double)intersection.Count / union.Count;
                LastSimilarity = similarity;
                if (similarity > 0.9) isStall = true;
            }
        }

        _lastResponse = response;
        return isStall;
    }

    /// <summary>
    /// Advances the cycle by one iteration, evaluates the response, and determines
    /// whether the cycle should continue. Returns true if another follow-up should be sent.
    /// Stops if: goal met, stalled for 2+ consecutive iterations, or max iterations reached.
    /// </summary>
    public bool Advance(string response)
    {
        if (!IsActive) return false;
        if (IsPaused) return false;
        ShouldWarnOnStall = false;

        CurrentIteration++;

        if (IsGoalMet(response))
        {
            GoalMet = true;
            IsActive = false;
            CompletedAt = DateTime.Now;
            return false;
        }

        if (CheckStall(response))
        {
            ConsecutiveStalls++;
            if (ConsecutiveStalls == 1)
                ShouldWarnOnStall = true;
            if (ConsecutiveStalls >= 2)
            {
                IsStalled = true;
                IsActive = false;
                CompletedAt = DateTime.Now;
                return false;
            }
        }
        else
        {
            ConsecutiveStalls = 0;
        }

        if (CurrentIteration >= MaxIterations)
        {
            IsActive = false;
            CompletedAt = DateTime.Now;
            return false;
        }

        return true;
    }

    /// <summary>
    /// Advances the cycle using an independent evaluator's judgment instead of self-evaluation.
    /// The evaluator's pass/fail overrides sentinel detection. Stall detection still runs as a safety net.
    /// </summary>
    public bool AdvanceWithEvaluation(string response, bool evaluatorPassed, string? evaluatorFeedback)
    {
        if (!IsActive) return false;
        if (IsPaused) return false;
        ShouldWarnOnStall = false;

        CurrentIteration++;
        EvaluatorFeedback = evaluatorFeedback;

        if (evaluatorPassed)
        {
            GoalMet = true;
            IsActive = false;
            CompletedAt = DateTime.Now;
            return false;
        }

        // Stall detection still runs as safety net even with evaluator
        if (CheckStall(response))
        {
            ConsecutiveStalls++;
            if (ConsecutiveStalls == 1)
                ShouldWarnOnStall = true;
            if (ConsecutiveStalls >= 2)
            {
                IsStalled = true;
                IsActive = false;
                CompletedAt = DateTime.Now;
                return false;
            }
        }
        else
        {
            ConsecutiveStalls = 0;
        }

        if (CurrentIteration >= MaxIterations)
        {
            IsActive = false;
            CompletedAt = DateTime.Now;
            return false;
        }

        return true;
    }

    /// <summary>
    /// Builds a rich completion summary with duration and iteration details.
    /// </summary>
    public string BuildCompletionSummary()
    {
        var emoji = GoalMet ? "✅" : IsCancelled ? "⏹️" : IsStalled ? "⚠️" : "⏱️";
        var reasonText = GoalMet ? "Goal met" : IsCancelled ? "Cancelled by user" : IsStalled ? $"Stalled ({LastSimilarity:P0} similarity)" : $"Max iterations reached ({MaxIterations})";
        var durationText = "";
        if (StartedAt.HasValue && CompletedAt.HasValue)
        {
            var duration = CompletedAt.Value - StartedAt.Value;
            durationText = duration.TotalMinutes >= 1
                ? $" | Duration: {duration.Minutes}m {duration.Seconds}s"
                : $" | Duration: {duration.TotalSeconds:F0}s";
        }
        return $"{emoji} Reflection complete — **{Goal}**\n" +
               $"Iterations: {CurrentIteration}/{MaxIterations}{durationText}\n" +
               $"Outcome: {reasonText}";
    }

    /// <summary>
    /// Creates a new reflection cycle with the given goal and iteration limit.
    /// </summary>
    public static ReflectionCycle Create(string goal, int maxIterations = 5, string? evaluationPrompt = null, string? evaluatorSession = null)
    {
        return new ReflectionCycle
        {
            Goal = goal,
            MaxIterations = maxIterations,
            EvaluationPrompt = evaluationPrompt ?? "",
            IsActive = true,
            CurrentIteration = 0,
            GoalMet = false,
            StartedAt = DateTime.Now,
            EvaluatorSessionName = evaluatorSession
        };
    }

    /// <summary>
    /// Record an evaluation result and return the quality trend.
    /// </summary>
    public QualityTrend RecordEvaluation(int iteration, double score, string rationale, string evaluatorModel)
    {
        EvaluationHistory.Add(new EvaluationResult
        {
            Iteration = iteration,
            Score = score,
            Rationale = rationale,
            EvaluatorModel = evaluatorModel,
            Timestamp = DateTime.Now
        });

        if (EvaluationHistory.Count < 2) return QualityTrend.Stable;

        var recent = EvaluationHistory.TakeLast(3).Select(e => e.Score).ToList();
        if (recent.Count >= 2 && recent.Last() > recent[^2] + 0.1) return QualityTrend.Improving;
        if (recent.Count >= 2 && recent.Last() < recent[^2] - 0.1) return QualityTrend.Degrading;
        return QualityTrend.Stable;
    }
}

/// <summary>Quality trend across iterations.</summary>
public enum QualityTrend { Improving, Stable, Degrading }

/// <summary>Structured evaluation result from one reflect iteration.</summary>
public class EvaluationResult
{
    public int Iteration { get; set; }
    /// <summary>Quality score 0.0-1.0.</summary>
    public double Score { get; set; }
    public string Rationale { get; set; } = "";
    public string EvaluatorModel { get; set; } = "";
    public DateTime Timestamp { get; set; }
}
