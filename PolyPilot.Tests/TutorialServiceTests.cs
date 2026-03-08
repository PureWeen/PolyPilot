using PolyPilot.Models;
using PolyPilot.Services;

namespace PolyPilot.Tests;

public class TutorialServiceTests
{
    [Fact]
    public void StartTutorial_SetsActiveAndFirstStep()
    {
        var svc = new TutorialService();
        svc.StartTutorial();

        Assert.True(svc.IsActive);
        Assert.Equal(0, svc.CurrentChapterIndex);
        Assert.Equal(0, svc.CurrentStepIndex);
        Assert.NotNull(svc.CurrentChapter);
        Assert.NotNull(svc.CurrentStep);
    }

    [Fact]
    public void NextStep_AdvancesWithinChapter()
    {
        var svc = new TutorialService();
        svc.StartTutorial();

        var firstStep = svc.CurrentStep;
        svc.NextStep();

        Assert.Equal(0, svc.CurrentChapterIndex);
        Assert.Equal(1, svc.CurrentStepIndex);
        Assert.NotEqual(firstStep?.Id, svc.CurrentStep?.Id);
    }

    [Fact]
    public void NextStep_AdvancesToNextChapter_WhenAtEnd()
    {
        var svc = new TutorialService();
        svc.StartTutorial();

        var stepsInFirst = svc.CurrentChapter!.Steps.Count;
        for (int i = 0; i < stepsInFirst; i++)
            svc.NextStep();

        Assert.Equal(1, svc.CurrentChapterIndex);
        Assert.Equal(0, svc.CurrentStepIndex);
        Assert.Contains(TutorialContent.Chapters[0].Id, svc.CompletedChapters);
    }

    [Fact]
    public void NextStep_EndsTutorial_WhenAtLastStep()
    {
        var svc = new TutorialService();
        svc.StartTutorial();

        // Advance through all chapters and steps
        int totalSteps = TutorialContent.Chapters.Sum(c => c.Steps.Count);
        for (int i = 0; i < totalSteps; i++)
            svc.NextStep();

        Assert.False(svc.IsActive);
    }

    [Fact]
    public void PreviousStep_GoesBackWithinChapter()
    {
        var svc = new TutorialService();
        svc.StartTutorial();
        svc.NextStep();

        svc.PreviousStep();

        Assert.Equal(0, svc.CurrentStepIndex);
    }

    [Fact]
    public void PreviousStep_GoesBackToPreviousChapter()
    {
        var svc = new TutorialService();
        svc.StartTutorial();

        var stepsInFirst = svc.CurrentChapter!.Steps.Count;
        for (int i = 0; i < stepsInFirst; i++)
            svc.NextStep();

        Assert.Equal(1, svc.CurrentChapterIndex);
        svc.PreviousStep();

        Assert.Equal(0, svc.CurrentChapterIndex);
        Assert.Equal(stepsInFirst - 1, svc.CurrentStepIndex);
    }

    [Fact]
    public void StartChapter_JumpsToSpecificChapter()
    {
        var svc = new TutorialService();
        var targetId = TutorialContent.Chapters[2].Id;

        svc.StartChapter(targetId);

        Assert.True(svc.IsActive);
        Assert.Equal(2, svc.CurrentChapterIndex);
        Assert.Equal(0, svc.CurrentStepIndex);
    }

    [Fact]
    public void StartChapter_InvalidId_DoesNothing()
    {
        var svc = new TutorialService();
        svc.StartChapter("nonexistent");

        Assert.False(svc.IsActive);
    }

    [Fact]
    public void EndTutorial_DeactivatesTutorial()
    {
        var svc = new TutorialService();
        svc.StartTutorial();
        svc.EndTutorial();

        Assert.False(svc.IsActive);
    }

    [Fact]
    public void ResetProgress_ClearsCompletedChapters()
    {
        var svc = new TutorialService();
        svc.StartTutorial();

        var stepsInFirst = svc.CurrentChapter!.Steps.Count;
        for (int i = 0; i < stepsInFirst; i++)
            svc.NextStep();

        Assert.NotEmpty(svc.CompletedChapters);

        svc.ResetProgress();
        Assert.Empty(svc.CompletedChapters);
    }

    [Fact]
    public void LoadCompleted_RestoresState()
    {
        var svc = new TutorialService();
        var completed = new HashSet<string> { "getting-started", "sessions" };

        svc.LoadCompleted(completed);

        Assert.Equal(2, svc.CompletedChapters.Count);
        Assert.Contains("getting-started", svc.CompletedChapters);
        Assert.Contains("sessions", svc.CompletedChapters);
    }

    [Fact]
    public void OnStateChanged_FiresOnStartAndNext()
    {
        var svc = new TutorialService();
        var count = 0;
        svc.OnStateChanged += () => count++;

        svc.StartTutorial();
        svc.NextStep();
        svc.EndTutorial();

        Assert.Equal(3, count);
    }
}

public class TutorialContentTests
{
    [Fact]
    public void AllChapters_HaveUniqueIds()
    {
        var ids = TutorialContent.Chapters.Select(c => c.Id).ToList();
        Assert.Equal(ids.Count, ids.Distinct().Count());
    }

    [Fact]
    public void AllChapters_HaveAtLeastOneStep()
    {
        foreach (var chapter in TutorialContent.Chapters)
        {
            Assert.NotEmpty(chapter.Steps);
        }
    }

    [Fact]
    public void AllSteps_HaveUniqueIdsWithinChapter()
    {
        foreach (var chapter in TutorialContent.Chapters)
        {
            var ids = chapter.Steps.Select(s => s.Id).ToList();
            Assert.Equal(ids.Count, ids.Distinct().Count());
        }
    }

    [Fact]
    public void AllSteps_HaveTitleAndDescription()
    {
        foreach (var chapter in TutorialContent.Chapters)
        {
            foreach (var step in chapter.Steps)
            {
                Assert.False(string.IsNullOrWhiteSpace(step.Title), $"Step {step.Id} in {chapter.Id} missing title");
                Assert.False(string.IsNullOrWhiteSpace(step.Description), $"Step {step.Id} in {chapter.Id} missing description");
            }
        }
    }

    [Fact]
    public void NavigateSteps_HaveNavigateTo()
    {
        foreach (var chapter in TutorialContent.Chapters)
        {
            foreach (var step in chapter.Steps.Where(s => s.Action == StepAction.Navigate))
            {
                Assert.False(string.IsNullOrWhiteSpace(step.NavigateTo),
                    $"Step {step.Id} in {chapter.Id} has Navigate action but no NavigateTo");
            }
        }
    }

    [Fact]
    public void Content_HasExpectedChapterCount()
    {
        Assert.Equal(8, TutorialContent.Chapters.Count);
    }
}
