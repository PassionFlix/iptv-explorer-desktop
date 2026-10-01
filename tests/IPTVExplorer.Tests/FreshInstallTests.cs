using IPTVExplorer.Infrastructure;

namespace IPTVExplorer.Tests;

public sealed class FreshInstallTests
{
    [Fact]
    public async Task FreshInstallHasNoProviders()
    {
        await using var database = await TestDatabase.CreateAsync();
        Assert.Empty(await database.Repository.ListAsync());
        Assert.Empty(Directory.EnumerateFiles(database.Paths.Indexes));
        Assert.Empty(Directory.EnumerateFiles(database.Paths.Secrets));
        await using var connection = database.Connections.Create();
        await connection.OpenAsync();
        foreach (var table in new[] { "providers", "provider_categories", "rebuild_jobs", "playback_history" })
        {
            await using var command = connection.CreateCommand();
            command.CommandText = $"SELECT COUNT(*) FROM {table}";
            Assert.Equal(0L, Convert.ToInt64(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture));
        }
    }

    [Fact]
    public async Task SecretsAreNotStoredInPlainText()
    {
        await using var database = await TestDatabase.CreateAsync();
        var store = new InMemorySecretStore();
        var reference = await store.PutAsync(new("user-demo", "password-demo", "00:00:00:00:00:00"));
        await database.AddProviderAsync(secretReference: reference);
        var storage = await database.ReadRawSqliteStorageAsync();
        Assert.NotEmpty(storage);
        foreach (var printable in storage)
        {
            Assert.DoesNotContain("password-demo", printable, StringComparison.Ordinal);
            Assert.DoesNotContain("00:00:00:00:00:00", printable, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task ExistingPlaybackHistoryIsMigratedWithoutLosingProgress()
    {
        await using var database = await TestDatabase.CreateAsync();
        await database.AddProviderAsync();
        await using (var connection = database.Connections.Create())
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                DROP INDEX IF EXISTS ix_playback_history_provider_updated;
                DROP TABLE playback_history;
                CREATE TABLE playback_history (
                    provider_key TEXT NOT NULL REFERENCES providers(provider_key) ON DELETE CASCADE,
                    catalog_type TEXT NOT NULL,
                    media_id TEXT NOT NULL,
                    series_id TEXT,
                    position_seconds REAL NOT NULL DEFAULT 0,
                    duration_seconds REAL,
                    updated_at TEXT NOT NULL,
                    PRIMARY KEY(provider_key,catalog_type,media_id)
                );
                INSERT INTO playback_history(provider_key,catalog_type,media_id,position_seconds,duration_seconds,updated_at)
                VALUES('fixture-provider','vod','legacy-film',120,3600,'2026-01-01T00:00:00.0000000+00:00');
                DELETE FROM schema_migrations WHERE version=3;
                """;
            await command.ExecuteNonQueryAsync();
        }

        await new DatabaseInitializer(database.Paths, database.Connections).InitializeAsync();

        var history = new PlaybackHistoryRepository(database.Connections);
        var migrated = Assert.IsType<IPTVExplorer.Core.PlaybackProgress>(await history.GetAsync("fixture-provider", IPTVExplorer.Core.CatalogType.Vod, "legacy-film"));
        Assert.Equal(TimeSpan.FromMinutes(2), migrated.Position);
        Assert.Equal(string.Empty, migrated.Title);
    }
}
