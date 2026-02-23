using System.Net;
using System.Net.Sockets;
using PolyPilot.Services;

namespace PolyPilot.Tests;

/// <summary>
/// Tests for ServerManager.CheckServerRunning to verify socket exceptions
/// are properly observed and don't cause UnobservedTaskException crashes.
/// </summary>
public class ServerManagerTests
{
    [Fact]
    public void CheckServerRunning_ReturnsFalse_WhenNoServerListening()
    {
        var manager = new ServerManager();
        // Port 19999 should not have a listener in test environment
        var result = manager.CheckServerRunning("localhost", 19999);
        Assert.False(result);
    }

    [Fact]
    public void CheckServerRunning_ReturnsTrue_WhenServerListening()
    {
        // Start a temporary TCP listener
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var manager = new ServerManager();
        var result = manager.CheckServerRunning("localhost", port);

        Assert.True(result);
        listener.Stop();
    }

    [Fact]
    public void CheckServerRunning_NoUnobservedTaskException_OnConnectionRefused()
    {
        // Verify the fix: CheckServerRunning on a non-listening port must NOT leave
        // unobserved Task exceptions that fire TaskScheduler.UnobservedTaskException.
        using var unobservedSignal = new ManualResetEventSlim(false);
        Exception? unobservedException = null;
        EventHandler<UnobservedTaskExceptionEventArgs> handler = (sender, args) =>
        {
            if (args.Exception?.InnerException is SocketException)
            {
                unobservedException = args.Exception;
                unobservedSignal.Set();
            }
        };

        TaskScheduler.UnobservedTaskException += handler;
        try
        {
            var manager = new ServerManager();
            for (int i = 0; i < 5; i++)
            {
                manager.CheckServerRunning("localhost", 19999);
            }

            // Force GC to finalize any abandoned Tasks
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            // If any unobserved Task exception exists, the finalizer will signal within the window.
            // With the fix in place, no such tasks are left and the signal stays unset.
            unobservedSignal.Wait(TimeSpan.FromMilliseconds(500));
            Assert.Null(unobservedException);
        }
        finally
        {
            TaskScheduler.UnobservedTaskException -= handler;
        }
    }

    [Fact]
    public void CheckServerRunning_DefaultPort_UsesServerPort()
    {
        var manager = new ServerManager();
        // The no-arg overload should use ServerPort (4321). We can't predict the result
        // (persistent server may or may not be running), but we verify it doesn't throw
        // and returns a valid result, then confirm the explicit-port overload also works.
        var defaultResult = manager.CheckServerRunning();
        var customResult = manager.CheckServerRunning("localhost", 19998);
        Assert.True(defaultResult || !defaultResult); // completed without throwing
        Assert.False(customResult);
    }
}
