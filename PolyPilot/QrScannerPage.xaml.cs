using PolyPilot.Services;
using ZXing.Net.Maui;

namespace PolyPilot;

public partial class QrScannerPage : ContentPage
{
    private readonly QrScannerService _service;
    private bool _scanned;
    private int _frameCount;
    private int _detectionCallCount;
    private bool _torchOn;

    public QrScannerPage(QrScannerService service)
    {
        _service = service;
        InitializeComponent();

        barcodeReader.Options = new BarcodeReaderOptions
        {
            Formats = BarcodeFormat.QrCode,
            AutoRotate = true,
            Multiple = false,
            TryHarder = true,
        };

        barcodeReader.FrameReady += (s, e) =>
        {
            _frameCount++;
            if (_frameCount <= 5 || _frameCount % 30 == 0)
            {
                Console.WriteLine($"[QrScanner] Frame #{_frameCount}: {e.Data.Size.Width}x{e.Data.Size.Height}");
            }
        };
    }

    protected override void OnSizeAllocated(double width, double height)
    {
        base.OnSizeAllocated(width, height);
        LayoutOverlays(width, height);
    }

    private void LayoutOverlays(double pageWidth, double pageHeight)
    {
        const double cutoutSize = 240;
        var cx = pageWidth / 2;
        var cy = pageHeight / 2;
        var left = cx - cutoutSize / 2;
        var top = cy - cutoutSize / 2;

        overlayTop.HeightRequest = top;
        overlayBottom.HeightRequest = pageHeight - top - cutoutSize;
        overlayLeft.WidthRequest = left;
        overlayLeft.HeightRequest = cutoutSize;
        overlayLeft.Margin = new Thickness(0, top, 0, 0);
        overlayRight.WidthRequest = pageWidth - left - cutoutSize;
        overlayRight.HeightRequest = cutoutSize;
        overlayRight.Margin = new Thickness(0, top, 0, 0);

        overlayTop.IsVisible = true;
        overlayBottom.IsVisible = true;
        overlayLeft.IsVisible = true;
        overlayRight.IsVisible = true;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();

        var status = await Permissions.CheckStatusAsync<Permissions.Camera>();
        if (status != PermissionStatus.Granted)
        {
            status = await Permissions.RequestAsync<Permissions.Camera>();
            if (status != PermissionStatus.Granted)
            {
                _service.SetResult(null);
                await Navigation.PopModalAsync();
                return;
            }
        }

        await Task.Delay(500);
        barcodeReader.IsDetecting = true;
    }

    private void OnBarcodesDetected(object? sender, BarcodeDetectionEventArgs e)
    {
        _detectionCallCount++;

        if (_scanned) return;

        var result = e.Results?.FirstOrDefault();
        if (result == null) return;

        _scanned = true;
        Console.WriteLine($"[QrScanner] Scanned: Format={result.Format}, Length={result.Value?.Length ?? 0}");

        MainThread.BeginInvokeOnMainThread(async () =>
        {
            try
            {
                barcodeReader.IsDetecting = false;

                // Flash viewfinder green on success
                viewfinder.Stroke = Color.FromArgb("#48bb78");
                statusLabel.Text = "✓ QR Code detected";
                statusBorder.IsVisible = true;
                await Task.Delay(500);

                _service.SetResult(result.Value);
                await Navigation.PopModalAsync();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[QrScanner] Error dismissing scanner: {ex}");
                _service.SetResult(result.Value);
                try { await Navigation.PopModalAsync(false); } catch { }
            }
        });
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        barcodeReader.IsDetecting = false;
    }

    private void OnTorchClicked(object? sender, EventArgs e)
    {
        _torchOn = !_torchOn;
        barcodeReader.IsTorchOn = _torchOn;
        torchBorder.Background = _torchOn ? new SolidColorBrush(Color.FromArgb("#993b82f6")) : new SolidColorBrush(Color.FromArgb("#661a1a2e"));
        torchIcon.Source = new FontImageSource
        {
            Glyph = "◉",
            Color = _torchOn ? Color.FromArgb("#3b82f6") : Color.FromArgb("#a0b4cc"),
            Size = 20
        };
    }

    private async void OnCancelClicked(object? sender, EventArgs e)
    {
        _service.SetResult(null);
        await Navigation.PopModalAsync();
    }
}
