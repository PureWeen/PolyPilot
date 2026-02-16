using System.Diagnostics;
using PolyPilot.Services;

namespace PolyPilot.Tests;

/// <summary>
/// Tests for ProcessHelper.HasExitedSafe and ServerManager defensive patterns.
/// Validates that process lifecycle operations don't throw InvalidOperationException
/// when a process is null, disposed, or not associated with a running process.
/// </summary>
public class ProcessSafetyTests
{
    // --- ProcessHelper.HasExitedSafe tests ---

    [Fact]
    public void HasExitedSafe_NullProcess_ReturnsTrue()
    {
        Assert.True(ProcessHelper.HasExitedSafe(null));
    }

    [Fact]
    public void HasExitedSafe_DisposedProcess_ReturnsTrue()
    {
        // Start a real short-lived process, dispose it, then check
        var psi = new ProcessStartInfo
        {
            FileName = OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/sh",
            Arguments = OperatingSystem.IsWindows() ? "/c echo test" : "-c \"echo test\"",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            CreateNoWindow = true
        };

        var process = Process.Start(psi);
        Assert.NotNull(process);
        process!.WaitForExit(5000);
        process.Dispose();

        // After dispose, HasExited would throw — our helper should not
        Assert.True(ProcessHelper.HasExitedSafe(process));
    }

    [Fact]
    public void HasExitedSafe_ExitedProcess_ReturnsTrue()
    {
        var psi = new ProcessStartInfo
        {
            FileName = OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/sh",
            Arguments = OperatingSystem.IsWindows() ? "/c echo test" : "-c \"echo test\"",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            CreateNoWindow = true
        };

        var process = Process.Start(psi);
        Assert.NotNull(process);
        process!.WaitForExit(5000);

        Assert.True(ProcessHelper.HasExitedSafe(process));
    }

    [Fact]
    public void HasExitedSafe_NewProcessObject_ReturnsTrue()
    {
        // A Process object created with new() has no associated OS process
        var process = new Process();
        Assert.True(ProcessHelper.HasExitedSafe(process));
    }

    [Fact]
    public void HasExitedSafe_RunningProcess_ReturnsFalse()
    {
        var psi = new ProcessStartInfo
        {
            FileName = OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/sh",
            Arguments = OperatingSystem.IsWindows() ? "/c timeout 10" : "-c \"sleep 10\"",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            CreateNoWindow = true
        };

        var process = Process.Start(psi);
        Assert.NotNull(process);

        try
        {
            Assert.False(ProcessHelper.HasExitedSafe(process));
        }
        finally
        {
            try { process!.Kill(); } catch { }
            process!.Dispose();
        }
    }

    [Fact]
    public void HasExitedSafe_KilledProcess_ReturnsTrue()
    {
        var psi = new ProcessStartInfo
        {
            FileName = OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/sh",
            Arguments = OperatingSystem.IsWindows() ? "/c timeout 10" : "-c \"sleep 10\"",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            CreateNoWindow = true
        };

        var process = Process.Start(psi);
        Assert.NotNull(process);
        process!.Kill();
        process.WaitForExit(5000);

        Assert.True(ProcessHelper.HasExitedSafe(process));
    }

    // --- Concurrent access safety tests ---

    [Fact]
    public async Task HasExitedSafe_ConcurrentAccessAfterDispose_NeverThrows()
    {
        var psi = new ProcessStartInfo
        {
            FileName = OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/sh",
            Arguments = OperatingSystem.IsWindows() ? "/c timeout 5" : "-c \"sleep 5\"",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            CreateNoWindow = true
        };

        var process = Process.Start(psi);
        Assert.NotNull(process);

        // Simulate the race condition: background tasks check HasExited
        // while the main thread kills and nulls the process
        var tasks = new List<Task>();
        for (int i = 0; i < 10; i++)
        {
            tasks.Add(Task.Run(() =>
            {
                for (int j = 0; j < 100; j++)
                {
                    // This should never throw
                    ProcessHelper.HasExitedSafe(process);
                }
            }));
        }

        // Kill and dispose while background tasks are checking
        try { process!.Kill(); } catch { }
        process!.Dispose();

        // All tasks should complete without throwing
        await Task.WhenAll(tasks);
    }
}
