using PolyPilot.Models;

namespace PolyPilot.Tests;

public class InstructionRecommendationHelperTests
{
    [Fact]
    public void BuildPrompt_NoArguments_ContainsBasicSections()
    {
        var prompt = InstructionRecommendationHelper.BuildRecommendationPrompt(null);

        Assert.Contains("Copilot Instructions", prompt);
        Assert.Contains("Skills", prompt);
        Assert.Contains("Agents", prompt);
        Assert.Contains("copilot-instructions.md", prompt);
    }

    [Fact]
    public void BuildPrompt_WithWorkingDirectory_IncludesDirectory()
    {
        var prompt = InstructionRecommendationHelper.BuildRecommendationPrompt("/home/user/myproject");

        Assert.Contains("/home/user/myproject", prompt);
    }

    [Fact]
    public void BuildPrompt_WithRepoName_IncludesRepoName()
    {
        var prompt = InstructionRecommendationHelper.BuildRecommendationPrompt(
            "/home/user/myproject", repoName: "MyOrg/MyRepo");

        Assert.Contains("MyOrg/MyRepo", prompt);
    }

    [Fact]
    public void BuildPrompt_WithExistingSkills_ListsThem()
    {
        var skills = new List<(string Name, string Description)>
        {
            ("build", "Build the project"),
            ("test", "Run tests")
        };

        var prompt = InstructionRecommendationHelper.BuildRecommendationPrompt(
            "/project", existingSkills: skills);

        Assert.Contains("Currently configured skills", prompt);
        Assert.Contains("**build**", prompt);
        Assert.Contains("Build the project", prompt);
        Assert.Contains("**test**", prompt);
        Assert.Contains("Run tests", prompt);
    }

    [Fact]
    public void BuildPrompt_WithExistingAgents_ListsThem()
    {
        var agents = new List<(string Name, string Description)>
        {
            ("reviewer", "Code review agent"),
            ("docs", "")
        };

        var prompt = InstructionRecommendationHelper.BuildRecommendationPrompt(
            "/project", existingAgents: agents);

        Assert.Contains("Currently configured agents", prompt);
        Assert.Contains("**reviewer**", prompt);
        Assert.Contains("Code review agent", prompt);
        Assert.Contains("**docs**", prompt);
    }

    [Fact]
    public void BuildPrompt_NoSkillsOrAgents_DoesNotListSections()
    {
        var prompt = InstructionRecommendationHelper.BuildRecommendationPrompt("/project");

        Assert.DoesNotContain("Currently configured skills", prompt);
        Assert.DoesNotContain("Currently configured agents", prompt);
    }

    [Fact]
    public void BuildPrompt_EmptySkillsAndAgents_DoesNotListSections()
    {
        var skills = new List<(string Name, string Description)>();
        var agents = new List<(string Name, string Description)>();

        var prompt = InstructionRecommendationHelper.BuildRecommendationPrompt(
            "/project", existingSkills: skills, existingAgents: agents);

        Assert.DoesNotContain("Currently configured skills", prompt);
        Assert.DoesNotContain("Currently configured agents", prompt);
    }

    [Fact]
    public void BuildPrompt_SkillWithEmptyDescription_OmitsDescription()
    {
        var skills = new List<(string Name, string Description)>
        {
            ("deploy", "")
        };

        var prompt = InstructionRecommendationHelper.BuildRecommendationPrompt(
            "/project", existingSkills: skills);

        Assert.Contains("**deploy**", prompt);
        // Should not have ": " after the name when description is empty
        Assert.DoesNotContain("**deploy**: ", prompt);
    }

    [Fact]
    public void BuildPrompt_AllParameters_IncludesEverything()
    {
        var skills = new List<(string Name, string Description)> { ("build", "Build it") };
        var agents = new List<(string Name, string Description)> { ("review", "Review code") };

        var prompt = InstructionRecommendationHelper.BuildRecommendationPrompt(
            "/home/user/repo",
            existingSkills: skills,
            existingAgents: agents,
            repoName: "org/repo");

        Assert.Contains("org/repo", prompt);
        Assert.Contains("/home/user/repo", prompt);
        Assert.Contains("Currently configured skills", prompt);
        Assert.Contains("Currently configured agents", prompt);
        Assert.Contains("file path", prompt);
    }
}
