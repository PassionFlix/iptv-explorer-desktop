using IPTVExplorer.Core;
using Microsoft.Data.Sqlite;

namespace IPTVExplorer.Infrastructure;

/// <summary>Local-only poster lookup. This service deliberately has no provider/network dependency.</summary>
public sealed class RecentSeriesArtwork(SeriesArtworkRepository cache)
{
    public async Task<IReadOnlyList<SearchHit>> ApplyCachedAsync(string providerKey, ProviderSecret? secret,
        IEnumerable<SearchHit> documents, CancellationToken cancellationToken = default)
    {
        var result = new List<SearchHit>();
        foreach (var item in documents.Where(item => item.ProviderKey == providerKey && item.Catalog == CatalogType.Series).Take(20))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var poster = MediaArtwork.SafeImageUrl(item.ImageUrl, secret);
            try
            {
                var cached = await cache.GetAsync(providerKey, item.RemoteId, cancellationToken);
                poster = MediaArtwork.SafeImageUrl(cached?.ImageUrl, secret) ?? poster;
            }
            catch (Exception exception) when (exception is SqliteException or IOException or FormatException)
            {
                // Optional local cache failure: keep the indexed poster/placeholder, never contact a provider.
            }
            result.Add(item with { ImageUrl = poster, BackdropUrl = HomeArtwork.SafeUrl(item.BackdropUrl, secret) });
        }
        return result;
    }

    /// <summary>Remember only an image from a detail response already requested by the user.</summary>
    public async Task RememberDetailAsync(string providerKey, string seriesId, string? poster, ProviderSecret? secret,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (MediaArtwork.SafeImageUrl(poster, secret) is not { } safe) return;
        try { await cache.SaveAsync(providerKey, seriesId, safe, secret, cancellationToken); }
        catch (Exception exception) when (exception is SqliteException or IOException)
        {
            // A cache write failure must not hide the requested series detail or cause another request.
        }
    }
}
