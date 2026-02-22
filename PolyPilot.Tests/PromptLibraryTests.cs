using PolyPilot.Models;
using PolyPilot.Services;

namespace PolyPilot.Tests;

public class PromptLibraryTests : IDisposable
{
    private readonly string _testDir;

    public PromptLibraryTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), $"PolyPilot-prompt-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testDir);
    }

    [Fact]
    public void SavedPrompt_DefaultValues()
    {
        var prompt = new SavedPrompt();

        Assert.Equal("", prompt.Name);
        Assert.Equal("", prompt.Content);
        Assert.Equal("", prompt.Description);
        Assert.Equal(PromptSource.User, prompt.Source);
        Assert.Null(prompt.FilePath);
    }

    [Fact]
    public void SavedPrompt_SourceLabel_User()
    {
        var prompt = new SavedPrompt { Source = PromptSource.User };
        Assert.Equal("user", prompt.SourceLabel);
    }

    [Fact]
    public void SavedPrompt_SourceLabel_Project()
    {
        var prompt = new SavedPrompt { Source = PromptSource.Project };
        Assert.Equal("project", prompt.SourceLabel);
    }

    [Fact]
    public void ParsePromptFile_PlainMarkdown_UsesFilename()
    {
        var content = "Fix all the bugs in the codebase.";
        var filePath = "/prompts/fix-bugs.md";

        var (name, description, body) = PromptLibraryService.ParsePromptFile(content, filePath);

        Assert.Equal("fix-bugs", name);
        Assert.Equal("", description);
        Assert.Equal("Fix all the bugs in the codebase.", body);
    }

    [Fact]
    public void ParsePromptFile_WithFrontmatter()
    {
        var content = "---\nname: Code Review\ndescription: Review code for best practices\n---\nPlease review the following code...";
        var filePath = "/prompts/review.md";

        var (name, description, body) = PromptLibraryService.ParsePromptFile(content, filePath);

        Assert.Equal("Code Review", name);
        Assert.Equal("Review code for best practices", description);
        Assert.Equal("Please review the following code...", body);
    }

    [Fact]
    public void ParsePromptFile_FrontmatterNameOnly()
    {
        var content = "---\nname: Quick Fix\n---\nFix the issue quickly.";
        var filePath = "/prompts/quick.md";

        var (name, description, body) = PromptLibraryService.ParsePromptFile(content, filePath);

        Assert.Equal("Quick Fix", name);
        Assert.Equal("", description);
        Assert.Equal("Fix the issue quickly.", body);
    }

    [Fact]
    public void ParsePromptFile_QuotedValues()
    {
        var content = "---\nname: \"My Prompt\"\ndescription: 'A helpful prompt'\n---\nDo something.";
        var filePath = "/prompts/test.md";

        var (name, description, body) = PromptLibraryService.ParsePromptFile(content, filePath);

        Assert.Equal("My Prompt", name);
        Assert.Equal("A helpful prompt", description);
    }

    [Fact]
    public void ParsePromptFile_NoFrontmatterEnd_UsesFilename()
    {
        var content = "---\nname: Broken\nThis is not closed";
        var filePath = "/prompts/broken.md";

        var (name, description, body) = PromptLibraryService.ParsePromptFile(content, filePath);

        Assert.Equal("broken", name);
        Assert.Equal("", description);
    }

    [Fact]
    public void ScanPromptDirectory_FindsMdFiles()
    {
        var promptDir = Path.Combine(_testDir, "prompts");
        Directory.CreateDirectory(promptDir);
        File.WriteAllText(Path.Combine(promptDir, "test1.md"), "---\nname: Test One\ndescription: First test\n---\nContent one");
        File.WriteAllText(Path.Combine(promptDir, "test2.md"), "Plain content without frontmatter");
        File.WriteAllText(Path.Combine(promptDir, "not-a-prompt.txt"), "Should be ignored");

        var prompts = new List<SavedPrompt>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        PromptLibraryService.ScanPromptDirectory(promptDir, PromptSource.Project, prompts, seen);

        Assert.Equal(2, prompts.Count);
        Assert.Contains(prompts, p => p.Name == "Test One" && p.Description == "First test");
        Assert.Contains(prompts, p => p.Name == "test2" && p.Content == "Plain content without frontmatter");
    }

    [Fact]
    public void ScanPromptDirectory_SkipsDuplicateNames()
    {
        var promptDir = Path.Combine(_testDir, "prompts-dedup");
        Directory.CreateDirectory(promptDir);
        File.WriteAllText(Path.Combine(promptDir, "review.md"), "---\nname: Review\n---\nFirst");

        var prompts = new List<SavedPrompt>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        seen.Add("Review"); // Already seen
        PromptLibraryService.ScanPromptDirectory(promptDir, PromptSource.Project, prompts, seen);

        Assert.Empty(prompts);
    }

    [Fact]
    public void DiscoverPrompts_FromProjectDirectories()
    {
        var projectDir = Path.Combine(_testDir, "my-project");
        var promptDir = Path.Combine(projectDir, ".github", "prompts");
        Directory.CreateDirectory(promptDir);
        File.WriteAllText(Path.Combine(promptDir, "deploy.md"), "---\nname: Deploy\ndescription: Deploy the app\n---\nDeploy steps...");

        var prompts = PromptLibraryService.DiscoverPrompts(projectDir)
            .Where(p => p.Source == PromptSource.Project).ToList();

        Assert.Single(prompts);
        Assert.Equal("Deploy", prompts[0].Name);
        Assert.Equal(PromptSource.Project, prompts[0].Source);
    }

    [Fact]
    public void DiscoverPrompts_CopilotPromptsDir()
    {
        var projectDir = Path.Combine(_testDir, "copilot-project");
        var promptDir = Path.Combine(projectDir, ".github", "copilot-prompts");
        Directory.CreateDirectory(promptDir);
        File.WriteAllText(Path.Combine(promptDir, "review.md"), "Review code carefully.");

        var prompts = PromptLibraryService.DiscoverPrompts(projectDir)
            .Where(p => p.Source == PromptSource.Project).ToList();

        Assert.Single(prompts);
        Assert.Equal("review", prompts[0].Name);
        Assert.Equal("Review code carefully.", prompts[0].Content);
    }

    [Fact]
    public void DiscoverPrompts_MultipleProjectDirs()
    {
        var projectDir = Path.Combine(_testDir, "multi-project");
        var githubDir = Path.Combine(projectDir, ".github", "prompts");
        var copilotDir = Path.Combine(projectDir, ".copilot", "prompts");
        Directory.CreateDirectory(githubDir);
        Directory.CreateDirectory(copilotDir);
        File.WriteAllText(Path.Combine(githubDir, "from-github.md"), "---\nname: GitHub Prompt\n---\nFrom github");
        File.WriteAllText(Path.Combine(copilotDir, "from-copilot.md"), "---\nname: Copilot Prompt\n---\nFrom copilot");

        var prompts = PromptLibraryService.DiscoverPrompts(projectDir)
            .Where(p => p.Source == PromptSource.Project).ToList();

        Assert.Equal(2, prompts.Count);
        Assert.Contains(prompts, p => p.Name == "GitHub Prompt");
        Assert.Contains(prompts, p => p.Name == "Copilot Prompt");
    }

    [Fact]
    public void DiscoverPrompts_NoDirectory_ReturnsEmpty()
    {
        var prompts = PromptLibraryService.DiscoverPrompts("/nonexistent/path");
        // Should not throw, may be empty (depends on user prompts dir existence)
        Assert.NotNull(prompts);
    }

    [Fact]
    public void DiscoverPrompts_NullDirectory_ReturnsAtLeastEmpty()
    {
        var prompts = PromptLibraryService.DiscoverPrompts(null);
        Assert.NotNull(prompts);
    }

    [Fact]
    public void SanitizeFileName_AlphanumericUnchanged()
    {
        Assert.Equal("hello-world", PromptLibraryService.SanitizeFileName("hello-world"));
    }

    [Fact]
    public void SanitizeFileName_SpacesReplaced()
    {
        Assert.Equal("hello-world", PromptLibraryService.SanitizeFileName("hello world"));
    }

    [Fact]
    public void SanitizeFileName_SpecialCharsReplaced()
    {
        Assert.Equal("test-prompt--v2", PromptLibraryService.SanitizeFileName("test/prompt!@v2"));
    }

    [Fact]
    public void SanitizeFileName_EmptyString_FallsBack()
    {
        Assert.Equal("prompt", PromptLibraryService.SanitizeFileName(""));
    }

    [Fact]
    public void SanitizeFileName_AllSpecialChars_FallsBack()
    {
        Assert.Equal("prompt", PromptLibraryService.SanitizeFileName("@#$"));
    }

    [Fact]
    public void SanitizeFileName_Underscores_Preserved()
    {
        Assert.Equal("my_prompt", PromptLibraryService.SanitizeFileName("my_prompt"));
    }

    [Fact]
    public void PromptSource_Enum_HasExpectedValues()
    {
        Assert.Equal(0, (int)PromptSource.User);
        Assert.Equal(1, (int)PromptSource.Project);
    }

    [Fact]
    public void ParsePromptFile_MultilineDescription_Skipped()
    {
        var content = "---\nname: Test\ndescription: >\n  multiline desc\n---\nBody content";
        var filePath = "/test.md";

        var (name, description, body) = PromptLibraryService.ParsePromptFile(content, filePath);

        Assert.Equal("Test", name);
        Assert.Equal("", description); // multiline > is skipped
        Assert.Equal("Body content", body);
    }

    [Fact]
    public void ParsePromptFile_EmptyContent()
    {
        var (name, description, body) = PromptLibraryService.ParsePromptFile("", "/empty.md");

        Assert.Equal("empty", name);
        Assert.Equal("", description);
        Assert.Equal("", body);
    }

    [Fact]
    public void DiscoverPrompts_ClaudePromptsDir()
    {
        var projectDir = Path.Combine(_testDir, "claude-project");
        var promptDir = Path.Combine(projectDir, ".claude", "prompts");
        Directory.CreateDirectory(promptDir);
        File.WriteAllText(Path.Combine(promptDir, "analyze.md"), "---\nname: Analyze\n---\nAnalyze the code.");

        var prompts = PromptLibraryService.DiscoverPrompts(projectDir)
            .Where(p => p.Source == PromptSource.Project).ToList();

        Assert.Single(prompts);
        Assert.Equal("Analyze", prompts[0].Name);
    }

    [Fact]
    public void ParsePromptFile_DashesInsideYamlValue_NotTreatedAsClosing()
    {
        var content = "---\nname: test---name\ndescription: a---b\n---\nBody here";
        var filePath = "/test.md";

        var (name, description, body) = PromptLibraryService.ParsePromptFile(content, filePath);

        Assert.Equal("test---name", name);
        Assert.Equal("a---b", description);
        Assert.Equal("Body here", body);
    }

    [Fact]
    public void SanitizeYamlValue_StripsNewlines()
    {
        var result = PromptLibraryService.SanitizeYamlValue("line1\nline2\r\nline3");
        Assert.Equal("line1 line2 line3", result);
    }

    [Fact]
    public void SanitizeYamlValue_EscapesQuotes()
    {
        var result = PromptLibraryService.SanitizeYamlValue("say \"hello\"");
        Assert.Equal("say \\\"hello\\\"", result);
    }

    [Fact]
    public void SanitizeYamlValue_PlainString_Unchanged()
    {
        var result = PromptLibraryService.SanitizeYamlValue("simple name");
        Assert.Equal("simple name", result);
    }

    public void Dispose()
    {
        try { Directory.Delete(_testDir, true); } catch { }
    }
}
