namespace PolyPilot.Models;

/// <summary>
/// Defines an iterative reflection cycle: a prompt is sent, the response is evaluated
/// against a goal, and if the goal is not yet met, a refined follow-up prompt is
/// automatically generated and sent. The cycle continues until the goal is satisfied
/// or the maximum number of iterations is reached.
/// </summary>
public class ReflectionCycle
{
    private const string CompletionMarker = "Goal complete";
    private const string CompletionMarkerWithEmoji = "✅ Goal complete";

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
    /// Optional instructions on how to evaluate whether the goal has been met.
    /// If empty, a default evaluation prompt is constructed from the Goal.
    /// </summary>
    public string EvaluationPrompt { get; set; } = "";

    /// <summary>
    /// Constructs the follow-up prompt to send when the cycle determines
    /// the goal has not yet been met after an iteration.
    /// </summary>
    public string BuildFollowUpPrompt(string lastResponse)
    {
        var evaluation = !string.IsNullOrWhiteSpace(EvaluationPrompt)
            ? EvaluationPrompt
            : $"The goal is: {Goal}";

        return $"[Reflection cycle iteration {CurrentIteration + 1}/{MaxIterations}]\n\n"
             + $"{evaluation}\n\n"
             + "Review your previous response and continue working toward the goal. "
             + $"If the goal is fully met, state \"{CompletionMarkerWithEmoji}\" at the start of your response. "
             + "Otherwise, continue making progress.";
    }

    /// <summary>
    /// Checks whether a response indicates the goal has been met.
    /// Looks for the completion marker in the response text.
    /// </summary>
    public bool IsGoalMet(string response)
    {
        if (string.IsNullOrWhiteSpace(response)) return false;

        return response.Contains(CompletionMarker, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Advances the cycle by one iteration, evaluates the response, and determines
    /// whether the cycle should continue. Increments CurrentIteration, checks for
    /// goal completion, and deactivates the cycle if done.
    /// Returns true if the cycle should send another follow-up prompt.
    /// </summary>
    public bool Advance(string response)
    {
        if (!IsActive) return false;

        CurrentIteration++;

        if (IsGoalMet(response))
        {
            GoalMet = true;
            IsActive = false;
            return false;
        }

        if (CurrentIteration >= MaxIterations)
        {
            IsActive = false;
            return false;
        }

        return true;
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
        };
    }
}
