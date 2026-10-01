namespace IPTVExplorer.Core;

public interface ISecretStore
{
    Task<string> PutAsync(ProviderSecret secret, CancellationToken cancellationToken = default);
    Task<ProviderSecret?> GetAsync(string reference, CancellationToken cancellationToken = default);
    Task DeleteAsync(string reference, CancellationToken cancellationToken = default);
}

public interface IProviderRepository
{
    Task<IReadOnlyList<ProviderRecord>> ListAsync(CancellationToken cancellationToken = default);
    Task<ProviderRecord?> GetAsync(string key, CancellationToken cancellationToken = default);
    Task AddAsync(ProviderRecord provider, CancellationToken cancellationToken = default);
    Task UpdateAsync(ProviderRecord provider, CancellationToken cancellationToken = default);
    Task SetEnabledAsync(string key, bool enabled, CancellationToken cancellationToken = default);
    Task DeleteAsync(string key, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ProviderCategory>> ListCategoriesAsync(string providerKey, CatalogType catalog, bool includeMissing = true, CancellationToken cancellationToken = default);
    Task SyncCategoriesAsync(string providerKey, CatalogType catalog, IReadOnlyList<ProviderCategory> categories, CancellationToken cancellationToken = default);
    Task SaveCategoryPolicyAsync(string providerKey, CatalogType catalog, CategoryPolicy policy, CancellationToken cancellationToken = default);
    Task<CategoryPolicy> GetCategoryPolicyAsync(string providerKey, CatalogType catalog, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<CategorySummary>> GetCategorySummariesAsync(string providerKey, CancellationToken cancellationToken = default);
}

public interface IProviderClient
{
    ProviderType Type { get; }
    Task<IReadOnlyList<CatalogItem>> GetAllLiveAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException("Bulk catalog is not supported.");
    Task<IReadOnlyList<CatalogItem>> GetAllVodAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException("Bulk catalog is not supported.");
    Task<IReadOnlyList<CatalogItem>> GetAllSeriesAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException("Bulk catalog is not supported.");
    Task<ConnectionTestResult> TestConnectionAsync(CancellationToken cancellationToken = default);
    Task<AccountInfo> GetAccountInfoAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ProviderCategory>> GetLiveCategoriesAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ProviderCategory>> GetVodCategoriesAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ProviderCategory>> GetSeriesCategoriesAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<CatalogItem>> GetLiveAsync(string categoryId, CancellationToken cancellationToken = default);
    Task<CatalogPage<CatalogItem>> GetVodPageAsync(string categoryId, int page, CancellationToken cancellationToken = default);
    Task<CatalogPage<CatalogItem>> GetSeriesPageAsync(string categoryId, int page, CancellationToken cancellationToken = default);
    Task<CatalogItem> GetVodInfoAsync(string id, CancellationToken cancellationToken = default);
    Task<CatalogItem> GetSeriesInfoAsync(string id, CancellationToken cancellationToken = default);
    Task<VodDetails> GetVodDetailsAsync(string id, CancellationToken cancellationToken = default);
    Task<SeriesDetails> GetSeriesDetailsAsync(string id, CancellationToken cancellationToken = default);
    Task<ResolvedMedia> ResolveMediaAsync(MediaRequest request, CancellationToken cancellationToken = default);
}

public interface IProviderClientFactory
{
    Task<IProviderClient> CreateAsync(ProviderRecord provider, CancellationToken cancellationToken = default);
}

public interface IProviderLocalData
{
    Task DeleteSearchIndexAsync(string providerKey, CancellationToken cancellationToken = default);
}

public interface ISearchService
{
    Task<CatalogPage<SearchHit>> SearchAsync(string? providerKey, CatalogType catalog, string query, int page, int pageSize, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<SearchHit>> RecentlyAddedAsync(string providerKey, CatalogType catalog, int limit, CancellationToken cancellationToken = default);
}

// Explicit network operations use this factory. Ordinary application reads use IProviderClientFactory.
public interface IRemoteProviderClientFactory : IProviderClientFactory
{
    void Evict(string providerKey) { }
}

public interface IPlaybackHistoryRepository
{
    Task<PlaybackProgress?> GetAsync(string providerKey, CatalogType catalog, string mediaId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<PlaybackProgress>> ListInProgressAsync(string providerKey, int limit, CancellationToken cancellationToken = default);
    Task UpsertAsync(PlaybackProgress progress, CancellationToken cancellationToken = default);
    Task DeleteAsync(string providerKey, CatalogType catalog, string mediaId, CancellationToken cancellationToken = default);
}

public interface IAppSettingsRepository
{
    Task<AppPreferences> GetAsync(CancellationToken cancellationToken = default);
    Task SaveAsync(AppPreferences preferences, CancellationToken cancellationToken = default);
}

public sealed record BridgeRequest(string Id, string Method, System.Text.Json.JsonElement? Params);
public sealed record BridgeResponse(string Id, bool Ok, object? Result = null, string? Error = null);

public static class BridgeProtocol
{
    private static readonly System.Text.Json.JsonSerializerOptions JsonOptions = CreateOptions();

    private static System.Text.Json.JsonSerializerOptions CreateOptions()
    {
        var options = new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web);
        options.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter(System.Text.Json.JsonNamingPolicy.CamelCase));
        return options;
    }

    public static BridgeRequest Parse(string json)
    {
        try
        {
            var request = System.Text.Json.JsonSerializer.Deserialize<BridgeRequest>(json, JsonOptions);
            if (request is null || string.IsNullOrWhiteSpace(request.Id) || string.IsNullOrWhiteSpace(request.Method))
                throw new FormatException("Bridge request requires id and method.");
            return request;
        }
        catch (System.Text.Json.JsonException exception)
        {
            throw new FormatException("Invalid bridge JSON.", exception);
        }
    }

    public static string Serialize(BridgeResponse response) => System.Text.Json.JsonSerializer.Serialize(response, JsonOptions);
}
