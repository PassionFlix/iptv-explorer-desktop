using System.Globalization;
using System.Text;
using IPTVExplorer.Core;
using Microsoft.Data.Sqlite;

namespace IPTVExplorer.Infrastructure;

public sealed class SearchService(AppPaths paths) : ISearchService
{
    public async Task<CatalogPage<SearchHit>> SearchAsync(string? providerKey, CatalogType catalog, string query, int page, int pageSize, CancellationToken cancellationToken = default)
    {
        if (providerKey is null) return new CatalogPage<SearchHit>([], page, pageSize, 0, 0);
        if (!ProviderKey.IsValid(providerKey)) throw new ArgumentException("Invalid provider key.", nameof(providerKey));
        page = Math.Max(1, page); pageSize = Math.Clamp(pageSize, 1, 100);
        var path = paths.SearchIndex(providerKey);
        if (!File.Exists(path)) return new CatalogPage<SearchHit>([], page, pageSize, 0, 0);
        var builder = new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = true };
        await using var connection = new SqliteConnection(builder.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        var normalized = Normalize(query);
        var total = 0;
        await using (var count = connection.CreateCommand())
        {
            count.CommandText = "SELECT COUNT(*) FROM search_documents WHERE catalog_type=$catalog AND normalized_title LIKE $query ESCAPE '\\'";
            count.Parameters.AddWithValue("$catalog", catalog.ToString().ToLowerInvariant()); count.Parameters.AddWithValue("$query", "%" + EscapeLike(normalized) + "%");
            total = Convert.ToInt32(await count.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
        }
        var items = new List<SearchHit>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT remote_id,title,image_url FROM search_documents WHERE catalog_type=$catalog AND normalized_title LIKE $query ESCAPE '\\' ORDER BY normalized_title LIMIT $limit OFFSET $offset";
            command.Parameters.AddWithValue("$catalog", catalog.ToString().ToLowerInvariant()); command.Parameters.AddWithValue("$query", "%" + EscapeLike(normalized) + "%"); command.Parameters.AddWithValue("$limit", pageSize); command.Parameters.AddWithValue("$offset", (page - 1) * pageSize);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken)) items.Add(new SearchHit(providerKey, catalog, reader.GetString(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2)));
        }
        return new CatalogPage<SearchHit>(items, page, pageSize, total, (int)Math.Ceiling(total / (double)pageSize));
    }

    public static string Normalize(string value)
    {
        var decomposed = value.Trim().Normalize(NormalizationForm.FormD);
        var chars = decomposed.Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark);
        return new string(chars.ToArray()).Normalize(NormalizationForm.FormC).ToUpperInvariant();
    }
    private static string EscapeLike(string value) => value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("%", "\\%", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal);
}

public sealed class AtomicSearchIndex(AppPaths paths)
{
    public async Task ReplaceAsync(string providerKey, IEnumerable<SearchHit> documents, CancellationToken cancellationToken = default)
    {
        var destination = paths.SearchIndex(providerKey);
        var temporary = destination + ".tmp";
        File.Delete(temporary);
        var builder = new SqliteConnectionStringBuilder { DataSource = temporary, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false };
        await using (var connection = new SqliteConnection(builder.ConnectionString))
        {
            await connection.OpenAsync(cancellationToken);
            await using var create = connection.CreateCommand();
            create.CommandText = "CREATE TABLE search_documents(catalog_type TEXT NOT NULL,remote_id TEXT NOT NULL,title TEXT NOT NULL,normalized_title TEXT NOT NULL,image_url TEXT,PRIMARY KEY(catalog_type,remote_id)); CREATE INDEX ix_search_title ON search_documents(catalog_type,normalized_title);";
            await create.ExecuteNonQueryAsync(cancellationToken);
            await using var transaction = connection.BeginTransaction();
            foreach (var item in documents)
            {
                await using var insert = connection.CreateCommand(); insert.Transaction = transaction;
                insert.CommandText = "INSERT OR REPLACE INTO search_documents VALUES($catalog,$id,$title,$normalized,$image)";
                insert.Parameters.AddWithValue("$catalog", item.Catalog.ToString().ToLowerInvariant()); insert.Parameters.AddWithValue("$id", item.RemoteId); insert.Parameters.AddWithValue("$title", item.Title); insert.Parameters.AddWithValue("$normalized", SearchService.Normalize(item.Title)); insert.Parameters.AddWithValue("$image", (object?)item.ImageUrl ?? DBNull.Value);
                await insert.ExecuteNonQueryAsync(cancellationToken);
            }
            await transaction.CommitAsync(cancellationToken);
            await using var check = connection.CreateCommand(); check.CommandText = "PRAGMA quick_check";
            if (!string.Equals(Convert.ToString(await check.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture), "ok", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("The temporary search index failed quick_check.");
        }
        if (File.Exists(destination))
        {
            var backup = destination + ".previous";
            File.Delete(backup);
            File.Replace(temporary, destination, backup, ignoreMetadataErrors: true);
            File.Delete(backup);
        }
        else
        {
            File.Move(temporary, destination);
        }
    }
}
