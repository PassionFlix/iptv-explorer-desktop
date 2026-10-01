using IPTVExplorer.Core;
using IPTVExplorer.Infrastructure;
using IPTVExplorer.Providers;

namespace IPTVExplorer.Tests;

public sealed class ProviderDeletionTests
{
    [Fact]
    public async Task UnreadIndexAndItsSidecarsAreDeleted()
    {
        await using var database = await TestDatabase.CreateAsync();
        var path = database.Paths.SearchIndex("fixture-provider");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, "index");
        foreach (var suffix in new[] { "-wal", "-shm", "-journal" }) await File.WriteAllTextAsync(path + suffix, suffix);

        await new ProviderLocalDataStore(database.Paths).DeleteSearchIndexAsync("fixture-provider");

        Assert.All(new[] { path, path + "-wal", path + "-shm", path + "-journal" }, candidate => Assert.False(File.Exists(candidate)));
    }

    [Fact]
    public async Task PooledIndexCanBeDeletedAndReopenedWithNewData()
    {
        await using var database = await TestDatabase.CreateAsync();
        var index = new AtomicSearchIndex(database.Paths);
        var search = new SearchService(database.Paths);
        await index.ReplaceAsync("fixture-provider", [new("fixture-provider", CatalogType.Vod, "old", "Old title", null)]);
        Assert.Single((await search.SearchAsync("fixture-provider", CatalogType.Vod, "old", 1, 10)).Items);

        await new ProviderLocalDataStore(database.Paths).DeleteSearchIndexAsync("fixture-provider");
        Assert.False(File.Exists(database.Paths.SearchIndex("fixture-provider")));

        await index.ReplaceAsync("fixture-provider", [new("fixture-provider", CatalogType.Vod, "new", "New title", null)]);
        Assert.Empty((await search.SearchAsync("fixture-provider", CatalogType.Vod, "old", 1, 10)).Items);
        Assert.Single((await search.SearchAsync("fixture-provider", CatalogType.Vod, "new", 1, 10)).Items);
    }

    [Fact]
    public async Task IndexDeletionRetriesOneTransientIoFailure()
    {
        await using var database = await TestDatabase.CreateAsync();
        var path = database.Paths.SearchIndex("fixture-provider");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, "index");
        var attempts = 0;
        var delays = new List<TimeSpan>();
        var store = new ProviderLocalDataStore(database.Paths,
            (candidate, _) =>
            {
                if (candidate == path && Interlocked.Increment(ref attempts) == 1) throw new IOException("simulated Windows sharing violation");
                File.Delete(candidate);
                return Task.CompletedTask;
            },
            (delay, _) => { delays.Add(delay); return Task.CompletedTask; });

        await store.DeleteSearchIndexAsync("fixture-provider");

        Assert.Equal(2, attempts);
        Assert.Equal([TimeSpan.FromMilliseconds(25)], delays);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task RemoveLocalDataFalsePreservesOnlyDetachedSearchIndex()
    {
        await using var database = await TestDatabase.CreateAsync();
        var secrets = new InMemorySecretStore();
        var secretReference = await secrets.PutAsync(new ProviderSecret("user", "password"));
        var provider = await database.AddProviderAsync(secretReference: secretReference);
        var index = new AtomicSearchIndex(database.Paths);
        await index.ReplaceAsync(provider.Key, [new(provider.Key, CatalogType.Vod, "old", "Retained title", null)]);
        var factory = new ProviderClientFactory(new StubHttpClientFactory(), secrets);
        var management = new ProviderManagementService(database.Repository, secrets, factory, new ProviderLocalDataStore(database.Paths));

        await management.DeleteAsync(provider.Key, removeLocalData: false);

        Assert.True(File.Exists(database.Paths.SearchIndex(provider.Key)));
        Assert.Null(await database.Repository.GetAsync(provider.Key));
        Assert.Null(await secrets.GetAsync(secretReference));
    }

    [Fact]
    public async Task RemoveLocalDataTrueDeletesScopedStateClearsActiveProviderAndCannotReuseOldIndex()
    {
        await using var database = await TestDatabase.CreateAsync();
        var secrets = new InMemorySecretStore();
        var secretReference = await secrets.PutAsync(new ProviderSecret(MacAddress: "00:11:22:33:44:55"));
        var provider = await database.AddProviderAsync(ProviderType.Stalker, secretReference);
        await new AppSettingsRepository(database.Connections).SaveAsync(new AppPreferences(ActiveProviderKey: provider.Key));
        await SeedScopedDataAsync(database, provider.Key);
        var index = new AtomicSearchIndex(database.Paths);
        await index.ReplaceAsync(provider.Key, [new(provider.Key, CatalogType.Vod, "old", "Old provider title", null)]);
        var path = database.Paths.SearchIndex(provider.Key);
        await File.WriteAllTextAsync(path + "-journal", "fixture");
        var factory = new ProviderClientFactory(new StubHttpClientFactory(), secrets);
        _ = await factory.CreateAsync(provider);
        var trackedFactory = new TrackingRemoteFactory(factory);
        var secretWasPresentDuringCleanup = false;
        var localData = new ObservingLocalData(new ProviderLocalDataStore(database.Paths), async () =>
            secretWasPresentDuringCleanup = await secrets.GetAsync(secretReference) is not null);
        var management = new ProviderManagementService(database.Repository, secrets, trackedFactory, localData);

        await management.DeleteAsync(provider.Key, removeLocalData: true);

        Assert.True(secretWasPresentDuringCleanup);
        Assert.Contains(provider.Key, trackedFactory.Evicted);
        Assert.Null(await secrets.GetAsync(secretReference));
        Assert.Null((await new AppSettingsRepository(database.Connections).GetAsync()).ActiveProviderKey);
        Assert.All(new[] { path, path + "-wal", path + "-shm", path + "-journal" }, candidate => Assert.False(File.Exists(candidate)));
        foreach (var table in new[] { "provider_categories", "provider_category_policy", "rebuild_jobs", "playback_history", "series_artwork", "catalog_snapshots", "catalog_items", "media_details", "playback_preferences" })
            Assert.Equal(0, await CountAsync(database, table, provider.Key));

        var replacementSecret = await secrets.PutAsync(new ProviderSecret("replacement", "password"));
        await database.Repository.AddAsync(provider with { Type = ProviderType.Xtream, SecretReference = replacementSecret });
        Assert.Empty((await new SearchService(database.Paths).SearchAsync(provider.Key, CatalogType.Vod, "old", 1, 10)).Items);
    }

    [Fact]
    public async Task CriticalIndexFailureLeavesProviderSecretAndActiveSettingIntact()
    {
        await using var database = await TestDatabase.CreateAsync();
        var secrets = new InMemorySecretStore();
        var secretReference = await secrets.PutAsync(new ProviderSecret("user", "password"));
        var provider = await database.AddProviderAsync(secretReference: secretReference);
        var settings = new AppSettingsRepository(database.Connections);
        await settings.SaveAsync(new AppPreferences(ActiveProviderKey: provider.Key));
        var factory = new ProviderClientFactory(new StubHttpClientFactory(), secrets);
        var management = new ProviderManagementService(database.Repository, secrets, factory, new FailingLocalData());

        await Assert.ThrowsAsync<IOException>(() => management.DeleteAsync(provider.Key, removeLocalData: true));

        Assert.NotNull(await database.Repository.GetAsync(provider.Key));
        Assert.NotNull(await secrets.GetAsync(secretReference));
        Assert.Equal(provider.Key, (await settings.GetAsync()).ActiveProviderKey);
    }

    [Fact]
    public async Task DeletingOneProviderDoesNotTouchAnotherProvidersState()
    {
        await using var database = await TestDatabase.CreateAsync();
        var secrets = new InMemorySecretStore();
        var firstReference = await secrets.PutAsync(new ProviderSecret("first", "password"));
        var secondReference = await secrets.PutAsync(new ProviderSecret("second", "password"));
        var first = await database.AddProviderAsync(secretReference: firstReference);
        var second = first with { Key = "second-provider", Name = "Second", SecretReference = secondReference };
        await database.Repository.AddAsync(second);
        var index = new AtomicSearchIndex(database.Paths);
        await index.ReplaceAsync(first.Key, [new(first.Key, CatalogType.Vod, "a", "First title", null)]);
        await index.ReplaceAsync(second.Key, [new(second.Key, CatalogType.Vod, "b", "Second title", null)]);
        var management = new ProviderManagementService(database.Repository, secrets,
            new ProviderClientFactory(new StubHttpClientFactory(), secrets), new ProviderLocalDataStore(database.Paths));

        await management.DeleteAsync(first.Key, removeLocalData: true);

        Assert.Null(await database.Repository.GetAsync(first.Key));
        Assert.NotNull(await database.Repository.GetAsync(second.Key));
        Assert.False(File.Exists(database.Paths.SearchIndex(first.Key)));
        Assert.True(File.Exists(database.Paths.SearchIndex(second.Key)));
        Assert.Null(await secrets.GetAsync(firstReference));
        Assert.NotNull(await secrets.GetAsync(secondReference));
    }

    private static async Task SeedScopedDataAsync(TestDatabase database, string providerKey)
    {
        await using var connection = database.Connections.Create();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA foreign_keys=ON;
            INSERT INTO rebuild_jobs(provider_key,status,created_at) VALUES($key,'completed',$now);
            INSERT INTO playback_history(provider_key,catalog_type,media_id,title,position_seconds,updated_at) VALUES($key,'vod','movie','Movie',10,$now);
            INSERT INTO series_artwork(provider_key,remote_id,image_url,checked_at) VALUES($key,'series','https://images.invalid/poster.jpg',$now);
            INSERT INTO catalog_snapshots(provider_key,refreshed_at,generation) VALUES($key,$now,1);
            INSERT INTO catalog_items(provider_key,catalog_type,remote_id,category_id,title,item_json) VALUES($key,'vod','movie','cat','Movie','{}');
            INSERT INTO media_details(provider_key,catalog_type,remote_id,detail_json) VALUES($key,'vod','movie','{}');
            INSERT INTO playback_preferences(provider_key,series_id,subtitle_mode,updated_at) VALUES($key,'series','off',$now);
            """;
        command.Parameters.AddWithValue("$key", providerKey);
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<long> CountAsync(TestDatabase database, string table, string providerKey)
    {
        await using var connection = database.Connections.Create();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM {table} WHERE provider_key=$key";
        command.Parameters.AddWithValue("$key", providerKey);
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private sealed class FailingLocalData : IProviderLocalData
    {
        public Task DeleteSearchIndexAsync(string providerKey, CancellationToken cancellationToken = default) =>
            Task.FromException(new IOException("simulated cleanup failure"));
    }

    private sealed class ObservingLocalData(IProviderLocalData inner, Func<Task> observe) : IProviderLocalData
    {
        public async Task DeleteSearchIndexAsync(string providerKey, CancellationToken cancellationToken = default)
        {
            await observe();
            await inner.DeleteSearchIndexAsync(providerKey, cancellationToken);
        }
    }

    private sealed class TrackingRemoteFactory(IRemoteProviderClientFactory inner) : IRemoteProviderClientFactory
    {
        public List<string> Evicted { get; } = [];

        public Task<IProviderClient> CreateAsync(ProviderRecord provider, CancellationToken cancellationToken = default) =>
            inner.CreateAsync(provider, cancellationToken);

        public void Evict(string providerKey)
        {
            Evicted.Add(providerKey);
            inner.Evict(providerKey);
        }
    }
}
