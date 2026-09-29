using IPTVExplorer.Core;

namespace IPTVExplorer.Player;

public enum PlayerState { Idle, Loading, Playing, Paused, Stopped, Error }
public sealed record PlayerStateChangedEventArgs(PlayerState State, string? SafeMessage = null);
public sealed record PlayerPositionChangedEventArgs(TimeSpan Position, TimeSpan? Duration);
public sealed record TrackListChangedEventArgs(IReadOnlyList<MediaTrack> Tracks);

public interface IPlayerService : IAsyncDisposable
{
    event EventHandler? MediaLoaded;
    event EventHandler<PlayerStateChangedEventArgs>? StateChanged;
    event EventHandler<PlayerPositionChangedEventArgs>? PositionChanged;
    event EventHandler<TrackListChangedEventArgs>? TrackListChanged;

    Task LoadAsync(ResolvedMedia media, nint renderHostHandle, CancellationToken cancellationToken = default);
    void Play();
    void Pause();
    void Stop();
    void Seek(TimeSpan position);
    void SetVolume(double volume);
    IReadOnlyList<MediaTrack> GetTracks();
    void SelectAudioTrack(long id);
    void SelectVideoTrack(long id);
    void SelectSubtitleTrack(long id);
    void SetSubtitleEnabled(bool enabled);
    void SetFullscreen(bool fullscreen);
}

/// <summary>Phase-one fail-closed placeholder. It never launches an external process.</summary>
public sealed class PlayerNotInstalledService : IPlayerService
{
    public event EventHandler? MediaLoaded;
    public event EventHandler<PlayerStateChangedEventArgs>? StateChanged;
    public event EventHandler<PlayerPositionChangedEventArgs>? PositionChanged;
    public event EventHandler<TrackListChangedEventArgs>? TrackListChanged;

    public Task LoadAsync(ResolvedMedia media, nint renderHostHandle, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(media);
        StateChanged?.Invoke(this, new(PlayerState.Error, "Native player is not installed in phase one."));
        return Task.CompletedTask;
    }
    public void Play() { }
    public void Pause() { }
    public void Stop() { }
    public void Seek(TimeSpan position) { }
    public void SetVolume(double volume) { }
    public IReadOnlyList<MediaTrack> GetTracks() => [];
    public void SelectAudioTrack(long id) { }
    public void SelectVideoTrack(long id) { }
    public void SelectSubtitleTrack(long id) { }
    public void SetSubtitleEnabled(bool enabled) { }
    public void SetFullscreen(bool fullscreen) { }
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    // Keep explicit event raisers referenced so strict builds do not flag unused event backing fields.
    internal void NotifyMediaLoaded() => MediaLoaded?.Invoke(this, EventArgs.Empty);
    internal void NotifyPosition(TimeSpan position) => PositionChanged?.Invoke(this, new(position, null));
    internal void NotifyTracks(IReadOnlyList<MediaTrack> tracks) => TrackListChanged?.Invoke(this, new(tracks));
}
