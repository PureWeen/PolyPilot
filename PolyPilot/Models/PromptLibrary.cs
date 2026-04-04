using System.Text.Json;
using System.Text.Json.Serialization;

namespace PolyPilot.Models;

public enum PromptSource
{
    User,       // Saved by the user in ~/.polypilot/prompts/
    Project,    // Discovered from project prompt directories
    BuiltIn     // Shipped with PolyPilot — available to all users
}

public class SavedPrompt
{
    public string Name { get; set; } = "";
    public string Content { get; set; } = "";
    public string Description { get; set; } = "";

    [JsonIgnore]
    public PromptSource Source { get; set; }

    [JsonIgnore]
    public string? FilePath { get; set; }

    [JsonIgnore]
    public string SourceLabel => Source switch
    {
        PromptSource.BuiltIn => "built-in",
        PromptSource.User => "user",
        PromptSource.Project => "project",
        _ => "unknown"
    };
}
