using System.Reflection;

namespace PolyPilot.Services;

/// <summary>
/// Manages LLM efficiency analysis for Copilot sessions.
/// Bundles the extraction script and SKILL.md, creates analysis sessions
/// with appropriate system prompts, and auto-sends the analysis prompt.
/// </summary>
public class EfficiencyAnalysisService
{
    private readonly CopilotService _copilotService;

    private static string? _scriptsDir;
    private static string ScriptsDir => _scriptsDir ??= Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".polypilot", "scripts");

    private static string ScriptPath => Path.Combine(ScriptsDir, "extract_session_metrics.py");

    public EfficiencyAnalysisService(CopilotService copilotService)
    {
        _copilotService = copilotService;
    }

    /// <summary>
    /// Creates a new analysis session for the given session and auto-sends the analysis prompt.
    /// Returns the name of the newly created session.
    /// </summary>
    public async Task<string> AnalyzeSessionAsync(string sessionName)
    {
        var targetSession = _copilotService.GetAllSessions()
            .FirstOrDefault(s => s.Name == sessionName)
            ?? throw new InvalidOperationException($"Session '{sessionName}' not found");

        EnsureScriptOnDisk();

        // Build the analysis session name
        var analysisName = $"📊 {sessionName}";
        // Ensure unique name
        var existing = _copilotService.GetAllSessions().Select(s => s.Name).ToHashSet();
        var finalName = analysisName;
        var counter = 2;
        while (existing.Contains(finalName))
            finalName = $"{analysisName} ({counter++})";

        // Create the session with the target session's working directory
        var workDir = targetSession.WorkingDirectory
            ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        var info = await _copilotService.CreateSessionAsync(finalName, null, workDir);

        // Set the SKILL.md as custom instructions
        _copilotService.SetSessionSystemPrompt(finalName, GetSkillPrompt());

        // Build and send the analysis prompt
        var prompt = BuildAnalysisPrompt(targetSession);
        _ = _copilotService.SendPromptAsync(finalName, prompt);

        return finalName;
    }

    private string BuildAnalysisPrompt(Models.AgentSessionInfo session)
    {
        var sessionId = session.SessionId ?? "unknown";
        var sessionDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".copilot", "session-state", sessionId);

        return $"""
            Analyze LLM efficiency for session "{session.Name}" (ID: {sessionId}).

            Session state directory: {sessionDir}
            Extraction script: {ScriptPath}

            Run the extraction script against this session, then produce the full efficiency report as specified in your instructions.
            """;
    }

    private static void EnsureScriptOnDisk()
    {
        if (File.Exists(ScriptPath))
            return;

        Directory.CreateDirectory(ScriptsDir);
        var scriptContent = GetEmbeddedScript();
        File.WriteAllText(ScriptPath, scriptContent);
    }

    private static string GetEmbeddedScript()
    {
        var asm = Assembly.GetExecutingAssembly();
        var resourceName = asm.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith("extract_session_metrics.py"));

        if (resourceName != null)
        {
            using var stream = asm.GetManifestResourceStream(resourceName)!;
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }

        // Fallback: try local file from the skill repo
        var localPaths = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Documents", "GitHub", "llm-efficiency-skill",
                ".github", "skills", "llm-efficiency", "scripts", "extract_session_metrics.py"),
        };

        foreach (var path in localPaths)
        {
            if (File.Exists(path))
                return File.ReadAllText(path);
        }

        throw new FileNotFoundException(
            "Could not find extract_session_metrics.py. " +
            "Clone https://github.com/mattleibow/llm-efficiency-skill and try again.");
    }

    private static string GetSkillPrompt()
    {
        var asm = Assembly.GetExecutingAssembly();
        var resourceName = asm.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith("llm-efficiency.SKILL.md"));

        if (resourceName != null)
        {
            using var stream = asm.GetManifestResourceStream(resourceName)!;
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }

        // Fallback: try local file
        var localPaths = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Documents", "GitHub", "llm-efficiency-skill",
                ".github", "skills", "llm-efficiency", "SKILL.md"),
        };

        foreach (var path in localPaths)
        {
            if (File.Exists(path))
                return File.ReadAllText(path);
        }

        // Inline minimal prompt as last resort
        return """
            You are an LLM efficiency analyst. Analyze Copilot CLI sessions for waste patterns,
            token bloat, excessive agentic turns, and cost optimization opportunities.
            Produce a structured efficiency report with quantified findings and reduction strategies.
            """;
    }
}
