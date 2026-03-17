using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
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
        try
        {
            if (OperatingSystem.IsWindows())
                return TryFocusTerminalWindows(pid);
            if (OperatingSystem.IsMacOS() || OperatingSystem.IsMacCatalyst())
                return TryFocusTerminalMac(pid);
            return false;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Whether the Focus Terminal feature is supported on the current platform.</summary>
    public static bool IsSupported => OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() || OperatingSystem.IsMacCatalyst();

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
    /// Reads the console title of the shell process via AttachConsole/GetConsoleTitle —
    /// this is the same string that Windows Terminal displays as the tab title.
    /// Falls back to process-name matching and then index-based selection if needed.
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
            if (TryFocusTabViaUIA(windowHandle, terminalPid, tabShellPid, ancestryChain))
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
    /// select the one whose title matches the target shell's console title.
    ///
    /// Strategy 1 (most reliable): Read the shell's console title via AttachConsole +
    /// GetConsoleTitle. The console title is exactly what Windows Terminal shows as the
    /// tab name — it's set by whatever foreground process is running (e.g., Copilot CLI
    /// sets it to the session/task name).
    ///
    /// Strategy 2: Match by known shell process-name → tab-name patterns (pwsh → "PowerShell").
    ///
    /// Strategy 3: Match by sort-order — sort WT's shell children by creation time and
    /// use the index of tabShellPid to select the corresponding UIA tab.
    /// </summary>
    private static bool TryFocusTabViaUIA(IntPtr windowHandle, int terminalPid, int tabShellPid, List<int> ancestryChain)
    {
        try
        {
            var root = UIA.AutomationElement.FromHandle(windowHandle);
            if (root == null) return false;

            // Find all TabItem elements in the WT window
            var tabCondition = new UIA.PropertyCondition(
                UIA.AutomationElement.ControlTypeProperty, UIA.ControlType.TabItem);
            var tabItems = root.FindAll(UIA.TreeScope.Descendants, tabCondition);

            if (tabItems == null || tabItems.Count == 0) return false;

            UIA.AutomationElement? bestMatch = null;

            // ── Strategy 1: AttachConsole + GetConsoleTitle ──────────────────────────
            // The console title of the shell's console IS what WT displays as the tab title.
            // Copilot CLI sets the console title to the session/task name (e.g. "Fix the tests").
            // This is the most accurate matching strategy.
            var consoleTitle = ReadConsoleTitleForProcess(tabShellPid);
            if (!string.IsNullOrEmpty(consoleTitle))
            {
                foreach (UIA.AutomationElement tab in tabItems)
                {
                    try
                    {
                        var tabName = tab.Current.Name;
                        // Exact match first
                        if (string.Equals(tabName, consoleTitle, StringComparison.OrdinalIgnoreCase))
                        {
                            bestMatch = tab;
                            break;
                        }
                    }
                    catch { }
                }

                // Partial match fallback (handles cases where WT trims or adds prefixes)
                if (bestMatch == null)
                {
                    foreach (UIA.AutomationElement tab in tabItems)
                    {
                        try
                        {
                            var tabName = tab.Current.Name ?? "";
                            if (tabName.Contains(consoleTitle, StringComparison.OrdinalIgnoreCase) ||
                                consoleTitle.Contains(tabName, StringComparison.OrdinalIgnoreCase))
                            {
                                bestMatch = tab;
                                break;
                            }
                        }
                        catch { }
                    }
                }
            }

            // ── Strategy 2: Known process-name → tab-name patterns ───────────────────
            // For shells that don't set custom titles: pwsh → "PowerShell", etc.
            if (bestMatch == null)
            {
                string? processName = null;
                try
                {
                    using var shellProc = Process.GetProcessById(tabShellPid);
                    processName = shellProc.HasExited ? null : shellProc.ProcessName?.ToLowerInvariant();
                }
                catch { }

                if (!string.IsNullOrEmpty(processName))
                {
                    // Build expected tab names for this process
                    var expectedTabNames = processName switch
                    {
                        "pwsh" => new[] { "PowerShell" },
                        "powershell" => new[] { "Windows PowerShell", "PowerShell" },
                        "cmd" => new[] { "Command Prompt", "cmd" },
                        "bash" => new[] { "bash", "Git Bash" },
                        "wsl" or "wsl2" => new[] { "Ubuntu", "Debian", "Kali", "openSUSE", "Linux" },
                        _ => Array.Empty<string>()
                    };

                    foreach (var expected in expectedTabNames)
                    {
                        foreach (UIA.AutomationElement tab in tabItems)
                        {
                            try
                            {
                                var tabName = tab.Current.Name ?? "";
                                if (tabName.StartsWith(expected, StringComparison.OrdinalIgnoreCase))
                                {
                                    bestMatch = tab;
                                    break;
                                }
                            }
                            catch { }
                        }
                        if (bestMatch != null) break;
                    }
                }
            }

            // ── Strategy 3: Creation-time sort → tab index ───────────────────────────
            // Sort WT's direct shell children (non-OpenConsole) by creation time.
            // Tab index in UIA (left to right) = tab creation order (unless user reordered).
            if (bestMatch == null)
            {
                bestMatch = FindTabByCreationOrder(tabItems, terminalPid, tabShellPid);
            }

            if (bestMatch == null) return false;

            // Select the tab via SelectionItemPattern (canonical for tab controls)
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

    /// <summary>
    /// Serializes all FreeConsole/AttachConsole/GetConsoleTitle calls.
    /// These Win32 functions modify process-wide console state; concurrent calls
    /// (e.g., two Focus Terminal clicks in quick succession) would interfere.
    /// </summary>
    private static readonly object _consoleLock = new();

    /// <summary>
    /// Reads the console title of a process's console by temporarily attaching to it.
    /// PolyPilot is a GUI app with no console, so FreeConsole is a safe no-op.
    /// The console title is what Windows Terminal displays as the tab title.
    /// </summary>
    private static string? ReadConsoleTitleForProcess(int shellPid)
    {
        try
        {
            lock (_consoleLock)
            {
                // Detach from any current console (PolyPilot is GUI-only; this is a no-op but harmless)
                FreeConsole();

                if (!AttachConsole((uint)shellPid)) return null;

                try
                {
                    var sb = new StringBuilder(2048);
                    uint len = GetConsoleTitle(sb, (uint)sb.Capacity);
                    return len > 0 ? sb.ToString() : null;
                }
                finally
                {
                    FreeConsole();
                }
            }
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Strategy 3 fallback: find which UIA tab corresponds to our shell by sorting
    /// WT's direct shell children by creation time (= tab creation order).
    /// WT creates tabs in order so UIA tab index[N] = Nth created shell child.
    /// </summary>
    private static UIA.AutomationElement? FindTabByCreationOrder(
        UIA.AutomationElementCollection tabItems, int terminalPid, int tabShellPid)
    {
        try
        {
            // Get WT's direct children, filter to shell processes (skip OpenConsole/ConHost)
            var children = GetChildProcessIds(terminalPid);
            var shells = new List<(int Pid, DateTime StartTime)>();

            foreach (var childPid in children)
            {
                try
                {
                    using var proc = Process.GetProcessById(childPid);
                    if (proc.HasExited) continue;
                    var name = proc.ProcessName?.ToLowerInvariant() ?? "";
                    // Skip the PTY host processes — only count shell processes
                    if (name.Contains("openconsole") || name.Contains("conhost")) continue;
                    shells.Add((childPid, proc.StartTime));
                }
                catch { }
            }

            if (shells.Count == 0) return null;
            shells.Sort((a, b) => a.StartTime.CompareTo(b.StartTime));

            var tabIndex = shells.FindIndex(s => s.Pid == tabShellPid);
            if (tabIndex < 0 || tabIndex >= tabItems.Count) return null;

            return tabItems[tabIndex];
        }
        catch
        {
            return null;
        }
    }
#else
    private static bool TryFocusTabViaUIA(IntPtr windowHandle, int terminalPid, int tabShellPid, List<int> ancestryChain) => false;
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

    [DllImport("kernel32.dll")]
    private static extern bool FreeConsole();

    [DllImport("kernel32.dll")]
    private static extern bool AttachConsole(uint dwProcessId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern uint GetConsoleTitle([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder lpConsoleTitle, uint nSize);

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

    /// <summary>
    /// macOS: Walk up the process tree from the CLI PID to find the terminal emulator,
    /// then use osascript to bring it to the foreground and select the correct tab.
    /// </summary>
    private static bool TryFocusTerminalMac(int pid)
    {
        var currentPid = pid;
        string? terminalApp = null;

        // Get the tty of the target process for tab matching
        string? targetTty = null;
        try
        {
            var ttyPsi = new ProcessStartInfo("ps", $"-p {pid} -o tty=")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var ttyProc = Process.Start(ttyPsi);
            if (ttyProc != null)
            {
                targetTty = "/dev/" + ttyProc.StandardOutput.ReadToEnd().Trim();
                ttyProc.WaitForExit(2000);
            }
        }
        catch { }

        for (int depth = 0; depth < 15; depth++)
        {
            try
            {
                var commPsi = new ProcessStartInfo("ps", $"-p {currentPid} -o comm=")
                {
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using var commProc = Process.Start(commPsi);
                if (commProc == null) break;
                var comm = commProc.StandardOutput.ReadToEnd().Trim().ToLowerInvariant();
                commProc.WaitForExit(2000);

                if (comm.Contains("terminal") || comm.Contains("iterm") ||
                    comm.Contains("warp") || comm.Contains("alacritty") ||
                    comm.Contains("kitty") || comm.Contains("hyper") ||
                    comm.Contains("ghostty"))
                {
                    terminalApp = comm switch
                    {
                        var c when c.Contains("iterm") => "iTerm",
                        var c when c.Contains("warp") => "Warp",
                        var c when c.Contains("alacritty") => "Alacritty",
                        var c when c.Contains("kitty") => "kitty",
                        var c when c.Contains("ghostty") => "Ghostty",
                        _ => "Terminal"
                    };
                    break;
                }

                var ppidPsi = new ProcessStartInfo("ps", $"-p {currentPid} -o ppid=")
                {
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using var ppidProc = Process.Start(ppidPsi);
                if (ppidProc == null) break;
                var ppidStr = ppidProc.StandardOutput.ReadToEnd().Trim();
                ppidProc.WaitForExit(2000);

                if (!int.TryParse(ppidStr, out var ppid) || ppid <= 1)
                    break;

                currentPid = ppid;
            }
            catch { break; }
        }

        if (terminalApp == null) return false;

        try
        {
            // For Terminal.app: activate the window AND select the specific tab by tty
            string script;
            if (terminalApp == "Terminal" && !string.IsNullOrEmpty(targetTty))
            {
                script = $@"tell application ""Terminal""
    activate
    repeat with w in windows
        repeat with t in tabs of w
            if tty of t is ""{targetTty}"" then
                set selected of t to true
                set index of w to 1
                return ""focused""
            end if
        end repeat
    end repeat
end tell";
            }
            else
            {
                script = $"tell application \"{terminalApp}\" to activate";
            }

            // Write script to temp file — multi-line AppleScript doesn't work with -e
            var scriptFile = Path.Combine(Path.GetTempPath(), $"polypilot-focus-{pid}.scpt");
            try
            {
                File.WriteAllText(scriptFile, script);
                var psi = new ProcessStartInfo("osascript", scriptFile)
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using var proc = Process.Start(psi);
                proc?.WaitForExit(3000);
                return proc?.ExitCode == 0;
            }
            finally
            {
                try { File.Delete(scriptFile); } catch { }
            }
        }
        catch { return false; }
    }
}
