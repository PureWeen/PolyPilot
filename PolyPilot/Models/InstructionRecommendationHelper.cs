using System.Text;

namespace PolyPilot.Models;

/// <summary>
/// Builds prompts for generating instruction recommendations (skills, agents, copilot-instructions).
/// </summary>
public static class InstructionRecommendationHelper
{
    /// <summary>
    /// Build a recommendation prompt that asks the AI to analyze the project and suggest
    /// improvements to skills, agents, and copilot instruction files.
    /// </summary>
    /// <param name="workingDirectory">The project directory to analyze (may be null).</param>
    /// <param name="existingSkills">Currently discovered skills.</param>
    /// <param name="existingAgents">Currently discovered agents.</param>
    /// <param name="repoName">Optional repository display name.</param>
    public static string BuildRecommendationPrompt(
        string? workingDirectory,
        IReadOnlyList<(string Name, string Description)>? existingSkills = null,
        IReadOnlyList<(string Name, string Description)>? existingAgents = null,
        string? repoName = null)
    {
        var sb = new StringBuilder();

        sb.AppendLine("Analyze this project and recommend improvements to the AI coding assistant configuration.");
        sb.AppendLine();

        if (!string.IsNullOrEmpty(repoName))
            sb.AppendLine($"**Repository:** {repoName}");
        if (!string.IsNullOrEmpty(workingDirectory))
            sb.AppendLine($"**Working directory:** {workingDirectory}");

        sb.AppendLine();
        sb.AppendLine("Please examine the project structure, code patterns, build system, and development workflow, then provide specific recommendations for:");
        sb.AppendLine();
        sb.AppendLine("1. **Copilot Instructions** (`.github/copilot-instructions.md`) — Suggest project-specific instructions that would help the AI understand conventions, architecture patterns, preferred libraries, naming conventions, error handling patterns, and other project-specific knowledge.");
        sb.AppendLine();
        sb.AppendLine("2. **Skills** (`.github/skills/` or `.copilot/skills/`) — Recommend reusable skills (with SKILL.md frontmatter) for common project tasks like building, testing, deploying, or domain-specific operations.");
        sb.AppendLine();
        sb.AppendLine("3. **Agents** (`.github/agents/` or `.copilot/agents/`) — Suggest specialized agent definitions for recurring workflows like code review, documentation generation, refactoring, or project-specific automation.");

        if (existingSkills != null && existingSkills.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("**Currently configured skills:**");
            foreach (var skill in existingSkills)
            {
                sb.Append($"- **{skill.Name}**");
                if (!string.IsNullOrEmpty(skill.Description))
                    sb.Append($": {skill.Description}");
                sb.AppendLine();
            }
        }

        if (existingAgents != null && existingAgents.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("**Currently configured agents:**");
            foreach (var agent in existingAgents)
            {
                sb.Append($"- **{agent.Name}**");
                if (!string.IsNullOrEmpty(agent.Description))
                    sb.Append($": {agent.Description}");
                sb.AppendLine();
            }
        }

        sb.AppendLine();
        sb.AppendLine("For each recommendation, provide:");
        sb.AppendLine("- The file path where it should be created or updated");
        sb.AppendLine("- The complete content to add");
        sb.AppendLine("- A brief explanation of why it would be valuable");

        return sb.ToString();
    }
}
