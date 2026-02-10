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
}
