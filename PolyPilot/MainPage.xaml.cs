using PolyPilot.Components;
using Microsoft.AspNetCore.Components.WebView;
using Microsoft.AspNetCore.Components.WebView.Maui;

namespace PolyPilot;

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

#if ANDROID
		blazorWebView.BlazorWebViewInitialized += OnBlazorWebViewInitialized;
#elif IOS
		blazorWebView.BlazorWebViewInitialized += OnBlazorWebViewInitializediOS;
#endif
	}

#if IOS
	private void OnBlazorWebViewInitializediOS(object? sender, BlazorWebViewInitializedEventArgs e)
	{
		var wkWebView = e.WebView;
		wkWebView.Opaque = false;
		wkWebView.BackgroundColor = UIKit.UIColor.FromRGB(0x1a, 0x1a, 0x2e);
		wkWebView.ScrollView.BackgroundColor = UIKit.UIColor.FromRGB(0x1a, 0x1a, 0x2e);
	}
#endif

#if ANDROID
	private void OnBlazorWebViewInitialized(object? sender, BlazorWebViewInitializedEventArgs e)
	{
		var webView = e.WebView;

		// Wait for layout so WindowInsets are available
		webView.ViewTreeObserver!.GlobalLayout += (s, args) =>
		{
			InjectInsetsJs(webView);
		};
		// Also try immediately in case layout already happened
		Dispatcher.DispatchDelayed(TimeSpan.FromMilliseconds(800), () => InjectInsetsJs(webView));
	}

	private bool _insetsInjected;
	private void InjectInsetsJs(Android.Webkit.WebView webView)
	{
		if (_insetsInjected) return;

		var activity = Platform.CurrentActivity;
		var rootInsets = activity?.Window?.DecorView?.RootWindowInsets;
		if (rootInsets == null)
		{
			Console.WriteLine("[Insets] RootWindowInsets is null, skipping");
			return;
		}

		var density = activity!.Resources?.DisplayMetrics?.Density ?? 1;
		double topInset, bottomInset;

		if (Android.OS.Build.VERSION.SdkInt >= Android.OS.BuildVersionCodes.R)
		{
			var systemBars = rootInsets.GetInsets(Android.Views.WindowInsets.Type.SystemBars());
			topInset = systemBars.Top / density;
			bottomInset = systemBars.Bottom / density;
		}
		else
		{
			#pragma warning disable CA1422
			topInset = rootInsets.StableInsetTop / density;
			bottomInset = rootInsets.StableInsetBottom / density;
			#pragma warning restore CA1422
		}

		if (bottomInset <= 0 && topInset <= 0)
		{
			Console.WriteLine("[Insets] Both insets are 0, skipping");
			return;
		}

		// Add visual buffer so content comfortably clears the gesture bar
		bottomInset += 14;

		_insetsInjected = true;
		Console.WriteLine($"[Insets] Injecting: top={topInset:F0}px bottom={bottomInset:F0}px");

		var js = $@"
			document.documentElement.style.setProperty('--status-bar-height', '{topInset:F0}px');
			document.documentElement.style.setProperty('--nav-bar-height', '{bottomInset:F0}px');
			console.log('Insets injected: top={topInset:F0}px bottom={bottomInset:F0}px');
		";

		try
		{
			webView.EvaluateJavascript(js, null);
		}
		catch (Exception ex)
		{
			Console.WriteLine($"[Insets] JS injection failed: {ex.Message}");
		}
	}
#endif
}
