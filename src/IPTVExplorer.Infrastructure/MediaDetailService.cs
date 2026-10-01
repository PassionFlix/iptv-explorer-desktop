using IPTVExplorer.Core;

namespace IPTVExplorer.Infrastructure;

/// <summary>The only Xtream detail network entrypoint: an explicit media-sheet action.</summary>
public sealed class MediaDetailService(IRemoteProviderClientFactory remote, CatalogSnapshotRepository snapshots,
    ISecretStore secrets, RecentSeriesArtwork artwork) : IDisposable
{
    private readonly SingleFlight<(string, CatalogType, string), object> _details = new();
    private readonly CancellationTokenSource _lifetime = new();

    public async Task<VodDetails> VodAsync(ProviderRecord provider, string id, CancellationToken token = default) =>
        (VodDetails)await _details.RunAsync((provider.Key, CatalogType.Vod, id), async () =>
        {
            var ct = _lifetime.Token;
            if (await snapshots.DetailAsync<VodDetails>(provider.Key, CatalogType.Vod, id, ct) is { } cached) return cached;
            var secret = await secrets.GetAsync(provider.SecretReference, ct);
            var item = await snapshots.FindAsync(provider.Key, CatalogType.Vod, id, ct);
            var detail = item is not null && CatalogSanitizer.HasVodDetail(item)
                ? CatalogSanitizer.FromCatalog(item)
                : await (await remote.CreateAsync(provider, ct)).GetVodDetailsAsync(id, ct);
            detail = CatalogSanitizer.Vod(detail with { Id = id }, secret);
            await snapshots.SaveAsync(provider.Key, detail, secret, ct);
            return detail;
        }, token);

    public async Task<SeriesDetails> SeriesAsync(ProviderRecord provider, string id, CancellationToken token = default) =>
        (SeriesDetails)await _details.RunAsync((provider.Key, CatalogType.Series, id), async () =>
        {
            var ct = _lifetime.Token;
            if (await snapshots.DetailAsync<SeriesDetails>(provider.Key, CatalogType.Series, id, ct) is { } cached) return cached;
            var detail = await (await remote.CreateAsync(provider, ct)).GetSeriesDetailsAsync(id, ct);
            var secret = await secrets.GetAsync(provider.SecretReference, ct);
            detail = CatalogSanitizer.Series(detail with { Id = id }, secret);
            await snapshots.SaveAsync(provider.Key, detail, secret, ct);
            await artwork.RememberDetailAsync(provider.Key, id, detail.Poster, secret, ct);
            return detail;
        }, token);

    public void Dispose() { _lifetime.Cancel(); _lifetime.Dispose(); }
}
