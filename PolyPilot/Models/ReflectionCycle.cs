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
    /// Number of consecutive stalls detected. Exposed for diagnostics and warning UI.
    /// </summary>
    public int ConsecutiveStalls { get; private set; }

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

    // Stall detection state (not serialized)
    private readonly List<int> _recentHashes = new();
    private string _lastResponse = "";

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

        // Exact repetition check over last 5 responses
        int currentHash = response.GetHashCode();
        if (_recentHashes.Contains(currentHash))
        {
            isStall = true;
            LastSimilarity = 1.0;
        }

        _recentHashes.Add(currentHash);
        if (_recentHashes.Count > 5) _recentHashes.RemoveAt(0);

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
    /// Builds a rich completion summary with duration and iteration details.
    /// </summary>
    public string BuildCompletionSummary()
    {
        var emoji = GoalMet ? "✅" : IsStalled ? "⚠️" : "⏱️";
        var reasonText = GoalMet ? "Goal met" : IsStalled ? $"Stalled ({LastSimilarity:P0} similarity)" : $"Max iterations reached ({MaxIterations})";
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
    public static ReflectionCycle Create(string goal, int maxIterations = 5, string? evaluationPrompt = null)
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
        };
    }
}
