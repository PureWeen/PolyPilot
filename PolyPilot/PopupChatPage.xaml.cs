using Microsoft.AspNetCore.Components.WebView.Maui;
using PolyPilot.Components.PopupChat;
using PolyPilot.Services;

namespace PolyPilot;

public partial class PopupChatPage : ContentPage
{
    public PopupChatPage(PopupRequest request, MinimizedModeService service)
    {
        InitializeComponent();

        blazorWebView.RootComponents.Add(new RootComponent
        {
            Selector = "#app",
            ComponentType = typeof(PopupChatHost),
            Parameters = new Dictionary<string, object?>
            {
                ["Request"] = request,
                ["Service"] = service,
            },
        });
    }
}
