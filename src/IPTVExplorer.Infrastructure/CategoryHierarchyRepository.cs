using IPTVExplorer.Core;
using Microsoft.Data.Sqlite;

namespace IPTVExplorer.Infrastructure;

public sealed class CategoryHierarchyRepository(SqliteConnectionFactory connections)
{
    private readonly SemaphoreSlim _initializationLock = new(1, 1);
    private volatile bool _initialized;

    public async Task<IReadOnlyDictionary<string, string>> ReadAsync(
        string providerKey,
        CatalogType catalog,
        CancellationToken cancellationToken = default)
    {
        if (!ProviderKey.IsValid(providerKey)) throw new ArgumentException("Invalid provider key.", nameof(providerKey));
        await EnsureInitializedAsync(cancellationToken);
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        await using var connection = connections.Create();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT remote_id,parent_remote_id
            FROM provider_category_hierarchy
            WHERE provider_key=$provider AND catalog_type=$catalog
            """;
        command.Parameters.AddWithValue("$provider", providerKey);
        command.Parameters.AddWithValue("$catalog", Db(catalog));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) result[reader.GetString(0)] = reader.GetString(1);
        return result;
    }

    public async Task ReplaceAsync(
        string providerKey,
        CatalogType catalog,
        IReadOnlyList<ProviderCategory> categories,
        CancellationToken cancellationToken = default)
    {
        if (!ProviderKey.IsValid(providerKey)) throw new ArgumentException("Invalid provider key.", nameof(providerKey));
        await EnsureInitializedAsync(cancellationToken);
        var relations = categories
            .Where(category => ValidRelation(category.RemoteId, category.ParentRemoteId))
            .GroupBy(category => category.RemoteId, StringComparer.Ordinal)
            .Select(group => (Id: group.Key, Parent: group.First().ParentRemoteId!))
            .ToArray();

        await using var connection = connections.Create();
        await connection.OpenAsync(cancellationToken);
        await ConfigureAsync(connection, cancellationToken);
        await using var transaction = connection.BeginTransaction();
        await using (var clear = connection.CreateCommand())
        {
            clear.Transaction = transaction;
            clear.CommandText = "DELETE FROM provider_category_hierarchy WHERE provider_key=$provider AND catalog_type=$catalog";
            clear.Parameters.AddWithValue("$provider", providerKey);
            clear.Parameters.AddWithValue("$catalog", Db(catalog));
            await clear.ExecuteNonQueryAsync(cancellationToken);
        }

        foreach (var relation in relations)
        {
            await using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO provider_category_hierarchy(provider_key,catalog_type,remote_id,parent_remote_id)
                VALUES($provider,$catalog,$id,$parent)
                """;
            insert.Parameters.AddWithValue("$provider", providerKey);
            insert.Parameters.AddWithValue("$catalog", Db(catalog));
            insert.Parameters.AddWithValue("$id", relation.Id);
            insert.Parameters.AddWithValue("$parent", relation.Parent);
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task EnsureInitializedAsync(CancellationToken cancellationToken = default)
    {
        if (_initialized) return;
        await _initializationLock.WaitAsync(cancellationToken);
        try
        {
            if (_initialized) return;
            await using var connection = connections.Create();
            await connection.OpenAsync(cancellationToken);
            await ConfigureAsync(connection, cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE IF NOT EXISTS provider_category_hierarchy (
                    provider_key TEXT NOT NULL REFERENCES providers(provider_key) ON DELETE CASCADE,
                    catalog_type TEXT NOT NULL CHECK (catalog_type IN ('live','vod','series')),
                    remote_id TEXT NOT NULL,
                    parent_remote_id TEXT NOT NULL,
                    PRIMARY KEY(provider_key,catalog_type,remote_id)
                );
                INSERT OR IGNORE INTO schema_migrations(version,applied_at)
                    VALUES(7,strftime('%Y-%m-%dT%H:%M:%fZ','now'));
                """;
            await command.ExecuteNonQueryAsync(cancellationToken);
            _initialized = true;
        }
        finally
        {
            _initializationLock.Release();
        }
    }

    private static bool ValidRelation(string id, string? parent) =>
        !string.IsNullOrWhiteSpace(id) && id.Length <= 180 &&
        !string.IsNullOrWhiteSpace(parent) && parent.Length <= 180 &&
        !string.Equals(id, parent, StringComparison.Ordinal);

    private static async Task ConfigureAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000;";
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static string Db(CatalogType catalog) => catalog.ToString().ToLowerInvariant();
}

public sealed class HierarchyProviderRepository(
    ProviderRepository inner,
    CategoryHierarchyRepository hierarchy) : IProviderRepository
{
    public Task<IReadOnlyList<ProviderRecord>> ListAsync(CancellationToken cancellationToken = default) => inner.ListAsync(cancellationToken);
    public Task<ProviderRecord?> GetAsync(string key, CancellationToken cancellationToken = default) => inner.GetAsync(key, cancellationToken);
    public Task AddAsync(ProviderRecord provider, CancellationToken cancellationToken = default) => inner.AddAsync(provider, cancellationToken);
    public Task UpdateAsync(ProviderRecord provider, CancellationToken cancellationToken = default) => inner.UpdateAsync(provider, cancellationToken);
    public Task SetEnabledAsync(string key, bool enabled, CancellationToken cancellationToken = default) => inner.SetEnabledAsync(key, enabled, cancellationToken);
    public Task DeleteAsync(string key, CancellationToken cancellationToken = default) => inner.DeleteAsync(key, cancellationToken);
    public Task SaveCategoryPolicyAsync(string providerKey, CatalogType catalog, CategoryPolicy policy, CancellationToken cancellationToken = default) => inner.SaveCategoryPolicyAsync(providerKey, catalog, policy, cancellationToken);
    public Task<CategoryPolicy> GetCategoryPolicyAsync(string providerKey, CatalogType catalog, CancellationToken cancellationToken = default) => inner.GetCategoryPolicyAsync(providerKey, catalog, cancellationToken);
    public Task<IReadOnlyList<CategorySummary>> GetCategorySummariesAsync(string providerKey, CancellationToken cancellationToken = default) => inner.GetCategorySummariesAsync(providerKey, cancellationToken);

    public async Task<IReadOnlyList<ProviderCategory>> ListCategoriesAsync(
        string providerKey,
        CatalogType catalog,
        bool includeMissing = true,
        CancellationToken cancellationToken = default)
    {
        var categories = await inner.ListCategoriesAsync(providerKey, catalog, includeMissing, cancellationToken);
        var parents = await hierarchy.ReadAsync(providerKey, catalog, cancellationToken);
        return categories.Select(category => parents.TryGetValue(category.RemoteId, out var parent)
            ? category with { ParentRemoteId = parent }
            : category with { ParentRemoteId = null }).ToArray();
    }

    public async Task SyncCategoriesAsync(
        string providerKey,
        CatalogType catalog,
        IReadOnlyList<ProviderCategory> categories,
        CancellationToken cancellationToken = default)
    {
        await inner.SyncCategoriesAsync(providerKey, catalog, categories, cancellationToken);
        await hierarchy.ReplaceAsync(providerKey, catalog, categories, cancellationToken);
    }
}
