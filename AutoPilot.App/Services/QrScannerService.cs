namespace AutoPilot.App.Services;

/// <summary>
/// Service to launch the QR code scanner and return the scanned value.
/// Uses ZXing.Net.MAUI modal page on all platforms.
/// </summary>
public class QrScannerService
{
    private TaskCompletionSource<string?>? _tcs;

    public Task<string?> ScanAsync()
    {
        _tcs = new TaskCompletionSource<string?>();

        MainThread.BeginInvokeOnMainThread(async () =>
        {
            try
            {
                var scannerPage = new QrScannerPage(this);
                var currentPage = Application.Current?.Windows?.FirstOrDefault()?.Page;
                if (currentPage != null)
                    await currentPage.Navigation.PushModalAsync(scannerPage);
                else
                    _tcs?.TrySetResult(null);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[QrScanner] Error launching scanner: {ex}");
                _tcs?.TrySetResult(null);
            }
        });

        return _tcs.Task;
    }

    internal void SetResult(string? value)
    {
        _tcs?.TrySetResult(value);
    }
}
