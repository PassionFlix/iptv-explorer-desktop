using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using IPTVExplorer.Core;
using IPTVExplorer.Player;

namespace IPTVExplorer.Desktop;

public partial class PlayerWindow : Window
{
    private readonly IPlayerService _player;
    private readonly TrueFullscreenBehavior _fullscreenBehavior;
    private readonly TaskCompletionSource<nint> _renderHandle = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _seeking;
    private bool _updatingTracks;
    private bool _updatingEpisodes;
    private PlayerState _state = PlayerState.Idle;
    private Func<PlayerEpisodeOption, CancellationToken, Task>? _episodeSelectionHandler;
    private CancellationTokenSource? _episodeSelectionCancellation;

    public PlayerWindow(IPlayerService player)
    {
        _player = player;
        InitializeComponent();
        _fullscreenBehavior = new(this);
        VideoHost.HandleReady += OnHandleReady;
        _player.StateChanged += OnStateChanged;
        _player.PositionChanged += OnPositionChanged;
        _player.TrackListChanged += OnTrackListChanged;
    }

    public Task<nint> WaitForRenderHandleAsync(CancellationToken cancellationToken) => _renderHandle.Task.WaitAsync(cancellationToken);

    public void ConfigureEpisodes(
        PlayerSeriesContext? context,
        Func<PlayerEpisodeOption, CancellationToken, Task>? selectionHandler)
    {
        _updatingEpisodes = true;
        try
        {
            _episodeSelectionCancellation?.Cancel();
            _episodeSelectionCancellation?.Dispose();
            _episodeSelectionCancellation = null;
            _episodeSelectionHandler = selectionHandler;

            if (context is null || context.Episodes.Count == 0 || selectionHandler is null)
            {
                EpisodeSelector.ItemsSource = null;
                EpisodePanel.Visibility = Visibility.Collapsed;
                Title = "Lecteur — IPTV Explorer";
                return;
            }

            EpisodeSelector.ItemsSource = context.Episodes;
            EpisodeSelector.SelectedItem = context.Episodes.FirstOrDefault(episode =>
                string.Equals(episode.Id, context.SelectedEpisodeId, StringComparison.Ordinal)) ?? context.Episodes[0];
            EpisodePanel.Visibility = Visibility.Visible;
            Title = $"Lecteur — {context.SeriesTitle}";
        }
        finally
        {
            _updatingEpisodes = false;
        }
    }

    private void OnHandleReady(object? sender, EventArgs e)
    {
        if (VideoHost.NativeHandle != 0) _renderHandle.TrySetResult(VideoHost.NativeHandle);
    }

    private void OnStateChanged(object? sender, PlayerStateChangedEventArgs e) => Dispatcher.BeginInvoke(() =>
    {
        _state = e.State;
        PlayPauseButton.Content = e.State == PlayerState.Paused ? "Lire" : "Pause";
        StatusText.Text = e.SafeMessage ?? e.State switch
        {
            PlayerState.Loading => "Chargement…",
            PlayerState.Playing => "Lecture",
            PlayerState.Paused => "Pause",
            PlayerState.Stopped => "Arrêté",
            PlayerState.Error => "Erreur du lecteur",
            _ => "Prêt"
        };
    });

    private void OnPositionChanged(object? sender, PlayerPositionChangedEventArgs e) => Dispatcher.BeginInvoke(() =>
    {
        var duration = e.Duration ?? TimeSpan.Zero;
        if (!_seeking)
        {
            PositionSlider.Maximum = Math.Max(1, duration.TotalSeconds);
            PositionSlider.Value = Math.Clamp(e.Position.TotalSeconds, 0, PositionSlider.Maximum);
        }
        PositionLabel.Text = FormatTime(e.Position);
        DurationLabel.Text = $"/ {FormatTime(duration)}";
    });

    private void OnTrackListChanged(object? sender, TrackListChangedEventArgs e) => Dispatcher.BeginInvoke(() =>
    {
        _updatingTracks = true;
        try
        {
            var audio = e.Tracks.Where(track => track.Type == MediaTrackType.Audio).Select(TrackChoice.From).ToArray();
            AudioTracks.ItemsSource = audio;
            AudioTracks.SelectedItem = audio.FirstOrDefault(choice => choice.Selected) ?? audio.FirstOrDefault();

            var subtitles = new[] { new TrackChoice(null, "Aucun", false) }
                .Concat(e.Tracks.Where(track => track.Type == MediaTrackType.Subtitle).Select(TrackChoice.From)).ToArray();
            SubtitleTracks.ItemsSource = subtitles;
            SubtitleTracks.SelectedItem = subtitles.FirstOrDefault(choice => choice.Selected) ?? subtitles[0];
        }
        finally { _updatingTracks = false; }
    });

    private void OnPlayPause(object sender, RoutedEventArgs e)
    {
        TogglePlayPause();
    }

    private void TogglePlayPause()
    {
        if (_state == PlayerState.Paused) _player.Play(); else _player.Pause();
    }

    private void OnStop(object sender, RoutedEventArgs e) => _player.Stop();
    private void OnSeekStarted(object sender, MouseButtonEventArgs e) => _seeking = true;
    private void OnSeekCompleted(object sender, MouseButtonEventArgs e) { _player.Seek(TimeSpan.FromSeconds(PositionSlider.Value)); _seeking = false; }
    private void OnVolumeChanged(object sender, RoutedPropertyChangedEventArgs<double> e) { if (IsLoaded) _player.SetVolume(e.NewValue); }

    private async void OnEpisodeChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_updatingEpisodes || EpisodeSelector.SelectedItem is not PlayerEpisodeOption episode || _episodeSelectionHandler is null) return;

        _episodeSelectionCancellation?.Cancel();
        _episodeSelectionCancellation?.Dispose();
        var cancellation = new CancellationTokenSource();
        _episodeSelectionCancellation = cancellation;
        EpisodeSelector.IsEnabled = false;
        StatusText.Text = "Chargement de l’épisode…";

        try
        {
            await _episodeSelectionHandler(episode, cancellation.Token);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        catch
        {
            StatusText.Text = "Impossible de charger l’épisode";
        }
        finally
        {
            if (ReferenceEquals(_episodeSelectionCancellation, cancellation))
            {
                _episodeSelectionCancellation = null;
                EpisodeSelector.IsEnabled = true;
            }
            cancellation.Dispose();
        }
    }

    private void OnAudioTrackChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (!_updatingTracks && AudioTracks.SelectedItem is TrackChoice { Id: long id }) _player.SelectAudioTrack(id);
    }

    private void OnSubtitleTrackChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_updatingTracks || SubtitleTracks.SelectedItem is not TrackChoice choice) return;
        if (choice.Id is long id) _player.SelectSubtitleTrack(id); else _player.SetSubtitleEnabled(false);
    }

    private void OnFullscreen(object sender, RoutedEventArgs e) => SetFullscreen(!_fullscreenBehavior.IsFullscreen);

    private void SetFullscreen(bool fullscreen)
    {
        if (_fullscreenBehavior.IsFullscreen == fullscreen) return;
        var changed = fullscreen ? _fullscreenBehavior.Enter() : _fullscreenBehavior.Exit();
        if (!changed)
        {
            StatusText.Text = fullscreen ? "Impossible d’activer le plein écran" : "Impossible de quitter le plein écran";
            return;
        }

        FullscreenButton.Content = fullscreen ? "Quitter le plein écran" : "Plein écran";
        _player.SetFullscreen(fullscreen);
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && _fullscreenBehavior.IsFullscreen)
        {
            SetFullscreen(false);
            e.Handled = true;
            return;
        }

        if (e.Key == Key.F11)
        {
            SetFullscreen(!_fullscreenBehavior.IsFullscreen);
            e.Handled = true;
        }
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Space && Keyboard.FocusedElement is not System.Windows.Controls.ComboBox)
        {
            TogglePlayPause();
            e.Handled = true;
        }
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        _episodeSelectionCancellation?.Cancel();
        _episodeSelectionCancellation?.Dispose();
        _episodeSelectionCancellation = null;
        _player.Stop();
        _player.StateChanged -= OnStateChanged;
        _player.PositionChanged -= OnPositionChanged;
        _player.TrackListChanged -= OnTrackListChanged;
        if (!_renderHandle.Task.IsCompleted) _renderHandle.TrySetException(new InvalidOperationException("La surface vidéo native a été fermée."));
    }

    private static string FormatTime(TimeSpan value) => value.TotalHours >= 1 ? value.ToString(@"hh\:mm\:ss") : value.ToString(@"mm\:ss");

    private sealed record TrackChoice(long? Id, string Label, bool Selected)
    {
        public static TrackChoice From(MediaTrack track)
        {
            var language = FriendlyLanguage(track.Language);
            var details = new List<string>();
            var title = CleanTrackTitle(track.Title, track.Language);
            if (title is not null) details.Add(title);
            if (!string.IsNullOrWhiteSpace(track.Codec)) details.Add(track.Codec.ToUpperInvariant());
            if (track.Channels is int channels && channels > 0)
            {
                details.Add(channels switch
                {
                    1 => "Mono",
                    2 => "Stéréo",
                    _ => $"{channels} canaux"
                });
            }

            var label = details.Count == 0 ? language : $"{language} · {string.Join(" · ", details)}";
            return new(track.Id, label, track.Selected);
        }

        private static string FriendlyLanguage(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return "Langue indéterminée";
            return value.Trim().ToLowerInvariant() switch
            {
                "fr" or "fra" or "fre" => "Français",
                "en" or "eng" => "Anglais",
                "es" or "spa" => "Espagnol",
                "de" or "deu" or "ger" => "Allemand",
                "it" or "ita" => "Italien",
                "pt" or "por" => "Portugais",
                "ja" or "jpn" => "Japonais",
                "ko" or "kor" => "Coréen",
                "zh" or "chi" or "zho" => "Chinois",
                "ori" => "Version originale",
                "und" or "oth" => "Autre",
                _ => value.Trim().ToUpperInvariant()
            };
        }

        private static string? CleanTrackTitle(string? title, string? language)
        {
            if (string.IsNullOrWhiteSpace(title)) return null;
            var value = title.Trim();
            if (!string.IsNullOrWhiteSpace(language) && string.Equals(value, language.Trim(), StringComparison.OrdinalIgnoreCase)) return null;
            if (value.Equals("oth", StringComparison.OrdinalIgnoreCase) ||
                value.Equals("ori", StringComparison.OrdinalIgnoreCase) ||
                value.Equals("fra", StringComparison.OrdinalIgnoreCase) ||
                value.Equals("fre", StringComparison.OrdinalIgnoreCase) ||
                value.Equals("eng", StringComparison.OrdinalIgnoreCase)) return null;
            return value;
        }

        public override string ToString() => Label;
    }
}
