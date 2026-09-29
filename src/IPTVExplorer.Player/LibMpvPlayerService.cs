using System.Globalization;
using IPTVExplorer.Core;

namespace IPTVExplorer.Player;

public enum LibMpvFailureReason
{
    RuntimeMissing,
    UnsupportedPlatform,
    NativeDependencyMissing,
    ArchitectureMismatch,
    IncompatibleRuntime,
    LoadFailure
}

public sealed class LibMpvUnavailableException(LibMpvFailureReason reason, string message) : InvalidOperationException(message)
{
    public LibMpvFailureReason Reason { get; } = reason;
}

public sealed class LibMpvPlayerService : IPlayerService, IDisposable
{
    private static readonly TimeSpan TrackChangeResumeWindow = TimeSpan.FromSeconds(15);

    private readonly ILibMpvApiFactory _apiFactory;
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private readonly object _sync = new();
    private ILibMpvApi? _api;
    private nint _handle;
    private nint _renderHostHandle;
    private CancellationTokenSource? _eventCancellation;
    private Task? _eventLoop;
    private IReadOnlyList<MediaTrack> _tracks = [];
    private TimeSpan _position;
    private TimeSpan? _duration;
    private bool _userPaused;
    private bool _playbackActive;
    private bool _pausedForCache;
    private DateTime _trackChangeResumeUntilUtc = DateTime.MinValue;
    private int _disposed;

    public LibMpvPlayerService() : this(new LibMpvApiFactory(new LibMpvLibraryLocator())) { }

    internal LibMpvPlayerService(ILibMpvApiFactory apiFactory) => _apiFactory = apiFactory;

    public event EventHandler<PlayerStateChangedEventArgs>? StateChanged;
    public event EventHandler<PlayerPositionChangedEventArgs>? PositionChanged;
    public event EventHandler<TrackListChangedEventArgs>? TrackListChanged;

    public async Task LoadAsync(ResolvedMedia media, nint renderHostHandle, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(media);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (renderHostHandle == 0) throw new ArgumentException("A native video surface is required.", nameof(renderHostHandle));
        if (IntPtr.Size != 8) throw new PlatformNotSupportedException("Le lecteur libmpv nécessite une application x64.");

        lock (_sync)
        {
            _userPaused = false;
            _playbackActive = false;
            _pausedForCache = false;
            _trackChangeResumeUntilUtc = DateTime.MinValue;
            _position = TimeSpan.Zero;
            _duration = null;
        }

        RaiseState(PlayerState.Loading);
        await _lifecycle.WaitAsync(cancellationToken);
        try
        {
            await EnsureEngineAsync(renderHostHandle, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            var (api, handle) = RequiredEngine();
            var headers = FormatHeaders(media.Headers);
            if (api.LoadFile(handle, media.Uri.AbsoluteUri, headers) < 0)
                throw new InvalidOperationException("Impossible de charger le média dans libmpv.");
        }
        catch (LibMpvUnavailableException exception)
        {
            RaiseState(PlayerState.Error, exception.Message);
            throw;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is not InvalidOperationException)
        {
            const string message = "Impossible d'initialiser le moteur vidéo libmpv.";
            RaiseState(PlayerState.Error, message);
            throw new InvalidOperationException(message);
        }
        catch (InvalidOperationException exception)
        {
            RaiseState(PlayerState.Error, exception.Message);
            throw;
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    public void Play()
    {
        lock (_sync)
        {
            _userPaused = false;
            _trackChangeResumeUntilUtc = DateTime.MinValue;
        }
        SetProperty("pause", "no");
    }

    public void Pause()
    {
        lock (_sync)
        {
            _userPaused = true;
            _trackChangeResumeUntilUtc = DateTime.MinValue;
        }
        SetProperty("pause", "yes");
    }

    public void Stop()
    {
        MarkPlaybackInactive();
        Execute((api, handle) => api.Command(handle, "stop"));
        RaiseState(PlayerState.Stopped);
    }

    public void Seek(TimeSpan position) => SetProperty("time-pos", Math.Max(0, position.TotalSeconds).ToString("0.###", CultureInfo.InvariantCulture));
    public void SetVolume(double volume) => SetProperty("volume", Math.Clamp(volume, 0, 100).ToString("0.###", CultureInfo.InvariantCulture));

    public IReadOnlyList<MediaTrack> GetTracks()
    {
        lock (_sync) return _tracks.ToArray();
    }

    public void SelectAudioTrack(long id) => SelectTrack("aid", id);
    public void SelectVideoTrack(long id) => SelectTrack("vid", id);
    public void SelectSubtitleTrack(long id) => SelectTrack("sid", id);
    public void SetSubtitleEnabled(bool enabled) => ChangeTrackProperty("sid", enabled ? "auto" : "no");

    // Embedded playback is made fullscreen by the owning WPF window, never by a second mpv window.
    public void SetFullscreen(bool fullscreen) { }

    private void SelectTrack(string property, long id)
    {
        if (id < 0) throw new ArgumentOutOfRangeException(nameof(id));
        ChangeTrackProperty(property, id.ToString(CultureInfo.InvariantCulture));
    }

    private void ChangeTrackProperty(string property, string value)
    {
        bool resumeAfterChange;
        lock (_sync)
        {
            resumeAfterChange = _playbackActive && !_userPaused;
            _trackChangeResumeUntilUtc = resumeAfterChange ? DateTime.UtcNow + TrackChangeResumeWindow : DateTime.MinValue;
        }

        SetProperty(property, value);
        if (resumeAfterChange) SetProperty("pause", "no");
    }

    private void SetProperty(string name, string value) => Execute((api, handle) => api.SetPropertyString(handle, name, value));

    private void Execute(Func<ILibMpvApi, nint, int> operation)
    {
        ILibMpvApi? api;
        nint handle;
        lock (_sync) { api = _api; handle = _handle; }
        if (api is null || handle == 0) return;
        try
        {
            if (operation(api, handle) < 0) RaiseState(PlayerState.Error, "La commande du lecteur a échoué.");
        }
        catch (ObjectDisposedException) { }
    }

    private async Task EnsureEngineAsync(nint renderHostHandle, CancellationToken cancellationToken)
    {
        lock (_sync)
        {
            if (_api is not null && _handle != 0 && _renderHostHandle == renderHostHandle) return;
        }

        await ShutdownEngineAsync();
        cancellationToken.ThrowIfCancellationRequested();

        var api = _apiFactory.Create();
        var handle = api.Create();
        if (handle == 0)
        {
            api.Dispose();
            throw new InvalidOperationException("Impossible de créer le moteur vidéo libmpv.");
        }

        try
        {
            RequiredSuccess(api.SetOptionString(handle, "config", "no"));
            RequiredSuccess(api.SetOptionString(handle, "terminal", "no"));
            RequiredSuccess(api.SetOptionString(handle, "input-default-bindings", "no"));
            RequiredSuccess(api.SetOptionString(handle, "input-cursor", "no"));
            var windowId = unchecked((uint)renderHostHandle.ToInt64());
            RequiredSuccess(api.SetOptionString(handle, "wid", windowId.ToString(CultureInfo.InvariantCulture)));
            RequiredSuccess(api.Initialize(handle));
            RequiredSuccess(api.ObserveProperty(handle, 1, "pause", MpvFormat.Flag));
            RequiredSuccess(api.ObserveProperty(handle, 2, "idle-active", MpvFormat.Flag));
            RequiredSuccess(api.ObserveProperty(handle, 3, "time-pos", MpvFormat.Double));
            RequiredSuccess(api.ObserveProperty(handle, 4, "duration", MpvFormat.Double));
            RequiredSuccess(api.ObserveProperty(handle, 5, "track-list", MpvFormat.Node));
            RequiredSuccess(api.ObserveProperty(handle, 6, "paused-for-cache", MpvFormat.Flag));
        }
        catch
        {
            api.TerminateDestroy(handle);
            api.Dispose();
            throw;
        }

        var eventCancellation = new CancellationTokenSource();
        lock (_sync)
        {
            _api = api;
            _handle = handle;
            _renderHostHandle = renderHostHandle;
            _eventCancellation = eventCancellation;
            _eventLoop = Task.Run(() => EventLoop(api, handle, eventCancellation.Token), CancellationToken.None);
        }
    }

    private static void RequiredSuccess(int result)
    {
        if (result < 0) throw new InvalidOperationException("Impossible d'initialiser le moteur vidéo libmpv.");
    }

    private async Task EventLoop(ILibMpvApi api, nint handle, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var value = api.WaitEvent(handle, 0.1);
                switch (value.Kind)
                {
                    case MpvEventKind.None:
                        await Task.Yield();
                        break;
                    case MpvEventKind.FileLoaded:
                    {
                        bool userPaused;
                        lock (_sync)
                        {
                            _playbackActive = true;
                            _pausedForCache = false;
                            userPaused = _userPaused;
                        }
                        RaiseState(userPaused ? PlayerState.Paused : PlayerState.Playing);
                        break;
                    }
                    case MpvEventKind.EndFile:
                        MarkPlaybackInactive();
                        RaiseState(PlayerState.Stopped);
                        break;
                    case MpvEventKind.Shutdown:
                        MarkPlaybackInactive();
                        RaiseState(PlayerState.Stopped);
                        return;
                    case MpvEventKind.PropertyChange:
                        ApplyProperty(value.PropertyName, value.Value);
                        break;
                }
            }
        }
        catch when (cancellationToken.IsCancellationRequested) { }
        catch
        {
            RaiseState(PlayerState.Error, "Le moteur vidéo libmpv a rencontré une erreur.");
        }
    }

    private void ApplyProperty(string? name, object? value)
    {
        switch (name)
        {
            case "pause" when value is bool paused:
            {
                bool userPaused;
                bool buffering;
                bool autoResume;
                lock (_sync)
                {
                    userPaused = _userPaused;
                    buffering = _pausedForCache;
                    autoResume = paused && ShouldAutoResumeTrackChangeLocked();
                }

                if (autoResume)
                {
                    SetProperty("pause", "no");
                    RaiseState(buffering ? PlayerState.Loading : PlayerState.Playing, buffering ? "Mise en mémoire tampon…" : null);
                }
                else if (buffering && !userPaused)
                {
                    RaiseState(PlayerState.Loading, "Mise en mémoire tampon…");
                }
                else if (userPaused)
                {
                    RaiseState(PlayerState.Paused);
                }
                else
                {
                    RaiseState(paused ? PlayerState.Paused : PlayerState.Playing);
                }
                break;
            }
            case "paused-for-cache" when value is bool buffering:
            {
                bool userPaused;
                bool autoResume;
                lock (_sync)
                {
                    _pausedForCache = buffering;
                    userPaused = _userPaused;
                    autoResume = !buffering && ShouldAutoResumeTrackChangeLocked();
                }

                if (buffering && !userPaused)
                {
                    RaiseState(PlayerState.Loading, "Mise en mémoire tampon…");
                }
                else if (userPaused)
                {
                    RaiseState(PlayerState.Paused);
                }
                else
                {
                    if (autoResume) SetProperty("pause", "no");
                    RaiseState(PlayerState.Playing);
                }
                break;
            }
            case "idle-active" when value is true:
                MarkPlaybackInactive();
                RaiseState(PlayerState.Idle);
                break;
            case "time-pos":
                lock (_sync) _position = Seconds(value) ?? TimeSpan.Zero;
                RaisePosition();
                break;
            case "duration":
                lock (_sync) _duration = Seconds(value);
                RaisePosition();
                break;
            case "track-list" when value is IReadOnlyList<MediaTrack> tracks:
                lock (_sync) _tracks = tracks.ToArray();
                TrackListChanged?.Invoke(this, new(_tracks));
                break;
        }
    }

    private bool ShouldAutoResumeTrackChangeLocked() =>
        _playbackActive && !_userPaused && DateTime.UtcNow <= _trackChangeResumeUntilUtc;

    private void MarkPlaybackInactive()
    {
        lock (_sync)
        {
            _playbackActive = false;
            _pausedForCache = false;
            _trackChangeResumeUntilUtc = DateTime.MinValue;
        }
    }

    private static TimeSpan? Seconds(object? value) => value is double seconds && double.IsFinite(seconds) && seconds >= 0 ? TimeSpan.FromSeconds(seconds) : null;

    private void RaisePosition()
    {
        TimeSpan position;
        TimeSpan? duration;
        lock (_sync) { position = _position; duration = _duration; }
        PositionChanged?.Invoke(this, new(position, duration));
    }

    private void RaiseState(PlayerState state, string? safeMessage = null) => StateChanged?.Invoke(this, new(state, safeMessage));

    private (ILibMpvApi Api, nint Handle) RequiredEngine()
    {
        lock (_sync)
        {
            if (_api is null || _handle == 0) throw new InvalidOperationException("Le moteur vidéo libmpv n'est pas disponible.");
            return (_api, _handle);
        }
    }

    private static string? FormatHeaders(IReadOnlyDictionary<string, string>? headers)
    {
        if (headers is null || headers.Count == 0) return null;
        var values = new List<string>(headers.Count);
        foreach (var (name, value) in headers)
        {
            if (string.IsNullOrWhiteSpace(name) || name.Any(character => char.IsControl(character) || character is ':' or ','))
                throw new InvalidOperationException("Les en-têtes HTTP du média sont invalides.");
            if (value.Any(character => character is '\r' or '\n' or '\0'))
                throw new InvalidOperationException("Les en-têtes HTTP du média sont invalides.");
            values.Add($"{name}: {EscapeListValue(value)}");
        }
        return string.Join(',', values);
    }

    private static string EscapeListValue(string value) => value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace(",", "\\,", StringComparison.Ordinal);

    private async Task ShutdownEngineAsync()
    {
        ILibMpvApi? api;
        nint handle;
        CancellationTokenSource? cancellation;
        Task? eventLoop;
        lock (_sync)
        {
            api = _api;
            handle = _handle;
            cancellation = _eventCancellation;
            eventLoop = _eventLoop;
            _api = null;
            _handle = 0;
            _renderHostHandle = 0;
            _eventCancellation = null;
            _eventLoop = null;
            _tracks = [];
            _userPaused = false;
            _playbackActive = false;
            _pausedForCache = false;
            _trackChangeResumeUntilUtc = DateTime.MinValue;
        }

        if (api is null) return;
        cancellation?.Cancel();
        if (handle != 0) api.Wakeup(handle);
        if (eventLoop is not null)
        {
            try { await eventLoop; }
            catch (OperationCanceledException) { }
        }
        if (handle != 0) api.TerminateDestroy(handle);
        api.Dispose();
        cancellation?.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        await _lifecycle.WaitAsync();
        try { await ShutdownEngineAsync(); }
        finally { _lifecycle.Release(); _lifecycle.Dispose(); }
    }

    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();
}
