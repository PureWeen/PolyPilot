namespace PolyPilot.Services;

/// <summary>
/// Interface for managing the persistent copilot server process.
/// </summary>
public interface IServerManager
{
    bool IsServerRunning { get; }
    int? ServerPid { get; }
    int ServerPort { get; }
    event Action? OnStatusChanged;

    bool CheckServerRunning(string host = "127.0.0.1", int? port = null);
    Task<bool> StartServerAsync(int port);
    void StopServer();
    bool DetectExistingServer();
}
