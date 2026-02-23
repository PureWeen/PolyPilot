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
        // This test verifies the fix: calling CheckServerRunning on a non-listening port
        // should NOT leave unobserved Task exceptions that fire during GC.
        Exception? unobservedException = null;
        EventHandler<UnobservedTaskExceptionEventArgs> handler = (sender, args) =>
        {
            if (args.Exception?.InnerException is SocketException)
            {
                unobservedException = args.Exception;
            }
        };

        TaskScheduler.UnobservedTaskException += handler;
        try
        {
            var manager = new ServerManager();
            // Call multiple times to increase chance of triggering
            for (int i = 0; i < 5; i++)
            {
                manager.CheckServerRunning("localhost", 19999);
            }

            // Force GC to finalize any abandoned Tasks
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            // Give the finalizer thread time to fire the event
            Thread.Sleep(200);

            Assert.Null(unobservedException);
        }
        finally
        {
            TaskScheduler.UnobservedTaskException -= handler;
        }
    }

    [Fact]
    public void CheckServerRunning_ReturnsFalse_WhenHostUnreachable()
    {
        var manager = new ServerManager();
        // Use a non-routable address to test timeout behavior
        var result = manager.CheckServerRunning("192.0.2.1", 19999);
        Assert.False(result);
    }

    [Fact]
    public void CheckServerRunning_DefaultPort_UsesServerPort()
    {
        var manager = new ServerManager();
        // Should use ServerPort (4321) when no port specified — just verify it doesn't throw
        _ = manager.CheckServerRunning();
    }
}
