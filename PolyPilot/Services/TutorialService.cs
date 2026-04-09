using PolyPilot.Models;

namespace PolyPilot.Services;

public class TutorialService
{
    public event Action? OnStateChanged;

    public bool IsActive { get; private set; }
    public int CurrentChapterIndex { get; private set; }
    public int CurrentStepIndex { get; private set; }
    public HashSet<string> CompletedChapters { get; private set; } = new();

    public TutorialChapter? CurrentChapter =>
        IsActive && CurrentChapterIndex >= 0 && CurrentChapterIndex < TutorialContent.Chapters.Count
            ? TutorialContent.Chapters[CurrentChapterIndex]
            : null;

    public TutorialStep? CurrentStep =>
        CurrentChapter != null && CurrentStepIndex >= 0 && CurrentStepIndex < CurrentChapter.Steps.Count
            ? CurrentChapter.Steps[CurrentStepIndex]
            : null;

    public int TotalStepsInChapter => CurrentChapter?.Steps.Count ?? 0;

    public int TotalChapters => TutorialContent.Chapters.Count;

    public int CompletedChapterCount => CompletedChapters.Count;

    /// <summary>Overall progress as a value from 0 to 100.</summary>
    public int OverallProgressPercent
    {
        get
        {
            var total = TutorialContent.Chapters.Count;
            if (total == 0) return 0;
            return (int)((double)CompletedChapters.Count / total * 100);
        }
    }

    /// <summary>Returns the global step index (0-based) across all chapters.</summary>
    public int GlobalStepIndex
    {
        get
        {
            int index = 0;
            for (int i = 0; i < CurrentChapterIndex && i < TutorialContent.Chapters.Count; i++)
                index += TutorialContent.Chapters[i].Steps.Count;
            index += CurrentStepIndex;
            return index;
        }
    }

    /// <summary>Total number of steps across all chapters.</summary>
    public int TotalSteps => TutorialContent.Chapters.Sum(c => c.Steps.Count);

    public void StartTutorial()
    {
        CurrentChapterIndex = 0;
        CurrentStepIndex = 0;
        IsActive = true;
        OnStateChanged?.Invoke();
    }

    public void StartChapter(string chapterId)
    {
        var idx = TutorialContent.Chapters.FindIndex(c => c.Id == chapterId);
        if (idx < 0) return;
        CurrentChapterIndex = idx;
        CurrentStepIndex = 0;
        IsActive = true;
        OnStateChanged?.Invoke();
    }

    public void NextStep()
    {
        if (!IsActive || CurrentChapter == null) return;

        if (CurrentStepIndex < CurrentChapter.Steps.Count - 1)
        {
            CurrentStepIndex++;
        }
        else
        {
            CompletedChapters.Add(CurrentChapter.Id);
            if (CurrentChapterIndex < TutorialContent.Chapters.Count - 1)
            {
                CurrentChapterIndex++;
                CurrentStepIndex = 0;
            }
            else
            {
                EndTutorial();
                return;
            }
        }
        OnStateChanged?.Invoke();
    }

    public void PreviousStep()
    {
        if (!IsActive) return;

        if (CurrentStepIndex > 0)
        {
            CurrentStepIndex--;
        }
        else if (CurrentChapterIndex > 0)
        {
            CurrentChapterIndex--;
            CurrentStepIndex = (CurrentChapter?.Steps.Count ?? 1) - 1;
        }
        OnStateChanged?.Invoke();
    }

    public void EndTutorial()
    {
        IsActive = false;
        OnStateChanged?.Invoke();
    }

    public void ResetProgress()
    {
        CompletedChapters.Clear();
        CurrentChapterIndex = 0;
        CurrentStepIndex = 0;
        OnStateChanged?.Invoke();
    }

    public void LoadCompleted(HashSet<string>? completed)
    {
        if (completed != null)
            CompletedChapters = new HashSet<string>(completed);
    }
}
