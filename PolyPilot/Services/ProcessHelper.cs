using System.Diagnostics;

namespace PolyPilot.Services;

/// <summary>
/// Safe wrappers for <see cref="Process"/> operations that can throw
/// <see cref="InvalidOperationException"/> when the process handle is
/// disposed or was never associated.
/// </summary>
public static class ProcessHelper
{
    /// <summary>
    /// Returns <c>true</c> if the process has exited or the handle is invalid/disposed.
    /// Unlike <see cref="Process.HasExited"/>, this never throws.
    /// A disposed or invalid process is treated as exited.
    /// </summary>
    public static bool SafeHasExited(Process? process)
    {
        if (process == null)
            return true;
        try
        {
            return process.HasExited;
        }
        catch (InvalidOperationException)
        {
            // "No process is associated with this object" — handle was disposed
            return true;
        }
        catch (SystemException)
        {
            // Win32Exception, NotSupportedException, etc.
            return true;
        }
    }

    /// <summary>
    /// Attempts to kill the process tree. Swallows all exceptions — safe to call
    /// on disposed or already-exited processes.
    /// </summary>
    public static void SafeKill(Process? process, bool entireProcessTree = true)
    {
        if (process == null)
            return;
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree);
        }
        catch
        {
            // Process already exited, disposed, or access denied — nothing to do
        }
    }

    /// <summary>
    /// Kills (if alive) and disposes the process. Safe to call multiple times.
    /// </summary>
    public static void SafeKillAndDispose(Process? process, bool entireProcessTree = true)
    {
        if (process == null)
            return;
        SafeKill(process, entireProcessTree);
        try
        {
            process.Dispose();
        }
        catch
        {
            // Already disposed — ignore
        }
    }
}
