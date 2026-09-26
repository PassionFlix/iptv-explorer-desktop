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
        var coordinator = new PlaybackCoordinator(database.Repository, new ProviderClientFactory(new StubHttpClientFactory(), secrets), player, windows);

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
    public async Task MissingLibMpvReturnsSafeUserError()
    {
        var root = Path.Combine(Path.GetTempPath(), "missing-libmpv", Guid.NewGuid().ToString("N"));
        await using var player = new LibMpvPlayerService(new LibMpvApiFactory(new LibMpvLibraryLocator(root)));

        var exception = await Assert.ThrowsAsync<LibMpvNotInstalledException>(() =>
            player.LoadAsync(new ResolvedMedia(new Uri("https://media.example.invalid/private")), (nint)123));

        Assert.Equal("Le moteur vidéo libmpv n'est pas installé.", exception.Message);
        Assert.DoesNotContain("media.example.invalid", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void LibMpvPathIsPinnedBelowApplicationBaseDirectory()
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "iptv-player-root", Guid.NewGuid().ToString("N")));
        var locator = new LibMpvLibraryLocator(root);

        Assert.Equal(Path.Combine(root, "native", "mpv", "mpv-2.dll"), locator.LibraryPath);
        Assert.True(Path.IsPathFullyQualified(locator.LibraryPath));
        Assert.StartsWith(root + Path.DirectorySeparatorChar, locator.LibraryPath, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LibMpvControlsAndDisposeUseOwnedNativeHandle()
    {
        var api = new RecordingMpvApi();
        var player = new LibMpvPlayerService(new RecordingMpvApiFactory(api));
        await player.LoadAsync(new ResolvedMedia(new Uri("https://media.example.invalid/private")), (nint)9876);

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

        Assert.Contains(("wid", "9876"), api.Options);
        Assert.Contains(("pause", "no"), api.Properties);
        Assert.Contains(("pause", "yes"), api.Properties);
        Assert.Contains(("time-pos", "12.5"), api.Properties);
        Assert.Contains(("volume", "73"), api.Properties);
        Assert.Contains(("aid", "4"), api.Properties);
        Assert.Contains(("vid", "2"), api.Properties);
        Assert.Contains(("sid", "7"), api.Properties);
        Assert.Contains(("sid", "no"), api.Properties);
        Assert.True(api.Terminated);
        Assert.True(api.Disposed);
    }

    private sealed class RecordingWindowManager(nint handle) : IPlayerWindowManager
    {
        public nint Handle { get; private set; }
        public Task<nint> ShowAsync(CancellationToken cancellationToken = default) { Handle = handle; return Task.FromResult(handle); }
    }

    private sealed class RecordingPlayerService : IPlayerService
    {
        public ResolvedMedia? Media { get; private set; }
        public nint RenderHostHandle { get; private set; }
        public event EventHandler<PlayerStateChangedEventArgs>? StateChanged;
        public event EventHandler<PlayerPositionChangedEventArgs>? PositionChanged;
        public event EventHandler<TrackListChangedEventArgs>? TrackListChanged;
        public Task LoadAsync(ResolvedMedia media, nint renderHostHandle, CancellationToken cancellationToken = default) { Media = media; RenderHostHandle = renderHostHandle; return Task.CompletedTask; }
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

        internal void NotifyState(PlayerState state) => StateChanged?.Invoke(this, new(state));
        internal void NotifyPosition(TimeSpan position) => PositionChanged?.Invoke(this, new(position, null));
        internal void NotifyTracks(IReadOnlyList<MediaTrack> tracks) => TrackListChanged?.Invoke(this, new(tracks));
    }

    private sealed class RecordingMpvApiFactory(RecordingMpvApi api) : ILibMpvApiFactory
    {
        public ILibMpvApi Create() => api;
    }

    private sealed class RecordingMpvApi : ILibMpvApi
    {
        private readonly ManualResetEventSlim _wakeup = new(false);
        public ConcurrentBag<(string Name, string Value)> Options { get; } = [];
        public ConcurrentBag<(string Name, string Value)> Properties { get; } = [];
        public bool Terminated { get; private set; }
        public bool Disposed { get; private set; }
        public nint Create() => (nint)55;
        public int SetOptionString(nint handle, string name, string value) { Options.Add((name, value)); return 0; }
        public int Initialize(nint handle) => 0;
        public int Command(nint handle, params string[] arguments) => 0;
        public int SetPropertyString(nint handle, string name, string value) { Properties.Add((name, value)); return 0; }
        public int ObserveProperty(nint handle, ulong userData, string name, MpvFormat format) => 0;
        public MpvEventValue WaitEvent(nint handle, double timeoutSeconds) { _wakeup.Wait(TimeSpan.FromSeconds(timeoutSeconds)); _wakeup.Reset(); return new(MpvEventKind.None); }
        public void Wakeup(nint handle) => _wakeup.Set();
        public void TerminateDestroy(nint handle) => Terminated = true;
        public void Dispose() { Disposed = true; _wakeup.Dispose(); }
    }
}
