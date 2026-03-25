using System.Collections.Concurrent;

namespace PolyPilot.Services;

/// <summary>
/// Manages the minimized popup window that appears when a session completes while
/// the main PolyPilot window is not focused. Allows the user to send a quick
/// follow-up prompt from the popup without switching to the full app.
/// </summary>
public class MinimizedModeService : IMinimizedModeService
{
    private readonly CopilotService _copilotService;

    private readonly ConcurrentQueue<PopupRequest> _pendingPopups = new();
    private Window? _activePopupWindow;
    private bool _isShowingPopup; // accessed only from main thread

    public bool IsMainWindowFocused { get; set; } = true;

    public MinimizedModeService(CopilotService copilotService)
    {
        _copilotService = copilotService;
    }

    /// <summary>
    /// Called when a session completes while the main window is not focused.
    /// Queues the popup and opens it if none is currently active.
    /// </summary>
    public void OnSessionCompleted(string sessionName, string sessionId, string? lastResponse)
    {
        _pendingPopups.Enqueue(new PopupRequest(sessionName, sessionId, lastResponse ?? ""));
        TryShowNextPopup();
    }

    private void TryShowNextPopup()
    {
        // Ensure all state access and window operations happen on the main thread.
        // OnSessionCompleted can be called from background threads (SDK event callbacks),
        // so we always marshal here before touching _isShowingPopup.
        if (!MainThread.IsMainThread)
        {
            MainThread.BeginInvokeOnMainThread(TryShowNextPopup);
            return;
        }

        if (_isShowingPopup) return;
        if (!_pendingPopups.TryDequeue(out var request)) return;

        _isShowingPopup = true;

#if MACCATALYST || WINDOWS
        var page = new PopupChatPage(request, this);
        var window = new Window(page)
        {
            Title = $"PolyPilot — {request.SessionName}",
            Width = 520,
            Height = 420,
        };

        window.Destroying += (_, _) =>
        {
            if (ReferenceEquals(_activePopupWindow, window))
            {
                _activePopupWindow = null;
                _isShowingPopup = false;
                TryShowNextPopup();
            }
        };

        _activePopupWindow = window;
        Application.Current?.OpenWindow(window);
#endif
    }

    /// <summary>
    /// Closes the active popup window and shows the next one if queued.
    /// </summary>
    public void DismissPopup()
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            if (_activePopupWindow != null)
            {
                Application.Current?.CloseWindow(_activePopupWindow);
                // The Destroying event will reset _isShowingPopup and call TryShowNextPopup
            }
        });
    }

    /// <summary>
    /// Sends a prompt to the session, then closes the popup.
    /// </summary>
    public void SendAndDismiss(string sessionName, string prompt)
    {
        if (string.IsNullOrWhiteSpace(prompt)) return;

        _ = Task.Run(async () =>
        {
            try
            {
                await _copilotService.SendPromptAsync(sessionName, prompt);
            }
            catch
            {
                // Best effort — session may have been closed
            }
        });

        DismissPopup();

        // Switch to the session in the main window
        MainThread.BeginInvokeOnMainThread(() =>
        {
            _copilotService.SwitchSession(sessionName);
        });
    }

    /// <summary>
    /// Opens the main window and navigates to the session, then closes the popup.
    /// </summary>
    public void OpenInFullApp(string sessionName)
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            _copilotService.SwitchSession(sessionName);
            DismissPopup();
        });
    }
}

public record PopupRequest(string SessionName, string SessionId, string LastResponse);
