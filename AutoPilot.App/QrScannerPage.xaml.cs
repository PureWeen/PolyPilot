using AutoPilot.App.Services;
using ZXing.Net.Maui;

namespace AutoPilot.App;

public partial class QrScannerPage : ContentPage
{
    private readonly QrScannerService _service;
    private bool _scanned;
    private int _frameCount;
    private int _detectionCallCount;

    public QrScannerPage(QrScannerService service)
    {
        _service = service;
        InitializeComponent();

        // Explicitly set barcode formats to all supported types for maximum compatibility
        barcodeReader.Options = new BarcodeReaderOptions
        {
            Formats = BarcodeFormats.All,
            AutoRotate = true,
            Multiple = false,
            TryHarder = true,
        };

        Console.WriteLine($"[QrScanner] Initialized. Options: Formats={barcodeReader.Options.Formats}, AutoRotate={barcodeReader.Options.AutoRotate}, TryHarder={barcodeReader.Options.TryHarder}");
        Console.WriteLine($"[QrScanner] IsDetecting={barcodeReader.IsDetecting}, IsTorchOn={barcodeReader.IsTorchOn}");
        Console.WriteLine($"[QrScanner] CameraLocation={barcodeReader.CameraLocation}");

        // Track frame delivery
        barcodeReader.FrameReady += (s, e) =>
        {
            _frameCount++;
            if (_frameCount <= 5 || _frameCount % 30 == 0)
            {
                Console.WriteLine($"[QrScanner] Frame #{_frameCount}: {e.Data.Size.Width}x{e.Data.Size.Height}");
            }
        };
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        Console.WriteLine("[QrScanner] OnAppearing called");

        var status = await Permissions.CheckStatusAsync<Permissions.Camera>();
        Console.WriteLine($"[QrScanner] Camera permission status: {status}");

        if (status != PermissionStatus.Granted)
        {
            status = await Permissions.RequestAsync<Permissions.Camera>();
            if (status != PermissionStatus.Granted)
            {
                Console.WriteLine("[QrScanner] Camera permission denied after request");
                _service.SetResult(null);
                await Navigation.PopModalAsync();
                return;
            }
        }

        Console.WriteLine($"[QrScanner] Camera permission granted");
        Console.WriteLine($"[QrScanner] Post-appear: IsDetecting={barcodeReader.IsDetecting}, CameraLocation={barcodeReader.CameraLocation}");

        // Force re-enable detection after a short delay
        await Task.Delay(500);
        barcodeReader.IsDetecting = true;
        Console.WriteLine($"[QrScanner] Re-set IsDetecting=true after 500ms delay");
    }

    private void OnBarcodesDetected(object? sender, BarcodeDetectionEventArgs e)
    {
        _detectionCallCount++;
        Console.WriteLine($"[QrScanner] === BarcodesDetected event #{_detectionCallCount} ===");
        Console.WriteLine($"[QrScanner] Results count: {e.Results?.Length ?? 0}");

        if (e.Results != null)
        {
            foreach (var r in e.Results)
            {
                Console.WriteLine($"[QrScanner] Result: Format={r.Format}, Value='{r.Value}' (len={r.Value?.Length ?? 0})");
            }
        }

        if (_scanned) return;

        var result = e.Results?.FirstOrDefault();
        if (result == null) return;

        _scanned = true;
        Console.WriteLine($"[QrScanner] *** SCANNED: Format={result.Format}, Value='{result.Value}' ***");

        MainThread.BeginInvokeOnMainThread(async () =>
        {
            _service.SetResult(result.Value);
            await Navigation.PopModalAsync();
        });
    }

    private async void OnCancelClicked(object? sender, EventArgs e)
    {
        Console.WriteLine($"[QrScanner] Cancelled. Frames={_frameCount}, DetectionEvents={_detectionCallCount}");
        _service.SetResult(null);
        await Navigation.PopModalAsync();
    }
}
