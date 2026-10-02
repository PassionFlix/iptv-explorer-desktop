using System.Text.Json;
using IPTVExplorer.Core;
using IPTVExplorer.Desktop;
using IPTVExplorer.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;

namespace IPTVExplorer.Tests;

public sealed class CatalogStatsTests
{
    [Fact]
    public async Task LocalStatsCountSnapshotAndLatestIndexWithoutNetwork()
    {
        await using var database = await TestDatabase.CreateAsync();
        var provider = await database.AddProviderAsync();
        await SeedAsync(database, provider.Key);
        var repository = new LocalCatalogStatsRepository(database.Connections);

        var stats = await repository.ReadAsync(provider.Key);

        Assert.True(stats.SnapshotAvailable);
        Assert.Equal(2, stats.LiveItems);
        Assert.Equal(1, stats.VodItems);
        Assert.Equal(1, stats.SeriesItems);
        Assert.Equal("completed", stats.IndexStatus);
        Assert.Equal(12, stats.IndexedVodItems);
        Assert.Equal(7, stats.IndexedSeriesItems);
    }

    [Fact]
    public async Task XtreamBridgeReportsCompleteSnapshotCounts()
    {
        await using var database = await TestDatabase.CreateAsync();
        var provider = await database.AddProviderAsync(ProviderType.Xtream);
        await SeedAsync(database, provider.Key);
        var bridge = new CatalogStatsBridge(database.Repository, new LocalCatalogStatsRepository(database.Connections), NullLogger<CatalogStatsBridge>.Instance);
        var request = JsonSerializer.Serialize(new { id = "s1", method = "catalog.stats", @params = new { providerKey = provider.Key } });

        var response = await bridge.TryHandleAsync(request);

        Assert.NotNull(response);
        using var document = JsonDocument.Parse(response);
        var result = document.RootElement.GetProperty("result");
        Assert.True(result.GetProperty("completeSnapshot").GetBoolean());
        Assert.Equal(2, result.GetProperty("live").GetInt64());
        Assert.Equal(1, result.GetProperty("vod").GetInt64());
        Assert.Equal(1, result.GetProperty("series").GetInt64());
    }

    [Fact]
    public async Task StalkerBridgeDoesNotInventGlobalVodOrSeriesCounts()
    {
        await using var database = await TestDatabase.CreateAsync();
        var provider = await database.AddProviderAsync(ProviderType.Stalker);
        await SeedAsync(database, provider.Key);
        var bridge = new CatalogStatsBridge(database.Repository, new LocalCatalogStatsRepository(database.Connections), NullLogger<CatalogStatsBridge>.Instance);
        var request = JsonSerializer.Serialize(new { id = "s2", method = "catalog.stats", @params = new { providerKey = provider.Key } });

        var response = await bridge.TryHandleAsync(request);

        Assert.NotNull(response);
        using var document = JsonDocument.Parse(response);
        var result = document.RootElement.GetProperty("result");
        Assert.False(result.GetProperty("completeSnapshot").GetBoolean());
        Assert.Equal(2, result.GetProperty("live").GetInt64());
        Assert.Equal(JsonValueKind.Null, result.GetProperty("vod").ValueKind);
        Assert.Equal(JsonValueKind.Null, result.GetProperty("series").ValueKind);
        Assert.Equal(12, result.GetProperty("indexedVod").GetInt64());
        Assert.Equal(7, result.GetProperty("indexedSeries").GetInt64());
    }

    private static async Task SeedAsync(TestDatabase database, string providerKey)
    {
        await using var connection = database.Connections.Create();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO catalog_snapshots(provider_key,refreshed_at,generation) VALUES($key,'2026-10-01T00:00:00Z',1);
            INSERT INTO catalog_items(provider_key,catalog_type,remote_id,category_id,title,item_json) VALUES
              ($key,'live','1','10','Live 1','{}'),
              ($key,'live','2','10','Live 2','{}'),
              ($key,'vod','3','20','Movie','{}'),
              ($key,'series','4','30','Series','{}');
            INSERT INTO rebuild_jobs(provider_key,status,progress_current,progress_total,progress_label,vod_items,series_items,created_at,finished_at)
              VALUES($key,'completed',19,19,'done',12,7,'2026-10-01T00:00:00Z','2026-10-01T00:01:00Z');
            """;
        command.Parameters.AddWithValue("$key", providerKey);
        await command.ExecuteNonQueryAsync();
    }
}
