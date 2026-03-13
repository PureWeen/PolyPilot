using System.Diagnostics;
using System.Runtime.InteropServices;

namespace PolyPilot.Services;

/// <summary>
/// Provides platform-specific functionality to bring a terminal window into focus
/// given a child process PID (e.g., the Copilot CLI process).
/// </summary>
internal static class WindowFocusHelper
{
    /// <summary>
    /// Attempts to bring the terminal window hosting the given process into focus.
    /// Walks up the process tree from <paramref name="pid"/> to find the nearest
    /// ancestor with a visible window (the terminal emulator), then activates it.
    /// </summary>
    /// <returns>True if a window was found and focused, false otherwise.</returns>
    public static bool TryFocusTerminalForProcess(int pid)
    {
        if (!OperatingSystem.IsWindows()) return false;

        try
        {
            return TryFocusTerminalWindows(pid);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Whether the Focus Terminal feature is supported on the current platform.</summary>
    public static bool IsSupported => OperatingSystem.IsWindows();

    private static bool TryFocusTerminalWindows(int pid)
    {
        // Walk up the process tree from the CLI PID looking for a process
        // that has a visible window (the terminal: wt.exe, conhost, cmd, powershell, etc.)
        var currentPid = pid;
        for (int depth = 0; depth < 10; depth++)
        {
            try
            {
                using var proc = Process.GetProcessById(currentPid);
                if (proc.HasExited) break;

                if (proc.MainWindowHandle != IntPtr.Zero)
                {
                    ShowWindow(proc.MainWindowHandle, SW_RESTORE);
                    SetForegroundWindow(proc.MainWindowHandle);
                    return true;
                }

                // Move to parent process
                var parentPid = GetParentProcessId(currentPid);
                if (parentPid <= 0 || parentPid == currentPid) break;
                currentPid = parentPid;
            }
            catch
            {
                break;
            }
        }

        return false;
    }

    private static int GetParentProcessId(int pid)
    {
        var snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
        if (snapshot == IntPtr.Zero || snapshot == INVALID_HANDLE_VALUE) return -1;

        try
        {
            var entry = new PROCESSENTRY32 { dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32>() };
            if (!Process32First(snapshot, ref entry)) return -1;

            do
            {
                if (entry.th32ProcessID == (uint)pid)
                    return (int)entry.th32ParentProcessID;
            } while (Process32Next(snapshot, ref entry));

            return -1;
        }
        finally
        {
            CloseHandle(snapshot);
        }
    }

    // ── Win32 Constants ────────────────────────────────────────────────────────

    private const int SW_RESTORE = 9;
    private const uint TH32CS_SNAPPROCESS = 0x00000002;
    private static readonly IntPtr INVALID_HANDLE_VALUE = new(-1);

    // ── Win32 P/Invoke (resolved lazily — safe to declare on all platforms) ────

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint dwFlags, uint th32ProcessID);

    [DllImport("kernel32.dll", CharSet = CharSet.Auto)]
    private static extern bool Process32First(IntPtr hSnapshot, ref PROCESSENTRY32 lppe);

    [DllImport("kernel32.dll", CharSet = CharSet.Auto)]
    private static extern bool Process32Next(IntPtr hSnapshot, ref PROCESSENTRY32 lppe);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr hObject);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct PROCESSENTRY32
    {
        public uint dwSize;
        public uint cntUsage;
        public uint th32ProcessID;
        public IntPtr th32DefaultHeapID;
        public uint th32ModuleID;
        public uint cntThreads;
        public uint th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szExeFile;
    }
}
