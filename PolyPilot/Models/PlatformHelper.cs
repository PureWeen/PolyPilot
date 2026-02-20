namespace PolyPilot.Models;

public static class PlatformHelper
{
    public static bool IsDesktop =>
#if MACCATALYST || WINDOWS
        true;
#else
        false;
#endif

    public static bool IsMobile =>
#if IOS || ANDROID
        true;
#else
        false;
#endif

    public static ConnectionMode[] AvailableModes => IsDesktop
        ? [ConnectionMode.Embedded, ConnectionMode.Persistent, ConnectionMode.Remote]
        : [ConnectionMode.Remote];

    public static ConnectionMode DefaultMode => IsDesktop
        ? ConnectionMode.Persistent
        : ConnectionMode.Remote;

    /// <summary>
    /// Shell-escapes a string for safe embedding in bash scripts using single quotes.
    /// Single quotes prevent all shell expansion (variables, command substitution, etc.).
    /// The only character that needs escaping inside single quotes is ' itself.
    /// </summary>
    public static string ShellEscape(string value) => "'" + value.Replace("'", "'\"'\"'") + "'";
}
