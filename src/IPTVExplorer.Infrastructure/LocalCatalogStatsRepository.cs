using System.Globalization;
using Microsoft.Data.Sqlite;

namespace IPTVExplorer.Infrastructure;

public sealed record LocalCatalogStats(
    bool SnapshotAvailable,
    long LiveItems,
    long VodItems,
    long SeriesItems,
    string? IndexStatus,
    long? IndexedVodItems,
    long? IndexedSeriesItems);

public sealed class LocalCatalogStatsRepository(SqliteConnectionFactory connections)
{
    public async Task<LocalCatalogStats> ReadAsync(string providerKey, CancellationToken cancellationToken = default)
    {
        if (!IPTVExplorer.Core.ProviderKey.IsValid(providerKey)) throw new ArgumentException("Invalid provider key.", nameof(providerKey));
        await using var connection = connections.Create();
        await connection.OpenAsync(cancellationToken);

        var snapshotAvailable = false;
        long live = 0, vod = 0, series = 0;
        await using (var snapshot = connection.CreateCommand())
        {
            snapshot.CommandText = "SELECT COUNT(*) FROM catalog_snapshots WHERE provider_key=$key";
            snapshot.Parameters.AddWithValue("$key", providerKey);
            snapshotAvailable = Convert.ToInt64(await snapshot.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture) > 0;
        }

        await using (var counts = connection.CreateCommand())
        {
            counts.CommandText = "SELECT catalog_type,COUNT(*) FROM catalog_items WHERE provider_key=$key GROUP BY catalog_type";
            counts.Parameters.AddWithValue("$key", providerKey);
            await using var reader = await counts.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var count = reader.GetInt64(1);
                switch (reader.GetString(0))
                {
                    case "live": live = count; break;
                    case "vod": vod = count; break;
                    case "series": series = count; break;
                }
            }
        }

        string? indexStatus = null;
        long? indexedVod = null, indexedSeries = null;
        await using (var index = connection.CreateCommand())
        {
            index.CommandText = "SELECT status,vod_items,series_items FROM rebuild_jobs WHERE provider_key=$key ORDER BY id DESC LIMIT 1";
            index.Parameters.AddWithValue("$key", providerKey);
            await using var reader = await index.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                indexStatus = reader.GetString(0);
                indexedVod = reader.GetInt64(1);
                indexedSeries = reader.GetInt64(2);
            }
        }

        return new(snapshotAvailable, live, vod, series, indexStatus, indexedVod, indexedSeries);
    }
}
