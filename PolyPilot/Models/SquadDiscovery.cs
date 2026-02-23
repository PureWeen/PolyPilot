using System.Text.RegularExpressions;

namespace PolyPilot.Models;

/// <summary>
/// Discovers bradygaster/squad team definitions from .squad/ or .ai-team/ directories.
/// Parses team.md, agent charters, routing.md, and decisions.md into GroupPreset(s).
/// Read-only: never writes to the .squad/ directory.
/// </summary>
public static class SquadDiscovery
{
    private const int MaxCharterLength = 4000;
    private const int MaxDecisionsLength = 8000;

    /// <summary>Names of agents that are infrastructure, not workers.</summary>
    private static readonly HashSet<string> InfraAgents = new(StringComparer.OrdinalIgnoreCase)
    {
        "scribe", "_scribe", "coordinator", "_coordinator", "_alumni"
    };

    /// <summary>
    /// Discover Squad team definitions from a worktree root.
    /// Returns empty list if no .squad/ or .ai-team/ directory found.
    /// </summary>
    public static List<GroupPreset> Discover(string worktreeRoot)
    {
        try
        {
            var squadDir = FindSquadDirectory(worktreeRoot);
            if (squadDir == null) return new();

            var teamFile = Path.Combine(squadDir, "team.md");
            if (!File.Exists(teamFile)) return new();

            var teamContent = File.ReadAllText(teamFile);
            var agents = DiscoverAgents(squadDir);

            if (agents.Count == 0) return new();

            var teamName = ParseTeamName(teamContent) ?? "Squad Team";
            var mode = ParseMode(teamContent);
            var decisions = ReadOptionalFile(Path.Combine(squadDir, "decisions.md"), MaxDecisionsLength);
            var routing = ReadOptionalFile(Path.Combine(squadDir, "routing.md"), MaxDecisionsLength);

            var preset = BuildPreset(teamName, agents, decisions, routing, squadDir, mode);
            return new List<GroupPreset> { preset };
        }
        catch
        {
            return new();
        }
    }

    /// <summary>
    /// Find .squad/ or .ai-team/ directory. Prefers .squad/ if both exist.
    /// </summary>
    internal static string? FindSquadDirectory(string worktreeRoot)
    {
        var squadPath = Path.Combine(worktreeRoot, ".squad");
        if (Directory.Exists(squadPath)) return squadPath;

        var aiTeamPath = Path.Combine(worktreeRoot, ".ai-team");
        if (Directory.Exists(aiTeamPath)) return aiTeamPath;

        return null;
    }

    /// <summary>
    /// Discover agents from the agents/ subdirectory.
    /// Each agent has a directory with charter.md inside.
    /// Skips infrastructure agents (scribe, coordinator, _alumni).
    /// </summary>
    internal static List<SquadAgent> DiscoverAgents(string squadDir)
    {
        var agentsDir = Path.Combine(squadDir, "agents");
        if (!Directory.Exists(agentsDir)) return new();

        var agents = new List<SquadAgent>();
        foreach (var dir in Directory.GetDirectories(agentsDir))
        {
            var name = Path.GetFileName(dir);
            if (InfraAgents.Contains(name)) continue;

            var charterPath = Path.Combine(dir, "charter.md");
            string? charter = null;
            if (File.Exists(charterPath))
            {
                charter = File.ReadAllText(charterPath);
                if (charter.Length > MaxCharterLength)
                    charter = charter[..MaxCharterLength];
            }

            agents.Add(new SquadAgent(name, charter));
        }

        return agents;
    }

    /// <summary>
    /// Parse team name from team.md content.
    /// Looks for: first H1 heading, or first line that looks like a title.
    /// </summary>
    internal static string? ParseTeamName(string teamContent)
    {
        foreach (var line in teamContent.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("# "))
                return trimmed[2..].Trim();
        }
        return null;
    }

    /// <summary>
    /// Parse mode from team.md content.
    /// Looks for a line like "mode: orchestrator" (case-insensitive).
    /// Supports: broadcast, sequential, orchestrator, orchestrator-reflect.
    /// Defaults to OrchestratorReflect if not specified.
    /// </summary>
    internal static MultiAgentMode ParseMode(string teamContent)
    {
        foreach (var line in teamContent.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("mode:", StringComparison.OrdinalIgnoreCase))
            {
                var value = trimmed["mode:".Length..].Trim().ToLowerInvariant();
                return value switch
                {
                    "broadcast" => MultiAgentMode.Broadcast,
                    "sequential" => MultiAgentMode.Sequential,
                    "orchestrator" => MultiAgentMode.Orchestrator,
                    "orchestrator-reflect" or "orchestratorreflect" or "reflect" => MultiAgentMode.OrchestratorReflect,
                    _ => MultiAgentMode.OrchestratorReflect
                };
            }
        }
        return MultiAgentMode.OrchestratorReflect;
    }

    /// <summary>
    /// Parse agent roster from team.md table rows.
    /// Returns member names from the first column of markdown tables.
    /// </summary>
    internal static List<string> ParseRosterNames(string teamContent)
    {
        var names = new List<string>();
        var tableRegex = new Regex(@"^\s*\|\s*([^\|\s]+)\s*\|", RegexOptions.Multiline);
        foreach (Match m in tableRegex.Matches(teamContent))
        {
            var name = m.Groups[1].Value.Trim();
            // Skip header row markers and header labels
            if (name == "---" || name.All(c => c == '-')
                || name.Equals("Member", StringComparison.OrdinalIgnoreCase)
                || name.Equals("Name", StringComparison.OrdinalIgnoreCase))
                continue;
            names.Add(name);
        }
        return names;
    }

    private static string? ReadOptionalFile(string path, int maxLength)
    {
        if (!File.Exists(path)) return null;
        try
        {
            var content = File.ReadAllText(path);
            if (string.IsNullOrWhiteSpace(content)) return null;
            return content.Length > maxLength ? content[..maxLength] : content;
        }
        catch { return null; }
    }

    private static GroupPreset BuildPreset(string teamName, List<SquadAgent> agents,
        string? decisions, string? routing, string squadDir, MultiAgentMode mode)
    {
        // Use a sensible default model for all agents (user can override after creation)
        var defaultModel = "claude-sonnet-4.6";
        var orchestratorModel = "claude-opus-4.6";

        var workerModels = agents.Select(_ => defaultModel).ToArray();
        var systemPrompts = agents.Select(a => a.Charter).ToArray();

        return new GroupPreset(
            teamName,
            $"Squad team from {Path.GetFileName(Path.GetDirectoryName(squadDir) ?? squadDir)}",
            "🫡",
            mode,
            orchestratorModel,
            workerModels)
        {
            IsRepoLevel = true,
            SourcePath = squadDir,
            WorkerSystemPrompts = systemPrompts,
            SharedContext = decisions,
            RoutingContext = routing,
        };
    }

    /// <summary>Represents a discovered Squad agent with name and charter content.</summary>
    internal record SquadAgent(string Name, string? Charter);
}
