using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using IPTVExplorer.Core;
using IPTVExplorer.Infrastructure;
using IPTVExplorer.Player;
using Microsoft.Web.WebView2.Core;

namespace IPTVExplorer.Desktop;

public partial class MainWindow : Window
{
    private const string ReleasesPrefix = "https://github.com/PassionFlix/iptv-explorer-desktop/releases/";
    private const double LiveBrowserWidth = 700;
    private static readonly HttpClient UpdateClient = CreateUpdateClient();
    private readonly BridgeRouter _bridge;
    private readonly MediaActionBridge _mediaActions;
    private readonly FavoriteBridge _favorites;
    private readonly ProviderSecretBridge _providerSecrets;
    private readonly CatalogStatsBridge _catalogStats;
    private readonly IPlayerService _player;
    private readonly TrueFullscreenBehavior _liveFullscreenBehavior;
    private readonly TaskCompletionSource<nint> _liveRenderHandle = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private PlayerState _liveState = PlayerState.Idle;
    private bool _liveSoftStopped;
    private bool _liveSurfaceActive;
    private bool _updatingLiveTracks;
    private double _liveSurfaceTop = 100;

    public MainWindow(
        BridgeRouter bridge,
        MediaActionBridge mediaActions,
        FavoriteBridge favorites,
        ProviderSecretBridge providerSecrets,
        CatalogStatsBridge catalogStats,
        IPlayerService player)
    {
        _bridge = bridge;
        _mediaActions = mediaActions;
        _favorites = favorites;
        _providerSecrets = providerSecrets;
        _catalogStats = catalogStats;
        _player = player;
        InitializeComponent();
        _liveFullscreenBehavior = new(this);
        LiveVideoHost.HandleReady += OnLiveHandleReady;
        _player.StateChanged += OnLivePlayerStateChanged;
        _player.TrackListChanged += OnLiveTrackListChanged;
        Loaded += FitWindowToWorkArea;
        Loaded += InitializeWebViewAsync;
        Closed += OnMainWindowClosed;
    }

    private static HttpClient CreateUpdateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(6) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"IPTV-Explorer-Desktop/{ApplicationVersion.Display}");
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
            var favoritesScript = await File.ReadAllTextAsync(Path.Combine(assets, "favorites.js"));
            await Browser.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(favoritesScript);
            var providerNativeActionsScript = await File.ReadAllTextAsync(Path.Combine(assets, "provider-native-actions.js"));
            await Browser.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(providerNativeActionsScript);
            var diagnosticStatsScript = await File.ReadAllTextAsync(Path.Combine(assets, "diagnostic-local-stats.js"));
            await Browser.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(diagnosticStatsScript);
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

            if (!ApplicationVersion.IsNewerRelease(tag)) return;
            var normalized = tag.StartsWith('v') ? tag[1..] : tag;
            var latest = Version.Parse(normalized);

            var payload = JsonSerializer.Serialize(new
            {
                version = latest.ToString(3),
                currentVersion = ApplicationVersion.Display,
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
        string? method;
        try { method = BridgeProtocol.Parse(message).Method; }
        catch (FormatException) { method = null; }
        var isLiveCatalog = method is "catalog.live" or "catalog.live.page";
        var response = await _providerSecrets.TryHandleAsync(message)
            ?? await _catalogStats.TryHandleAsync(message)
            ?? await _favorites.TryHandleAsync(message)
            ?? await _mediaActions.TryHandleAsync(message)
            ?? (isLiveCatalog ? await Task.Run(() => _bridge.HandleAsync(message)) : await _bridge.HandleAsync(message));
        Browser.CoreWebView2.PostWebMessageAsJson(response);
    }

    internal async Task<nint> ShowIntegratedLivePlayerAsync(string title, CancellationToken cancellationToken = default)
    {
        if (!Dispatcher.CheckAccess())
            return await Dispatcher.InvokeAsync(() => ShowIntegratedLivePlayerAsync(title, cancellationToken)).Task.Unwrap();

        SetIntegratedLivePlayerVisible(true, stopPlayback: false);
        _liveSurfaceActive = true;
        _liveSoftStopped = false;
        LivePlayerTitle.Text = CleanLiveTitle(title);
        LiveStatusText.Text = "Chargement…";
        LivePlayPauseButton.Content = "Pause";

        if (LiveVideoHost.NativeHandle != 0) _liveRenderHandle.TrySetResult(LiveVideoHost.NativeHandle);
        return await _liveRenderHandle.Task.WaitAsync(cancellationToken);
    }

    internal void SetIntegratedLivePlayerVisible(bool visible, bool stopPlayback, double top = 0)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(() => SetIntegratedLivePlayerVisible(visible, stopPlayback, top));
            return;
        }

        if (visible)
        {
            if (double.IsFinite(top) && top > 0) _liveSurfaceTop = Math.Clamp(top, 0, 300);
            LivePlayerPane.Margin = new Thickness(0, _liveSurfaceTop, 0, 0);
            LivePlayerPane.Visibility = Visibility.Visible;
            if (!_liveFullscreenBehavior.IsFullscreen)
            {
                BrowserColumn.Width = new GridLength(LiveBrowserWidth);
                LivePlayerColumn.Width = new GridLength(1, GridUnitType.Star);
            }
            return;
        }

        if (_liveFullscreenBehavior.IsFullscreen) SetLiveFullscreen(false);
        if (stopPlayback && _liveSurfaceActive) _player.Stop();
        _liveSurfaceActive = false;
        _liveSoftStopped = false;
        _liveState = PlayerState.Idle;
        LivePlayerPane.Visibility = Visibility.Collapsed;
        BrowserColumn.Width = new GridLength(1, GridUnitType.Star);
        LivePlayerColumn.Width = new GridLength(0);
        LivePlayerTitle.Text = "Sélectionnez une chaîne";
        LiveStatusText.Text = "Prêt";
        LivePlayPauseButton.Content = "Pause";
        LiveAudioTracks.ItemsSource = null;
        LiveSubtitleTracks.ItemsSource = null;
    }

    private void OnLiveHandleReady(object? sender, EventArgs e)
    {
        if (LiveVideoHost.NativeHandle != 0) _liveRenderHandle.TrySetResult(LiveVideoHost.NativeHandle);
    }

    private void OnLivePlayerStateChanged(object? sender, PlayerStateChangedEventArgs e) => Dispatcher.BeginInvoke(() =>
    {
        if (!_liveSurfaceActive) return;
        if (e.State is PlayerState.Loading or PlayerState.Playing) _liveSoftStopped = false;
        var state = _liveSoftStopped && e.State == PlayerState.Paused ? PlayerState.Stopped : e.State;
        _liveState = state;
        LivePlayPauseButton.Content = state is PlayerState.Paused or PlayerState.Stopped ? "Lecture" : "Pause";
        LiveStatusText.Text = e.SafeMessage ?? state switch
        {
            PlayerState.Loading => "Chargement…",
            PlayerState.Playing => "Lecture",
            PlayerState.Paused => "Pause",
            PlayerState.Stopped => "Arrêté",
            PlayerState.Error => "Erreur du lecteur",
            _ => "Prêt"
        };
    });

    private void OnLiveTrackListChanged(object? sender, TrackListChangedEventArgs e) => Dispatcher.BeginInvoke(() =>
    {
        if (!_liveSurfaceActive) return;
        _updatingLiveTracks = true;
        try
        {
            var audio = e.Tracks.Where(track => track.Type == MediaTrackType.Audio).Select(LiveTrackChoice.From).ToArray();
            LiveAudioTracks.ItemsSource = audio;
            LiveAudioTracks.SelectedItem = audio.FirstOrDefault(choice => choice.Selected) ?? audio.FirstOrDefault();
            LiveAudioTracks.IsEnabled = audio.Length > 0;

            var subtitles = new[] { new LiveTrackChoice(null, "Aucun", false) }
                .Concat(e.Tracks.Where(track => track.Type == MediaTrackType.Subtitle).Select(LiveTrackChoice.From)).ToArray();
            LiveSubtitleTracks.ItemsSource = subtitles;
            LiveSubtitleTracks.SelectedItem = subtitles.FirstOrDefault(choice => choice.Selected) ?? subtitles[0];
            LiveSubtitleTracks.IsEnabled = subtitles.Length > 1;
        }
        finally { _updatingLiveTracks = false; }
    });

    private void OnLivePlayPause(object sender, RoutedEventArgs e)
    {
        if (!_liveSurfaceActive) return;
        if (_liveState is PlayerState.Paused or PlayerState.Stopped)
        {
            _liveSoftStopped = false;
            _player.Play();
        }
        else
        {
            _player.Pause();
        }
    }

    private void OnLiveStop(object sender, RoutedEventArgs e)
    {
        if (!_liveSurfaceActive || _liveState is PlayerState.Idle or PlayerState.Error) return;
        _liveSoftStopped = true;
        _player.Stop();
        _liveState = PlayerState.Stopped;
        LivePlayPauseButton.Content = "Lecture";
        LiveStatusText.Text = "Arrêté";
    }

    private void OnLiveVolumeChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (IsLoaded && _liveSurfaceActive) _player.SetVolume(e.NewValue);
    }

    private void OnLiveAudioTrackChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_updatingLiveTracks && LiveAudioTracks.SelectedItem is LiveTrackChoice { Id: long id })
            _player.SelectAudioTrack(id);
    }

    private void OnLiveSubtitleTrackChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingLiveTracks || LiveSubtitleTracks.SelectedItem is not LiveTrackChoice choice) return;
        if (choice.Id is long id) _player.SelectSubtitleTrack(id);
        else _player.SetSubtitleEnabled(false);
    }

    private void OnLiveFullscreen(object sender, RoutedEventArgs e) => SetLiveFullscreen(!_liveFullscreenBehavior.IsFullscreen);

    private void SetLiveFullscreen(bool fullscreen)
    {
        if (!_liveSurfaceActive || _liveFullscreenBehavior.IsFullscreen == fullscreen) return;
        if (fullscreen)
        {
            if (!_liveFullscreenBehavior.Enter())
            {
                LiveStatusText.Text = "Impossible d’activer le plein écran";
                return;
            }
            BrowserHost.Visibility = Visibility.Collapsed;
            BrowserColumn.Width = new GridLength(0);
            LivePlayerColumn.Width = new GridLength(1, GridUnitType.Star);
            LivePlayerPane.BorderThickness = new Thickness(0);
            LivePlayerPane.Margin = new Thickness(0);
            LiveFullscreenButton.Content = "Quitter";
        }
        else
        {
            BrowserHost.Visibility = Visibility.Visible;
            BrowserColumn.Width = new GridLength(LiveBrowserWidth);
            LivePlayerColumn.Width = new GridLength(1, GridUnitType.Star);
            LivePlayerPane.BorderThickness = new Thickness(1, 0, 0, 0);
            LivePlayerPane.Margin = new Thickness(0, _liveSurfaceTop, 0, 0);
            _ = _liveFullscreenBehavior.Exit();
            LiveFullscreenButton.Content = "Plein écran";
        }
        _player.SetFullscreen(fullscreen);
    }

    private void OnMainWindowPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (!_liveSurfaceActive) return;
        if (e.Key == Key.Escape && _liveFullscreenBehavior.IsFullscreen)
        {
            SetLiveFullscreen(false);
            e.Handled = true;
        }
        else if (e.Key == Key.F11)
        {
            SetLiveFullscreen(!_liveFullscreenBehavior.IsFullscreen);
            e.Handled = true;
        }
        else if (e.Key == Key.Space && Keyboard.FocusedElement is not ComboBox)
        {
            OnLivePlayPause(sender, new RoutedEventArgs());
            e.Handled = true;
        }
    }

    private void OnMainWindowClosed(object? sender, EventArgs e)
    {
        LiveVideoHost.HandleReady -= OnLiveHandleReady;
        _player.StateChanged -= OnLivePlayerStateChanged;
        _player.TrackListChanged -= OnLiveTrackListChanged;
    }

    private static string CleanLiveTitle(string? value)
    {
        var title = new string((value ?? string.Empty).Where(character => !char.IsControl(character)).ToArray()).Trim();
        return title.Length switch { 0 => "Chaîne Live", > 180 => title[..180].TrimEnd(), _ => title };
    }

    private sealed record LiveTrackChoice(long? Id, string Label, bool Selected)
    {
        public static LiveTrackChoice From(MediaTrack track)
        {
            var language = string.IsNullOrWhiteSpace(track.Language) ? "Audio" : track.Language.Trim().ToUpperInvariant();
            var title = string.IsNullOrWhiteSpace(track.Title) ? null : track.Title.Trim();
            return new(track.Id, title is null ? language : $"{language} · {title}", track.Selected);
        }

        public override string ToString() => Label;
    }
}
