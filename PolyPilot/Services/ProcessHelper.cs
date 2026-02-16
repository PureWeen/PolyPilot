using System.Diagnostics;

namespace PolyPilot.Services;

/// <summary>
/// Safe wrappers for Process operations that can throw InvalidOperationException
/// when the process object is not associated with a running process.
/// </summary>
public static class ProcessHelper
{
    /// <summary>
    /// Safely checks Process.HasExited without throwing InvalidOperationException.
    /// Returns true (treat as exited) if the process is null, disposed, or not associated.
    /// </summary>
    public static bool HasExitedSafe(Process? process)
    {
        if (process == null)
            return true;
        try
        {
            return process.HasExited;
        }
        catch (InvalidOperationException)
        {
            // "No process is associated with this object"
            return true;
        }
        catch (SystemException)
        {
            // ObjectDisposedException or other system-level issues
            return true;
        }
    }
}
