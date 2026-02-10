using Windows.Storage.Pickers;
using WinRT.Interop;

namespace AutoPilot.App.Services;

public static class FolderPickerService
{
    public static async Task<string?> PickFolderAsync()
    {
        try
        {
            var picker = new FolderPicker();
            picker.SuggestedStartLocation = PickerLocationId.DocumentsLibrary;
            picker.FileTypeFilter.Add("*");

            // Get the window handle for the current MAUI window
            var window = Application.Current?.Windows?.FirstOrDefault();
            if (window?.Handler?.PlatformView is Microsoft.UI.Xaml.Window winUIWindow)
            {
                var hwnd = WindowNative.GetWindowHandle(winUIWindow);
                InitializeWithWindow.Initialize(picker, hwnd);
            }

            var folder = await picker.PickSingleFolderAsync();
            return folder?.Path;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[FolderPicker] Error: {ex.Message}");
            return null;
        }
    }
}
