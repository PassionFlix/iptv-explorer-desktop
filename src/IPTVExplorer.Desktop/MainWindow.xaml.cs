using System.IO;
using System.Windows;
using IPTVExplorer.Infrastructure;
using Microsoft.Web.WebView2.Core;

namespace IPTVExplorer.Desktop;

public partial class MainWindow : Window
{
    private readonly BridgeRouter _bridge;
    private readonly MediaActionBridge _mediaActions;

    public MainWindow(BridgeRouter bridge, MediaActionBridge mediaActions)
    {
        _bridge = bridge;
        _mediaActions = mediaActions;
        InitializeComponent();
        Loaded += FitWindowToWorkArea;
        Loaded += InitializeWebViewAsync;
    }

    private void FitWindowToWorkArea(object? sender, RoutedEventArgs e)
    {
        var workArea = SystemParameters.WorkArea;
        const double margin = 16;

        MaxWidth = Math.Max(MinWidth, workArea.Width - (margin * 2));
        MaxHeight = Math.Max(MinHeight, workArea.Height - (margin * 2));

        Width = Math.Min(Width, MaxWidth);
        Height = Math.Min(Height, MaxHeight);

        Left = workArea.Left + Math.Max(margin, (workArea.Width - Width) / 2);
        Top = workArea.Top + Math.Max(margin, (workArea.Height - Height) / 2);
    }

    private async void InitializeWebViewAsync(object sender, RoutedEventArgs e)
    {
        try
        {
            await Browser.EnsureCoreWebView2Async();
            var assets = Path.Combine(AppContext.BaseDirectory, "ui");
            Browser.CoreWebView2.SetVirtualHostNameToFolderMapping("appassets.local", assets, CoreWebView2HostResourceAccessKind.DenyCors);
            Browser.CoreWebView2.Settings.AreDevToolsEnabled = false;
            Browser.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
            Browser.CoreWebView2.Settings.IsStatusBarEnabled = false;
            Browser.CoreWebView2.Settings.IsZoomControlEnabled = true;
            Browser.CoreWebView2.WebMessageReceived += OnWebMessageReceived;
            Browser.CoreWebView2.NavigationStarting += (_, args) =>
            {
                if (!Uri.TryCreate(args.Uri, UriKind.Absolute, out var uri) || !string.Equals(uri.Host, "appassets.local", StringComparison.OrdinalIgnoreCase)) args.Cancel = true;
            };

            var mediaActionsScript = await File.ReadAllTextAsync(Path.Combine(assets, "media-actions.js"));
            await Browser.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(mediaActionsScript);
            var polishScript = await File.ReadAllTextAsync(Path.Combine(assets, "v1-polish.js"));
            await Browser.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(polishScript);
            Browser.CoreWebView2.Navigate("https://appassets.local/index.html");
        }
        catch (Exception exception)
        {
            Browser.Visibility = Visibility.Collapsed;
            FallbackMessage.Text = "The Microsoft Edge WebView2 Runtime is required.\n\n" + LogRedactor.Redact(exception.Message);
            FallbackMessage.Visibility = Visibility.Visible;
        }
    }

    private async void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        var message = e.WebMessageAsJson;
        if (message.Length > 2_000_000) return;
        var response = await _mediaActions.TryHandleAsync(message) ?? await _bridge.HandleAsync(message);
        Browser.CoreWebView2.PostWebMessageAsJson(response);
    }
}
