using System.Text.Json.Serialization;

namespace PolyPilot.Models;

public class ScheduledPrompt
{
    public string Id { get; set; } = Guid.NewGuid().ToString();

    /// <summary>
    /// Optional display label shown in the UI. If empty, the prompt text is used.
    /// </summary>
    public string Label { get; set; } = "";

    /// <summary>
    /// The prompt text to send to the session when the timer fires.
    /// </summary>
    public string Prompt { get; set; } = "";

    /// <summary>
    /// Name of the session to send the prompt to.
    /// </summary>
    public string SessionName { get; set; } = "";

    /// <summary>
    /// When this scheduled prompt should next run (UTC).
    /// Null means it has no pending run (one-shot that has already fired, or disabled).
    /// </summary>
    public DateTime? NextRunAt { get; set; }

    /// <summary>
    /// If > 0, the prompt repeats every N minutes after each run.
    /// If 0, the prompt fires once and is then disabled.
    /// </summary>
    public int RepeatIntervalMinutes { get; set; } = 0;

    /// <summary>
    /// When this prompt last ran (UTC).
    /// </summary>
    public DateTime? LastRunAt { get; set; }

    /// <summary>
    /// Whether this scheduled prompt is active.
    /// </summary>
    public bool IsEnabled { get; set; } = true;

    /// <summary>
    /// Display name: label if set, otherwise truncated prompt.
    /// </summary>
    [JsonIgnore]
    public string DisplayName => !string.IsNullOrWhiteSpace(Label)
        ? Label
        : Prompt.Length > 40 ? Prompt[..37] + "…" : Prompt;
}
