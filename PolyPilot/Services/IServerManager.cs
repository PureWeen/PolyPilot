namespace PolyPilot.Services;

/// <summary>
/// Interface for managing the persistent copilot server process.
/// </summary>
public interface IServerManager
{
    bool IsServerRunning { get; }
    int? ServerPid { get; }
    int ServerPort { get; }
    /// <summary>Last error message from a failed StartServerAsync call, including any stderr output.</summary>
    string? LastError { get; }
    event Action? OnStatusChanged;

    bool CheckServerRunning(string host = "127.0.0.1", int? port = null);
    Task<bool> StartServerAsync(int port, string? githubToken = null);
    void StopServer();
    bool DetectExistingServer();
}
