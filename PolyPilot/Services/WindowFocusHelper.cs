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
    /// For Windows Terminal, also focuses the specific tab containing the process.
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
        // Collect the ancestry chain so we can identify the tab for Windows Terminal.
        var ancestryChain = new List<int>();
        var currentPid = pid;
        IntPtr windowHandle = IntPtr.Zero;
        int terminalPid = -1;
        bool isWindowsTerminal = false;

        for (int depth = 0; depth < 10; depth++)
        {
            ancestryChain.Add(currentPid);
            try
            {
                using var proc = Process.GetProcessById(currentPid);
                if (proc.HasExited) break;

                if (proc.MainWindowHandle != IntPtr.Zero)
                {
                    windowHandle = proc.MainWindowHandle;
                    terminalPid = currentPid;
                    var name = proc.ProcessName?.ToLowerInvariant() ?? "";
                    isWindowsTerminal = name.Contains("windowsterminal");
                    break;
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

        if (windowHandle == IntPtr.Zero) return false;

        ShowWindow(windowHandle, SW_RESTORE);
        SetForegroundWindow(windowHandle);

        // For Windows Terminal, also try to focus the correct tab
        if (isWindowsTerminal)
            TryFocusWindowsTerminalTab(terminalPid, ancestryChain);

        return true;
    }

    /// <summary>
    /// Attempts to focus the Windows Terminal tab containing the target process.
    /// Enumerates WT's direct shell children (filtering out OpenConsole.exe helper
    /// processes), sorts by start time to approximate tab order, then sends
    /// Ctrl+Alt+&lt;N&gt; to the focused window to switch tabs.
    /// </summary>
    private static void TryFocusWindowsTerminalTab(int terminalPid, List<int> ancestryChain)
    {
        try
        {
            // The ancestry chain runs from target PID up to terminal PID.
            // The entry just before the terminal is the direct child of WT in our chain
            // (typically a shell process like pwsh.exe, cmd.exe, or bash.exe).
            var terminalIndex = ancestryChain.IndexOf(terminalPid);
            if (terminalIndex <= 0) return;

            var tabRootPid = ancestryChain[terminalIndex - 1];

            // Enumerate all direct children of the Windows Terminal process.
            // WT spawns two processes per tab: OpenConsole.exe (PTY host) and
            // the shell (pwsh, cmd, bash). Only shells map 1:1 with tabs.
            var children = GetChildProcessIds(terminalPid);
            if (children.Count <= 1) return;

            // Sort by process start time to approximate visual tab order.
            // NOTE: This heuristic assumes tabs are in creation order. If the user has
            // manually reordered tabs by dragging, the computed index will be wrong.
            var sorted = new List<(int Pid, DateTime StartTime)>();
            foreach (var childPid in children)
            {
                try
                {
                    using var proc = Process.GetProcessById(childPid);
                    if (proc.HasExited) continue;
                    // Filter out OpenConsole.exe — it's a PTY host, not a tab shell.
                    var name = proc.ProcessName?.ToLowerInvariant() ?? "";
                    if (name.Contains("openconsole") || name.Contains("conhost"))
                        continue;
                    sorted.Add((childPid, proc.StartTime));
                }
                catch
                {
                    // Process exited between enumeration and query
                }
            }

            if (sorted.Count <= 1) return;
            sorted.Sort((a, b) => a.StartTime.CompareTo(b.StartTime));

            var tabIndex = sorted.FindIndex(c => c.Pid == tabRootPid);
            if (tabIndex < 0 || tabIndex > 8) return; // WT supports Ctrl+Alt+1..9

            // Send Ctrl+Alt+<N> to the focused WT window to switch tabs.
            // Tab numbers are 1-based in WT shortcuts (Ctrl+Alt+1 = first tab).
            // This is more reliable than `wt -w 0 focus-tab` which may target
            // a different WT window than the one we just brought to foreground.
            Thread.Sleep(100); // Brief pause for SetForegroundWindow to settle
            SendCtrlAltNumber(tabIndex + 1);
        }
        catch
        {
            // Tab focusing is best-effort; the window is already in the foreground.
        }
    }

    /// <summary>
    /// Returns the PIDs of all processes whose parent is <paramref name="parentPid"/>.
    /// </summary>
    private static List<int> GetChildProcessIds(int parentPid)
    {
        var children = new List<int>();
        var snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
        if (snapshot == IntPtr.Zero || snapshot == INVALID_HANDLE_VALUE) return children;

        try
        {
            var entry = new PROCESSENTRY32 { dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32>() };
            if (!Process32First(snapshot, ref entry)) return children;

            do
            {
                if (entry.th32ParentProcessID == (uint)parentPid)
                    children.Add((int)entry.th32ProcessID);
            } while (Process32Next(snapshot, ref entry));
        }
        finally
        {
            CloseHandle(snapshot);
        }

        return children;
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

    // SendInput constants
    private const uint INPUT_KEYBOARD = 1;
    private const ushort VK_CONTROL = 0x11;
    private const ushort VK_MENU = 0x12; // Alt key
    private const uint KEYEVENTF_KEYUP = 0x0002;

    // Virtual key codes for number keys 1-9
    private static readonly ushort[] VK_NUMBERS = { 0x31, 0x32, 0x33, 0x34, 0x35, 0x36, 0x37, 0x38, 0x39 };

    /// <summary>
    /// Sends Ctrl+Alt+&lt;N&gt; keystrokes to the foreground window.
    /// Windows Terminal uses this shortcut to switch to tab N (1-based).
    /// </summary>
    private static void SendCtrlAltNumber(int number)
    {
        if (number < 1 || number > 9) return;
        var vkNumber = VK_NUMBERS[number - 1];

        var inputs = new INPUT[]
        {
            // Key down: Ctrl, Alt, Number
            new() { type = INPUT_KEYBOARD, ki = new KEYBDINPUT { wVk = VK_CONTROL } },
            new() { type = INPUT_KEYBOARD, ki = new KEYBDINPUT { wVk = VK_MENU } },
            new() { type = INPUT_KEYBOARD, ki = new KEYBDINPUT { wVk = vkNumber } },
            // Key up: Number, Alt, Ctrl (reverse order)
            new() { type = INPUT_KEYBOARD, ki = new KEYBDINPUT { wVk = vkNumber, dwFlags = KEYEVENTF_KEYUP } },
            new() { type = INPUT_KEYBOARD, ki = new KEYBDINPUT { wVk = VK_MENU, dwFlags = KEYEVENTF_KEYUP } },
            new() { type = INPUT_KEYBOARD, ki = new KEYBDINPUT { wVk = VK_CONTROL, dwFlags = KEYEVENTF_KEYUP } },
        };

        SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
    }

    // ── Win32 P/Invoke (resolved lazily — safe to declare on all platforms) ────

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint dwFlags, uint th32ProcessID);

    [DllImport("kernel32.dll", CharSet = CharSet.Auto)]
    private static extern bool Process32First(IntPtr hSnapshot, ref PROCESSENTRY32 lppe);

    [DllImport("kernel32.dll", CharSet = CharSet.Auto)]
    private static extern bool Process32Next(IntPtr hSnapshot, ref PROCESSENTRY32 lppe);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr hObject);

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
        // Padding to match MOUSEINPUT union size
        private readonly int _padding1;
        private readonly int _padding2;
    }

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
