using CommunityToolkit.Maui;
using CommunityToolkit.Maui.Media;
#if DEBUG
using Microsoft.Maui.DevFlow.Agent.Gtk;
using Microsoft.Maui.DevFlow.Blazor.Gtk;
#endif
using Microsoft.Extensions.Logging;
using Platform.Maui.Linux.Gtk4.BlazorWebView;
using Platform.Maui.Linux.Gtk4.Essentials.Hosting;
using Platform.Maui.Linux.Gtk4.Hosting;
using PolyPilot.Services;

namespace PolyPilot;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();
        builder
            .UseMauiAppLinuxGtk4<App>()
            .UseMauiCommunityToolkit()
            .AddLinuxGtk4Essentials()
            .ConfigureFonts(fonts =>
            {
                fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
            });

        builder.Services.AddMauiBlazorWebView();
        builder.Services.AddLinuxGtk4BlazorWebView();
        builder.ConfigureMauiHandlers(handlers =>
        {
            handlers.AddHandler<Microsoft.AspNetCore.Components.WebView.Maui.BlazorWebView, BlazorWebViewHandler>();
        });

        builder.Services.AddSingleton<CopilotService>();
        builder.Services.AddSingleton<ChatDatabase>();
        builder.Services.AddSingleton<IChatDatabase>(sp => sp.GetRequiredService<ChatDatabase>());
        builder.Services.AddSingleton<ServerManager>();
        builder.Services.AddSingleton<IServerManager>(sp => sp.GetRequiredService<ServerManager>());
        builder.Services.AddSingleton<DevTunnelService>();
        builder.Services.AddSingleton<WsBridgeServer>();
        builder.Services.AddSingleton<TailscaleService>();
        builder.Services.AddSingleton<WsBridgeClient>();
        builder.Services.AddSingleton<IWsBridgeClient>(sp => sp.GetRequiredService<WsBridgeClient>());
        builder.Services.AddSingleton<FiestaService>();
        builder.Services.AddSingleton<QrScannerService>();
        builder.Services.AddSingleton<KeyCommandService>();
        builder.Services.AddSingleton<GitAutoUpdateService>();
        builder.Services.AddSingleton<RepoManager>();
        builder.Services.AddSingleton<TutorialService>();
        builder.Services.AddSingleton<UsageStatsService>();
        builder.Services.AddSingleton<INotificationManagerService, NotificationManagerService>();
        builder.Services.AddSingleton<ISpeechToText>(SpeechToText.Default);

#if DEBUG
        builder.Logging.AddDebug();
        builder.AddMauiDevFlowAgent();
        builder.AddMauiBlazorDevFlowTools();
#endif

        return builder.Build();
    }
}
