using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using IPTVExplorer.Core;
using IPTVExplorer.Player;

namespace IPTVExplorer.Desktop;

public partial class PlayerWindow : Window
{
    private readonly IPlayerService _player;
    private readonly TaskCompletionSource<nint> _renderHandle = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _seeking;
    private bool _updatingTracks;
    private bool _fullscreen;
    private WindowStyle _savedStyle;
    private ResizeMode _savedResizeMode;
    private WindowState _savedState;
    private PlayerState _state = PlayerState.Idle;

    public PlayerWindow(IPlayerService player)
    {
        _player = player;
        InitializeComponent();
        VideoHost.HandleReady += OnHandleReady;
        _player.StateChanged += OnStateChanged;
        _player.PositionChanged += OnPositionChanged;
        _player.TrackListChanged += OnTrackListChanged;
    }

    public Task<nint> WaitForRenderHandleAsync(CancellationToken cancellationToken) => _renderHandle.Task.WaitAsync(cancellationToken);

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
        if (_state == PlayerState.Paused) _player.Play(); else _player.Pause();
    }

    private void OnStop(object sender, RoutedEventArgs e) => _player.Stop();
    private void OnSeekStarted(object sender, MouseButtonEventArgs e) => _seeking = true;
    private void OnSeekCompleted(object sender, MouseButtonEventArgs e) { _player.Seek(TimeSpan.FromSeconds(PositionSlider.Value)); _seeking = false; }
    private void OnVolumeChanged(object sender, RoutedPropertyChangedEventArgs<double> e) { if (IsLoaded) _player.SetVolume(e.NewValue); }

    private void OnAudioTrackChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (!_updatingTracks && AudioTracks.SelectedItem is TrackChoice { Id: long id }) _player.SelectAudioTrack(id);
    }

    private void OnSubtitleTrackChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_updatingTracks || SubtitleTracks.SelectedItem is not TrackChoice choice) return;
        if (choice.Id is long id) _player.SelectSubtitleTrack(id); else _player.SetSubtitleEnabled(false);
    }

    private void OnFullscreen(object sender, RoutedEventArgs e) => SetFullscreen(!_fullscreen);

    private void SetFullscreen(bool fullscreen)
    {
        if (_fullscreen == fullscreen) return;
        if (fullscreen)
        {
            _savedStyle = WindowStyle;
            _savedResizeMode = ResizeMode;
            _savedState = WindowState;
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            WindowState = WindowState.Maximized;
        }
        else
        {
            WindowStyle = _savedStyle;
            ResizeMode = _savedResizeMode;
            WindowState = _savedState;
        }
        _fullscreen = fullscreen;
        _player.SetFullscreen(fullscreen);
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && _fullscreen) SetFullscreen(false);
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
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
            var language = string.IsNullOrWhiteSpace(track.Language) ? "indéterminée" : track.Language;
            var title = string.IsNullOrWhiteSpace(track.Title) ? string.Empty : $" — {track.Title}";
            return new(track.Id, $"{language}{title}", track.Selected);
        }

        public override string ToString() => Label;
    }
}
