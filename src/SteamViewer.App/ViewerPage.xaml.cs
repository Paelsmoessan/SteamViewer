using Microsoft.Extensions.Logging;

namespace SteamViewer.App;

public partial class ViewerPage : ContentPage
{
    public ViewerPage()
    {
        InitializeComponent();

#if WINDOWS
        blazorWebView.BlazorWebViewInitialized += OnBlazorWebViewInitialized;
#endif
    }

#if WINDOWS
    private void OnBlazorWebViewInitialized(object? sender, Microsoft.AspNetCore.Components.WebView.BlazorWebViewInitializedEventArgs e)
    {
        // Re-target NativeFrameBridge to this viewer window's CoreWebView2.
        // SharedBuffer frames must post to the window that has the video canvas.
        if (e.WebView is Microsoft.UI.Xaml.Controls.WebView2 webView2)
        {
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
            var bridge = MauiProgram.ServiceProvider?.GetService<Services.NativeFrameBridge>();
            bridge?.Initialize(coreWebView2);

            var inputRouter = MauiProgram.ServiceProvider?.GetService<Services.InputMessageRouter>();
            inputRouter?.Initialize(coreWebView2);
        }
        catch (Exception ex)
        {
            var logger = MauiProgram.ServiceProvider?.GetService<ILogger<ViewerPage>>();
            logger?.LogError(ex, "Failed to initialize NativeFrameBridge for viewer window");
        }
    }
#endif
}
