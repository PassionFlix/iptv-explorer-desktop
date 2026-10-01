using System.Globalization;
using IPTVExplorer.Core;

namespace IPTVExplorer.Infrastructure;

public sealed record CachedSeriesArtwork(string? ImageUrl, DateTimeOffset CheckedAt);

/// <summary>Small provider-scoped cache in the application DB, never in a replaceable search file.</summary>
public sealed class SeriesArtworkRepository(SqliteConnectionFactory connections)
{
    public async Task<CachedSeriesArtwork?> GetAsync(string providerKey, string remoteId, CancellationToken cancellationToken)
    {
        await using var connection = connections.Create();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT image_url,checked_at FROM series_artwork WHERE provider_key=$provider AND remote_id=$id";
        command.Parameters.AddWithValue("$provider", providerKey);
        command.Parameters.AddWithValue("$id", remoteId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new(reader.IsDBNull(0) ? null : reader.GetString(0), DateTimeOffset.Parse(reader.GetString(1), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind))
            : null;
    }

    public async Task SaveAsync(string providerKey, string remoteId, string? imageUrl, ProviderSecret? secret, CancellationToken cancellationToken)
    {
        if (!ProviderKey.IsValid(providerKey)) throw new ArgumentException("Invalid provider key.", nameof(providerKey));
        await using var connection = connections.Create();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA foreign_keys=ON;
            INSERT INTO series_artwork(provider_key,remote_id,image_url,checked_at)
            VALUES($provider,$id,$image,$now)
            ON CONFLICT(provider_key,remote_id) DO UPDATE SET image_url=excluded.image_url,checked_at=excluded.checked_at;
            """;
        command.Parameters.AddWithValue("$provider", providerKey);
        command.Parameters.AddWithValue("$id", remoteId);
        command.Parameters.AddWithValue("$image", (object?)MediaArtwork.SafeImageUrl(imageUrl, secret) ?? DBNull.Value);
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
