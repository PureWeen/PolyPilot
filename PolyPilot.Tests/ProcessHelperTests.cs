using System.Diagnostics;
using PolyPilot.Services;

namespace PolyPilot.Tests;

/// <summary>
/// Tests for ProcessHelper — safe wrappers for Process lifecycle operations
/// that prevent InvalidOperationException / UnobservedTaskException crashes
/// when a process is disposed while background tasks are still monitoring it.
/// </summary>
public class ProcessHelperTests
{
    // ===== SafeHasExited =====

    [Fact]
    public void SafeHasExited_NullProcess_ReturnsTrue()
    {
        Assert.True(ProcessHelper.SafeHasExited(null));
    }

    [Fact]
    public void SafeHasExited_DisposedProcess_ReturnsTrue()
    {
        // Start a short-lived process and dispose it immediately
        var psi = new ProcessStartInfo
        {
            FileName = OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/sh",
            Arguments = OperatingSystem.IsWindows() ? "/c echo test" : "-c \"echo test\"",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            CreateNoWindow = true
        };
        var process = Process.Start(psi)!;
        process.WaitForExit(5000);
        process.Dispose();

        // After disposal, HasExited would throw InvalidOperationException.
        // SafeHasExited must return true instead.
        Assert.True(ProcessHelper.SafeHasExited(process));
    }

    [Fact]
    public void SafeHasExited_ExitedProcess_ReturnsTrue()
    {
        var psi = new ProcessStartInfo
        {
            FileName = OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/sh",
            Arguments = OperatingSystem.IsWindows() ? "/c echo done" : "-c \"echo done\"",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            CreateNoWindow = true
        };
        var process = Process.Start(psi)!;
        process.WaitForExit(5000);

        Assert.True(ProcessHelper.SafeHasExited(process));
        process.Dispose();
    }

    [Fact]
    public void SafeHasExited_RunningProcess_ReturnsFalse()
    {
        // Start a long-running process
        var psi = new ProcessStartInfo
        {
            FileName = OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/sh",
            Arguments = OperatingSystem.IsWindows() ? "/c ping -n 30 127.0.0.1 > nul" : "-c \"sleep 30\"",
            UseShellExecute = false,
            CreateNoWindow = true
        };
        var process = Process.Start(psi)!;
        try
        {
            Assert.False(ProcessHelper.SafeHasExited(process));
        }
        finally
        {
            try { process.Kill(true); } catch { }
            process.Dispose();
        }
    }

    // ===== SafeKill =====

    [Fact]
    public void SafeKill_NullProcess_DoesNotThrow()
    {
        ProcessHelper.SafeKill(null);
    }

    [Fact]
    public void SafeKill_DisposedProcess_DoesNotThrow()
    {
        var psi = new ProcessStartInfo
        {
            FileName = OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/sh",
            Arguments = OperatingSystem.IsWindows() ? "/c echo test" : "-c \"echo test\"",
            UseShellExecute = false,
            CreateNoWindow = true
        };
        var process = Process.Start(psi)!;
        process.WaitForExit(5000);
        process.Dispose();

        // Must not throw
        ProcessHelper.SafeKill(process);
    }

    [Fact]
    public void SafeKill_RunningProcess_KillsIt()
    {
        var psi = new ProcessStartInfo
        {
            FileName = OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/sh",
            Arguments = OperatingSystem.IsWindows() ? "/c ping -n 30 127.0.0.1 > nul" : "-c \"sleep 30\"",
            UseShellExecute = false,
            CreateNoWindow = true
        };
        var process = Process.Start(psi)!;

        ProcessHelper.SafeKill(process);
        process.WaitForExit(5000);
        Assert.True(process.HasExited);
        process.Dispose();
    }

    // ===== SafeKillAndDispose =====

    [Fact]
    public void SafeKillAndDispose_NullProcess_DoesNotThrow()
    {
        ProcessHelper.SafeKillAndDispose(null);
    }

    [Fact]
    public void SafeKillAndDispose_AlreadyDisposed_DoesNotThrow()
    {
        var psi = new ProcessStartInfo
        {
            FileName = OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/sh",
            Arguments = OperatingSystem.IsWindows() ? "/c echo test" : "-c \"echo test\"",
            UseShellExecute = false,
            CreateNoWindow = true
        };
        var process = Process.Start(psi)!;
        process.WaitForExit(5000);
        process.Dispose();

        // Calling SafeKillAndDispose on already-disposed process must not throw
        ProcessHelper.SafeKillAndDispose(process);
    }

    [Fact]
    public void SafeKillAndDispose_RunningProcess_KillsAndDisposes()
    {
        var psi = new ProcessStartInfo
        {
            FileName = OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/sh",
            Arguments = OperatingSystem.IsWindows() ? "/c ping -n 30 127.0.0.1 > nul" : "-c \"sleep 30\"",
            UseShellExecute = false,
            CreateNoWindow = true
        };
        var process = Process.Start(psi)!;
        var pid = process.Id;

        ProcessHelper.SafeKillAndDispose(process);

        // Verify the process is no longer running
        try
        {
            var check = Process.GetProcessById(pid);
            // Process might still be there for a moment — give it time
            check.WaitForExit(2000);
        }
        catch (ArgumentException)
        {
            // Process already gone — expected
        }
    }

    // ===== Race condition regression test =====

    [Fact]
    public void SafeHasExited_ConcurrentDispose_NoUnobservedTaskException()
    {
        // Regression test: simulates the race condition where a background task
        // checks HasExited while another thread disposes the process.
        using var unobservedSignal = new ManualResetEventSlim(false);
        Exception? unobservedException = null;
        EventHandler<UnobservedTaskExceptionEventArgs> handler = (sender, args) =>
        {
            // Only capture exceptions from Process operations (HasExited/Kill).
            // Other tests may leak unobserved tasks (e.g., "DB connection failed"
            // from StubChatDatabase) — ignore those to avoid flaky cross-test pollution.
            if (args.Exception?.InnerException is InvalidOperationException ioe
                && (ioe.Message.Contains("process", StringComparison.OrdinalIgnoreCase)
                    || ioe.Message.Contains("exited", StringComparison.OrdinalIgnoreCase)
                    || ioe.Message.Contains("handle", StringComparison.OrdinalIgnoreCase)))
            {
                unobservedException = args.Exception;
                unobservedSignal.Set();
            }
        };

        TaskScheduler.UnobservedTaskException += handler;
        try
        {
            for (int i = 0; i < 5; i++)
            {
                var psi = new ProcessStartInfo
                {
                    FileName = OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/sh",
                    Arguments = OperatingSystem.IsWindows() ? "/c ping -n 10 127.0.0.1 > nul" : "-c \"sleep 10\"",
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                var process = Process.Start(psi)!;

                // Background task monitoring HasExited (like DevTunnel's fire-and-forget tasks)
                _ = Task.Run(() =>
                {
                    for (int j = 0; j < 50; j++)
                    {
                        if (ProcessHelper.SafeHasExited(process))
                            break;
                        Thread.Sleep(10);
                    }
                });

                // Simulate concurrent disposal (like Stop() being called)
                Thread.Sleep(50);
                ProcessHelper.SafeKillAndDispose(process);
            }

            // Force GC to surface any unobserved task exceptions
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            unobservedSignal.Wait(TimeSpan.FromMilliseconds(500));
            Assert.Null(unobservedException);
        }
        finally
        {
            TaskScheduler.UnobservedTaskException -= handler;
        }
    }
}
