using System.Globalization;
using IPTVExplorer.Core;
using Microsoft.Data.Sqlite;

namespace IPTVExplorer.Infrastructure;

public sealed record FavoriteItem(
    string ProviderKey,
    CatalogType Catalog,
    string MediaId,
    string Title,
    string? ImageUrl = null,
    string? Extension = null,
    string? CategoryId = null,
    DateTimeOffset? CreatedAt = null,
    DateTimeOffset? UpdatedAt = null);

public sealed class FavoriteRepository(SqliteConnectionFactory connections)
{
    private readonly SemaphoreSlim _initializationLock = new(1, 1);
    private volatile bool _initialized;

    public async Task<IReadOnlyList<FavoriteItem>> ListAsync(
        string providerKey,
        CatalogType? catalog = null,
        int limit = 500,
        CancellationToken cancellationToken = default)
    {
        ValidateProvider(providerKey);
        if (catalog is not null && !Enum.IsDefined(catalog.Value)) throw new ArgumentException("Invalid catalog type.", nameof(catalog));
        await EnsureInitializedAsync(cancellationToken);
        limit = Math.Clamp(limit, 1, 1000);

        await using var connection = connections.Create();
        await connection.OpenAsync(cancellationToken);
        await ConfigureAsync(connection, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT provider_key,catalog_type,media_id,title,image_url,extension,category_id,created_at,updated_at
            FROM media_favorites
            WHERE provider_key=$provider AND ($catalog IS NULL OR catalog_type=$catalog)
            ORDER BY created_at DESC,title COLLATE NOCASE,media_id
            LIMIT $limit
            """;
        command.Parameters.AddWithValue("$provider", providerKey);
        command.Parameters.AddWithValue("$catalog", catalog is null ? DBNull.Value : Db(catalog.Value));
        command.Parameters.AddWithValue("$limit", limit);

        var result = new List<FavoriteItem>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) result.Add(Read(reader));
        return result;
    }

    public async Task UpsertAsync(FavoriteItem item, CancellationToken cancellationToken = default)
    {
        ValidateProvider(item.ProviderKey);
        ValidateCatalog(item.Catalog);
        ValidateOpaqueId(item.MediaId, nameof(item.MediaId));
        ValidateOpaqueId(item.CategoryId, nameof(item.CategoryId), optional: true);
        await EnsureInitializedAsync(cancellationToken);

        var title = CleanTitle(item.Title);
        var image = MediaArtwork.SafeImageUrl(item.ImageUrl);
        var extension = CleanExtension(item.Extension);
        var now = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);

        await using var connection = connections.Create();
        await connection.OpenAsync(cancellationToken);
        await ConfigureAsync(connection, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO media_favorites(provider_key,catalog_type,media_id,title,image_url,extension,category_id,created_at,updated_at)
            VALUES($provider,$catalog,$media,$title,$image,$extension,$category,$now,$now)
            ON CONFLICT(provider_key,catalog_type,media_id) DO UPDATE SET
                title=excluded.title,image_url=excluded.image_url,extension=excluded.extension,
                category_id=excluded.category_id,updated_at=excluded.updated_at
            """;
        command.Parameters.AddWithValue("$provider", item.ProviderKey);
        command.Parameters.AddWithValue("$catalog", Db(item.Catalog));
        command.Parameters.AddWithValue("$media", item.MediaId);
        command.Parameters.AddWithValue("$title", title);
        command.Parameters.AddWithValue("$image", (object?)image ?? DBNull.Value);
        command.Parameters.AddWithValue("$extension", (object?)extension ?? DBNull.Value);
        command.Parameters.AddWithValue("$category", (object?)item.CategoryId ?? DBNull.Value);
        command.Parameters.AddWithValue("$now", now);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task RemoveAsync(string providerKey, CatalogType catalog, string mediaId, CancellationToken cancellationToken = default)
    {
        ValidateProvider(providerKey);
        ValidateCatalog(catalog);
        ValidateOpaqueId(mediaId, nameof(mediaId));
        await EnsureInitializedAsync(cancellationToken);

        await using var connection = connections.Create();
        await connection.OpenAsync(cancellationToken);
        await ConfigureAsync(connection, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM media_favorites WHERE provider_key=$provider AND catalog_type=$catalog AND media_id=$media";
        command.Parameters.AddWithValue("$provider", providerKey);
        command.Parameters.AddWithValue("$catalog", Db(catalog));
        command.Parameters.AddWithValue("$media", mediaId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<bool> ContainsAsync(string providerKey, CatalogType catalog, string mediaId, CancellationToken cancellationToken = default)
    {
        ValidateProvider(providerKey);
        ValidateCatalog(catalog);
        ValidateOpaqueId(mediaId, nameof(mediaId));
        await EnsureInitializedAsync(cancellationToken);

        await using var connection = connections.Create();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM media_favorites WHERE provider_key=$provider AND catalog_type=$catalog AND media_id=$media LIMIT 1";
        command.Parameters.AddWithValue("$provider", providerKey);
        command.Parameters.AddWithValue("$catalog", Db(catalog));
        command.Parameters.AddWithValue("$media", mediaId);
        return await command.ExecuteScalarAsync(cancellationToken) is not null;
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
                CREATE TABLE IF NOT EXISTS media_favorites (
                    provider_key TEXT NOT NULL REFERENCES providers(provider_key) ON DELETE CASCADE,
                    catalog_type TEXT NOT NULL CHECK (catalog_type IN ('live','vod','series')),
                    media_id TEXT NOT NULL,
                    title TEXT NOT NULL,
                    image_url TEXT,
                    extension TEXT,
                    category_id TEXT,
                    created_at TEXT NOT NULL,
                    updated_at TEXT NOT NULL,
                    PRIMARY KEY(provider_key,catalog_type,media_id)
                );
                CREATE INDEX IF NOT EXISTS ix_media_favorites_provider_created
                    ON media_favorites(provider_key,created_at DESC);
                INSERT OR IGNORE INTO schema_migrations(version,applied_at)
                    VALUES(6,strftime('%Y-%m-%dT%H:%M:%fZ','now'));
                """;
            await command.ExecuteNonQueryAsync(cancellationToken);
            _initialized = true;
        }
        finally
        {
            _initializationLock.Release();
        }
    }

    private static FavoriteItem Read(SqliteDataReader reader) => new(
        reader.GetString(0),
        Enum.Parse<CatalogType>(reader.GetString(1), true),
        reader.GetString(2),
        reader.GetString(3),
        reader.IsDBNull(4) ? null : reader.GetString(4),
        reader.IsDBNull(5) ? null : reader.GetString(5),
        reader.IsDBNull(6) ? null : reader.GetString(6),
        DateTimeOffset.Parse(reader.GetString(7), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
        DateTimeOffset.Parse(reader.GetString(8), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind));

    private static async Task ConfigureAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000;";
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static void ValidateProvider(string providerKey)
    {
        if (!ProviderKey.IsValid(providerKey)) throw new ArgumentException("Invalid provider key.", nameof(providerKey));
    }

    private static void ValidateCatalog(CatalogType catalog)
    {
        if (!Enum.IsDefined(catalog)) throw new ArgumentException("Invalid catalog type.", nameof(catalog));
    }

    private static void ValidateOpaqueId(string? value, string parameter, bool optional = false)
    {
        if (optional && string.IsNullOrWhiteSpace(value)) return;
        if (string.IsNullOrWhiteSpace(value) || value.Length > 512 || Uri.TryCreate(value, UriKind.Absolute, out _))
            throw new ArgumentException("Invalid opaque media reference.", parameter);
    }

    private static string CleanTitle(string? value)
    {
        var cleaned = CatalogSanitizer.Text(value, null) ?? "Contenu";
        if (cleaned.Length > 300) cleaned = cleaned[..300].TrimEnd();
        return cleaned.Length == 0 ? "Contenu" : cleaned;
    }

    private static string? CleanExtension(string? value)
    {
        var extension = value?.Trim().TrimStart('.');
        return extension is { Length: > 0 and <= 20 } && extension.All(char.IsAsciiLetterOrDigit) ? extension : null;
    }

    private static string Db(CatalogType catalog) => catalog.ToString().ToLowerInvariant();
}
