using System.Globalization;
using IPTVExplorer.Core;
using Microsoft.Data.Sqlite;

namespace IPTVExplorer.Infrastructure;

public sealed class PlaybackHistoryRepository(SqliteConnectionFactory connections) : IPlaybackHistoryRepository
{
    public async Task<PlaybackProgress?> GetAsync(string providerKey, CatalogType catalog, string mediaId, CancellationToken cancellationToken = default)
    {
        ValidateIdentity(providerKey, catalog, mediaId);
        await using var connection = connections.Create();
        await connection.OpenAsync(cancellationToken);
        await ConfigureAsync(connection, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT provider_key,catalog_type,media_id,series_id,title,series_title,season_number,episode_number,
                   poster_url,extension,position_seconds,duration_seconds,updated_at
            FROM playback_history
            WHERE provider_key=$provider AND catalog_type=$catalog AND media_id=$media
            """;
        command.Parameters.AddWithValue("$provider", providerKey);
        command.Parameters.AddWithValue("$catalog", CatalogValue(catalog));
        command.Parameters.AddWithValue("$media", mediaId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? Read(reader) : null;
    }

    public async Task<IReadOnlyList<PlaybackProgress>> ListInProgressAsync(string providerKey, int limit, CancellationToken cancellationToken = default)
    {
        if (!ProviderKey.IsValid(providerKey)) throw new ArgumentException("Invalid provider key.", nameof(providerKey));
        limit = Math.Clamp(limit, 1, 50);
        await using var connection = connections.Create();
        await connection.OpenAsync(cancellationToken);
        await ConfigureAsync(connection, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT provider_key,catalog_type,media_id,series_id,title,series_title,season_number,episode_number,
                   poster_url,extension,position_seconds,duration_seconds,updated_at
            FROM playback_history
            WHERE provider_key=$provider
            ORDER BY updated_at DESC
            LIMIT $scanLimit
            """;
        command.Parameters.AddWithValue("$provider", providerKey);
        command.Parameters.AddWithValue("$scanLimit", Math.Min(200, limit * 4));
        var result = new List<PlaybackProgress>(limit);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (result.Count < limit && await reader.ReadAsync(cancellationToken))
        {
            var progress = Read(reader);
            if (PlaybackProgressPolicy.ShouldList(progress)) result.Add(progress);
        }
        return result;
    }

    public async Task UpsertAsync(PlaybackProgress progress, CancellationToken cancellationToken = default)
    {
        ValidateIdentity(progress.ProviderKey, progress.Catalog, progress.MediaId);
        ValidateOpaqueId(progress.SeriesId, nameof(progress.SeriesId), optional: true);
        var title = CleanText(progress.Title, 300, "Contenu");
        var seriesTitle = CleanText(progress.SeriesTitle, 300, null);
        var extension = CleanExtension(progress.Extension);
        var poster = SafePoster(progress.PosterUrl);
        var position = Math.Max(0, progress.Position.TotalSeconds);
        var duration = progress.Duration is { } value && value > TimeSpan.Zero ? value.TotalSeconds : (double?)null;

        await using var connection = connections.Create();
        await connection.OpenAsync(cancellationToken);
        await ConfigureAsync(connection, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO playback_history(
                provider_key,catalog_type,media_id,series_id,title,series_title,season_number,episode_number,
                poster_url,extension,position_seconds,duration_seconds,updated_at)
            VALUES($provider,$catalog,$media,$series,$title,$seriesTitle,$season,$episode,$poster,$extension,$position,$duration,$updated)
            ON CONFLICT(provider_key,catalog_type,media_id) DO UPDATE SET
                series_id=excluded.series_id,title=excluded.title,series_title=excluded.series_title,
                season_number=excluded.season_number,episode_number=excluded.episode_number,
                poster_url=excluded.poster_url,extension=excluded.extension,
                position_seconds=excluded.position_seconds,duration_seconds=excluded.duration_seconds,updated_at=excluded.updated_at
            """;
        command.Parameters.AddWithValue("$provider", progress.ProviderKey);
        command.Parameters.AddWithValue("$catalog", CatalogValue(progress.Catalog));
        command.Parameters.AddWithValue("$media", progress.MediaId);
        command.Parameters.AddWithValue("$series", (object?)progress.SeriesId ?? DBNull.Value);
        command.Parameters.AddWithValue("$title", title);
        command.Parameters.AddWithValue("$seriesTitle", (object?)seriesTitle ?? DBNull.Value);
        command.Parameters.AddWithValue("$season", (object?)progress.Season ?? DBNull.Value);
        command.Parameters.AddWithValue("$episode", (object?)progress.Episode ?? DBNull.Value);
        command.Parameters.AddWithValue("$poster", (object?)poster ?? DBNull.Value);
        command.Parameters.AddWithValue("$extension", (object?)extension ?? DBNull.Value);
        command.Parameters.AddWithValue("$position", position);
        command.Parameters.AddWithValue("$duration", (object?)duration ?? DBNull.Value);
        command.Parameters.AddWithValue("$updated", progress.UpdatedAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task DeleteAsync(string providerKey, CatalogType catalog, string mediaId, CancellationToken cancellationToken = default)
    {
        ValidateIdentity(providerKey, catalog, mediaId);
        await using var connection = connections.Create();
        await connection.OpenAsync(cancellationToken);
        await ConfigureAsync(connection, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM playback_history WHERE provider_key=$provider AND catalog_type=$catalog AND media_id=$media";
        command.Parameters.AddWithValue("$provider", providerKey);
        command.Parameters.AddWithValue("$catalog", CatalogValue(catalog));
        command.Parameters.AddWithValue("$media", mediaId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static PlaybackProgress Read(SqliteDataReader reader) => new(
        reader.GetString(0),
        Enum.Parse<CatalogType>(reader.GetString(1), true),
        reader.GetString(2),
        reader.IsDBNull(3) ? null : reader.GetString(3),
        reader.GetString(4),
        reader.IsDBNull(5) ? null : reader.GetString(5),
        reader.IsDBNull(6) ? null : reader.GetInt32(6),
        reader.IsDBNull(7) ? null : reader.GetInt32(7),
        reader.IsDBNull(8) ? null : reader.GetString(8),
        reader.IsDBNull(9) ? null : reader.GetString(9),
        TimeSpan.FromSeconds(reader.GetDouble(10)),
        reader.IsDBNull(11) ? null : TimeSpan.FromSeconds(reader.GetDouble(11)),
        DateTimeOffset.Parse(reader.GetString(12), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind));

    private static async Task ConfigureAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000;";
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static void ValidateIdentity(string providerKey, CatalogType catalog, string mediaId)
    {
        if (!ProviderKey.IsValid(providerKey)) throw new ArgumentException("Invalid provider key.", nameof(providerKey));
        if (catalog is not (CatalogType.Vod or CatalogType.Series)) throw new ArgumentException("Playback history supports films and episodes only.", nameof(catalog));
        ValidateOpaqueId(mediaId, nameof(mediaId));
    }

    private static void ValidateOpaqueId(string? value, string parameter, bool optional = false)
    {
        if (optional && value is null) return;
        if (string.IsNullOrWhiteSpace(value) || value.Length > 512 || Uri.TryCreate(value, UriKind.Absolute, out _))
            throw new ArgumentException("Invalid opaque media reference.", parameter);
    }

    private static string CatalogValue(CatalogType catalog) => catalog.ToString().ToLowerInvariant();

    private static string? CleanText(string? value, int maxLength, string? fallback)
    {
        var cleaned = new string((value ?? string.Empty).Where(character => !char.IsControl(character)).ToArray()).Trim();
        if (cleaned.Length > maxLength) cleaned = cleaned[..maxLength].TrimEnd();
        return cleaned.Length == 0 ? fallback : cleaned;
    }

    private static string? CleanExtension(string? value)
    {
        var extension = value?.Trim().TrimStart('.');
        return extension is { Length: > 0 and <= 20 } && extension.All(char.IsAsciiLetterOrDigit) ? extension : null;
    }

    private static string? SafePoster(string? value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || !string.IsNullOrEmpty(uri.UserInfo)) return null;
        var sensitive = uri.Query.Contains("token=", StringComparison.OrdinalIgnoreCase) ||
            uri.Query.Contains("password=", StringComparison.OrdinalIgnoreCase) ||
            uri.Query.Contains("username=", StringComparison.OrdinalIgnoreCase) ||
            uri.Query.Contains("mac=", StringComparison.OrdinalIgnoreCase) ||
            uri.AbsolutePath.Contains("/movie/", StringComparison.OrdinalIgnoreCase) ||
            uri.AbsolutePath.Contains("/series/", StringComparison.OrdinalIgnoreCase) ||
            uri.AbsolutePath.Contains("/live/", StringComparison.OrdinalIgnoreCase);
        return sensitive ? null : uri.ToString();
    }
}
