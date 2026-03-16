using System.Diagnostics;
using System.Runtime.InteropServices;
#if WINDOWS
using UIA = System.Windows.Automation;
#endif

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
    /// For Windows Terminal, also focuses the specific tab via UI Automation.
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

        if (isWindowsTerminal)
            TryFocusWindowsTerminalTab(windowHandle, terminalPid, ancestryChain);

        return true;
    }

    /// <summary>
    /// Uses UI Automation to find and select the correct tab in Windows Terminal.
    /// Walks the UIA tree to find TabItem elements, matches by tab title containing
    /// the target process's working directory or name, then calls SelectionItemPattern.Select().
    /// Falls back to the process start-time heuristic if UIA matching fails.
    /// </summary>
    private static void TryFocusWindowsTerminalTab(IntPtr windowHandle, int terminalPid, List<int> ancestryChain)
    {
        try
        {
            // Find the direct child of WT in our ancestry chain (the shell process for the target tab)
            var terminalIndex = ancestryChain.IndexOf(terminalPid);
            if (terminalIndex <= 0) return;

            var tabShellPid = ancestryChain[terminalIndex - 1];

            // Try UI Automation first — most reliable for tab selection
            if (TryFocusTabViaUIA(windowHandle, tabShellPid))
                return;

            // Fallback: start-time heuristic with Ctrl+Alt+N
            TryFocusTabViaKeystroke(terminalPid, tabShellPid);
        }
        catch
        {
            // Tab focusing is best-effort; the window is already in the foreground.
        }
    }

#if WINDOWS
    /// <summary>
    /// Uses UI Automation to enumerate TabItem elements in the WT window and
    /// select the one whose title matches the target process's context.
    /// </summary>
    private static bool TryFocusTabViaUIA(IntPtr windowHandle, int tabShellPid)
    {
        try
        {
            // Get the tab title we're looking for from the shell process
            string? targetTitle = null;
            string? targetProcessName = null;
            try
            {
                using var shellProc = Process.GetProcessById(tabShellPid);
                if (!shellProc.HasExited)
                {
                    targetTitle = shellProc.MainWindowTitle;
                    targetProcessName = shellProc.ProcessName?.ToLowerInvariant();
                }
            }
            catch { }

            var root = UIA.AutomationElement.FromHandle(windowHandle);
            if (root == null) return false;

            // Find all TabItem elements in the WT window
            var tabCondition = new UIA.PropertyCondition(
                UIA.AutomationElement.ControlTypeProperty, UIA.ControlType.TabItem);
            var tabItems = root.FindAll(UIA.TreeScope.Descendants, tabCondition);

            if (tabItems == null || tabItems.Count <= 1) return false;

            UIA.AutomationElement? bestMatch = null;

            // Strategy 1: Match by tab title containing the shell process's window title
            if (!string.IsNullOrEmpty(targetTitle))
            {
                foreach (UIA.AutomationElement tab in tabItems)
                {
                    try
                    {
                        var tabName = tab.Current.Name;
                        if (!string.IsNullOrEmpty(tabName) &&
                            tabName.Contains(targetTitle, StringComparison.OrdinalIgnoreCase))
                        {
                            bestMatch = tab;
                            break;
                        }
                    }
                    catch { }
                }
            }

            // Strategy 2: Match by tab title containing the process name
            if (bestMatch == null && !string.IsNullOrEmpty(targetProcessName))
            {
                foreach (UIA.AutomationElement tab in tabItems)
                {
                    try
                    {
                        var tabName = tab.Current.Name?.ToLowerInvariant() ?? "";
                        if (tabName.Contains(targetProcessName))
                        {
                            bestMatch = tab;
                            break;
                        }
                    }
                    catch { }
                }
            }

            if (bestMatch == null) return false;

            // Try SelectionItemPattern (canonical for tab controls)
            if (bestMatch.TryGetCurrentPattern(UIA.SelectionItemPattern.Pattern, out object? selPattern) &&
                selPattern is UIA.SelectionItemPattern sel)
            {
                sel.Select();
                return true;
            }

            // Fallback: InvokePattern (like clicking the tab)
            if (bestMatch.TryGetCurrentPattern(UIA.InvokePattern.Pattern, out object? invPattern) &&
                invPattern is UIA.InvokePattern inv)
            {
                inv.Invoke();
                return true;
            }

            // Last resort: just set focus on the tab element
            bestMatch.SetFocus();
            return true;
        }
        catch
        {
            return false;
        }
    }
#else
    private static bool TryFocusTabViaUIA(IntPtr windowHandle, int tabShellPid) => false;
#endif

    /// <summary>
    /// Fallback: sorts WT child processes by start time and sends Ctrl+Alt+N keystrokes.
    /// Less reliable than UIA — tab order may not match process creation order if user reordered tabs.
    /// </summary>
    private static void TryFocusTabViaKeystroke(int terminalPid, int tabShellPid)
    {
        var children = GetChildProcessIds(terminalPid);
        if (children.Count <= 1) return;

        var sorted = new List<(int Pid, DateTime StartTime)>();
        foreach (var childPid in children)
        {
            try
            {
                using var proc = Process.GetProcessById(childPid);
                if (proc.HasExited) continue;
                var name = proc.ProcessName?.ToLowerInvariant() ?? "";
                if (name.Contains("openconsole") || name.Contains("conhost"))
                    continue;
                sorted.Add((childPid, proc.StartTime));
            }
            catch { }
        }

        if (sorted.Count <= 1) return;
        sorted.Sort((a, b) => a.StartTime.CompareTo(b.StartTime));

        var tabIndex = sorted.FindIndex(c => c.Pid == tabShellPid);
        if (tabIndex < 0 || tabIndex > 8) return;

        Thread.Sleep(100);
        SendCtrlAltNumber(tabIndex + 1);
    }

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

    private const uint INPUT_KEYBOARD = 1;
    private const ushort VK_CONTROL = 0x11;
    private const ushort VK_MENU = 0x12;
    private const uint KEYEVENTF_KEYUP = 0x0002;
    private static readonly ushort[] VK_NUMBERS = { 0x31, 0x32, 0x33, 0x34, 0x35, 0x36, 0x37, 0x38, 0x39 };

    private static void SendCtrlAltNumber(int number)
    {
        if (number < 1 || number > 9) return;
        var vkNumber = VK_NUMBERS[number - 1];

        var inputs = new INPUT[]
        {
            new() { type = INPUT_KEYBOARD, ki = new KEYBDINPUT { wVk = VK_CONTROL } },
            new() { type = INPUT_KEYBOARD, ki = new KEYBDINPUT { wVk = VK_MENU } },
            new() { type = INPUT_KEYBOARD, ki = new KEYBDINPUT { wVk = vkNumber } },
            new() { type = INPUT_KEYBOARD, ki = new KEYBDINPUT { wVk = vkNumber, dwFlags = KEYEVENTF_KEYUP } },
            new() { type = INPUT_KEYBOARD, ki = new KEYBDINPUT { wVk = VK_MENU, dwFlags = KEYEVENTF_KEYUP } },
            new() { type = INPUT_KEYBOARD, ki = new KEYBDINPUT { wVk = VK_CONTROL, dwFlags = KEYEVENTF_KEYUP } },
        };

        SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
    }

    // ── Win32 P/Invoke ─────────────────────────────────────────────────────────

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
