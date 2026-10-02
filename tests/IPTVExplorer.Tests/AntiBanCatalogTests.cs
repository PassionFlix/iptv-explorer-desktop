using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using IPTVExplorer.Core;
using IPTVExplorer.Desktop;
using IPTVExplorer.Infrastructure;
using IPTVExplorer.Providers;
using Microsoft.Extensions.Logging.Abstractions;

namespace IPTVExplorer.Tests;

public sealed class AntiBanCatalogTests
{
    private static readonly string[] Bulk = ["get_live_streams", "get_vod_streams", "get_series"];

    [Fact]
    public async Task StartupWithoutCacheMakesExactlyThreePhysicalRequestsInOrderAndNeverAgainInSession()
    {
        await using var f = await Fixture.Create();
        Assert.True((await f.Rpc("catalog.session")).GetProperty("updated").GetBoolean());
        await f.Rpc("catalog.session");
        f.Clock.Now += TimeSpan.FromHours(3);
        await f.Refresh.EnsureSessionAsync(f.Provider.Key);
        Assert.Equal(Bulk, f.Http.Actions.ToArray());
        Assert.False(f.Http.HadCategoryParameter);
        Assert.Equal(1, f.Http.MaxActive);
        foreach (var type in Enum.GetValues<CatalogType>()) Assert.Equal(10, (await f.Snapshots.ReadAsync(f.Provider.Key, type)).Count);
        Assert.Equal(20, (await f.Snapshots.SearchDocumentsAsync(f.Provider.Key)).Count);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(29, 0)]
    [InlineData(30, 3)]
    [InlineData(31, 3)]
    public async Task PersistentCooldownSurvivesRestart(int ageMinutes, int expected)
    {
        await using var f = await Fixture.Create();
        await f.Seed(f.Clock.Now - TimeSpan.FromMinutes(ageMinutes));
        using var restarted = f.NewRefresh();
        await restarted.EnsureSessionAsync(f.Provider.Key);
        Assert.Equal(expected, f.Http.Actions.Count);
        Assert.Equal(TimeSpan.FromMinutes(30), CatalogRefreshService.StartupCooldown);
    }

    [Fact]
    public async Task LocalUiAndIndexNeverUseProviderAfterRefresh()
    {
        await using var f = await Fixture.Create();
        await f.Refresh.EnsureSessionAsync(f.Provider.Key);
        for (var i = 0; i < 2; i++)
            Assert.Single((await f.Rpc("catalog.live", new { providerKey = f.Provider.Key, categoryId = "1" })).EnumerateArray());
        foreach (var catalog in new[] { "vod", "series" })
            for (var i = 1; i <= 10; i++)
            {
                var page = await f.Rpc($"catalog.{catalog}.page", new { providerKey = f.Provider.Key, categoryId = i.ToString(), page = 1 });
                Assert.Equal(1, page.GetProperty("total").GetInt32());
                await f.Rpc("categories.list", new { providerKey = f.Provider.Key, catalogType = catalog });
            }
        await f.RunIndex();
        Assert.Equal(10, (await f.Rpc("search.query", new { providerKey = f.Provider.Key, catalogType = "vod", query = "Media", page = 1, pageSize = 40 })).GetProperty("total").GetInt32());
        for (var i = 0; i < 2; i++)
        {
            var home = await f.Rpc("home.content");
            Assert.Equal(10, home.GetProperty("recentlyAddedFilms").GetArrayLength());
            await f.Rpc("providers.dashboard");
            await f.Rpc("catalog.status");
        }
        await f.Rpc("index.queue");
        await f.RunIndex();
        Assert.Equal(Bulk, f.Http.Actions.ToArray());
    }

    [Theory]
    [InlineData("403", 0)] [InlineData("429", 1)] [InlineData("500", 2)]
    [InlineData("520", 0)] [InlineData("520", 1)] [InlineData("520", 2)]
    [InlineData("timeout", 0)] [InlineData("network", 1)] [InlineData("json", 2)]
    [InlineData("shape", 1)] [InlineData("redirect", 0)]
    public async Task BulkFailureKeepsEntireOldSnapshotAndNeverRetries(string failure, int endpoint)
    {
        await using var f = await Fixture.Create();
        var previous = f.Clock.Now - TimeSpan.FromHours(1);
        await f.Seed(previous);
        f.Http.FailAction = Bulk[endpoint]; f.Http.Failure = failure;
        var result = await f.Refresh.EnsureSessionAsync(f.Provider.Key);
        Assert.False(result.Updated); Assert.Equal("retained", result.State);
        Assert.Equal(previous, await f.Snapshots.RefreshedAtAsync(f.Provider.Key));
        foreach (var type in Enum.GetValues<CatalogType>()) Assert.Equal("Old", Assert.Single(await f.Snapshots.ReadAsync(f.Provider.Key, type)).Title);
        await f.Refresh.EnsureSessionAsync(f.Provider.Key);
        Assert.Equal(Bulk.Take(endpoint + 1), f.Http.Actions);
    }

    [Fact]
    public async Task ManualRefreshIgnoresCooldownAndDoubleClickIsSingleFlight()
    {
        await using var f = await Fixture.Create();
        await f.Seed(f.Clock.Now);
        Assert.Equal("recent", (await f.Refresh.EnsureSessionAsync(f.Provider.Key)).State);
        f.Http.BlockAction = Bulk[0];
        var first = f.Refresh.RefreshManualAsync(f.Provider.Key);
        await f.Http.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = f.Refresh.RefreshManualAsync(f.Provider.Key);
        Assert.Equal([Bulk[0]], f.Http.Actions.ToArray());
        f.Http.Release.TrySetResult();
        Assert.True((await first).Updated); Assert.True((await second).Updated);
        Assert.Equal(Bulk, f.Http.Actions.ToArray());
        Assert.Equal(1, f.Http.MaxActive);
    }

    [Fact]
    public async Task RefreshIsNonblockingAndOldCacheIsReadableWhileFirstRequestIsInFlight()
    {
        await using var f = await Fixture.Create();
        await f.Seed(f.Clock.Now - TimeSpan.FromHours(1)); f.Http.BlockAction = Bulk[0];
        var refresh = f.Refresh.EnsureSessionAsync(f.Provider.Key);
        await f.Http.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(refresh.IsCompleted);
        Assert.Equal("Old", Assert.Single(await f.Snapshots.ReadAsync(f.Provider.Key, CatalogType.Vod)).Title);
        Assert.Equal([Bulk[0]], f.Http.Actions.ToArray());
        f.Http.Release.TrySetResult(); await refresh;
        Assert.Equal(Bulk, f.Http.Actions.ToArray());
    }

    [Fact]
    public async Task ExplicitSeriesDetailIsPersistedIncludingEpisodesAndNeverExpires()
    {
        await using var f = await Fixture.Create();
        var detail = await f.Rpc("catalog.series.detail", new { providerKey = f.Provider.Key, mediaId = "1" });
        Assert.Equal("501", detail.GetProperty("seasons")[0].GetProperty("episodes")[0].GetProperty("id").GetString());
        Assert.Equal("mkv", detail.GetProperty("seasons")[0].GetProperty("episodes")[0].GetProperty("extension").GetString());
        f.Clock.Now += TimeSpan.FromDays(365);
        using var restartedDetails = new MediaDetailService(f.Remote, new(f.Database.Connections), f.Secrets, new(new(f.Database.Connections)));
        var cached = await restartedDetails.SeriesAsync(f.Provider, "1");
        Assert.Equal("501", cached.Seasons[0].Episodes[0].Id);
        Assert.Equal("Fixture synopsis", cached.Plot);
        var playerClient = await f.Local.CreateAsync(f.Provider);
        Assert.Equal(cached, await playerClient.GetSeriesDetailsAsync("1") is { } player ? player with { Seasons = cached.Seasons } : null);
        await f.Rpc("catalog.series.detail", new { providerKey = f.Provider.Key, mediaId = "1" });
        Assert.Equal(["get_series_info"], f.Http.Actions.ToArray());
        Assert.Equal(cached.Poster, (await new SeriesArtworkRepository(f.Database.Connections).GetAsync(f.Provider.Key, "1", default))?.ImageUrl);
    }

    [Fact]
    public async Task SimultaneousDetailsAndCancelledFirstWaitShareOneRequest()
    {
        await using var f = await Fixture.Create(); f.Http.BlockAction = "get_series_info";
        using var cancel = new CancellationTokenSource();
        var first = f.Details.SeriesAsync(f.Provider, "1", cancel.Token);
        await f.Http.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancel.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        var second = f.Details.SeriesAsync(f.Provider, "1");
        f.Http.Release.TrySetResult();
        Assert.Single((await second).Seasons);
        Assert.Equal(["get_series_info"], f.Http.Actions.ToArray());
    }

    [Theory]
    [InlineData("403")] [InlineData("429")] [InlineData("520")] [InlineData("timeout")] [InlineData("network")] [InlineData("json")]
    public async Task DetailFailureHasOnePhysicalAttemptAndNoHomeRetry(string failure)
    {
        await using var f = await Fixture.Create(); f.Http.FailAction = "get_series_info"; f.Http.Failure = failure;
        await Assert.ThrowsAnyAsync<Exception>(() => f.Details.SeriesAsync(f.Provider, "1"));
        await f.Rpc("home.content"); await f.Rpc("providers.dashboard");
        Assert.Equal(["get_series_info"], f.Http.Actions.ToArray());
        Assert.Null(await f.Snapshots.DetailAsync<SeriesDetails>(f.Provider.Key, CatalogType.Series, "1"));
    }

    [Theory]
    [InlineData(true, 0)] [InlineData(false, 1)]
    public async Task FilmUsesCatalogMetadataWhenSufficientOtherwiseOneCachedDetail(bool complete, int expected)
    {
        await using var f = await Fixture.Create();
        var item = new CatalogItem("1", "Film", Extension: "mkv", Plot: complete ? "Synopsis in bulk" : null);
        await f.Seed(f.Clock.Now, item);
        await f.Rpc("catalog.vod.detail", new { providerKey = f.Provider.Key, mediaId = "1" });
        await f.Rpc("catalog.vod.detail", new { providerKey = f.Provider.Key, mediaId = "1" });
        Assert.Equal(expected, f.Http.Actions.Count);
        Assert.NotNull(await f.Snapshots.DetailAsync<VodDetails>(f.Provider.Key, CatalogType.Vod, "1"));
    }

    [Fact]
    public async Task ProviderSwitchIsIsolatedAndEachProviderHasOnlyOneAutomaticAttempt()
    {
        await using var f = await Fixture.Create();
        var other = f.Provider with { Key = "other-provider" }; await f.Database.Repository.AddAsync(other);
        await f.Refresh.EnsureSessionAsync(f.Provider.Key); await f.Refresh.EnsureSessionAsync(other.Key);
        await f.Refresh.EnsureSessionAsync(f.Provider.Key); await f.Refresh.EnsureSessionAsync(other.Key);
        Assert.Equal(Bulk.Concat(Bulk), f.Http.Actions);
        await f.Details.SeriesAsync(f.Provider, "1");
        Assert.Null(await f.Snapshots.DetailAsync<SeriesDetails>(other.Key, CatalogType.Series, "1"));
        await f.Database.Repository.DeleteAsync(f.Provider.Key);
        Assert.Null(await f.Snapshots.RefreshedAtAsync(f.Provider.Key));
        Assert.Null(await f.Snapshots.DetailAsync<SeriesDetails>(f.Provider.Key, CatalogType.Series, "1"));
        Assert.NotNull(await f.Snapshots.RefreshedAtAsync(other.Key));
    }

    [Fact]
    public async Task ExplicitCategoryRefreshAndAccountDiagnosticDoNotFetchCatalogOrRetry()
    {
        await using var f = await Fixture.Create();
        await f.Management.SyncCategoriesAsync(f.Provider.Key);
        Assert.Equal(["get_live_categories", "get_vod_categories", "get_series_categories"], f.Http.Actions.ToArray());
        f.Http.Actions.Clear();
        await f.Management.DiagnoseAsync(f.Provider.Key);
        Assert.Equal(["account"], f.Http.Actions.ToArray());
        f.Http.Actions.Clear();
        await f.Management.SetEnabledAsync(f.Provider.Key, true);
        await f.Management.UpdateAsync(f.Provider.Key, new("Renamed", "https://example.invalid", null, null, null));
        Assert.Empty(f.Http.Actions);
    }

    [Fact]
    public async Task ExplicitOnboardingTestUsesOneAccountAndNoImplicitCategorySync()
    {
        await using var f = await Fixture.Create();
        var onboarding = new ProviderOnboardingService(f.Secrets, f.Database.Repository, f.Remote);
        var draft = onboarding.AddDraft(new("New", "xtream", "https://example.invalid", "fixture-user", "fixture-password", null));
        var tested = await onboarding.TestAsync(draft.Id);
        Assert.Equal(ProviderType.Xtream, tested.DetectedType);
        Assert.All(tested.Categories.Values, Assert.Empty);
        Assert.Equal(["account"], f.Http.Actions.ToArray());
    }

    [Fact]
    public async Task MetadataFromDifferentOperationsNeverRunsInParallel()
    {
        await using var f = await Fixture.Create(); f.Http.BlockAction = Bulk[0];
        var refresh = f.Refresh.EnsureSessionAsync(f.Provider.Key);
        await f.Http.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var detail = f.Details.SeriesAsync(f.Provider, "1");
        await Task.Delay(30);
        Assert.Equal([Bulk[0]], f.Http.Actions.ToArray());
        f.Http.Release.TrySetResult(); await refresh; await detail;
        Assert.Equal(1, f.Http.MaxActive); Assert.Equal(4, f.Http.Actions.Count);
        Assert.Equal(Bulk, f.Http.Actions.Where(action => action != "get_series_info"));
    }

    [Fact]
    public async Task CatalogSanitizationNeverWritesSecretsRawJsonOrPlaybackUrls()
    {
        await using var f = await Fixture.Create();
        using var raw = JsonDocument.Parse("""{"password":"fixture-password","token":"never-persist-this","url":"https://example.invalid/movie/fixture-user/fixture-password/1.mkv"}""");
        await f.Seed(f.Clock.Now, new CatalogItem("1", "Title fixture-password", "https://images.example.invalid/a.jpg?token=never-persist-this",
            Metadata: raw.RootElement.Clone(), BackdropUrl: "https://images.example.invalid/a.jpg?w=1", Plot: "https://example.invalid/movie/fixture-user/fixture-password/1.mkv 00:11:22:33:44:55"));
        var item = Assert.Single(await f.Snapshots.ReadAsync(f.Provider.Key, CatalogType.Vod));
        Assert.Null(item.Metadata); Assert.Null(item.ImageUrl); Assert.Null(item.BackdropUrl);
        foreach (var content in await f.Database.ReadRawSqliteStorageAsync())
            foreach (var sensitive in new[] { "fixture-password", "fixture-user", "never-persist-this", "/movie/", "00:11:22:33:44:55" }) Assert.DoesNotContain(sensitive, content);
        Assert.Empty(f.Http.Actions);
    }

    [Fact]
    public async Task FailedSqlitePublicationRollsBackAllCatalogsAndTimestamp()
    {
        await using var f = await Fixture.Create(); var before = f.Clock.Now - TimeSpan.FromHours(1); await f.Seed(before);
        await using (var connection = f.Database.Connections.Create())
        {
            await connection.OpenAsync(); await using var command = connection.CreateCommand();
            command.CommandText = "CREATE TRIGGER fixture_abort BEFORE INSERT ON catalog_items WHEN NEW.catalog_type='series' BEGIN SELECT RAISE(ABORT,'fixture abort'); END";
            await command.ExecuteNonQueryAsync();
        }
        Assert.False((await f.Refresh.EnsureSessionAsync(f.Provider.Key)).Updated);
        Assert.Equal(before, await f.Snapshots.RefreshedAtAsync(f.Provider.Key));
        foreach (var type in Enum.GetValues<CatalogType>()) Assert.Equal("Old", Assert.Single(await f.Snapshots.ReadAsync(f.Provider.Key, type)).Title);
        Assert.Equal(Bulk, f.Http.Actions.ToArray());
    }

    [Fact]
    public async Task KnownCategoryNamesAndPoliciesSurviveAndNewCategoriesWorkWithoutSync()
    {
        await using var f = await Fixture.Create();
        await f.Database.Repository.SyncCategoriesAsync(f.Provider.Key, CatalogType.Vod, [new("1", "Known name", "KNOWN")]);
        await f.Database.Repository.SaveCategoryPolicyAsync(f.Provider.Key, CatalogType.Vod, new(CategoryPolicyMode.Custom, new HashSet<string> { "1" }));
        await f.Refresh.EnsureSessionAsync(f.Provider.Key);
        var categories = await f.Database.Repository.ListCategoriesAsync(f.Provider.Key, CatalogType.Vod);
        Assert.Equal("Known name", categories.Single(category => category.RemoteId == "1").Name);
        Assert.False(categories.Single(category => category.RemoteId == "2").Selected);
        Assert.Single(await f.Snapshots.SearchDocumentsAsync(f.Provider.Key), item => item.Catalog == CatalogType.Vod);
        Assert.Equal(10, (await f.Database.Repository.ListCategoriesAsync(f.Provider.Key, CatalogType.Series)).Count);
        Assert.Equal(Bulk, f.Http.Actions.ToArray());
    }

    [Fact]
    public async Task PlayerMetadataMissIsLocalOnlyAndStalkerHasNoBulkStartup()
    {
        await using var f = await Fixture.Create();
        var local = await f.Local.CreateAsync(f.Provider);
        await Assert.ThrowsAsync<InvalidOperationException>(() => local.GetSeriesDetailsAsync("1"));
        var stalker = f.Provider with { Key = "stalker-fixture", Type = ProviderType.Stalker };
        await f.Database.Repository.AddAsync(stalker);
        Assert.Equal("notApplicable", (await f.Refresh.EnsureSessionAsync(stalker.Key)).State);
        Assert.IsType<StalkerProviderClient>(await f.Local.CreateAsync(stalker));
        Assert.Empty(f.Http.Actions);
    }

    [Fact]
    public async Task RemoteLegacyCategoryAndArtworkHydrationMethodsFailClosed()
    {
        await using var f = await Fixture.Create(); var client = await f.Remote.CreateAsync(f.Provider);
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetLiveAsync("1"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetVodPageAsync("1", 1));
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetSeriesPageAsync("1", 1));
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetSeriesInfoAsync("1"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetVodInfoAsync("1"));
        Assert.Empty(f.Http.Actions);
    }

    [Theory]
    [InlineData(CatalogType.Vod)] [InlineData(CatalogType.Series)]
    public async Task PaginationIsEntirelyLocalAndStable(CatalogType type)
    {
        await using var f = await Fixture.Create();
        var catalogs = Enum.GetValues<CatalogType>().ToDictionary(c => c, _ => (IReadOnlyList<CatalogItem>)Array.Empty<CatalogItem>());
        catalogs[type] = Enumerable.Range(1, 210).Select(i => new CatalogItem(i.ToString(), $"Media {i:000}", CategoryId: "1")).ToArray();
        await f.Snapshots.ReplaceAsync(f.Provider.Key, catalogs, null, f.Clock.Now);
        var client = await f.Local.CreateAsync(f.Provider);
        var page = type == CatalogType.Vod ? await client.GetVodPageAsync("1", 2) : await client.GetSeriesPageAsync("1", 2);
        Assert.Equal(210, page.Total); Assert.Equal(3, page.TotalPages); Assert.Equal(100, page.Items.Count);
        Assert.Equal("101", page.Items[0].Id); Assert.Equal("200", page.Items[^1].Id);
        Assert.Empty(f.Http.Actions);
    }

    [Fact]
    public async Task SnapshotPopulatesArtworkIncludingOldNullRowsWithoutOverwritingExplicitDetail()
    {
        await using var f = await Fixture.Create(); var cache = new SeriesArtworkRepository(f.Database.Connections);
        await cache.SaveAsync(f.Provider.Key, "1", null, null, default);
        await f.Seed(f.Clock.Now, new("1", "Series", "https://images.example.invalid/bulk.jpg"));
        Assert.EndsWith("bulk.jpg", (await cache.GetAsync(f.Provider.Key, "1", default))?.ImageUrl);
        await f.Details.SeriesAsync(f.Provider, "1");
        await f.Seed(f.Clock.Now, new("1", "Series", "https://images.example.invalid/new-bulk.jpg"));
        Assert.EndsWith("cover.jpg", (await cache.GetAsync(f.Provider.Key, "1", default))?.ImageUrl);
        Assert.Equal(["get_series_info"], f.Http.Actions.ToArray());
    }

    [Fact]
    public async Task GenerationChangeDuringIndexPublicationQueuesAnotherLocalPass()
    {
        await using var f = await Fixture.Create(); await f.Seed(f.Clock.Now);
        await using (var connection = f.Database.Connections.Create())
        {
            await connection.OpenAsync(); await using var command = connection.CreateCommand();
            // Deterministic stand-in for a snapshot committed while the first rebuild was running.
            command.CommandText = """
                CREATE TRIGGER fixture_concurrent_snapshot AFTER UPDATE OF status ON rebuild_jobs
                WHEN NEW.status='completed' AND (SELECT generation FROM catalog_snapshots WHERE provider_key=NEW.provider_key)=1
                BEGIN
                  UPDATE catalog_snapshots SET generation=2 WHERE provider_key=NEW.provider_key;
                  UPDATE catalog_items SET title='Refreshed',item_json=json_set(item_json,'$.Title','Refreshed') WHERE provider_key=NEW.provider_key;
                END;
                """;
            await command.ExecuteNonQueryAsync();
        }
        await f.RunIndex(expectFollowup: true);
        Assert.Single((await new SearchService(f.Database.Paths).SearchAsync(f.Provider.Key, CatalogType.Vod, "Refreshed", 1, 10)).Items);
        Assert.Equal(2, await f.Snapshots.GenerationAsync(f.Provider.Key));
        Assert.Empty(f.Http.Actions);
    }

    [Theory]
    [InlineData("get_live_categories")] [InlineData("account")]
    public async Task ManualCategoryOrAccountErrorIsNotRetried(string action)
    {
        await using var f = await Fixture.Create(); f.Http.FailAction = action; f.Http.Failure = "520";
        if (action == "account") await Assert.ThrowsAsync<HttpRequestException>(() => f.Management.DiagnoseAsync(f.Provider.Key));
        else await Assert.ThrowsAsync<HttpRequestException>(() => f.Management.SyncCategoriesAsync(f.Provider.Key));
        Assert.Equal([action], f.Http.Actions.ToArray());
    }

    [Fact]
    public async Task SimultaneousFailedDetailSharesOnePhysicalAttempt()
    {
        await using var f = await Fixture.Create();
        f.Http.BlockAction = f.Http.FailAction = "get_series_info"; f.Http.Failure = "520";
        var first = f.Details.SeriesAsync(f.Provider, "1");
        await f.Http.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = f.Details.SeriesAsync(f.Provider, "1");
        f.Http.Release.TrySetResult();
        await Assert.ThrowsAsync<HttpRequestException>(() => first);
        await Assert.ThrowsAsync<HttpRequestException>(() => second);
        Assert.Equal(["get_series_info"], f.Http.Actions.ToArray());
    }

    [Fact]
    public async Task SessionBridgeRejectsInactiveProviderWithoutNetwork()
    {
        await using var f = await Fixture.Create();
        var other = f.Provider with { Key = "other-provider" }; await f.Database.Repository.AddAsync(other);
        using var result = JsonDocument.Parse(await f.Router.HandleAsync(JsonSerializer.Serialize(new
        {
            id = "inactive-startup", method = "catalog.session", @params = new { providerKey = other.Key }
        })));
        Assert.False(result.RootElement.GetProperty("ok").GetBoolean());
        Assert.Empty(f.Http.Actions);
    }

    [Fact]
    public async Task ConcurrentReadersSeeOnlyCompleteGenerationsDuringBulkPublication()
    {
        await using var f = await Fixture.Create(); await f.Seed(f.Clock.Now);
        var items = Enumerable.Range(1, 3000).Select(i => new CatalogItem(i.ToString(), "New generation", CategoryId: "1")).ToArray();
        var catalogs = Enum.GetValues<CatalogType>().ToDictionary(type => type, _ => (IReadOnlyList<CatalogItem>)items);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var write = Task.Run(() => f.Snapshots.ReplaceAsync(f.Provider.Key, catalogs, null, f.Clock.Now, timeout.Token));
        try
        {
            do
            {
                var documents = await f.Snapshots.SearchDocumentsAsync(f.Provider.Key, timeout.Token);
                Assert.True(documents.Count is 2 or 6000);
                Assert.Single(documents.Select(item => item.Title).Distinct());
                await Task.Delay(1, timeout.Token);
            } while (!write.IsCompleted);
        }
        finally { await write; }
        Assert.Equal(6000, (await f.Snapshots.SearchDocumentsAsync(f.Provider.Key)).Count);
        Assert.Empty(f.Http.Actions);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        public required TestDatabase Database { get; init; }
        public required ProviderRecord Provider { get; init; }
        public required InMemorySecretStore Secrets { get; init; }
        public required CountingHttp Http { get; init; }
        public required ProviderClientFactory Remote { get; init; }
        public required LocalProviderClientFactory Local { get; init; }
        public required CatalogSnapshotRepository Snapshots { get; init; }
        public required ProviderManagementService Management { get; init; }
        public FakeClock Clock { get; } = new();
        public CatalogRefreshService Refresh { get; private set; } = null!;
        public MediaDetailService Details { get; private set; } = null!;
        public BridgeRouter Router { get; private set; } = null!;
        public static async Task<Fixture> Create()
        {
            var database = await TestDatabase.CreateAsync(); var secrets = new InMemorySecretStore();
            var provider = (await database.AddProviderAsync(secretReference: await secrets.PutAsync(new("fixture-user", "fixture-password")))) with { Enabled = true };
            await database.Repository.UpdateAsync(provider);
            await new AppSettingsRepository(database.Connections).SaveAsync(new(ActiveProviderKey: provider.Key));
            var http = new CountingHttp(); var remote = new ProviderClientFactory(new StubHttpClientFactory(http), secrets);
            var snapshots = new CatalogSnapshotRepository(database.Connections); var local = new LocalProviderClientFactory(remote, snapshots, database.Repository);
            var f = new Fixture { Database = database, Provider = provider, Secrets = secrets, Http = http, Remote = remote, Local = local, Snapshots = snapshots, Management = new(database.Repository, secrets, remote, new ProviderLocalDataStore(database.Paths)) };
            f.Refresh = f.NewRefresh(); var artwork = new RecentSeriesArtwork(new(database.Connections));
            f.Details = new(remote, snapshots, secrets, artwork);
            f.Router = new(database.Repository, local, null!, f.Management, new AppSettingsRepository(database.Connections),
                new RebuildJobRepository(database.Connections), new SearchService(database.Paths), artwork, new PlaybackHistoryRepository(database.Connections),
                secrets, null!, null!, NullLogger<BridgeRouter>.Instance, snapshots, f.Refresh, f.Details, new LiveChannelDisplayNameCache());
            return f;
        }
        public CatalogRefreshService NewRefresh() => new(Database.Repository, Remote, Secrets, Snapshots, new(Database.Connections), Clock);
        public Task Seed(DateTimeOffset at, CatalogItem? item = null) => Snapshots.ReplaceAsync(Provider.Key,
            Enum.GetValues<CatalogType>().ToDictionary(type => type, _ => (IReadOnlyList<CatalogItem>)[item ?? new("1", "Old", CategoryId: "1")]), new("fixture-user", "fixture-password"), at);
        public async Task<JsonElement> Rpc(string method, object? parameters = null)
        {
            using var response = JsonDocument.Parse(await Router.HandleAsync(JsonSerializer.Serialize(new { id = Guid.NewGuid().ToString("N"), method, @params = parameters ?? new { providerKey = Provider.Key } })));
            Assert.True(response.RootElement.GetProperty("ok").GetBoolean(), response.RootElement.ToString());
            return response.RootElement.GetProperty("result").Clone();
        }
        public async Task RunIndex(bool expectFollowup = false)
        {
            var jobs = new RebuildJobRepository(Database.Connections); var firstJob = await jobs.QueueAsync(Provider.Key);
            using var worker = new IndexRebuildWorker(jobs, Database.Repository, Local, Secrets, new(Database.Paths), NullLogger<IndexRebuildWorker>.Instance, Snapshots);
            await worker.StartAsync(default);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            try
            {
                IndexJobSnapshot? status;
                do { await Task.Delay(15, timeout.Token); status = await jobs.LatestAsync(Provider.Key, timeout.Token); }
                while (status?.Status is RebuildJobStatus.Running or RebuildJobStatus.Queued || (expectFollowup && status?.Id == firstJob));
                Assert.Equal(RebuildJobStatus.Completed, status?.Status);
            }
            finally { await worker.StopAsync(default); }
        }
        public async ValueTask DisposeAsync() { Refresh.Dispose(); Details.Dispose(); Http.Dispose(); await Database.DisposeAsync(); }
    }

    private sealed class FakeClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 9, 30, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class CountingHttp : HttpMessageHandler
    {
        public ConcurrentQueue<string> Actions { get; } = new();
        public string? FailAction { get; set; }
        public string? Failure { get; set; }
        public string? BlockAction { get; set; }
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool HadCategoryParameter;
        private int _active;
        public int MaxActive;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var query = request.RequestUri!.Query.TrimStart('?').Split('&').Select(pair => pair.Split('=', 2)).ToDictionary(pair => pair[0], pair => Uri.UnescapeDataString(pair[1]));
            var action = query.GetValueOrDefault("action", "account"); Actions.Enqueue(action);
            HadCategoryParameter |= query.ContainsKey("category_id");
            var active = Interlocked.Increment(ref _active); MaxActive = Math.Max(active, MaxActive);
            try
            {
                if (BlockAction == action) { Entered.TrySetResult(); await Release.Task.WaitAsync(cancellationToken); }
                await Task.Delay(2, cancellationToken);
                if (action == FailAction)
                {
                    if (Failure == "timeout") throw new TaskCanceledException("Fixture timeout");
                    if (Failure == "network") throw new HttpRequestException("Fixture network error");
                    if (Failure == "json") return Response("{broken");
                    if (Failure == "shape") return Response("{}");
                    return new(Failure == "redirect" ? HttpStatusCode.Redirect : (HttpStatusCode)int.Parse(Failure!)) { Content = new StringContent("Fixture error") };
                }
                if (Bulk.Contains(action)) return Response(JsonSerializer.Serialize(Enumerable.Range(1, 10).Select(i => new
                {
                    stream_id = i, series_id = i, name = $"Media {i}", category_id = i.ToString(), added = "1700000000", container_extension = "mkv"
                })));
                return Response(action switch
                {
                    "account" => """{"user_info":{"auth":1,"status":"Active"}}""",
                    "get_live_categories" or "get_vod_categories" or "get_series_categories" => """[{"category_id":"1","category_name":"Category fixture"}]""",
                    "get_series_info" => """{"info":{"name":"Series","cover":"https://images.example.invalid/cover.jpg","plot":"Fixture synopsis","genre":"Drama"},"episodes":{"1":[{"id":"501","title":"Episode one","episode_num":1,"container_extension":"mkv"}]}}""",
                    "get_vod_info" => """{"movie_data":{"name":"Film","container_extension":"mkv"},"info":{"plot":"Fixture synopsis"}}""",
                    _ => throw new InvalidOperationException("Unexpected fixture action. Network fallback is forbidden.")
                });
            }
            finally { Interlocked.Decrement(ref _active); }
        }
        private static HttpResponseMessage Response(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    }
}
