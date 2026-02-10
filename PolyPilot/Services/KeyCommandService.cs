namespace PolyPilot.Services;

/// <summary>
/// Bridges native Mac Catalyst key commands to Blazor components.
/// </summary>
public class KeyCommandService
{
    public event Action<bool>? OnCycleSession; // bool = reverse (shift+tab)

    public void CycleSession(bool reverse) => OnCycleSession?.Invoke(reverse);
}
