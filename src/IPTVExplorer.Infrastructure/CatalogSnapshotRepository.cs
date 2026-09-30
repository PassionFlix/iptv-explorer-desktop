using System.Globalization;
using System.Text.Json;
using IPTVExplorer.Core;
using Microsoft.Data.Sqlite;

namespace IPTVExplorer.Infrastructure;

/// <summary>One atomic generation containing all three catalogs, with provider-scoped normalized rows.</summary>
public sealed class CatalogSnapshotRepository(SqliteConnectionFactory connections)
{
    public async Task<DateTimeOffset?> RefreshedAtAsync(string providerKey, CancellationToken token = default)
    {
        await using var connection = connections.Create(); await connection.OpenAsync(token);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT refreshed_at FROM catalog_snapshots WHERE provider_key=$key";
        command.Parameters.AddWithValue("$key", providerKey);
        return await command.ExecuteScalarAsync(token) is string value ? DateTimeOffset.Parse(value, CultureInfo.InvariantCulture) : null;
    }

    public async Task<long> GenerationAsync(string providerKey, CancellationToken token = default)
    {
        await using var connection = connections.Create(); await connection.OpenAsync(token);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT generation FROM catalog_snapshots WHERE provider_key=$key";
        command.Parameters.AddWithValue("$key", providerKey);
        return Convert.ToInt64(await command.ExecuteScalarAsync(token), CultureInfo.InvariantCulture);
    }

    public async Task ReplaceAsync(string providerKey, IReadOnlyDictionary<CatalogType, IReadOnlyList<CatalogItem>> catalogs,
        ProviderSecret? secret, DateTimeOffset refreshedAt, CancellationToken token = default)
    {
        if (!ProviderKey.IsValid(providerKey) || Enum.GetValues<CatalogType>().Any(type => !catalogs.ContainsKey(type)))
            throw new InvalidDataException("A complete provider snapshot is required.");
        // Sanitize before writing even to a journal/WAL. Raw Metadata is discarded, not serialized.
        var clean = catalogs.ToDictionary(pair => pair.Key, pair => pair.Value.Select(item => CatalogSanitizer.Item(item, secret)).ToArray());
        await using var connection = connections.Create();
        // A private WAL writer lets existing UI readers keep reading the committed generation.
        // Do not take shared-cache table locks for the duration of a large bulk publication.
        // This infrequent writer is unpooled; normal application/index pools are unchanged.
        connection.ConnectionString = new SqliteConnectionStringBuilder(connection.ConnectionString)
        {
            Cache = SqliteCacheMode.Private, Pooling = false
        }.ToString();
        await connection.OpenAsync(token);
        await using (var pragma = connection.CreateCommand()) { pragma.CommandText = "PRAGMA foreign_keys=ON"; await pragma.ExecuteNonQueryAsync(token); }
        await using var transaction = connection.BeginTransaction();
        await Execute("INSERT INTO catalog_snapshots(provider_key,refreshed_at,generation) VALUES($key,$now,1) ON CONFLICT(provider_key) DO UPDATE SET refreshed_at=excluded.refreshed_at,generation=generation+1");
        await Execute("DELETE FROM catalog_items WHERE provider_key=$key");
        await using var insert = connection.CreateCommand(); insert.Transaction = transaction;
        insert.CommandText = "INSERT OR REPLACE INTO catalog_items VALUES($key,$catalog,$id,$category,$title,$json)";
        insert.Parameters.AddWithValue("$key", providerKey);
        foreach (var parameter in new[] { "$catalog", "$id", "$category", "$title", "$json" }) insert.Parameters.Add(parameter, SqliteType.Text);
        foreach (var pair in clean)
        {
            foreach (var item in pair.Value)
            {
                token.ThrowIfCancellationRequested();
                insert.Parameters["$catalog"].Value = Db(pair.Key); insert.Parameters["$id"].Value = item.Id;
                insert.Parameters["$category"].Value = item.CategoryId!; insert.Parameters["$title"].Value = item.Title;
                insert.Parameters["$json"].Value = JsonSerializer.Serialize(item);
                await insert.ExecuteNonQueryAsync(token);
            }
        }
        // Retain known category names/selections. Unknown ids remain navigable without a categories API.
        await Execute("""
            INSERT INTO provider_categories(provider_key,catalog_type,remote_id,category_name,normalized_name,selected,present,created_at,updated_at)
            SELECT DISTINCT i.provider_key,i.catalog_type,i.category_id,
                CASE WHEN i.category_id='_uncategorized' THEN 'Sans catégorie' ELSE 'Catégorie ' || i.category_id END,
                i.category_id,CASE WHEN p.mode='all' THEN 1 ELSE 0 END,1,$now,$now
            FROM catalog_items i JOIN provider_category_policy p ON p.provider_key=i.provider_key AND p.catalog_type=i.catalog_type
            WHERE i.provider_key=$key
            ON CONFLICT(provider_key,catalog_type,remote_id) DO UPDATE SET present=1;
            UPDATE provider_category_policy SET index_dirty=1 WHERE provider_key=$key AND catalog_type IN ('vod','series');
            INSERT INTO series_artwork(provider_key,remote_id,image_url,checked_at)
            SELECT provider_key,remote_id,json_extract(item_json,'$.ImageUrl'),$now FROM catalog_items
            WHERE provider_key=$key AND catalog_type='series' AND json_extract(item_json,'$.ImageUrl') IS NOT NULL
            ON CONFLICT(provider_key,remote_id) DO UPDATE SET image_url=excluded.image_url,checked_at=excluded.checked_at
            WHERE series_artwork.image_url IS NULL;
            """);
        await transaction.CommitAsync(token);

        async Task Execute(string sql)
        {
            await using var command = connection.CreateCommand(); command.Transaction = transaction; command.CommandText = sql;
            command.Parameters.AddWithValue("$key", providerKey); command.Parameters.AddWithValue("$now", refreshedAt.ToString("O"));
            await command.ExecuteNonQueryAsync(token);
        }
    }

    public async Task<IReadOnlyList<CatalogItem>> ReadAsync(string providerKey, CatalogType catalog, string? categoryId = null, CancellationToken token = default)
    {
        await using var connection = connections.Create(); await connection.OpenAsync(token);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT item_json FROM catalog_items WHERE provider_key=$key AND catalog_type=$catalog AND ($category IS NULL OR category_id=$category) ORDER BY title,remote_id";
        command.Parameters.AddWithValue("$key", providerKey); command.Parameters.AddWithValue("$catalog", Db(catalog));
        command.Parameters.AddWithValue("$category", (object?)categoryId ?? DBNull.Value);
        var items = new List<CatalogItem>();
        await using var reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token)) items.Add(JsonSerializer.Deserialize<CatalogItem>(reader.GetString(0))!);
        return items;
    }

    public async Task<CatalogItem?> FindAsync(string providerKey, CatalogType catalog, string id, CancellationToken token = default)
    {
        await using var connection = connections.Create(); await connection.OpenAsync(token);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT item_json FROM catalog_items WHERE provider_key=$key AND catalog_type=$catalog AND remote_id=$id";
        command.Parameters.AddWithValue("$key", providerKey); command.Parameters.AddWithValue("$catalog", Db(catalog)); command.Parameters.AddWithValue("$id", id);
        return await command.ExecuteScalarAsync(token) is string json ? JsonSerializer.Deserialize<CatalogItem>(json) : null;
    }

    public async Task<CatalogPage<CatalogItem>> PageAsync(string key, CatalogType catalog, string category, int page, CancellationToken token = default)
    {
        const int size = 100; page = Math.Max(1, page);
        await using var connection = connections.Create(); await connection.OpenAsync(token);
        await using var transaction = connection.BeginTransaction(deferred: true);
        await using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.Parameters.AddWithValue("$key", key); command.Parameters.AddWithValue("$catalog", Db(catalog)); command.Parameters.AddWithValue("$category", category);
        command.CommandText = "SELECT COUNT(*) FROM catalog_items WHERE provider_key=$key AND catalog_type=$catalog AND category_id=$category";
        var total = Convert.ToInt32(await command.ExecuteScalarAsync(token), CultureInfo.InvariantCulture);
        command.CommandText = "SELECT item_json FROM catalog_items WHERE provider_key=$key AND catalog_type=$catalog AND category_id=$category ORDER BY title,remote_id LIMIT $size OFFSET $offset";
        command.Parameters.AddWithValue("$size", size); command.Parameters.AddWithValue("$offset", (page - 1) * size);
        var items = new List<CatalogItem>();
        await using (var reader = await command.ExecuteReaderAsync(token))
            while (await reader.ReadAsync(token)) items.Add(JsonSerializer.Deserialize<CatalogItem>(reader.GetString(0))!);
        await transaction.CommitAsync(token);
        return new(items, page, size, total, (int)Math.Ceiling(total / (double)size));
    }

    public async Task<IReadOnlyList<SearchHit>> SearchDocumentsAsync(string providerKey, CancellationToken token = default)
    {
        if (await RefreshedAtAsync(providerKey, token) is null) throw new InvalidOperationException("Aucun snapshot local. Utilisez Actualiser le catalogue dans Paramètres.");
        await using var connection = connections.Create(); await connection.OpenAsync(token);
        await using var command = connection.CreateCommand();
        // Both catalogs are read in the same SQLite statement/generation, not two independently timed reads.
        command.CommandText = """
            SELECT i.catalog_type,i.item_json FROM catalog_items i
            JOIN provider_category_policy p ON p.provider_key=i.provider_key AND p.catalog_type=i.catalog_type
            LEFT JOIN provider_categories c ON c.provider_key=i.provider_key AND c.catalog_type=i.catalog_type AND c.remote_id=i.category_id
            WHERE i.provider_key=$key AND i.catalog_type IN ('vod','series')
              AND (p.mode='all' OR (p.mode='custom' AND c.selected=1))
            """;
        command.Parameters.AddWithValue("$key", providerKey);
        var result = new List<SearchHit>();
        await using var reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token))
        {
            var item = JsonSerializer.Deserialize<CatalogItem>(reader.GetString(1))!;
            result.Add(new(providerKey, Enum.Parse<CatalogType>(reader.GetString(0), true), item.Id, item.Title, item.ImageUrl, item.AddedAt, item.BackdropUrl));
        }
        return result;
    }

    public async Task<T?> DetailAsync<T>(string providerKey, CatalogType catalog, string id, CancellationToken token = default) where T : class
    {
        await using var connection = connections.Create(); await connection.OpenAsync(token);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT detail_json FROM media_details WHERE provider_key=$key AND catalog_type=$catalog AND remote_id=$id";
        command.Parameters.AddWithValue("$key", providerKey); command.Parameters.AddWithValue("$catalog", Db(catalog)); command.Parameters.AddWithValue("$id", id);
        return await command.ExecuteScalarAsync(token) is string json ? JsonSerializer.Deserialize<T>(json) : null;
    }

    public Task SaveAsync(string providerKey, VodDetails detail, ProviderSecret? secret, CancellationToken token = default) =>
        SaveDetailAsync(providerKey, CatalogType.Vod, detail.Id, JsonSerializer.Serialize(CatalogSanitizer.Vod(detail, secret)), token);
    public Task SaveAsync(string providerKey, SeriesDetails detail, ProviderSecret? secret, CancellationToken token = default) =>
        SaveDetailAsync(providerKey, CatalogType.Series, detail.Id, JsonSerializer.Serialize(CatalogSanitizer.Series(detail, secret)), token);

    private async Task SaveDetailAsync(string providerKey, CatalogType catalog, string id, string json, CancellationToken token)
    {
        await using var connection = connections.Create(); await connection.OpenAsync(token);
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA foreign_keys=ON; INSERT INTO media_details VALUES($key,$catalog,$id,$json) ON CONFLICT(provider_key,catalog_type,remote_id) DO UPDATE SET detail_json=excluded.detail_json";
        command.Parameters.AddWithValue("$key", providerKey); command.Parameters.AddWithValue("$catalog", Db(catalog));
        command.Parameters.AddWithValue("$id", id); command.Parameters.AddWithValue("$json", json);
        await command.ExecuteNonQueryAsync(token);
    }
    private static string Db(CatalogType catalog) => catalog.ToString().ToLowerInvariant();
}
