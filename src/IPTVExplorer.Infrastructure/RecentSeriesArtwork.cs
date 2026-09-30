using System.Collections.Concurrent;
using IPTVExplorer.Core;

namespace IPTVExplorer.Infrastructure;

/// <summary>Best-effort artwork for at most the 20 recent series, including browser-reported broken posters.</summary>
public sealed class RecentSeriesArtwork(SeriesArtworkRepository cache, IProviderClientFactory clients)
{
    public const int Limit = 20;
    public const int MaxConcurrency = 3;
    public static readonly TimeSpan RetryDelay = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan TimeBudget = TimeSpan.FromSeconds(8);
    private readonly SemaphoreSlim _network = new(MaxConcurrency, MaxConcurrency);
    private readonly ConcurrentDictionary<(string Provider, string Id), SemaphoreSlim> _items = new();

    public static IReadOnlyList<SearchHit> SelectRecent(string providerKey, IEnumerable<SearchHit> documents) => documents
        .Where(item => item.ProviderKey == providerKey && item.Catalog == CatalogType.Series)
        // Match INSERT OR REPLACE's last occurrence before choosing the same dated top 20 as SQLite.
        .GroupBy(item => item.RemoteId, StringComparer.Ordinal).Select(group => group.Last())
        .Where(item => item.AddedAt is not null)
        .OrderByDescending(item => item.AddedAt).ThenBy(item => item.Title, StringComparer.Ordinal)
        .ThenBy(item => item.RemoteId, StringComparer.Ordinal).Take(Limit).ToArray();

    public async Task<IReadOnlyList<SearchHit>> EnrichAsync(ProviderRecord provider, ProviderSecret? secret,
        IEnumerable<SearchHit> documents, CancellationToken cancellationToken = default,
        IReadOnlyDictionary<string, string>? failedImages = null)
    {
        var result = SelectRecent(provider.Key, documents).Select(item => item with
        {
            ImageUrl = MediaArtwork.SafeImageUrl(item.ImageUrl, secret),
            BackdropUrl = HomeArtwork.SafeUrl(item.BackdropUrl, secret)
        }).ToArray();
        if (provider.Type != ProviderType.Xtream || secret is null) return result;
        cancellationToken.ThrowIfCancellationRequested();
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(TimeBudget);
        try
        {
            await Parallel.ForEachAsync(Enumerable.Range(0, result.Length), new ParallelOptions
            {
                MaxDegreeOfParallelism = MaxConcurrency, CancellationToken = budget.Token
            }, async (i, token) =>
            {
                if (failedImages is not null && !failedImages.ContainsKey(result[i].RemoteId)) return;
                var failed = failedImages?.GetValueOrDefault(result[i].RemoteId);
                // Failed URLs are untrusted UI input; only act on the current indexed/cached poster.
                if (failed is not null && MediaArtwork.SafeImageUrl(failed, secret) is null) return;
                result[i] = await ResolveAsync(provider, secret, result[i], failed, token);
            });
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { }
        cancellationToken.ThrowIfCancellationRequested();
        return result;
    }

    private async Task<SearchHit> ResolveAsync(ProviderRecord provider, ProviderSecret secret, SearchHit item, string? failed, CancellationToken token)
    {
        var gate = _items.GetOrAdd((provider.Key, item.RemoteId), _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(token);
        try
        {
            var cached = await cache.GetAsync(provider.Key, item.RemoteId, token);
            var saved = MediaArtwork.SafeImageUrl(cached?.ImageUrl, secret);
            if (failed is not null && failed != saved && failed != item.ImageUrl) return item;
            if (saved is not null && saved != failed) return item with { ImageUrl = saved };
            if (cached is not null && cached.CheckedAt + RetryDelay > DateTimeOffset.UtcNow)
                return item with { ImageUrl = failed is null ? item.ImageUrl : null };
            if (failed is null && item.ImageUrl is not null) return item;

            await _network.WaitAsync(token);
            try
            {
                var client = await clients.CreateAsync(provider, token);
                var info = await client.GetSeriesInfoAsync(item.RemoteId, token);
                var poster = MediaArtwork.SafeImageUrl(info.ImageUrl, secret);
                if (poster == failed) poster = null; // Do not reinsert a known broken URL or loop on img.onerror.
                await cache.SaveAsync(provider.Key, item.RemoteId, poster, secret, token);
                return item with { ImageUrl = poster };
            }
            finally { _network.Release(); }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception)
        {
            // Artwork is optional. Never log exception text, which may contain a credentialized request URL.
            try { await cache.SaveAsync(provider.Key, item.RemoteId, null, secret, token); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception) { }
            return item with { ImageUrl = failed is null ? item.ImageUrl : null };
        }
        finally { gate.Release(); }
    }
}
