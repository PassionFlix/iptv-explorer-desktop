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

public sealed class PlaybackCoordinator(
    IProviderRepository providers,
    IProviderClientFactory clients,
    IPlayerService player,
    IPlayerWindowManager windows)
{
    public async Task<PlaybackOpenResult> OpenAsync(MediaReference reference, CancellationToken cancellationToken = default)
    {
        if (!ProviderKey.IsValid(reference.ProviderKey)) throw new ArgumentException("Invalid provider key.");
        ValidateOpaqueId(reference.MediaId, nameof(reference.MediaId));
        if (reference.EpisodeId is not null) ValidateOpaqueId(reference.EpisodeId, nameof(reference.EpisodeId));
        if (reference.Extension is not null && (reference.Extension.Length is < 1 or > 20 || reference.Extension.Any(character => !char.IsAsciiLetterOrDigit(character)))) throw new ArgumentException("Invalid media extension.");

        var provider = await providers.GetAsync(reference.ProviderKey, cancellationToken) ?? throw new KeyNotFoundException("Provider was not found.");
        if (!provider.Enabled) throw new InvalidOperationException("Enable this provider before playback.");

        var renderHostHandle = await windows.ShowAsync(cancellationToken);
        if (renderHostHandle == 0) throw new InvalidOperationException("The native video surface is unavailable.");

        var client = await clients.CreateAsync(provider, cancellationToken);
        var playbackId = reference.EpisodeId ?? reference.MediaId;
        var request = new MediaRequest(reference.MediaType, playbackId, reference.MediaType == CatalogType.Series ? reference.MediaId : null, reference.Extension);
        var resolved = await client.ResolveMediaAsync(request, cancellationToken);
        await player.LoadAsync(resolved, renderHostHandle, cancellationToken);

        if (reference.MediaType == CatalogType.Series && reference.EpisodeId is not null)
        {
            await ConfigureSeriesEpisodesAsync(reference, client, renderHostHandle, cancellationToken);
        }
        else
        {
            windows.ConfigureEpisodes(null, null);
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
                windows.ConfigureEpisodes(null, null);
                return;
            }

            var context = new PlayerSeriesContext(details.Title, details.Id, episodes, reference.EpisodeId!);
            windows.ConfigureEpisodes(context, async (episode, token) =>
            {
                var media = await client.ResolveMediaAsync(
                    new MediaRequest(CatalogType.Series, episode.Id, details.Id, episode.Extension),
                    token);
                await player.LoadAsync(media, renderHostHandle, token);
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
            windows.ConfigureEpisodes(null, null);
        }
    }

    private static void ValidateOpaqueId(string value, string parameter)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 512 || Uri.TryCreate(value, UriKind.Absolute, out _)) throw new ArgumentException("Invalid opaque media reference.", parameter);
    }
}
