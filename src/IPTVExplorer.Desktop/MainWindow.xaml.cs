using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using IPTVExplorer.Infrastructure;
using Microsoft.Web.WebView2.Core;

namespace IPTVExplorer.Desktop;

public partial class MainWindow : Window
{
    private const string ReleasesPrefix = "https://github.com/PassionFlix/iptv-explorer-desktop/releases/";
    private static readonly HttpClient UpdateClient = CreateUpdateClient();
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

    private static HttpClient CreateUpdateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(6) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("IPTV-Explorer-Desktop/1.0");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    }

    private void FitWindowToWorkArea(object? sender, RoutedEventArgs e)
    {
        var workArea = SystemParameters.WorkArea;
        const double margin = 16;

        var availableWidth = Math.Max(MinWidth, workArea.Width - (margin * 2));
        var availableHeight = Math.Max(MinHeight, workArea.Height - (margin * 2));

        Width = Math.Min(Width, availableWidth);
        Height = Math.Min(Height, availableHeight);

        Left = workArea.Left + Math.Max(0, (workArea.Width - Width) / 2);
        Top = workArea.Top + Math.Max(0, (workArea.Height - Height) / 2);
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
            Browser.CoreWebView2.NavigationStarting += OnNavigationStarting;
            Browser.CoreWebView2.NavigationCompleted += OnNavigationCompleted;

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

    private void OnNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs args)
    {
        if (!Uri.TryCreate(args.Uri, UriKind.Absolute, out var uri))
        {
            args.Cancel = true;
            return;
        }

        if (string.Equals(uri.Host, "appassets.local", StringComparison.OrdinalIgnoreCase)) return;

        args.Cancel = true;
        if (args.Uri.StartsWith(ReleasesPrefix, StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                Process.Start(new ProcessStartInfo(args.Uri) { UseShellExecute = true });
            }
            catch
            {
                // The update notification remains visible if the system cannot open a browser.
            }
        }
    }

    private async void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs args)
    {
        if (!args.IsSuccess) return;
        Browser.CoreWebView2.NavigationCompleted -= OnNavigationCompleted;
        await CheckForUpdatesAsync();
    }

    private async Task CheckForUpdatesAsync()
    {
        try
        {
            using var response = await UpdateClient.GetAsync(
                "https://api.github.com/repos/PassionFlix/iptv-explorer-desktop/releases/latest");
            if (!response.IsSuccessStatusCode) return;

            await using var stream = await response.Content.ReadAsStreamAsync();
            using var document = await JsonDocument.ParseAsync(stream);
            var root = document.RootElement;
            if (!root.TryGetProperty("tag_name", out var tagElement) ||
                !root.TryGetProperty("html_url", out var urlElement)) return;

            var tag = tagElement.GetString()?.Trim();
            var releaseUrl = urlElement.GetString()?.Trim();
            if (string.IsNullOrWhiteSpace(tag) || string.IsNullOrWhiteSpace(releaseUrl) ||
                !releaseUrl.StartsWith(ReleasesPrefix, StringComparison.OrdinalIgnoreCase)) return;

            var normalized = tag.StartsWith('v') ? tag[1..] : tag;
            var current = typeof(MainWindow).Assembly.GetName().Version ?? new Version(0, 0, 0);
            if (!Version.TryParse(normalized, out var latest) || latest <= current) return;

            var payload = JsonSerializer.Serialize(new
            {
                version = latest.ToString(3),
                currentVersion = current.ToString(3),
                url = releaseUrl
            });
            var script = $"window.dispatchEvent(new CustomEvent('iptv-update-available', {{ detail: {payload} }}));";
            await Browser.CoreWebView2.ExecuteScriptAsync(script);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or JsonException)
        {
            // Update checks are best-effort and must never prevent the application from starting.
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
