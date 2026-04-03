using Microsoft.Maui.DevFlow.Agent.Gtk;
using Microsoft.Maui.DevFlow.Blazor.Gtk;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Maui.Hosting;
using Platform.Maui.Linux.Gtk4.BlazorWebView;
using Platform.Maui.Linux.Gtk4.Platform;

namespace PolyPilot;

public class Program : GtkMauiApplication
{
    protected override MauiApp CreateMauiApp() => MauiProgram.CreateMauiApp();

    protected override void OnStarted()
    {
        base.OnStarted();

        var app = Microsoft.Maui.Controls.Application.Current;
        if (OperatingSystem.IsLinux() && app?.Windows.FirstOrDefault() is Microsoft.Maui.Controls.Window window)
        {
            window.Width = 1400;
            window.Height = 900;
        }

#if DEBUG
        app?.StartDevFlowAgent();

        var blazorService = app?.Handler?.MauiContext?.Services.GetService<GtkBlazorWebViewDebugService>();
        blazorService?.WireBlazorCdpToAgent();
#endif
    }

    public static void Main(string[] args)
    {
        GtkBlazorWebView.InitializeWebKit();

        var app = new Program();
        app.Run(args);
    }
}
