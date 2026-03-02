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
#if DEBUG
        : [ConnectionMode.Remote, ConnectionMode.Demo];
#else
        : [ConnectionMode.Remote];
#endif

    public static ConnectionMode DefaultMode => IsDesktop
        ? ConnectionMode.Persistent
        : ConnectionMode.Remote;

    /// <summary>
    /// Shell-escapes a string for safe embedding in bash scripts using single quotes.
    /// Single quotes prevent all shell expansion (variables, command substitution, etc.).
    /// The only character that needs escaping inside single quotes is ' itself.
    /// </summary>
    public static string ShellEscape(string value) => "'" + value.Replace("'", "'\"'\"'") + "'";

    /// <summary>
    /// Returns the platform-appropriate shell executable and arguments for running a command.
    /// On Windows uses cmd.exe /c; on Mac/Linux uses /bin/bash -c.
    /// </summary>
    public static (string FileName, string Arguments) GetShellCommand(string command)
    {
        if (OperatingSystem.IsWindows())
            // Outer quotes ensure cmd.exe's quote-stripping is deterministic
            return ("cmd.exe", $"/c \"{command}\"");

        var escaped = command.Replace("\\", "\\\\").Replace("\"", "\\\"");
        return ("/bin/bash", $"-c \"{escaped}\"");
    }

    /// <summary>
    /// Builds a <c>vscode-remote://</c> folder URI for opening a remote folder in VS Code
    /// via the Remote - Tunnels extension. Returns null when not in remote mode or machine name unknown.
    /// </summary>
    public static string? BuildVSCodeRemoteFolderUri(bool isRemoteMode, string? serverMachineName, string? folderPath)
    {
        if (!isRemoteMode || string.IsNullOrEmpty(serverMachineName) || string.IsNullOrEmpty(folderPath))
            return null;

        // Normalize to forward slashes for URI path
        var uriPath = folderPath.Replace('\\', '/');
        // Windows paths like C:/Users/... need a leading slash → /C:/Users/...
        if (uriPath.Length >= 2 && uriPath[1] == ':')
            uriPath = "/" + uriPath;

        return $"vscode-remote://tunnel+{serverMachineName}{uriPath}";
    }
}
