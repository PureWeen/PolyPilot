using UIKit;
using UniformTypeIdentifiers;

namespace AutoPilot.App.Services;

public static class FolderPickerService
{
    public static Task<string?> PickFolderAsync()
    {
        var tcs = new TaskCompletionSource<string?>();

        var picker = new UIDocumentPickerViewController(new[] { UTTypes.Folder });
        picker.AllowsMultipleSelection = false;

        picker.DidPickDocumentAtUrls += (_, e) =>
        {
            var url = e.Urls?.FirstOrDefault();
            if (url != null)
            {
                url.StartAccessingSecurityScopedResource();
                tcs.TrySetResult(url.Path);
            }
            else
            {
                tcs.TrySetResult(null);
            }
        };

        picker.WasCancelled += (_, _) =>
        {
            tcs.TrySetResult(null);
        };

        var viewController = GetTopViewController();
        viewController?.PresentViewController(picker, true, null);

        if (viewController == null)
            tcs.TrySetResult(null);

        return tcs.Task;
    }

    private static UIViewController? GetTopViewController()
    {
        var scenes = UIApplication.SharedApplication.ConnectedScenes;
        var windowScene = scenes.ToArray().OfType<UIWindowScene>().FirstOrDefault();
        var window = windowScene?.Windows.FirstOrDefault(w => w.IsKeyWindow);
        var vc = window?.RootViewController;
        while (vc?.PresentedViewController != null)
            vc = vc.PresentedViewController;
        return vc;
    }
}
