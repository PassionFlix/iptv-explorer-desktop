using IPTVExplorer.Core;

namespace IPTVExplorer.Infrastructure;

/// <summary>Fail closed for Xtream: normal navigation and the player cannot fetch metadata.</summary>
public sealed class LocalProviderClientFactory(IRemoteProviderClientFactory remote, CatalogSnapshotRepository snapshots,
    IProviderRepository providers) : IProviderClientFactory
{
    public Task<IProviderClient> CreateAsync(ProviderRecord provider, CancellationToken cancellationToken = default) => provider.Type == ProviderType.Xtream
        ? Task.FromResult<IProviderClient>(new LocalClient(provider, remote, snapshots, providers))
        : remote.CreateAsync(provider, cancellationToken); // Stalker/MAG protocol is unchanged.

    private sealed class LocalClient(ProviderRecord provider, IRemoteProviderClientFactory remote,
        CatalogSnapshotRepository snapshots, IProviderRepository providers) : IProviderClient
    {
        public ProviderType Type => ProviderType.Xtream;
        public Task<ConnectionTestResult> TestConnectionAsync(CancellationToken cancellationToken = default) => throw ExplicitOnly();
        public Task<AccountInfo> GetAccountInfoAsync(CancellationToken cancellationToken = default) => throw ExplicitOnly();
        public Task<IReadOnlyList<ProviderCategory>> GetLiveCategoriesAsync(CancellationToken cancellationToken = default) => providers.ListCategoriesAsync(provider.Key, CatalogType.Live, false, cancellationToken);
        public Task<IReadOnlyList<ProviderCategory>> GetVodCategoriesAsync(CancellationToken cancellationToken = default) => providers.ListCategoriesAsync(provider.Key, CatalogType.Vod, false, cancellationToken);
        public Task<IReadOnlyList<ProviderCategory>> GetSeriesCategoriesAsync(CancellationToken cancellationToken = default) => providers.ListCategoriesAsync(provider.Key, CatalogType.Series, false, cancellationToken);
        public Task<IReadOnlyList<CatalogItem>> GetLiveAsync(string categoryId, CancellationToken cancellationToken = default) => snapshots.ReadAsync(provider.Key, CatalogType.Live, categoryId, cancellationToken);
        public Task<CatalogPage<CatalogItem>> GetVodPageAsync(string categoryId, int page, CancellationToken cancellationToken = default) => Page(CatalogType.Vod, categoryId, page, cancellationToken);
        public Task<CatalogPage<CatalogItem>> GetSeriesPageAsync(string categoryId, int page, CancellationToken cancellationToken = default) => Page(CatalogType.Series, categoryId, page, cancellationToken);
        public async Task<CatalogItem> GetVodInfoAsync(string id, CancellationToken cancellationToken = default) => await snapshots.FindAsync(provider.Key, CatalogType.Vod, id, cancellationToken) ?? throw Missing();
        public async Task<CatalogItem> GetSeriesInfoAsync(string id, CancellationToken cancellationToken = default) => await snapshots.FindAsync(provider.Key, CatalogType.Series, id, cancellationToken) ?? throw Missing();
        public async Task<VodDetails> GetVodDetailsAsync(string id, CancellationToken cancellationToken = default) => await snapshots.DetailAsync<VodDetails>(provider.Key, CatalogType.Vod, id, cancellationToken) ?? throw Missing();
        public async Task<SeriesDetails> GetSeriesDetailsAsync(string id, CancellationToken cancellationToken = default) => await snapshots.DetailAsync<SeriesDetails>(provider.Key, CatalogType.Series, id, cancellationToken) ?? throw Missing();
        // Xtream resolution only constructs the playback URL in memory, never sends a metadata request.
        public async Task<ResolvedMedia> ResolveMediaAsync(MediaRequest request, CancellationToken cancellationToken = default) =>
            await (await remote.CreateAsync(provider, cancellationToken)).ResolveMediaAsync(request, cancellationToken);

        private Task<CatalogPage<CatalogItem>> Page(CatalogType type, string category, int page, CancellationToken token) => snapshots.PageAsync(provider.Key, type, category, page, token);
        private static InvalidOperationException ExplicitOnly() => new("Cette opération réseau nécessite une action explicite dans Paramètres.");
        private static InvalidOperationException Missing() => new("Détail absent du cache local. Ouvrez explicitement la fiche du média.");
    }
}
