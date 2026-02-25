using System.Diagnostics;

namespace PolyPilot.Models;

public static class LinkHelper
{
    /// <summary>
    /// Validates that a URL is a safe external URL (http/https only).
    /// </summary>
    public static bool IsValidExternalUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
        return uri.Scheme is "http" or "https";
    }

    /// <summary>
    /// Opens a URL in the default browser without bringing it to the foreground.
    /// On macOS, uses 'open -g'. On other platforms, this is a no-op
    /// (callers should fall back to Launcher.Default.OpenAsync).
    /// </summary>
    public static void OpenInBackground(string url)
    {
        if (!IsValidExternalUrl(url)) return;

#if MACCATALYST
        try
        {
            var psi = new ProcessStartInfo("open") { UseShellExecute = false };
            psi.ArgumentList.Add("-g");
            psi.ArgumentList.Add(url);
            Process.Start(psi)?.Dispose();
        }
        catch { }
#endif
    }
}
