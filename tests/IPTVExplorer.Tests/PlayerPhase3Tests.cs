using System.Collections.Concurrent;
using System.Text.Json;
using IPTVExplorer.Core;
using IPTVExplorer.Infrastructure;
using IPTVExplorer.Player;
using IPTVExplorer.Providers;

namespace IPTVExplorer.Tests;

public sealed class PlayerPhase3Tests
{
    [Fact]
    public async Task PlaybackCoordinatorPassesNativeHandleWithoutReturningResolvedUrl()
    {
        await using var database = await TestDatabase.CreateAsync();
        var secrets = new InMemorySecretStore();
        var secretReference = await secrets.PutAsync(new ProviderSecret("user-demo", "password-demo"));
        var provider = await database.AddProviderAsync(ProviderType.Xtream, secretReference);
        await database.Repository.SetEnabledAsync(provider.Key, true);
        var player = new RecordingPlayerService();
        var windows = new RecordingWindowManager((nint)4242);
        var history = new PlaybackHistoryRepository(database.Connections);
        await using var coordinator = new PlaybackCoordinator(database.Repository, new ProviderClientFactory(new StubHttpClientFactory(), secrets), history, player, windows);

        var result = await coordinator.OpenAsync(new MediaReference(provider.Key, CatalogType.Vod, "movie-42", Extension: "mkv"));

        Assert.Equal((nint)4242, player.RenderHostHandle);
        Assert.Equal((nint)4242, windows.Handle);
        Assert.NotNull(player.Media);
        var serialized = JsonSerializer.Serialize(result);
        Assert.DoesNotContain("https://", serialized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("user-demo", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("password-demo", serialized, StringComparison.Ordinal);
        Assert.Equal("opened", result.State);
    }

    [Fact]
    public async Task ResumeMovieSeeksToStoredSecondsOnlyAfterMediaLoaded()
    {
        await using var database = await TestDatabase.CreateAsync();
        var secrets = new InMemorySecretStore();
        var secretReference = await secrets.PutAsync(new ProviderSecret("user-demo", "password-demo"));
        var provider = await database.AddProviderAsync(ProviderType.Xtream, secretReference);
        await database.Repository.SetEnabledAsync(provider.Key, true);
        var history = new PlaybackHistoryRepository(database.Connections);
        await history.UpsertAsync(new PlaybackProgress(
            provider.Key,
            CatalogType.Vod,
            "movie-42",
            null,
            "Fixture film",
            null,
            null,
            null,
            "https://images.example.invalid/poster.jpg",
            "mkv",
            TimeSpan.FromSeconds(550),
            TimeSpan.FromMinutes(90),
            DateTimeOffset.UtcNow));
        var player = new RecordingPlayerService();
        var windows = new RecordingWindowManager((nint)5252);
        await using var coordinator = new PlaybackCoordinator(database.Repository, new ProviderClientFactory(new StubHttpClientFactory(), secrets), history, player, windows);

        await coordinator.ResumeAsync(provider.Key, CatalogType.Vod, "movie-42");

        Assert.Null(player.SeekPosition);
        player.NotifyState(PlayerState.Playing);
        Assert.Null(player.SeekPosition);
        player.NotifyMediaLoaded();
        Assert.Equal(TimeSpan.FromSeconds(550), player.SeekPosition);
        Assert.Equal(550, player.SeekPosition?.TotalSeconds);
        Assert.Equal(1, player.PlayCalls);
        Assert.NotNull(player.Media);
        Assert.Contains("/movie/user-demo/password-demo/movie-42.mkv", player.Media.Uri.AbsoluteUri, StringComparison.Ordinal);
        var stored = JsonSerializer.Serialize(await history.GetAsync(provider.Key, CatalogType.Vod, "movie-42"));
        Assert.DoesNotContain("user-demo", stored, StringComparison.Ordinal);
        Assert.DoesNotContain("password-demo", stored, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OpenWithoutResumeNeverSeeksWhenMediaLoads()
    {
        await using var database = await TestDatabase.CreateAsync();
        var secrets = new InMemorySecretStore();
        var secretReference = await secrets.PutAsync(new ProviderSecret("user-demo", "password-demo"));
        var provider = await database.AddProviderAsync(ProviderType.Xtream, secretReference);
        await database.Repository.SetEnabledAsync(provider.Key, true);
        var player = new RecordingPlayerService();
        var windows = new RecordingWindowManager((nint)5353);
        var history = new PlaybackHistoryRepository(database.Connections);
        await using var coordinator = new PlaybackCoordinator(database.Repository, new ProviderClientFactory(new StubHttpClientFactory(), secrets), history, player, windows);

        await coordinator.OpenAsync(new MediaReference(provider.Key, CatalogType.Vod, "movie-43", Extension: "mp4"));
        player.NotifyMediaLoaded();

        Assert.Empty(player.SeekPositions);
        Assert.Equal(0, player.PlayCalls);
    }

    [Fact]
    public async Task ResumeEpisodeUsesItsOwnPositionAndEpisodeChangeDoesNotReuseIt()
    {
        await using var database = await TestDatabase.CreateAsync();
        var secrets = new InMemorySecretStore();
        var secretReference = await secrets.PutAsync(new ProviderSecret("user-demo", "password-demo"));
        var provider = await database.AddProviderAsync(ProviderType.Xtream, secretReference);
        await database.Repository.SetEnabledAsync(provider.Key, true);
        var history = new PlaybackHistoryRepository(database.Connections);
        await history.UpsertAsync(new PlaybackProgress(
            provider.Key,
            CatalogType.Series,
            "episode-501",
            "series-301",
            "Pilot",
            "Fixture series",
            1,
            1,
            "https://images.example.invalid/series.jpg",
            "mkv",
            TimeSpan.FromSeconds(125.5),
            TimeSpan.FromMinutes(42),
            DateTimeOffset.UtcNow));
        var player = new RecordingPlayerService();
        var windows = new RecordingWindowManager((nint)5454);
        var http = new StubHttpClientFactory(new SeriesDetailsHandler());
        await using var coordinator = new PlaybackCoordinator(database.Repository, new ProviderClientFactory(http, secrets), history, player, windows);

        await coordinator.ResumeAsync(provider.Key, CatalogType.Series, "episode-501");
        player.NotifyMediaLoaded();

        Assert.Equal([TimeSpan.FromSeconds(125.5)], player.SeekPositions);
        Assert.Contains("/series/user-demo/password-demo/episode-501.mkv", player.Media?.Uri.AbsoluteUri, StringComparison.Ordinal);

        await windows.SelectEpisodeAsync("episode-502");
        player.NotifyMediaLoaded();

        Assert.Equal([TimeSpan.FromSeconds(125.5)], player.SeekPositions);
        Assert.Contains("/series/user-demo/password-demo/episode-502.mkv", player.Media?.Uri.AbsoluteUri, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MissingLibMpvReturnsSafeUserError()
    {
        var root = Path.Combine(Path.GetTempPath(), "missing-libmpv", Guid.NewGuid().ToString("N"));
        await using var player = new LibMpvPlayerService(new LibMpvApiFactory(new LibMpvLibraryLocator(root)));

        var exception = await Assert.ThrowsAsync<LibMpvUnavailableException>(() =>
            player.LoadAsync(new ResolvedMedia(new Uri("https://media.example.invalid/private")), (nint)123));

        Assert.Equal(LibMpvFailureReason.RuntimeMissing, exception.Reason);
        Assert.Equal("Le runtime vidéo libmpv est absent du dossier de l’application.", exception.Message);
        Assert.DoesNotContain("media.example.invalid", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(126, LibMpvFailureReason.NativeDependencyMissing, "libmpv a été trouvé, mais une dépendance native requise est absente.")]
    [InlineData(193, LibMpvFailureReason.ArchitectureMismatch, "libmpv a été trouvé, mais son architecture est incompatible avec l’application x64.")]
    [InlineData(127, LibMpvFailureReason.IncompatibleRuntime, "libmpv a été trouvé, mais cette version est incompatible avec IPTV Explorer.")]
    [InlineData(1114, LibMpvFailureReason.LoadFailure, "libmpv a été trouvé, mais Windows n’a pas pu charger le runtime vidéo.")]
    public void LibMpvLoadErrorsProduceSpecificSafeDiagnostics(
        int errorCode,
        LibMpvFailureReason expected,
        string expectedMessage)
    {
        var exception = LibMpvApiFactory.FailureForWindowsError(errorCode);

        Assert.Equal(expected, exception.Reason);
        Assert.Equal(expectedMessage, exception.Message);
        Assert.DoesNotContain(".dll", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\\", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void LibMpvPathIsPinnedBelowApplicationBaseDirectory()
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "iptv-player-root", Guid.NewGuid().ToString("N")));
        try
        {
            var locator = new LibMpvLibraryLocator(root);
            Assert.Equal(Path.Combine(root, "native", "mpv", "libmpv-2.dll"), locator.LibraryPath);
            Assert.Equal(Path.Combine(root, "native", "mpv", "libmpv-2.dll"), locator.PrimaryLibraryPath);
            Assert.Equal(Path.Combine(root, "native", "mpv", "mpv-2.dll"), locator.FallbackLibraryPath);
            Assert.True(Path.IsPathFullyQualified(locator.LibraryPath));
            Assert.StartsWith(root + Path.DirectorySeparatorChar, locator.LibraryPath, StringComparison.Ordinal);

            Directory.CreateDirectory(Path.GetDirectoryName(locator.FallbackLibraryPath)!);
            File.WriteAllText(locator.FallbackLibraryPath, "test placeholder");
            Assert.Equal(locator.FallbackLibraryPath, new LibMpvLibraryLocator(root).LibraryPath);

            File.WriteAllText(locator.PrimaryLibraryPath, "test placeholder");
            Assert.Equal(locator.PrimaryLibraryPath, new LibMpvLibraryLocator(root).LibraryPath);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task EndFileKeepsEventLoopAliveForSecondMediaOnSameWindow()
    {
        var api = new RecordingMpvApi();
        await using var player = new LibMpvPlayerService(new RecordingMpvApiFactory(api));
        var firstPlaying = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondPlaying = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var playingCount = 0;
        player.StateChanged += (_, value) =>
        {
            if (value.State == PlayerState.Playing && Interlocked.Increment(ref playingCount) == 1) firstPlaying.TrySetResult();
            else if (value.State == PlayerState.Playing) secondPlaying.TrySetResult();
            if (value.State == PlayerState.Stopped) stopped.TrySetResult();
        };

        await player.LoadAsync(new ResolvedMedia(new Uri("https://media.example.invalid/first")), (nint)321);
        api.Enqueue(new(MpvEventKind.FileLoaded));
        await firstPlaying.Task.WaitAsync(TimeSpan.FromSeconds(2));
        api.Enqueue(new(MpvEventKind.EndFile));
        await stopped.Task.WaitAsync(TimeSpan.FromSeconds(2));

        await player.LoadAsync(new ResolvedMedia(new Uri("https://media.example.invalid/second")), (nint)321);
        api.Enqueue(new(MpvEventKind.FileLoaded));
        await secondPlaying.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(1, api.CreateCalls);
        Assert.Equal(2, playingCount);
    }

    [Fact]
    public async Task FileLoadedIsTheOnlyInitialSignalThatMediaCanResume()
    {
        var api = new RecordingMpvApi();
        await using var player = new LibMpvPlayerService(new RecordingMpvApiFactory(api));
        var sequence = new ConcurrentQueue<string>();
        var playing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        player.MediaLoaded += (_, _) => sequence.Enqueue("loaded");
        player.StateChanged += (_, value) =>
        {
            if (value.State != PlayerState.Playing) return;
            sequence.Enqueue("playing");
            playing.TrySetResult();
        };

        await player.LoadAsync(new ResolvedMedia(new Uri("https://media.example.invalid/private")), (nint)322);
        api.Enqueue(new(MpvEventKind.PropertyChange, "pause", false));
        api.Enqueue(new(MpvEventKind.FileLoaded));
        await playing.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(["loaded", "playing"], sequence);
    }

    [Fact]
    public async Task FirstLoadWithoutHeadersUsesStructuredLoadFileWithoutPropertyWrite()
    {
        var api = new RecordingMpvApi();
        await using var player = new LibMpvPlayerService(new RecordingMpvApiFactory(api));

        await player.LoadAsync(new ResolvedMedia(new Uri("https://media.example.invalid/first")), (nint)653);

        var load = Assert.Single(api.Loads);
        Assert.Null(load.HttpHeaderFields);
        Assert.DoesNotContain(api.Properties, value => value.Name == "file-local-options/http-header-fields");
        Assert.DoesNotContain(api.Options, value => value.Name == "http-header-fields");
    }

    [Fact]
    public async Task StructuredLoadFileKeepsHeadersLocalToTheirMedia()
    {
        var api = new RecordingMpvApi();
        await using var player = new LibMpvPlayerService(new RecordingMpvApiFactory(api));
        var sensitiveHeaders = new Dictionary<string, string>
        {
            ["Authorization"] = "Bearer private-token",
            ["X-Fixture"] = "comma,value\\tail"
        };

        await player.LoadAsync(new ResolvedMedia(new Uri("https://media.example.invalid/first"), sensitiveHeaders), (nint)654);
        await player.LoadAsync(new ResolvedMedia(new Uri("https://media.example.invalid/second")), (nint)654);

        var loads = api.Loads.ToArray();
        Assert.Equal(2, loads.Length);
        Assert.Equal("Authorization: Bearer private-token,X-Fixture: comma\\,value\\\\tail", loads[0].HttpHeaderFields);
        Assert.Null(loads[1].HttpHeaderFields);
        Assert.DoesNotContain(api.Properties, value => value.Name == "file-local-options/http-header-fields");
        Assert.DoesNotContain(api.Options, value => value.Name == "http-header-fields");
    }

    [Fact]
    public async Task LoadingAnotherMediaStopsThePreviousSelectionBeforeReplacement()
    {
        var api = new RecordingMpvApi();
        await using var player = new LibMpvPlayerService(new RecordingMpvApiFactory(api));

        await player.LoadAsync(new ResolvedMedia(new Uri("https://media.example.invalid/first")), (nint)655);
        await player.LoadAsync(new ResolvedMedia(new Uri("https://media.example.invalid/second")), (nint)655);

        Assert.Equal(2, api.Loads.Count);
        Assert.Equal(["stop"], api.Commands.Select(command => string.Join(' ', command)));
    }

    [Fact]
    public async Task TrackChangeResumesAfterCachePauseButRespectsExplicitUserPause()
    {
        var api = new RecordingMpvApi();
        await using var player = new LibMpvPlayerService(new RecordingMpvApiFactory(api));
        var initialPlaying = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var buffering = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resumed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sawBuffering = 0;

        player.StateChanged += (_, value) =>
        {
            if (value.State == PlayerState.Playing && Volatile.Read(ref sawBuffering) == 0) initialPlaying.TrySetResult();
            if (value.State == PlayerState.Loading && value.SafeMessage == "Mise en mémoire tampon…")
            {
                Interlocked.Exchange(ref sawBuffering, 1);
                buffering.TrySetResult();
            }
            else if (value.State == PlayerState.Playing && Volatile.Read(ref sawBuffering) == 1)
            {
                resumed.TrySetResult();
            }
        };

        await player.LoadAsync(new ResolvedMedia(new Uri("https://media.example.invalid/private")), (nint)777);
        api.Enqueue(new(MpvEventKind.FileLoaded));
        await initialPlaying.Task.WaitAsync(TimeSpan.FromSeconds(2));

        player.SelectAudioTrack(4);
        var pauseNoBeforeCacheEnd = api.Properties.Count(value => value is ("pause", "no"));
        Assert.True(pauseNoBeforeCacheEnd >= 1);

        api.Enqueue(new(MpvEventKind.PropertyChange, "paused-for-cache", true));
        await buffering.Task.WaitAsync(TimeSpan.FromSeconds(2));
        api.Enqueue(new(MpvEventKind.PropertyChange, "paused-for-cache", false));
        await resumed.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.True(api.Properties.Count(value => value is ("pause", "no")) > pauseNoBeforeCacheEnd);
        Assert.Contains("paused-for-cache", api.ObservedProperties);

        player.Pause();
        var pauseNoAfterExplicitPause = api.Properties.Count(value => value is ("pause", "no"));
        player.SelectSubtitleTrack(7);
        Assert.Equal(pauseNoAfterExplicitPause, api.Properties.Count(value => value is ("pause", "no")));
    }

    [Fact]
    public async Task LibMpvControlsAndDisposeUseOwnedNativeHandle()
    {
        var api = new RecordingMpvApi();
        var player = new LibMpvPlayerService(new RecordingMpvApiFactory(api));
        PlayerPositionChangedEventArgs? reportedSeek = null;
        player.PositionChanged += (_, value) => reportedSeek = value;
        var signExtendedWindowHandle = unchecked((nint)(long)0xFFFFFFFF80000001);
        await player.LoadAsync(new ResolvedMedia(new Uri("https://media.example.invalid/private")), signExtendedWindowHandle);

        player.Play();
        player.Pause();
        player.Seek(TimeSpan.FromSeconds(12.5));
        player.SetVolume(73);
        player.SelectAudioTrack(4);
        player.SelectVideoTrack(2);
        player.SelectSubtitleTrack(7);
        player.SetSubtitleEnabled(false);
        player.Stop();
        await player.DisposeAsync();

        Assert.Contains(("wid", "2147483649"), api.Options);
        Assert.Contains(("pause", "no"), api.Properties);
        Assert.Contains(("pause", "yes"), api.Properties);
        Assert.Contains(("time-pos", "12.5"), api.Properties);
        Assert.Equal(TimeSpan.FromSeconds(12.5), reportedSeek?.Position);
        Assert.Contains(("volume", "73"), api.Properties);
        Assert.Contains(("aid", "4"), api.Properties);
        Assert.Contains(("vid", "2"), api.Properties);
        Assert.Contains(("sid", "7"), api.Properties);
        Assert.Contains(("sid", "no"), api.Properties);
        Assert.Contains("idle-active", api.ObservedProperties);
        Assert.Contains("paused-for-cache", api.ObservedProperties);
        Assert.DoesNotContain("core-idle", api.ObservedProperties);
        Assert.True(api.Terminated);
        Assert.True(api.Disposed);
    }

    private sealed class RecordingWindowManager(nint handle) : IPlayerWindowManager
    {
        public nint Handle { get; private set; }
        public PlayerSeriesContext? SeriesContext { get; private set; }
        public Func<PlayerEpisodeOption, CancellationToken, Task>? EpisodeSelectionHandler { get; private set; }
        public Task<nint> ShowAsync(CancellationToken cancellationToken = default) { Handle = handle; return Task.FromResult(handle); }
        public void ConfigureEpisodes(PlayerSeriesContext? context, Func<PlayerEpisodeOption, CancellationToken, Task>? selectionHandler)
        {
            SeriesContext = context;
            EpisodeSelectionHandler = selectionHandler;
        }

        public Task SelectEpisodeAsync(string id)
        {
            var episode = SeriesContext?.Episodes.Single(value => string.Equals(value.Id, id, StringComparison.Ordinal))
                ?? throw new InvalidOperationException("Episode fixture not configured.");
            return (EpisodeSelectionHandler ?? throw new InvalidOperationException("Episode fixture handler not configured."))(episode, CancellationToken.None);
        }
    }

    private sealed class RecordingPlayerService : IPlayerService
    {
        public ResolvedMedia? Media { get; private set; }
        public nint RenderHostHandle { get; private set; }
        public TimeSpan? SeekPosition { get; private set; }
        public List<TimeSpan> SeekPositions { get; } = [];
        public int PlayCalls { get; private set; }
        public event EventHandler? MediaLoaded;
        public event EventHandler<PlayerStateChangedEventArgs>? StateChanged;
        public event EventHandler<PlayerPositionChangedEventArgs>? PositionChanged;
        public event EventHandler<TrackListChangedEventArgs>? TrackListChanged;
        public Task LoadAsync(ResolvedMedia media, nint renderHostHandle, CancellationToken cancellationToken = default) { Media = media; RenderHostHandle = renderHostHandle; return Task.CompletedTask; }
        public void Play() => PlayCalls++;
        public void Pause() { }
        public void Stop() { }
        public void Seek(TimeSpan position) { SeekPosition = position; SeekPositions.Add(position); }
        public void SetVolume(double volume) { }
        public IReadOnlyList<MediaTrack> GetTracks() => [];
        public void SelectAudioTrack(long id) { }
        public void SelectVideoTrack(long id) { }
        public void SelectSubtitleTrack(long id) { }
        public void SetSubtitleEnabled(bool enabled) { }
        public void SetFullscreen(bool fullscreen) { }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        internal void NotifyMediaLoaded() => MediaLoaded?.Invoke(this, EventArgs.Empty);
        internal void NotifyState(PlayerState state) => StateChanged?.Invoke(this, new(state));
        internal void NotifyPosition(TimeSpan position, TimeSpan? duration = null) => PositionChanged?.Invoke(this, new(position, duration));
        internal void NotifyTracks(IReadOnlyList<MediaTrack> tracks) => TrackListChanged?.Invoke(this, new(tracks));
    }

    private sealed class RecordingMpvApiFactory(RecordingMpvApi api) : ILibMpvApiFactory
    {
        public ILibMpvApi Create() => api;
    }

    private sealed class RecordingMpvApi : ILibMpvApi
    {
        private readonly ManualResetEventSlim _wakeup = new(false);
        private readonly ConcurrentQueue<MpvEventValue> _events = new();
        public ConcurrentQueue<(string Name, string Value)> Options { get; } = [];
        public ConcurrentQueue<(string Name, string Value)> Properties { get; } = [];
        public ConcurrentQueue<string> ObservedProperties { get; } = [];
        public ConcurrentQueue<LoadFileCall> Loads { get; } = [];
        public ConcurrentQueue<string[]> Commands { get; } = [];
        public int CreateCalls { get; private set; }
        public bool Terminated { get; private set; }
        public bool Disposed { get; private set; }
        public nint Create() { CreateCalls++; return (nint)55; }
        public int SetOptionString(nint handle, string name, string value) { Options.Enqueue((name, value)); return 0; }
        public int Initialize(nint handle) => 0;
        public int Command(nint handle, params string[] arguments) { Commands.Enqueue(arguments); return 0; }
        public int LoadFile(nint handle, string uri, string? httpHeaderFields) { Loads.Enqueue(new(uri, httpHeaderFields)); return 0; }
        public int SetPropertyString(nint handle, string name, string value) { Properties.Enqueue((name, value)); return 0; }
        public int ObserveProperty(nint handle, ulong userData, string name, MpvFormat format) { ObservedProperties.Enqueue(name); return 0; }
        public MpvEventValue WaitEvent(nint handle, double timeoutSeconds)
        {
            if (_events.TryDequeue(out var value)) return value;
            _wakeup.Wait(TimeSpan.FromSeconds(timeoutSeconds));
            _wakeup.Reset();
            return _events.TryDequeue(out value) ? value : new(MpvEventKind.None);
        }
        public void Enqueue(MpvEventValue value) { _events.Enqueue(value); _wakeup.Set(); }
        public void Wakeup(nint handle) => _wakeup.Set();
        public void TerminateDestroy(nint handle) => Terminated = true;
        public void Dispose() { Disposed = true; _wakeup.Dispose(); }
    }

    private sealed record LoadFileCall(string Uri, string? HttpHeaderFields);

    private sealed class SeriesDetailsHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            const string json = """
                {
                  "info": { "name": "Fixture series", "cover": "https://images.example.invalid/series.jpg" },
                  "episodes": {
                    "1": [
                      { "id": "episode-501", "episode_num": 1, "title": "Pilot", "container_extension": "mkv" },
                      { "id": "episode-502", "episode_num": 2, "title": "Second", "container_extension": "mkv" }
                    ]
                  }
                }
                """;
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
            });
        }
    }
}
