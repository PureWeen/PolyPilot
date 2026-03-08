using PolyPilot.Models;

namespace PolyPilot.Services;

/// <summary>
/// Discovers prompts from standard coding-agent locations and manages user-saved prompts.
/// Standard project locations scanned:
///   .github/copilot-prompts/, .github/prompts/, .copilot/prompts/, .claude/prompts/
/// User prompts stored in ~/.polypilot/prompts/ as .md files.
/// </summary>
public class PromptLibraryService
{
    private static string? _userPromptsDir;
    private static string UserPromptsDir => _userPromptsDir ??= Path.Combine(GetPolyPilotDir(), "prompts");

    /// <summary>
    /// Override the user prompts directory. Used by tests to avoid reading real ~/.polypilot/prompts/.
    /// </summary>
    internal static void SetUserPromptsDirForTesting(string path) => _userPromptsDir = path;

    /// <summary>
    /// Standard project subdirectories where coding agents store prompt files.
    /// </summary>
    private static readonly string[] ProjectPromptDirs = new[]
    {
        ".github/copilot-prompts",
        ".github/prompts",
        ".copilot/prompts",
        ".claude/prompts"
    };

    private static string GetPolyPilotDir()
    {
#if IOS || ANDROID
        try
        {
            return Path.Combine(FileSystem.AppDataDirectory, ".polypilot");
        }
        catch
        {
            var fallback = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrEmpty(fallback))
                fallback = Path.GetTempPath();
            return Path.Combine(fallback, ".polypilot");
        }
#else
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrEmpty(home))
            home = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(home, ".polypilot");
#endif
    }

    /// <summary>
    /// Discover all available prompts from user-saved prompts and project prompt directories.
    /// </summary>
    public static List<SavedPrompt> DiscoverPrompts(string? workingDirectory = null)
    {
        var prompts = new List<SavedPrompt>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            // User-saved prompts (~/.polypilot/prompts/)
            if (Directory.Exists(UserPromptsDir))
                ScanPromptDirectory(UserPromptsDir, PromptSource.User, prompts, seen);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Prompts] User prompt discovery failed: {ex.Message}");
        }

        try
        {
            // Project-level prompts from standard coding-agent locations
            if (!string.IsNullOrEmpty(workingDirectory))
            {
                foreach (var subdir in ProjectPromptDirs)
                {
                    var promptDir = Path.Combine(workingDirectory, subdir);
                    if (Directory.Exists(promptDir))
                        ScanPromptDirectory(promptDir, PromptSource.Project, prompts, seen);
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Prompts] Project prompt discovery failed: {ex.Message}");
        }

        return prompts;
    }

    internal static void ScanPromptDirectory(string directory, PromptSource source, List<SavedPrompt> prompts, HashSet<string> seen)
    {
        foreach (var file in Directory.GetFiles(directory, "*.md"))
        {
            try
            {
                var content = File.ReadAllText(file);
                var (name, description, body) = ParsePromptFile(content, file);
                if (seen.Add(name))
                {
                    prompts.Add(new SavedPrompt
                    {
                        Name = name,
                        Content = body,
                        Description = description,
                        Source = source,
                        FilePath = file
                    });
                }
            }
            catch { }
        }
    }

    /// <summary>
    /// Parse a prompt markdown file. Supports optional YAML frontmatter with name/description fields.
    /// Falls back to the filename (without extension) as the name.
    /// </summary>
    internal static (string name, string description, string body) ParsePromptFile(string content, string filePath)
    {
        string? name = null;
        string? description = null;
        var body = content;

        if (content.StartsWith("---"))
        {
            // Search for closing --- that starts on its own line
            var endIdx = -1;
            var searchFrom = 3;
            while (searchFrom < content.Length)
            {
                var idx = content.IndexOf("---", searchFrom, StringComparison.Ordinal);
                if (idx < 0) break;
                if (idx == 0 || content[idx - 1] == '\n')
                {
                    endIdx = idx;
                    break;
                }
                searchFrom = idx + 1;
            }
            if (endIdx > 0)
            {
                var frontmatter = content[3..endIdx];
                body = content[(endIdx + 3)..].TrimStart('\r', '\n');

                foreach (var line in frontmatter.Split('\n'))
                {
                    var trimmed = line.Trim();
                    if (trimmed.StartsWith("name:"))
                        name = trimmed[5..].Trim().Trim('"', '\'');
                    else if (trimmed.StartsWith("description:"))
                    {
                        var desc = trimmed[12..].Trim();
                        if (!desc.StartsWith(">"))
                            description = desc.Trim('"', '\'');
                    }
                }
            }
        }

        if (string.IsNullOrEmpty(name))
            name = Path.GetFileNameWithoutExtension(filePath);

        return (name!, description ?? "", body);
    }

    /// <summary>
    /// Save a prompt to the user's prompt library (~/.polypilot/prompts/).
    /// </summary>
    public static SavedPrompt SavePrompt(string name, string content, string? description = null)
    {
        Directory.CreateDirectory(UserPromptsDir);

        // Sanitize name/description to prevent YAML corruption
        var yamlName = SanitizeYamlValue(name);
        var yamlDesc = description != null ? SanitizeYamlValue(description) : null;

        var safeName = SanitizeFileName(name);
        var filePath = Path.Combine(UserPromptsDir, safeName + ".md");

        // Resolve filename collisions: if file exists with a different prompt name, append suffix
        if (File.Exists(filePath))
        {
            try
            {
                var existing = File.ReadAllText(filePath);
                var (existingName, _, _) = ParsePromptFile(existing, filePath);
                if (!string.Equals(existingName, yamlName, StringComparison.OrdinalIgnoreCase))
                {
                    var found = false;
                    for (var i = 2; i < 100; i++)
                    {
                        filePath = Path.Combine(UserPromptsDir, $"{safeName}-{i}.md");
                        if (!File.Exists(filePath)) { found = true; break; }
                        var existingN = File.ReadAllText(filePath);
                        var (n, _, _) = ParsePromptFile(existingN, filePath);
                        if (string.Equals(n, yamlName, StringComparison.OrdinalIgnoreCase))
                        { found = true; break; } // Same logical name — overwrite is fine
                    }
                    if (!found)
                        filePath = Path.Combine(UserPromptsDir, $"{safeName}-{Guid.NewGuid():N}.md");
                }
            }
            catch { }
        }

        var fileContent = "";
        if (!string.IsNullOrWhiteSpace(yamlDesc))
        {
            fileContent = $"---\nname: \"{yamlName}\"\ndescription: \"{yamlDesc}\"\n---\n{content}";
        }
        else
        {
            fileContent = $"---\nname: \"{yamlName}\"\n---\n{content}";
        }

        File.WriteAllText(filePath, fileContent);

        return new SavedPrompt
        {
            Name = yamlName,
            Content = content,
            Description = yamlDesc ?? "",
            Source = PromptSource.User,
            FilePath = filePath
        };
    }

    /// <summary>
    /// Delete a user-saved prompt by name.
    /// </summary>
    public static bool DeletePrompt(string name)
    {
        if (!Directory.Exists(UserPromptsDir))
            return false;

        foreach (var file in Directory.GetFiles(UserPromptsDir, "*.md"))
        {
            try
            {
                var content = File.ReadAllText(file);
                var (parsedName, _, _) = ParsePromptFile(content, file);
                if (string.Equals(parsedName, name, StringComparison.OrdinalIgnoreCase))
                {
                    File.Delete(file);
                    return true;
                }
            }
            catch { }
        }

        return false;
    }

    /// <summary>
    /// Get a specific prompt by name from user or project prompts.
    /// </summary>
    public static SavedPrompt? GetPrompt(string name, string? workingDirectory = null)
    {
        return DiscoverPrompts(workingDirectory)
            .FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Sanitize a string for safe inclusion in a YAML double-quoted value.
    /// Strips newlines, backslashes, and double quotes to avoid YAML corruption.
    /// The hand-rolled parser does not handle YAML escapes, so we strip rather than escape.
    /// </summary>
    internal static string SanitizeYamlValue(string value)
    {
        return value
            .Replace("\r", "")
            .Replace("\n", " ")
            .Replace("\\", "")
            .Replace("\"", "")
            .Replace("'", "");
    }

    /// <summary>
    /// Sanitize a name into a safe filename (alphanumeric, hyphens, underscores).
    /// </summary>
    internal static string SanitizeFileName(string name)
    {
        var sanitized = new char[name.Length];
        for (var i = 0; i < name.Length; i++)
        {
            var c = name[i];
            sanitized[i] = char.IsLetterOrDigit(c) || c == '-' || c == '_' ? c : '-';
        }

        var result = new string(sanitized).Trim('-');
        return string.IsNullOrEmpty(result) ? "prompt" : result;
    }
}
