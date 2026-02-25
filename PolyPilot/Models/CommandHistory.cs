namespace PolyPilot.Models;

/// <summary>
/// Manages per-session command history with up/down navigation.
/// </summary>
public class CommandHistory
{
    private readonly List<string> _entries = new();
    private int _index;
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
    }

    /// <summary>
    /// Navigate history. Returns (text, cursorAtStart).
    /// cursorAtStart is true when navigating up (so next ArrowUp fires immediately),
    /// false when navigating down (so next ArrowDown fires immediately).
    /// Returns null if history is empty.
    /// </summary>
    public (string Text, bool CursorAtStart)? Navigate(bool up)
    {
        if (_entries.Count == 0) return null;

        if (up)
            _index = Math.Max(0, _index - 1);
        else
            _index = Math.Min(_entries.Count, _index + 1);

        var text = _index < _entries.Count ? _entries[_index] : "";
        return (text, up);
    }
}
