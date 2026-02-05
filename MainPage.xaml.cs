using AutoPilot.App.Components;
using Microsoft.AspNetCore.Components.WebView.Maui;

namespace AutoPilot.App;

public partial class MainPage : ContentPage
{
	public MainPage()
	{
		InitializeComponent();
		
		blazorWebView.RootComponents.Add(new RootComponent
		{
			Selector = "#app",
			ComponentType = typeof(Routes)
		});
	}
}
