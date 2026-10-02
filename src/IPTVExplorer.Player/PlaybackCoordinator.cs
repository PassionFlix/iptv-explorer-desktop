using IPTVExplorer.Core;

namespace IPTVExplorer.Player;

public sealed record PlaybackOpenResult(string State, string Message);

public sealed record PlayerEpisodeOption(string Id, string Title, int Season, int? Episode, string? Extension)
{
    public string Label => Episode is int episode && episode > 0
        ? $"S{Season:00}E{episode:00} — {Title}"
        : $"S{Season:00} — {Title}";

    public override string ToString() => Label;
}

public sealed record PlayerSeriesContext(
    string SeriesTitle,
    string SeriesId,
    IReadOnlyList<PlayerEpisodeOption> Episodes,
    string SelectedEpisodeId);

public interface IPlayerWindowManager
{
    Task<nint> ShowAsync(CancellationToken cancellationToken = default);

    void ConfigureEpisodes(
        PlayerSeriesContext? context,
        Func<PlayerEpisodeOption, CancellationToken, Task>? selectionHandler)
    {
    }
}

public sealed class PlaybackCoordinator : IAsyncDisposable
{
    private readonly IProviderRepository _providers;
    private readonly IProviderClientFactory _clients;
    private readonly IPlaybackHistoryRepository _history;
    private readonly IPlayerService _player;
    private readonly IPlayerWindowManager _windows;
    private readonly IPlaybackDiagnosticTrace _trace;
    private readonly IStalkerMediaProbe _mediaProbe;
    private readonly PlaybackProgressRecorder _recorder;
    private readonly object _resumeLock = new();
    private TimeSpan? _pendingResumePosition;

    public PlaybackCoordinator(
        IProviderRepository providers,
        IProviderClientFactory clients,
        IPlaybackHistoryRepository history,
        IPlayerService player,
        IPlayerWindowManager windows,
        IPlaybackDiagnosticTrace? diagnosticTrace = null,
        IStalkerMediaProbe? mediaProbe = null)
    {
        _providers = providers;
        _clients = clients;
        _history = history;
        _player = player;
        _windows = windows;
        _trace = diagnosticTrace ?? NullPlaybackDiagnosticTrace.Instance;
        _mediaProbe = mediaProbe ?? NullStalkerMediaProbe.Instance;
        _recorder = new PlaybackProgressRecorder(history);
        _player.MediaLoaded += OnMediaLoaded;
        _player.PositionChanged += OnPositionChanged;
        _player.StateChanged += OnStateChanged;
    }

    public Task<PlaybackOpenResult> OpenAsync(MediaReference reference, CancellationToken cancellationToken = default) =>
        OpenCoreAsync(reference, null, null, cancellationToken);

    public async Task<PlaybackOpenResult> ResumeAsync(string providerKey, CatalogType catalog, string mediaId, CancellationToken cancellationToken = default)
    {
        var progress = await _history.GetAsync(providerKey, catalog, mediaId, cancellationToken) ?? throw new KeyNotFoundException("Progression de lecture introuvable.");
        if (!PlaybackProgressPolicy.ShouldList(progress)) throw new InvalidOperationException("Ce contenu n’est plus disponible dans Continuer à regarder.");
        var reference = progress.Catalog == CatalogType.Series
            ? new MediaReference(
                progress.ProviderKey,
                CatalogType.Series,
                progress.SeriesId ?? throw new InvalidDataException("La série associée à cet épisode est introuvable."),
                progress.MediaId,
                progress.Extension,
                progress.Title,
                progress.PosterUrl,
                progress.SeriesTitle,
                progress.Season,
                progress.Episode)
            : new MediaReference(
                progress.ProviderKey,
                CatalogType.Vod,
                progress.MediaId,
                Extension: progress.Extension,
                Title: progress.Title,
                PosterUrl: progress.PosterUrl);
        return await OpenCoreAsync(reference, progress.Position, progress, cancellationToken);
    }

    public Task FlushAsync() => _recorder.FlushAsync();

    private async Task<PlaybackOpenResult> OpenCoreAsync(
        MediaReference reference,
        TimeSpan? resumePosition,
        PlaybackProgress? storedProgress,
        CancellationToken cancellationToken)
    {
        ValidateReference(reference);
        var provider = await _providers.GetAsync(reference.ProviderKey, cancellationToken) ?? throw new KeyNotFoundException("Provider was not found.");
        if (!provider.Enabled) throw new InvalidOperationException("Enable this provider before playback.");

        var renderHostHandle = await _windows.ShowAsync(cancellationToken);
        if (renderHostHandle == 0) throw new InvalidOperationException("The native video surface is unavailable.");

        var client = await _clients.CreateAsync(provider, cancellationToken);
        var progress = storedProgress ?? CreateProgress(reference);
        await _recorder.BeginAsync(progress, cancellationToken);
        try
        {
            var playbackId = reference.EpisodeId ?? reference.MediaId;
            var request = new MediaRequest(reference.MediaType, playbackId, reference.MediaType == CatalogType.Series ? reference.MediaId : null,
                reference.Extension, reference.CategoryId);
            ResolvedMedia resolved;
            try
            {
                resolved = await client.ResolveMediaAsync(request, cancellationToken);
                TraceResolved(provider.Type, reference.MediaType, playbackId, resolved);
            }
            catch (Exception exception)
            {
                TraceResolutionError(provider.Type, reference.MediaType, playbackId, exception);
                throw;
            }
            lock (_resumeLock) _pendingResumePosition = resumePosition is { } position && position > TimeSpan.Zero ? position : null;
            if (provider.Type == ProviderType.Stalker && _mediaProbe.Enabled)
            {
                lock (_resumeLock) _pendingResumePosition = null;
                _windows.ConfigureEpisodes(null, null);
                await _mediaProbe.ProbeAsync(resolved, cancellationToken);
                return new PlaybackOpenResult("probed", "Sonde média Stalker terminée.");
            }
            TraceLoadRequest(provider.Type, reference.MediaType, playbackId, resolved);
            await _player.LoadAsync(resolved, renderHostHandle, cancellationToken);

            if (reference.MediaType == CatalogType.Series && reference.EpisodeId is not null)
            {
                await ConfigureSeriesEpisodesAsync(reference, client, renderHostHandle, cancellationToken);
            }
            else
            {
                _windows.ConfigureEpisodes(null, null);
            }
        }
        catch
        {
            lock (_resumeLock) _pendingResumePosition = null;
            throw;
        }

        return new PlaybackOpenResult("opened", "Lecteur natif ouvert.");
    }

    private async Task ConfigureSeriesEpisodesAsync(
        MediaReference reference,
        IProviderClient client,
        nint renderHostHandle,
        CancellationToken cancellationToken)
    {
        try
        {
            var details = await client.GetSeriesDetailsAsync(reference.MediaId, cancellationToken);
            var episodes = details.Seasons
                .OrderBy(season => season.Number)
                .SelectMany(season => season.Episodes
                    .OrderBy(episode => episode.Episode ?? int.MaxValue)
                    .Select(episode => new PlayerEpisodeOption(
                        episode.Id,
                        episode.Title,
                        episode.Season ?? season.Number,
                        episode.Episode,
                        episode.Extension)))
                .ToArray();

            if (episodes.Length == 0)
            {
                _windows.ConfigureEpisodes(null, null);
                return;
            }

            var selected = episodes.FirstOrDefault(episode => string.Equals(episode.Id, reference.EpisodeId, StringComparison.Ordinal));
            if (selected is not null)
            {
                var enriched = CreateProgress(new MediaReference(
                    reference.ProviderKey,
                    CatalogType.Series,
                    details.Id,
                    selected.Id,
                    selected.Extension,
                    selected.Title,
                    details.Poster,
                    details.Title,
                    selected.Season,
                    selected.Episode));
                if (enriched is not null) _recorder.UpdateMetadata(enriched);
            }

            var context = new PlayerSeriesContext(details.Title, details.Id, episodes, reference.EpisodeId!);
            _windows.ConfigureEpisodes(context, async (episode, token) =>
            {
                var nextReference = new MediaReference(
                    reference.ProviderKey,
                    CatalogType.Series,
                    details.Id,
                    episode.Id,
                    episode.Extension,
                    episode.Title,
                    details.Poster,
                    details.Title,
                    episode.Season,
                    episode.Episode);
                await _recorder.BeginAsync(CreateProgress(nextReference), token);
                lock (_resumeLock) _pendingResumePosition = null;
                var media = await client.ResolveMediaAsync(new MediaRequest(CatalogType.Series, episode.Id, details.Id, episode.Extension), token);
                TraceResolved(client.Type, CatalogType.Series, episode.Id, media);
                TraceLoadRequest(client.Type, CatalogType.Series, episode.Id, media);
                await _player.LoadAsync(media, renderHostHandle, token);
            });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // Playback itself is already open. A provider that cannot refresh series metadata
            // should not make the currently selected episode fail just because the selector is unavailable.
            _windows.ConfigureEpisodes(null, null);
        }
    }

    private void OnPositionChanged(object? sender, PlayerPositionChangedEventArgs value) =>
        _recorder.RecordPosition(value.Position, value.Duration);

    private void OnStateChanged(object? sender, PlayerStateChangedEventArgs value)
    {
        _ = _recorder.RecordStateAsync(value.State);
    }

    private void OnMediaLoaded(object? sender, EventArgs value)
    {
        TimeSpan? resume;
        lock (_resumeLock)
        {
            resume = _pendingResumePosition;
            _pendingResumePosition = null;
        }
        if (resume is { } position)
        {
            _player.Seek(position);
            _player.Play();
        }
    }

    private static PlaybackProgress? CreateProgress(MediaReference reference)
    {
        if (reference.MediaType is not (CatalogType.Vod or CatalogType.Series)) return null;
        if (reference.MediaType == CatalogType.Series && reference.EpisodeId is null) return null;
        var mediaId = reference.MediaType == CatalogType.Series ? reference.EpisodeId! : reference.MediaId;
        var fallback = reference.MediaType == CatalogType.Series ? "Épisode" : "Film";
        return new PlaybackProgress(
            reference.ProviderKey,
            reference.MediaType,
            mediaId,
            reference.MediaType == CatalogType.Series ? reference.MediaId : null,
            CleanLabel(reference.Title, fallback),
            reference.MediaType == CatalogType.Series ? CleanLabel(reference.SeriesTitle, "Série") : null,
            reference.Season,
            reference.Episode,
            reference.PosterUrl,
            reference.Extension,
            TimeSpan.Zero,
            null,
            DateTimeOffset.UtcNow);
    }

    private static string CleanLabel(string? value, string fallback)
    {
        var cleaned = new string((value ?? string.Empty).Where(character => !char.IsControl(character)).ToArray()).Trim();
        return cleaned.Length switch { 0 => fallback, > 300 => cleaned[..300].TrimEnd(), _ => cleaned };
    }

    private static void ValidateReference(MediaReference reference)
    {
        if (!ProviderKey.IsValid(reference.ProviderKey)) throw new ArgumentException("Invalid provider key.");
        ValidateOpaqueId(reference.MediaId, nameof(reference.MediaId));
        if (reference.EpisodeId is not null) ValidateOpaqueId(reference.EpisodeId, nameof(reference.EpisodeId));
        if (reference.CategoryId is not null) ValidateOpaqueId(reference.CategoryId, nameof(reference.CategoryId));
        if (reference.Extension is not null && (reference.Extension.Length is < 1 or > 20 || reference.Extension.Any(character => !char.IsAsciiLetterOrDigit(character))))
            throw new ArgumentException("Invalid media extension.");
    }

    private static void ValidateOpaqueId(string value, string parameter)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 512 || Uri.TryCreate(value, UriKind.Absolute, out _))
            throw new ArgumentException("Invalid opaque media reference.", parameter);
    }

    private void TraceResolved(ProviderType providerType, CatalogType mediaType, string mediaId, ResolvedMedia media)
    {
        if (!_trace.Enabled || providerType != ProviderType.Stalker) return;
        _trace.Write("STALKER PLAYBACK RESOLVED",
            Field("layer", "coordinator"),
            Field("provider_type", providerType.ToString()),
            Field("media_type", mediaType.ToString()),
            Field("media_id", SafePlaybackDiagnosticData.OpaqueId(mediaId)),
            Field("scheme", SafePlaybackDiagnosticData.Scheme(media.Uri)),
            Field("path", SafePlaybackDiagnosticData.PathBaseName(media.Uri)),
            Field("query_keys", SafePlaybackDiagnosticData.QueryKeyNames(media.Uri)),
            Field("header_names", SafePlaybackDiagnosticData.HeaderNames(media.Headers)));
    }

    private void TraceResolutionError(ProviderType providerType, CatalogType mediaType, string mediaId, Exception exception)
    {
        if (!_trace.Enabled || providerType != ProviderType.Stalker) return;
        _trace.Write("STALKER PLAYBACK RESOLVE ERROR",
            Field("layer", "coordinator"),
            Field("provider_type", providerType.ToString()),
            Field("media_type", mediaType.ToString()),
            Field("media_id", SafePlaybackDiagnosticData.OpaqueId(mediaId)),
            Field("error_type", exception.GetType().Name));
    }

    private void TraceLoadRequest(ProviderType providerType, CatalogType mediaType, string mediaId, ResolvedMedia media)
    {
        if (!_trace.Enabled) return;
        _trace.Write("PLAYBACK LOAD REQUEST",
            Field("provider_type", providerType.ToString()),
            Field("media_type", mediaType.ToString()),
            Field("media_id", SafePlaybackDiagnosticData.OpaqueId(mediaId)),
            Field("scheme", SafePlaybackDiagnosticData.Scheme(media.Uri)),
            Field("path", SafePlaybackDiagnosticData.PathBaseName(media.Uri)),
            Field("query_keys", SafePlaybackDiagnosticData.QueryKeyNames(media.Uri)),
            Field("header_names", SafePlaybackDiagnosticData.HeaderNames(media.Headers)));
    }

    private static PlaybackDiagnosticField Field(string name, string value) => new(name, value);

    public async ValueTask DisposeAsync()
    {
        _player.MediaLoaded -= OnMediaLoaded;
        _player.PositionChanged -= OnPositionChanged;
        _player.StateChanged -= OnStateChanged;
        await _recorder.DisposeAsync();
    }
}
