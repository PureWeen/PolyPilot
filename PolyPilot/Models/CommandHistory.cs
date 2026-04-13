namespace PolyPilot.Models;

/// <summary>
/// Manages per-session command history with up/down navigation.
/// Preserves the user's in-progress draft when entering history mode
/// and restores it when navigating back down past the newest entry.
/// </summary>
public class CommandHistory
{
    private readonly List<string> _entries = new();
    private int _index;
    private string? _draft;
    private const int MaxEntries = 50;

    public int Count => _entries.Count;
    public int Index => _index;
    /// <summary>True when the user has navigated up and has not yet returned to the "live" position.</summary>
    public bool IsNavigating => _index < _entries.Count;

    public void Add(string command)
    {
        if (string.IsNullOrEmpty(command)) return;
        if (_entries.Count == 0 || _entries[^1] != command)
        {
            _entries.Add(command);
            if (_entries.Count > MaxEntries) _entries.RemoveAt(0);
        }
        _index = _entries.Count; // past the end = "no selection"
        _draft = null; // draft consumed — message was sent
    }

    /// <summary>
    /// Navigate history. Returns (text, cursorAtStart).
    /// cursorAtStart is true when navigating up (so next ArrowUp fires immediately),
    /// false when navigating down (so next ArrowDown fires immediately).
    /// Returns null if history is empty or already at live position going down.
    /// </summary>
    /// <param name="up">True for ArrowUp (older), false for ArrowDown (newer).</param>
    /// <param name="currentText">The current input text — saved as draft on first up-navigation.</param>
    public (string Text, bool CursorAtStart)? Navigate(bool up, string? currentText = null)
    {
        if (_entries.Count == 0) return null;

        // Already at live position going down — nothing to navigate
        if (!up && !IsNavigating) return null;

        // Stash the draft when first entering history mode
        if (up && !IsNavigating)
            _draft = currentText ?? "";

        if (up)
            _index = Math.Max(0, _index - 1);
        else
            _index = Math.Min(_entries.Count, _index + 1);

        var text = _index < _entries.Count ? _entries[_index] : (_draft ?? "");
        // Clear draft after restoring it — prevents stale re-use on spurious ArrowDown
        if (_index == _entries.Count)
            _draft = null;
        return (text, up);
    }
}
