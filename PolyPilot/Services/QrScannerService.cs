namespace PolyPilot.Services;

/// <summary>
/// Service to launch the QR code scanner and return the scanned value.
/// Uses ZXing.Net.MAUI modal page on all platforms.
/// </summary>
public class QrScannerService
{
    private readonly object _lock = new();
    private TaskCompletionSource<string?>? _tcs;

    public Task<string?> ScanAsync()
    {
        TaskCompletionSource<string?> captured;
        lock (_lock)
        {
            if (_tcs != null && !_tcs.Task.IsCompleted)
                return _tcs.Task;

            _tcs = new TaskCompletionSource<string?>();
            captured = _tcs; // capture inside lock — safe from field-swap races
        }

        MainThread.BeginInvokeOnMainThread(async () =>
        {
            try
            {
                var scannerPage = new QrScannerPage(this);
                var currentPage = Application.Current?.Windows?.FirstOrDefault()?.Page;
                if (currentPage != null)
                    await currentPage.Navigation.PushModalAsync(scannerPage);
                else
                    captured.TrySetResult(null);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[QrScanner] Error launching scanner: {ex}");
                captured.TrySetResult(null);
            }
        });

        return captured.Task;
    }

    internal void SetResult(string? value)
    {
        TaskCompletionSource<string?>? current;
        lock (_lock) current = _tcs;
        current?.TrySetResult(value);
    }
}
