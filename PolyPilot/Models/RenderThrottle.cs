namespace PolyPilot.Models;

/// <summary>
/// Determines whether a state-change refresh should be throttled.
/// Extracted from Dashboard.RefreshState for testability.
/// </summary>
public class RenderThrottle
{
    private readonly int _throttleMs;
    private DateTime _lastRefresh = DateTime.MinValue;

    public RenderThrottle(int throttleMs = 500)
    {
        _throttleMs = throttleMs;
    }

    /// <summary>
    /// Returns true if the refresh should proceed, false if throttled.
    /// A refresh always proceeds when hasCompletedSessions is true (turn just finished)
    /// or when isSessionSwitch is true.
    /// </summary>
    public bool ShouldRefresh(bool isSessionSwitch, bool hasCompletedSessions)
    {
        if (isSessionSwitch)
            return true;

        var now = DateTime.UtcNow;

        // Always allow when a session just completed — ensures final message renders
        if (hasCompletedSessions)
        {
            _lastRefresh = now;
            return true;
        }

        if ((now - _lastRefresh).TotalMilliseconds < _throttleMs)
            return false;

        _lastRefresh = now;
        return true;
    }

    // For testing: allow injecting time
    internal DateTime LastRefresh => _lastRefresh;
    internal void SetLastRefresh(DateTime time) => _lastRefresh = time;
}
