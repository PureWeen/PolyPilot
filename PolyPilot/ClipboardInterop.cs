using Microsoft.JSInterop;
using Microsoft.Maui.ApplicationModel.DataTransfer;

namespace PolyPilot;

/// <summary>
/// Static JS-invokable methods for clipboard interop.
/// Enables integration tests to verify clipboard content via CDP.
/// </summary>
public static class ClipboardInterop
{
    [JSInvokable]
    public static async Task<string> GetClipboardText()
    {
        if (Clipboard.HasText)
            return await Clipboard.GetTextAsync() ?? "";
        return "";
    }
}
