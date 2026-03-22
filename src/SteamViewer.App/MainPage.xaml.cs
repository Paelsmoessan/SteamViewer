using Microsoft.Extensions.Logging;

namespace SteamViewer.App;

public partial class MainPage : ContentPage
{
    public MainPage()
    {
        InitializeComponent();

#if WINDOWS
        blazorWebView.BlazorWebViewInitialized += OnBlazorWebViewInitialized;
#endif
    }

#if WINDOWS
    private void OnBlazorWebViewInitialized(object? sender, Microsoft.AspNetCore.Components.WebView.BlazorWebViewInitializedEventArgs e)
    {
        // Access the underlying WebView2 control to set up SharedBuffer for frame transfer
        if (e.WebView is Microsoft.UI.Xaml.Controls.WebView2 webView2)
        {
            // CoreWebView2 should already be initialized at this point
            if (webView2.CoreWebView2 != null)
            {
                InitializeFrameBridge(webView2.CoreWebView2);
            }
            else
            {
                webView2.CoreWebView2Initialized += (s, args) =>
                {
                    if (webView2.CoreWebView2 != null)
                    {
                        InitializeFrameBridge(webView2.CoreWebView2);
                    }
                };
            }
        }
    }

    private void InitializeFrameBridge(Microsoft.Web.WebView2.Core.CoreWebView2 coreWebView2)
    {
        try
        {
            // Disable Ctrl+scroll zoom on home screen
            coreWebView2.Settings.IsZoomControlEnabled = false;
            coreWebView2.Settings.IsPinchZoomEnabled = false;

            var bridge = MauiProgram.ServiceProvider?.GetService<Services.NativeFrameBridge>();
            bridge?.Initialize(coreWebView2);

            var inputRouter = MauiProgram.ServiceProvider?.GetService<Services.InputMessageRouter>();
            inputRouter?.Initialize(coreWebView2);
        }
        catch (Exception ex)
        {
            var logger = MauiProgram.ServiceProvider?.GetService<ILogger<MainPage>>();
            logger?.LogError(ex, "Failed to initialize NativeFrameBridge");
        }
    }
#endif
}
