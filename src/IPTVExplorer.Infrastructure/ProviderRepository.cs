using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using IPTVExplorer.Core;
using Microsoft.Data.Sqlite;

namespace IPTVExplorer.Infrastructure;

public sealed partial class ProviderRepository(SqliteConnectionFactory connections) : IProviderRepository
{
    public async Task<IReadOnlyList<ProviderRecord>> ListAsync(CancellationToken cancellationToken = default)
    {
        var result = new List<ProviderRecord>();
        await using var connection = connections.Create();
        await connection.OpenAsync(cancellationToken);
        await EnableForeignKeys(connection, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT provider_key,type,display_name,server_url,secret_reference,portal_path,enabled,status FROM providers ORDER BY display_name";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) result.Add(ReadProvider(reader));
        return result;
    }

    public async Task<ProviderRecord?> GetAsync(string key, CancellationToken cancellationToken = default)
    {
        RequireKey(key);
        await using var connection = connections.Create();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT provider_key,type,display_name,server_url,secret_reference,portal_path,enabled,status FROM providers WHERE provider_key=$key";
        command.Parameters.AddWithValue("$key", key);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadProvider(reader) : null;
    }

    public async Task AddAsync(ProviderRecord provider, CancellationToken cancellationToken = default)
    {
        RequireKey(provider.Key);
        if (provider.Name.Length is < 1 or > 80) throw new ArgumentException("Provider name must contain 1 to 80 characters.", nameof(provider));
        if (provider.ServerUri.Scheme is not ("http" or "https") || !string.IsNullOrEmpty(provider.ServerUri.UserInfo)) throw new ArgumentException("Only HTTP(S) provider URLs without embedded credentials are supported.", nameof(provider));
        if (string.IsNullOrWhiteSpace(provider.SecretReference)) throw new ArgumentException("A secret reference is required.", nameof(provider));
        var now = DateTimeOffset.UtcNow.ToString("O");
        await using var connection = connections.Create();
        await connection.OpenAsync(cancellationToken);
        await EnableForeignKeys(connection, cancellationToken);
        await using var transaction = connection.BeginTransaction();
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO providers(provider_key,type,display_name,server_url,portal_path,secret_reference,enabled,status,created_at,updated_at)
                VALUES($key,$type,$name,$url,$portal,$secret,$enabled,$status,$now,$now)
                """;
            command.Parameters.AddWithValue("$key", provider.Key);
            command.Parameters.AddWithValue("$type", Db(provider.Type));
            command.Parameters.AddWithValue("$name", provider.Name.Trim());
            command.Parameters.AddWithValue("$url", provider.ServerUri.GetLeftPart(UriPartial.Authority) + provider.ServerUri.AbsolutePath.TrimEnd('/'));
            command.Parameters.AddWithValue("$portal", NormalizePortalPath(provider.PortalPath));
            command.Parameters.AddWithValue("$secret", provider.SecretReference);
            command.Parameters.AddWithValue("$enabled", provider.Enabled ? 1 : 0);
            command.Parameters.AddWithValue("$status", provider.Status);
            command.Parameters.AddWithValue("$now", now);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        foreach (var catalog in Enum.GetValues<CatalogType>())
        {
            await using var policy = connection.CreateCommand();
            policy.Transaction = transaction;
            policy.CommandText = """
                INSERT INTO provider_category_policy(provider_key,catalog_type,mode,selection_updated_at,index_dirty,created_at,updated_at)
                VALUES($key,$catalog,'all',$now,0,$now,$now)
                """;
            policy.Parameters.AddWithValue("$key", provider.Key);
            policy.Parameters.AddWithValue("$catalog", Db(catalog));
            policy.Parameters.AddWithValue("$now", now);
            await policy.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task UpdateAsync(ProviderRecord provider, CancellationToken cancellationToken = default)
    {
        RequireKey(provider.Key);
        if (provider.Name.Trim().Length is < 1 or > 80) throw new ArgumentException("Provider name must contain 1 to 80 characters.", nameof(provider));
        if (provider.ServerUri.Scheme is not ("http" or "https") || !string.IsNullOrEmpty(provider.ServerUri.UserInfo)) throw new ArgumentException("Only HTTP(S) provider URLs without embedded credentials are supported.", nameof(provider));
        await using var connection = connections.Create();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE providers SET type=$type,display_name=$name,server_url=$url,portal_path=$portal,
              secret_reference=$secret,enabled=$enabled,status=$status,updated_at=$now WHERE provider_key=$key
            """;
        command.Parameters.AddWithValue("$key", provider.Key);
        command.Parameters.AddWithValue("$type", Db(provider.Type));
        command.Parameters.AddWithValue("$name", provider.Name.Trim());
        command.Parameters.AddWithValue("$url", provider.ServerUri.GetLeftPart(UriPartial.Authority) + provider.ServerUri.AbsolutePath.TrimEnd('/'));
        command.Parameters.AddWithValue("$portal", NormalizePortalPath(provider.PortalPath));
        command.Parameters.AddWithValue("$secret", provider.SecretReference);
        command.Parameters.AddWithValue("$enabled", provider.Enabled ? 1 : 0);
        command.Parameters.AddWithValue("$status", provider.Status);
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1) throw new KeyNotFoundException("Provider was not found.");
    }

    public async Task SetEnabledAsync(string key, bool enabled, CancellationToken cancellationToken = default)
    {
        RequireKey(key);
        await using var connection = connections.Create(); await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE providers SET enabled=$enabled,updated_at=$now WHERE provider_key=$key";
        command.Parameters.AddWithValue("$enabled", enabled ? 1 : 0); command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O")); command.Parameters.AddWithValue("$key", key);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1) throw new KeyNotFoundException("Provider was not found.");
    }

    public async Task DeleteAsync(string key, CancellationToken cancellationToken = default)
    {
        RequireKey(key);
        await using var connection = connections.Create(); await connection.OpenAsync(cancellationToken); await EnableForeignKeys(connection, cancellationToken);
        await using var command = connection.CreateCommand(); command.CommandText = "DELETE FROM providers WHERE provider_key=$key"; command.Parameters.AddWithValue("$key", key);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1) throw new KeyNotFoundException("Provider was not found.");
    }

    public async Task<IReadOnlyList<ProviderCategory>> ListCategoriesAsync(string providerKey, CatalogType catalog, bool includeMissing = true, CancellationToken cancellationToken = default)
    {
        RequireKey(providerKey);
        var result = new List<ProviderCategory>();
        await using var connection = connections.Create();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT remote_id,category_name,normalized_name,selected,present,needs_review,last_seen_at,technical
            FROM provider_categories WHERE provider_key=$key AND catalog_type=$catalog
              AND ($missing=1 OR present=1) ORDER BY normalized_name
            """;
        command.Parameters.AddWithValue("$key", providerKey);
        command.Parameters.AddWithValue("$catalog", Db(catalog));
        command.Parameters.AddWithValue("$missing", includeMissing ? 1 : 0);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new ProviderCategory(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetBoolean(3), reader.GetBoolean(4), reader.GetBoolean(5), reader.IsDBNull(6) ? null : DateTimeOffset.Parse(reader.GetString(6), CultureInfo.InvariantCulture), reader.GetBoolean(7)));
        }
        return result;
    }

    public async Task SyncCategoriesAsync(string providerKey, CatalogType catalog, IReadOnlyList<ProviderCategory> categories, CancellationToken cancellationToken = default)
    {
        RequireKey(providerKey);
        var sanitized = categories
            .Where(c => !string.IsNullOrWhiteSpace(c.RemoteId) && !string.IsNullOrWhiteSpace(c.Name))
            .GroupBy(c => c.RemoteId, StringComparer.Ordinal)
            .Select(g => g.First() with { Name = g.First().Name.Trim(), NormalizedName = NormalizeCategoryName(g.First().Name) })
            .ToArray();
        var now = DateTimeOffset.UtcNow.ToString("O");
        await using var connection = connections.Create();
        await connection.OpenAsync(cancellationToken);
        await EnableForeignKeys(connection, cancellationToken);
        await using var transaction = connection.BeginTransaction();

        await using (var missing = connection.CreateCommand())
        {
            missing.Transaction = transaction;
            missing.CommandText = "UPDATE provider_categories SET present=0,needs_review=1,updated_at=$now WHERE provider_key=$key AND catalog_type=$catalog";
            missing.Parameters.AddWithValue("$now", now);
            missing.Parameters.AddWithValue("$key", providerKey);
            missing.Parameters.AddWithValue("$catalog", Db(catalog));
            await missing.ExecuteNonQueryAsync(cancellationToken);
        }

        var mode = await ReadPolicyMode(connection, transaction, providerKey, catalog, cancellationToken);
        foreach (var category in sanitized)
        {
            await MatchRenamedIdentifier(connection, transaction, providerKey, catalog, category, cancellationToken);
            await using var upsert = connection.CreateCommand();
            upsert.Transaction = transaction;
            upsert.CommandText = """
                INSERT INTO provider_categories(provider_key,catalog_type,remote_id,category_name,normalized_name,selected,present,needs_review,technical,last_seen_at,created_at,updated_at)
                VALUES($key,$catalog,$id,$name,$normalized,$selected,1,0,$technical,$now,$now,$now)
                ON CONFLICT(provider_key,catalog_type,remote_id) DO UPDATE SET
                  category_name=excluded.category_name,normalized_name=excluded.normalized_name,present=1,needs_review=0,technical=excluded.technical,last_seen_at=excluded.last_seen_at,updated_at=excluded.updated_at
                """;
            upsert.Parameters.AddWithValue("$key", providerKey);
            upsert.Parameters.AddWithValue("$catalog", Db(catalog));
            upsert.Parameters.AddWithValue("$id", category.RemoteId);
            upsert.Parameters.AddWithValue("$name", category.Name);
            upsert.Parameters.AddWithValue("$normalized", category.NormalizedName);
            upsert.Parameters.AddWithValue("$selected", mode == "all" ? 1 : 0);
            upsert.Parameters.AddWithValue("$technical", category.Technical ? 1 : 0);
            upsert.Parameters.AddWithValue("$now", now);
            await upsert.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var policy = connection.CreateCommand())
        {
            policy.Transaction = transaction;
            policy.CommandText = "UPDATE provider_category_policy SET last_synced_at=$now,updated_at=$now WHERE provider_key=$key AND catalog_type=$catalog";
            policy.Parameters.AddWithValue("$now", now);
            policy.Parameters.AddWithValue("$key", providerKey);
            policy.Parameters.AddWithValue("$catalog", Db(catalog));
            await policy.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task SaveCategoryPolicyAsync(string providerKey, CatalogType catalog, CategoryPolicy policy, CancellationToken cancellationToken = default)
    {
        RequireKey(providerKey);
        var now = DateTimeOffset.UtcNow.ToString("O");
        await using var connection = connections.Create();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction();
        await using (var update = connection.CreateCommand())
        {
            update.Transaction = transaction;
            update.CommandText = "UPDATE provider_category_policy SET mode=$mode,selection_updated_at=$now,index_dirty=1,updated_at=$now WHERE provider_key=$key AND catalog_type=$catalog";
            update.Parameters.AddWithValue("$mode", Db(policy.Mode));
            update.Parameters.AddWithValue("$now", now);
            update.Parameters.AddWithValue("$key", providerKey);
            update.Parameters.AddWithValue("$catalog", Db(catalog));
            if (await update.ExecuteNonQueryAsync(cancellationToken) != 1) throw new InvalidOperationException("Category policy was not found.");
        }
        await using (var clear = connection.CreateCommand())
        {
            clear.Transaction = transaction;
            clear.CommandText = "UPDATE provider_categories SET selected=$selected,updated_at=$now WHERE provider_key=$key AND catalog_type=$catalog";
            clear.Parameters.AddWithValue("$selected", policy.Mode == CategoryPolicyMode.All ? 1 : 0);
            clear.Parameters.AddWithValue("$now", now);
            clear.Parameters.AddWithValue("$key", providerKey);
            clear.Parameters.AddWithValue("$catalog", Db(catalog));
            await clear.ExecuteNonQueryAsync(cancellationToken);
        }
        if (policy.Mode == CategoryPolicyMode.Custom)
        {
            foreach (var id in policy.SelectedIds)
            {
                await using var select = connection.CreateCommand();
                select.Transaction = transaction;
                select.CommandText = "UPDATE provider_categories SET selected=1,updated_at=$now WHERE provider_key=$key AND catalog_type=$catalog AND remote_id=$id";
                select.Parameters.AddWithValue("$now", now);
                select.Parameters.AddWithValue("$key", providerKey);
                select.Parameters.AddWithValue("$catalog", Db(catalog));
                select.Parameters.AddWithValue("$id", id);
                await select.ExecuteNonQueryAsync(cancellationToken);
            }
        }
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<CategoryPolicy> GetCategoryPolicyAsync(string providerKey, CatalogType catalog, CancellationToken cancellationToken = default)
    {
        RequireKey(providerKey);
        await using var connection = connections.Create(); await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT mode FROM provider_category_policy WHERE provider_key=$key AND catalog_type=$catalog";
        command.Parameters.AddWithValue("$key", providerKey); command.Parameters.AddWithValue("$catalog", Db(catalog));
        var raw = Convert.ToString(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture) ?? throw new KeyNotFoundException("Category policy was not found.");
        var mode = Enum.Parse<CategoryPolicyMode>(raw, true);
        var selected = new HashSet<string>(StringComparer.Ordinal);
        await using var categories = connection.CreateCommand();
        categories.CommandText = "SELECT remote_id FROM provider_categories WHERE provider_key=$key AND catalog_type=$catalog AND selected=1";
        categories.Parameters.AddWithValue("$key", providerKey); categories.Parameters.AddWithValue("$catalog", Db(catalog));
        await using var reader = await categories.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) selected.Add(reader.GetString(0));
        return new CategoryPolicy(mode, selected);
    }

    public async Task<IReadOnlyList<CategorySummary>> GetCategorySummariesAsync(string providerKey, CancellationToken cancellationToken = default)
    {
        RequireKey(providerKey);
        var summaries = new List<CategorySummary>();
        await using var connection = connections.Create(); await connection.OpenAsync(cancellationToken);
        foreach (var catalog in Enum.GetValues<CatalogType>())
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT p.mode,p.index_dirty,
                  COUNT(c.id),COALESCE(SUM(CASE WHEN c.selected=1 THEN 1 ELSE 0 END),0),
                  COALESCE(SUM(CASE WHEN c.present=0 THEN 1 ELSE 0 END),0)
                FROM provider_category_policy p
                LEFT JOIN provider_categories c ON c.provider_key=p.provider_key AND c.catalog_type=p.catalog_type
                WHERE p.provider_key=$key AND p.catalog_type=$catalog GROUP BY p.mode,p.index_dirty
                """;
            command.Parameters.AddWithValue("$key", providerKey); command.Parameters.AddWithValue("$catalog", Db(catalog));
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken)) summaries.Add(new CategorySummary(catalog, Enum.Parse<CategoryPolicyMode>(reader.GetString(0), true), reader.GetInt32(3), reader.GetInt32(2), reader.GetInt32(4), reader.GetBoolean(1)));
        }
        return summaries;
    }

    public static string NormalizeCategoryName(string name)
    {
        var decomposed = name.Trim().Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder();
        foreach (var c in decomposed)
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark) builder.Append(char.ToUpperInvariant(c));
        return Whitespace().Replace(builder.ToString().Normalize(NormalizationForm.FormC), " ").Trim();
    }

    private static async Task MatchRenamedIdentifier(SqliteConnection connection, SqliteTransaction transaction, string key, CatalogType catalog, ProviderCategory category, CancellationToken cancellationToken)
    {
        await using var exists = connection.CreateCommand();
        exists.Transaction = transaction;
        exists.CommandText = "SELECT COUNT(*) FROM provider_categories WHERE provider_key=$key AND catalog_type=$catalog AND remote_id=$id";
        exists.Parameters.AddWithValue("$key", key); exists.Parameters.AddWithValue("$catalog", Db(catalog)); exists.Parameters.AddWithValue("$id", category.RemoteId);
        if (Convert.ToInt32(await exists.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture) > 0) return;

        await using var match = connection.CreateCommand();
        match.Transaction = transaction;
        match.CommandText = "SELECT id FROM provider_categories WHERE provider_key=$key AND catalog_type=$catalog AND normalized_name=$name AND present=0";
        match.Parameters.AddWithValue("$key", key); match.Parameters.AddWithValue("$catalog", Db(catalog)); match.Parameters.AddWithValue("$name", category.NormalizedName);
        var ids = new List<long>();
        await using (var reader = await match.ExecuteReaderAsync(cancellationToken)) while (await reader.ReadAsync(cancellationToken)) ids.Add(reader.GetInt64(0));
        if (ids.Count != 1) return;
        await using var rename = connection.CreateCommand();
        rename.Transaction = transaction;
        rename.CommandText = "UPDATE provider_categories SET remote_id=$remote WHERE id=$id";
        rename.Parameters.AddWithValue("$remote", category.RemoteId); rename.Parameters.AddWithValue("$id", ids[0]);
        await rename.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<string> ReadPolicyMode(SqliteConnection connection, SqliteTransaction transaction, string key, CatalogType catalog, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT mode FROM provider_category_policy WHERE provider_key=$key AND catalog_type=$catalog";
        command.Parameters.AddWithValue("$key", key); command.Parameters.AddWithValue("$catalog", Db(catalog));
        return Convert.ToString(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture) ?? "all";
    }

    private static ProviderRecord ReadProvider(SqliteDataReader reader) => new(reader.GetString(0), ParseProviderType(reader.GetString(1)), reader.GetString(2), new Uri(reader.GetString(3)), reader.GetString(4), reader.GetString(5), reader.GetBoolean(6), reader.GetString(7));
    private static ProviderType ParseProviderType(string value) => value == "xtream" ? ProviderType.Xtream : ProviderType.Stalker;
    private static string Db(ProviderType value) => value == ProviderType.Xtream ? "xtream" : "stalker";
    private static string Db(CatalogType value) => value.ToString().ToLowerInvariant();
    private static string Db(CategoryPolicyMode value) => value.ToString().ToLowerInvariant();
    private static string NormalizePortalPath(string path) => "/" + path.Trim().Trim('/');
    private static void RequireKey(string key) { if (!ProviderKey.IsValid(key)) throw new ArgumentException("Invalid provider key.", nameof(key)); }
    private static async Task EnableForeignKeys(SqliteConnection connection, CancellationToken cancellationToken) { await using var command = connection.CreateCommand(); command.CommandText = "PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000;"; await command.ExecuteNonQueryAsync(cancellationToken); }
    [GeneratedRegex("\\s+")] private static partial Regex Whitespace();
}
