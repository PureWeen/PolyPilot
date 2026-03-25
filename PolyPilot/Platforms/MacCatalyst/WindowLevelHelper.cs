using System.Runtime.InteropServices;
using ObjCRuntime;
using UIKit;

namespace PolyPilot.Platforms.MacCatalyst;

/// <summary>
/// Sets the Mac Catalyst window to "always on top" by messaging the underlying NSWindow
/// directly via the ObjC runtime. UIWindow.WindowLevel only reorders windows within the
/// app; to float above ALL apps we must set the NSWindow level to NSFloatingWindowLevel (3).
///
/// The NSWindow is retrieved by sending the [nsWindow] message to the UIWindow (Mac Catalyst
/// private bridge — works on all tested Catalyst versions).
///
/// CGWindowLevel constants:
///   NSNormalWindowLevel   = 0  (regular apps)
///   NSFloatingWindowLevel = 3  (always above normal windows, below Dock/menu bar)
/// </summary>
public static class WindowLevelHelper
{
    // NSFloatingWindowLevel = 3: floats above all normal app windows
    private const nint FloatingLevel = 3;
    private const nint NormalLevel = 0;

    // Retrieve an ObjC object: id objc_msgSend(id self, SEL op)
    [DllImport("/usr/lib/libobjc.dylib", EntryPoint = "objc_msgSend")]
    private static extern IntPtr IntPtr_objc_msgSend(IntPtr receiver, IntPtr selector);

    // Set NSWindow level: void objc_msgSend(id self, SEL op, NSInteger level)
    [DllImport("/usr/lib/libobjc.dylib", EntryPoint = "objc_msgSend")]
    private static extern void void_objc_msgSend_nint(IntPtr receiver, IntPtr selector, nint arg);

    public static void SetAlwaysOnTop(bool onTop)
    {
        try
        {
            var uiWindow = UIApplication.SharedApplication.ConnectedScenes
                .OfType<UIWindowScene>()
                .SelectMany(s => s.Windows)
                .FirstOrDefault(w => w.IsKeyWindow)
                ?? UIApplication.SharedApplication.ConnectedScenes
                    .OfType<UIWindowScene>()
                    .SelectMany(s => s.Windows)
                    .FirstOrDefault();

            if (uiWindow == null)
            {
                Console.WriteLine("[WindowLevel] No UIWindow found");
                return;
            }

            // Get the underlying NSWindow via [uiWindow nsWindow] — Catalyst private bridge
            IntPtr uiWindowHandle = uiWindow.Handle;
            IntPtr nsWindowPtr = IntPtr_objc_msgSend(uiWindowHandle, Selector.GetHandle("nsWindow"));

            if (nsWindowPtr == IntPtr.Zero)
            {
                Console.WriteLine("[WindowLevel] [nsWindow] returned nil — cannot set level");
                return;
            }

            // [nsWindow setLevel: NSFloatingWindowLevel (3)] — floats above all other apps
            nint level = onTop ? FloatingLevel : NormalLevel;
            void_objc_msgSend_nint(nsWindowPtr, Selector.GetHandle("setLevel:"), level);

            Console.WriteLine($"[WindowLevel] NSWindow.setLevel:{level} ({(onTop ? "floating" : "normal")})");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[WindowLevel] SetAlwaysOnTop failed: {ex.Message}");
        }
    }
}
