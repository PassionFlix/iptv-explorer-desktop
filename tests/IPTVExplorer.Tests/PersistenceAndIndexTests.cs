using System.Collections;
using System.Net;
using System.Text;
using IPTVExplorer.Core;
using IPTVExplorer.Infrastructure;
using IPTVExplorer.Providers;

namespace IPTVExplorer.Tests;

public sealed class PersistenceAndIndexTests
{
    [Fact]
    public async Task PreferencesRoundTripAndCorruptJsonFallsBackToDefaults()
    {
        await using var database = await TestDatabase.CreateAsync();
        var repository = new AppSettingsRepository(database.Connections);
        var expected = new AppPreferences("fixture-provider", "fr", "dark", "fra", "eng", "fra", false);
        await repository.SaveAsync(expected);
        Assert.Equal(expected, await repository.GetAsync());

        await using var connection = database.Connections.Create();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE app_settings SET value_json='{broken' WHERE setting_key='app.preferences'";
        await command.ExecuteNonQueryAsync();
        Assert.Equal(new AppPreferences(), await repository.GetAsync());
    }

    [Fact]
    public async Task CategorySynchronizationHonorsPolicyForNewAndMissingCategories()
    {
        await using var database = await TestDatabase.CreateAsync();
        await database.AddProviderAsync();

        await database.Repository.SyncCategoriesAsync("fixture-provider", CatalogType.Live, [new("1", "News", "")]);
        await database.Repository.SaveCategoryPolicyAsync("fixture-provider", CatalogType.Live, new(CategoryPolicyMode.All, new HashSet<string>()));
        await database.Repository.SyncCategoriesAsync("fixture-provider", CatalogType.Live, [new("1", "News", ""), new("2", "Sports", "")]);
        Assert.True((await database.Repository.ListCategoriesAsync("fixture-provider", CatalogType.Live)).Single(value => value.RemoteId == "2").Selected);

        await database.Repository.SyncCategoriesAsync("fixture-provider", CatalogType.Vod, [new("10", "Drama", "")]);
        await database.Repository.SaveCategoryPolicyAsync("fixture-provider", CatalogType.Vod, new(CategoryPolicyMode.Custom, new HashSet<string> { "10" }));
        await database.Repository.SyncCategoriesAsync("fixture-provider", CatalogType.Vod, [new("10", "Drama", ""), new("11", "Comedy", "")]);
        var vod = await database.Repository.ListCategoriesAsync("fixture-provider", CatalogType.Vod);
        Assert.True(vod.Single(value => value.RemoteId == "10").Selected);
        Assert.False(vod.Single(value => value.RemoteId == "11").Selected);

        await database.Repository.SyncCategoriesAsync("fixture-provider", CatalogType.Series, [new("20", "Mystery", "")]);
        await database.Repository.SaveCategoryPolicyAsync("fixture-provider", CatalogType.Series, new(CategoryPolicyMode.None, new HashSet<string>()));
        await database.Repository.SyncCategoriesAsync("fixture-provider", CatalogType.Series, [new("21", "Science", "")]);
        var series = await database.Repository.ListCategoriesAsync("fixture-provider", CatalogType.Series);
        Assert.False(series.Single(value => value.RemoteId == "21").Selected);
        var missing = series.Single(value => value.RemoteId == "20");
        Assert.False(missing.Present);
        Assert.True(missing.NeedsReview);
    }

    [Fact]
    public async Task CompletedIndexJobClearsVodAndSeriesDirtyState()
    {
        await using var database = await TestDatabase.CreateAsync();
        await database.AddProviderAsync();
        await database.Repository.SaveCategoryPolicyAsync("fixture-provider", CatalogType.Vod, new(CategoryPolicyMode.None, new HashSet<string>()));
        await database.Repository.SaveCategoryPolicyAsync("fixture-provider", CatalogType.Series, new(CategoryPolicyMode.None, new HashSet<string>()));
        Assert.All((await database.Repository.GetCategorySummariesAsync("fixture-provider")).Where(value => value.Catalog is CatalogType.Vod or CatalogType.Series), value => Assert.True(value.IndexDirty));

        var jobs = new RebuildJobRepository(database.Connections);
        var id = await jobs.QueueAsync("fixture-provider");
        Assert.NotNull(await jobs.ClaimNextAsync());
        await jobs.CompleteAsync(id, 0, 0);
        Assert.All((await database.Repository.GetCategorySummariesAsync("fixture-provider")).Where(value => value.Catalog is CatalogType.Vod or CatalogType.Series), value => Assert.False(value.IndexDirty));
    }

    [Fact]
    public async Task AtomicIndexDeduplicatesAndPreservesPreviousIndexWhenBuildFails()
    {
        await using var database = await TestDatabase.CreateAsync();
        var index = new AtomicSearchIndex(database.Paths);
        var search = new SearchService(database.Paths);
        await index.ReplaceAsync("fixture-provider",
        [
            new("fixture-provider", CatalogType.Vod, "1", "First title", null),
            new("fixture-provider", CatalogType.Vod, "1", "Updated title", null),
            new("fixture-provider", CatalogType.Series, "1", "Series title", null)
        ]);
        Assert.Equal("Updated title", Assert.Single((await search.SearchAsync("fixture-provider", CatalogType.Vod, "title", 1, 20)).Items).Title);

        await Assert.ThrowsAsync<FixtureIndexException>(() => index.ReplaceAsync("fixture-provider", new ThrowingDocuments()));
        Assert.Equal("Updated title", Assert.Single((await search.SearchAsync("fixture-provider", CatalogType.Vod, "title", 1, 20)).Items).Title);
    }

    [Fact]
    public async Task AtomicIndexReplacesPreviouslyReadPooledIndexAndReopensNewData()
    {
        await using var database = await TestDatabase.CreateAsync();
        var index = new AtomicSearchIndex(database.Paths);
        var search = new SearchService(database.Paths);
        var oldDate = new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var newDate = new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);
        await index.ReplaceAsync("fixture-provider",
        [
            new("fixture-provider", CatalogType.Vod, "old", "Old title", null, oldDate)
        ]);

        Assert.Single((await search.SearchAsync("fixture-provider", CatalogType.Vod, "old", 1, 20)).Items);
        Assert.Equal("old", Assert.Single(await search.RecentlyAddedAsync("fixture-provider", CatalogType.Vod, 12)).RemoteId);

        await index.ReplaceAsync("fixture-provider",
        [
            new("fixture-provider", CatalogType.Series, "new", "New title", null, newDate)
        ]);

        Assert.Empty((await search.SearchAsync("fixture-provider", CatalogType.Vod, "old", 1, 20)).Items);
        var recent = Assert.Single(await search.RecentlyAddedAsync("fixture-provider", CatalogType.Series, 12));
        Assert.Equal("new", recent.RemoteId);
        Assert.Equal(newDate, recent.AddedAt);
        var path = database.Paths.SearchIndex("fixture-provider");
        Assert.False(File.Exists(path + ".tmp"));
        Assert.False(File.Exists(path + ".tmp-wal"));
        Assert.False(File.Exists(path + ".tmp-shm"));
        Assert.False(File.Exists(path + ".previous"));
        Assert.False(File.Exists(path + ".previous-wal"));
        Assert.False(File.Exists(path + ".previous-shm"));
    }

    [Fact]
    public async Task ProviderUpdateKeepsBlankSecretAndDeleteRemovesIt()
    {
        await using var database = await TestDatabase.CreateAsync();
        var secrets = new InMemorySecretStore();
        var originalReference = await secrets.PutAsync(new ProviderSecret("fixture-user", "fixture-password"));
        var provider = await database.AddProviderAsync(secretReference: originalReference);
        var factory = new ProviderClientFactory(new StubHttpClientFactory(new SuccessfulXtreamHandler()), secrets);
        var management = new ProviderManagementService(database.Repository, secrets, factory, new ProviderLocalDataStore(database.Paths));

        var updated = await management.UpdateAsync(provider.Key, new("Renamed Fixture", "https://example.invalid/base", null, null, null));
        Assert.Null(await secrets.GetAsync(originalReference));
        Assert.Equal("fixture-password", (await secrets.GetAsync(updated.SecretReference))?.Password);
        Assert.Equal("Renamed Fixture", (await database.Repository.GetAsync(provider.Key))?.Name);

        await management.DeleteAsync(provider.Key, removeLocalData: false);
        Assert.Null(await secrets.GetAsync(updated.SecretReference));
        Assert.Null(await database.Repository.GetAsync(provider.Key));
    }

    private sealed class SuccessfulXtreamHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"user_info\":{\"auth\":1,\"status\":\"Active\"},\"server_info\":{}}", Encoding.UTF8, "application/json")
        });
    }

    private sealed class ThrowingDocuments : IEnumerable<SearchHit>
    {
        public IEnumerator<SearchHit> GetEnumerator()
        {
            yield return new SearchHit("fixture-provider", CatalogType.Vod, "2", "Temporary", null);
            throw new FixtureIndexException();
        }
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private sealed class FixtureIndexException : Exception;
}
