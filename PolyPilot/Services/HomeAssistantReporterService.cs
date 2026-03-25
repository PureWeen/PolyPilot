using System.Net.Http.Headers;
using System.Net.Http.Json;
using PolyPilot.Models;

namespace PolyPilot.Services;

/// <summary>
/// Reports PolyPilot status (mode, session count, processing state) to a Home Assistant
/// instance via its REST API whenever CopilotService state changes.
/// All errors are silently swallowed — reporting is best-effort.
/// </summary>
public class HomeAssistantReporterService : IAsyncDisposable
{
    private readonly CopilotService _copilotService;
    private readonly HttpMessageHandler? _httpHandler;
    private HttpClient? _httpClient;
    private HttpClient HttpClient => _httpClient ??= _httpHandler != null
        ? new HttpClient(_httpHandler, disposeHandler: false)
        : new HttpClient();

    // 0 = idle, 1 = report pending. Atomic flag for debouncing rapid state changes.
    private int _pendingReport;

    public HomeAssistantReporterService(CopilotService copilotService)
        : this(copilotService, null) { }

    internal HomeAssistantReporterService(CopilotService copilotService, HttpMessageHandler? httpHandler)
    {
        _copilotService = copilotService;
        _httpHandler = httpHandler;
        _copilotService.OnStateChanged += OnStateChanged;
    }

    private void OnStateChanged()
    {
        // Debounce: collapse rapid-fire state changes into a single deferred report.
        if (Interlocked.CompareExchange(ref _pendingReport, 1, 0) != 0)
            return;

        _ = Task.Run(async () =>
        {
            await Task.Delay(500);
            Interlocked.Exchange(ref _pendingReport, 0);
            await DoReportAsync();
        });
    }

    private async Task DoReportAsync()
    {
        var settings = _copilotService.CurrentSettings;
        if (settings is null || !settings.HomeAssistantEnabled)
            return;

        var url = settings.HomeAssistantUrl?.TrimEnd('/');
        var token = settings.HomeAssistantToken;

        if (string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(token))
            return;

        try
        {
            var payload = new
            {
                state = _copilotService.CurrentMode.ToString(),
                attributes = new
                {
                    session_count = _copilotService.Organization.Sessions.Count,
                    processing_count = _copilotService.ProcessingSessionCount,
                    active_session = _copilotService.ActiveSessionName ?? "",
                    is_initialized = _copilotService.IsInitialized,
                    friendly_name = "PolyPilot Status"
                }
            };

            using var request = new HttpRequestMessage(
                HttpMethod.Post,
                $"{url}/api/states/sensor.polypilot_status");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Content = JsonContent.Create(payload);

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await HttpClient.SendAsync(request, cts.Token);
        }
        catch
        {
            // Best-effort — never propagate errors to the caller
        }
    }

    public async ValueTask DisposeAsync()
    {
        _copilotService.OnStateChanged -= OnStateChanged;
        _httpClient?.Dispose();
        _httpClient = null;
    }
}
