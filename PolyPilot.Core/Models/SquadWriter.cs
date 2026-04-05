using System.Text;

namespace PolyPilot.Models;

/// <summary>
/// Writes GroupPreset data as a bradygaster/squad .squad/ directory structure.
/// Produces: team.md, agents/{name}/charter.md, decisions.md (optional), routing.md (optional).
/// This is the inverse of SquadDiscovery — write what we read.
/// </summary>
public static class SquadWriter
{
    /// <summary>
    /// Write a GroupPreset to .squad/ format in the given worktree root.
    /// Creates .squad/ directory if it doesn't exist. Overwrites existing files.
    /// </summary>
    public static string WritePreset(string worktreeRoot, GroupPreset preset,
        List<(string Name, string? SystemPrompt)> workers)
    {
        var squadDir = Path.Combine(worktreeRoot, ".squad");
        Directory.CreateDirectory(squadDir);

        WriteTeamFile(squadDir, preset.Name, workers);
        WriteAgentCharters(squadDir, workers);

        if (!string.IsNullOrWhiteSpace(preset.SharedContext))
            File.WriteAllText(Path.Combine(squadDir, "decisions.md"), preset.SharedContext);

        if (!string.IsNullOrWhiteSpace(preset.RoutingContext))
            File.WriteAllText(Path.Combine(squadDir, "routing.md"), preset.RoutingContext);

        return squadDir;
    }

    /// <summary>
    /// Write a GroupPreset from live session data (orchestrator + workers with their system prompts and group context).
    /// </summary>
    public static string WriteFromGroup(string worktreeRoot, string teamName,
        SessionGroup group, List<SessionMeta> members, Func<string, string> getEffectiveModel)
    {
        var workers = members
            .Where(m => m.Role != MultiAgentRole.Orchestrator)
            .Select(m => (Name: SanitizeAgentName(m.SessionName, teamName), SystemPrompt: m.SystemPrompt))
            .ToList();

        var preset = new GroupPreset(
            teamName, "", "🫡", group.OrchestratorMode,
            getEffectiveModel(members.FirstOrDefault(m => m.Role == MultiAgentRole.Orchestrator)?.SessionName ?? ""),
            members.Where(m => m.Role != MultiAgentRole.Orchestrator)
                   .Select(m => getEffectiveModel(m.SessionName)).ToArray())
        {
            SharedContext = group.SharedContext,
            RoutingContext = group.RoutingContext,
        };

        return WritePreset(worktreeRoot, preset, workers);
    }

    private static void WriteTeamFile(string squadDir, string teamName,
        List<(string Name, string? SystemPrompt)> workers)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# {teamName}");
        sb.AppendLine();
        sb.AppendLine("| Member | Role |");
        sb.AppendLine("|--------|------|");
        foreach (var (name, prompt) in workers)
        {
            var role = DeriveRole(name, prompt);
            sb.AppendLine($"| {name} | {role} |");
        }
        File.WriteAllText(Path.Combine(squadDir, "team.md"), sb.ToString());
    }

    private static void WriteAgentCharters(string squadDir,
        List<(string Name, string? SystemPrompt)> workers)
    {
        var agentsDir = Path.Combine(squadDir, "agents");
        // Clean stale agent dirs before re-writing to prevent phantom agents on re-discovery
        if (Directory.Exists(agentsDir))
            Directory.Delete(agentsDir, true);
        Directory.CreateDirectory(agentsDir);

        foreach (var (name, prompt) in workers)
        {
            var agentDir = Path.Combine(agentsDir, name);
            Directory.CreateDirectory(agentDir);
            var charter = prompt ?? $"You are {name}. Complete assigned tasks thoroughly.";
            File.WriteAllText(Path.Combine(agentDir, "charter.md"), charter);
        }
    }

    /// <summary>
    /// Derive a short role description from the agent name or system prompt.
    /// </summary>
    private static string DeriveRole(string name, string? prompt)
    {
        if (!string.IsNullOrWhiteSpace(prompt))
        {
            // Take first sentence of prompt as role, capped at 60 chars
            var firstSentence = prompt.Split('.', '\n')[0].Trim();
            if (firstSentence.StartsWith("You are a ", StringComparison.OrdinalIgnoreCase))
                firstSentence = firstSentence[10..];
            else if (firstSentence.StartsWith("You are an ", StringComparison.OrdinalIgnoreCase))
                firstSentence = firstSentence[11..];
            if (firstSentence.Length > 60)
                firstSentence = firstSentence[..57] + "...";
            if (!string.IsNullOrWhiteSpace(firstSentence))
                return firstSentence;
        }
        // Fall back to name-based role
        return name.Replace("-", " ").Replace("_", " ");
    }

    /// <summary>
    /// Convert a session name like "Code Review Team-worker-1" into an agent name like "worker-1".
    /// Strips the team name prefix and sanitizes for filesystem use.
    /// </summary>
    internal static string SanitizeAgentName(string sessionName, string teamName)
    {
        var name = sessionName;
        // Strip team name prefix (e.g., "Code Review Team-worker-1" → "worker-1")
        if (name.StartsWith(teamName + "-", StringComparison.OrdinalIgnoreCase))
            name = name[(teamName.Length + 1)..];

        // Replace invalid path chars with hyphens
        foreach (var c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '-');

        return name.Trim('-').ToLowerInvariant();
    }
}
