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

public sealed class SeriesArtworkTests
{
    private const string Poster = "https://images.example.invalid/cover.jpg";
    private const string Broken = "https://images.example.invalid/old-cover.jpg";
    private static readonly ProviderSecret Secret = new("fixture-user", "fixture-password");

    [Theory]
    [InlineData(null)]
    [InlineData(Poster)]
    [InlineData(Broken)]
    public async Task HomeAndDashboardRemainLocalRegardlessOfIndexedPoster(string? image)
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.IndexAsync([Hit(fixture.Provider, "1", image)]);
        for (var i = 0; i < 3; i++)
        {
            var home = await fixture.SendAsync("home.content");
            Assert.Equal(image, home.GetProperty("recentlyAddedSeries")[0].GetProperty("imageUrl").GetString());
            var dashboard = await fixture.SendAsync("providers.dashboard");
            Assert.Equal(JsonValueKind.Null, dashboard.GetProperty("available").ValueKind);
        }
        Assert.Equal(0, fixture.Factory.Calls);
        Assert.Empty(fixture.Handler.Actions);
        Assert.Null(await fixture.Cache.GetAsync(fixture.Provider.Key, "1", default));
    }

    [Fact]
    public async Task HomeSerializesTypedBackdropThenPosterWithoutProviderTraffic()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.IndexAsync([new(fixture.Provider.Key, CatalogType.Series, "1", "Series 1", Poster, DateTimeOffset.FromUnixTimeSeconds(1700000000), "https://images.example.invalid/backdrop.jpg")]);

        var backgrounds = (await fixture.SendAsync("home.content")).GetProperty("backgroundImages");
        var candidates = backgrounds[0];
        Assert.Equal("backdrop", candidates[0].GetProperty("kind").GetString());
        Assert.Equal("https://images.example.invalid/backdrop.jpg", candidates[0].GetProperty("url").GetString());
        Assert.Equal("poster", candidates[1].GetProperty("kind").GetString());
        Assert.Equal(Poster, candidates[1].GetProperty("url").GetString());
        Assert.Equal(0, fixture.Factory.Calls);
        Assert.Empty(fixture.Handler.Actions);
    }

    [Theory]
    [InlineData("cover")]
    [InlineData("movie_image")]
    public async Task UserDetailRequestRemembersPosterAndFollowingHomesUseCache(string field)
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Handler.DetailJson = field == "cover" ? """{"info":{"cover":"https://images.example.invalid/cover.jpg"}}"""
            : """{"info":{"cover":"","movie_image":"https://images.example.invalid/cover.jpg"}}""";
        await fixture.IndexAsync([Hit(fixture.Provider, "1")]);
        Assert.Null((await fixture.SendAsync("home.content")).GetProperty("recentlyAddedSeries")[0].GetProperty("imageUrl").GetString());
        Assert.Equal(Poster, (await fixture.SendAsync("catalog.series.detail", "1")).GetProperty("poster").GetString());
        Assert.Equal(Poster, (await fixture.Cache.GetAsync(fixture.Provider.Key, "1", default))?.ImageUrl);
        fixture.Router = fixture.CreateRouter(); // Fresh local artwork service; persistence is not just in-memory.
        for (var i = 0; i < 2; i++)
            Assert.Equal(Poster, (await fixture.SendAsync("home.content")).GetProperty("recentlyAddedSeries")[0].GetProperty("imageUrl").GetString());
        Assert.Equal(["get_series_info"], fixture.Handler.Actions.ToArray());
        Assert.Equal(1, fixture.Factory.Calls);
    }

    [Fact]
    public async Task CachedDetailPosterOverridesBrokenIndexedPosterWithoutProviderCall()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.IndexAsync([Hit(fixture.Provider, "1", Broken)]);
        await fixture.SendAsync("catalog.series.detail", "1");
        Assert.Equal(Poster, (await fixture.SendAsync("home.content")).GetProperty("recentlyAddedSeries")[0].GetProperty("imageUrl").GetString());
        Assert.Single(fixture.Handler.Actions);
    }

    [Fact]
    public async Task OldAutomaticRecoveryEndpointNoLongerExists()
    {
        await using var fixture = await Fixture.CreateAsync();
        using var response = JsonDocument.Parse(await fixture.Router.HandleAsync(JsonSerializer.Serialize(new
        {
            id = "obsolete", method = "home.seriesArtwork", @params = new { providerKey = fixture.Provider.Key, mediaId = "1", failedImageUrl = Broken }
        })));
        Assert.False(response.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal(0, fixture.Factory.Calls);
        Assert.Empty(fixture.Handler.Actions);
    }

    [Fact]
    public async Task MissingDetailPosterDoesNotEraseAnExistingSafeCacheEntry()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.SendAsync("catalog.series.detail", "1");
        fixture.Handler.DetailJson = """{"info":{}}""";
        Assert.Equal(Poster, (await fixture.SendAsync("catalog.series.detail", "1")).GetProperty("poster").GetString());
        Assert.Equal(Poster, (await fixture.Cache.GetAsync(fixture.Provider.Key, "1", default))?.ImageUrl);
        Assert.Single(fixture.Handler.Actions);
    }

    [Theory]
    [InlineData("403", 1)]
    [InlineData("429", 1)]
    [InlineData("520", 1)]
    [InlineData("timeout", 1)]
    [InlineData("network", 1)]
    public async Task DetailFailureNeverRetriesAndHomeRemainsPassive(string error, int expectedRequests)
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Handler.Error = error;
        await fixture.IndexAsync([Hit(fixture.Provider, "1")]);
        using var response = JsonDocument.Parse(await fixture.Router.HandleAsync(JsonSerializer.Serialize(new
        {
            id = "detail-error", method = "catalog.series.detail", @params = new { providerKey = fixture.Provider.Key, mediaId = "1" }
        })));
        Assert.False(response.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal(expectedRequests, fixture.Handler.Actions.Count);
        await fixture.SendAsync("home.content");
        await fixture.SendAsync("home.content");
        await fixture.SendAsync("providers.dashboard");
        Assert.Equal(expectedRequests, fixture.Handler.Actions.Count);
        Assert.Equal(1, fixture.Factory.Calls);
        Assert.Null(await fixture.Cache.GetAsync(fixture.Provider.Key, "1", default));
    }

    [Fact]
    public async Task EmptyDetailAndEmptyCacheKeepPlaceholderOnSubsequentHome()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Handler.DetailJson = """{"info":{}}""";
        await fixture.IndexAsync([Hit(fixture.Provider, "1")]);
        await fixture.SendAsync("catalog.series.detail", "1");
        Assert.Null((await fixture.SendAsync("home.content")).GetProperty("recentlyAddedSeries")[0].GetProperty("imageUrl").GetString());
        Assert.Null(await fixture.Cache.GetAsync(fixture.Provider.Key, "1", default));
        Assert.Single(fixture.Handler.Actions);
    }

    [Fact]
    public async Task ConcurrentHomesDoNotHydrateAnyOfThe20RecentSeries()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.IndexAsync(Enumerable.Range(1, 30).Select(i => Hit(fixture.Provider, i.ToString())));
        var homes = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => fixture.SendAsync("home.content")));
        Assert.All(homes, home =>
        {
            var recent = home.GetProperty("recentlyAddedSeries");
            Assert.Equal(20, recent.GetArrayLength());
            Assert.All(recent.EnumerateArray(), item => Assert.Equal(JsonValueKind.Null, item.GetProperty("imageUrl").ValueKind));
        });
        Assert.Equal(0, fixture.Factory.Calls);
        Assert.Empty(fixture.Handler.Actions);
    }

    [Fact]
    public async Task CacheIsProviderIsolatedAndDeletedWithProvider()
    {
        await using var fixture = await Fixture.CreateAsync();
        var other = fixture.Provider with { Key = "other-provider" };
        await fixture.Database.Repository.AddAsync(other);
        await fixture.SendAsync("catalog.series.detail", "1");
        Assert.Null(await fixture.Cache.GetAsync(other.Key, "1", default));
        var service = new RecentSeriesArtwork(fixture.Cache);
        Assert.Null(Assert.Single(await service.ApplyCachedAsync(other.Key, Secret, [Hit(other, "1")])).ImageUrl);
        await service.RememberDetailAsync(other.Key, "1", Poster, Secret);
        await fixture.Database.Repository.DeleteAsync(fixture.Provider.Key);
        Assert.Null(await fixture.Cache.GetAsync(fixture.Provider.Key, "1", default));
        Assert.NotNull(await fixture.Cache.GetAsync(other.Key, "1", default));
        Assert.Single(fixture.Handler.Actions);
    }

    [Fact]
    public async Task CachedPosterSurvivesAtomicReplacementAfterPooledIndexReads()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.IndexAsync([Hit(fixture.Provider, "1")]);
        await fixture.SendAsync("home.content");
        await fixture.SendAsync("catalog.series.detail", "1");
        await fixture.IndexAsync([Hit(fixture.Provider, "1") with { Title = "Rebuilt series" }]);
        var recent = (await fixture.SendAsync("home.content")).GetProperty("recentlyAddedSeries")[0];
        Assert.Equal("Rebuilt series", recent.GetProperty("title").GetString());
        Assert.Equal(Poster, recent.GetProperty("imageUrl").GetString());
        Assert.Single(fixture.Handler.Actions);
        Assert.False(File.Exists(fixture.Database.Paths.SearchIndex(fixture.Provider.Key) + ".previous"));
    }

    [Fact]
    public async Task RebuildWorkerUsesOnlySnapshotAndNeverRequestsProvider()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.Snapshots.ReplaceAsync(fixture.Provider.Key, new Dictionary<CatalogType, IReadOnlyList<CatalogItem>>
        {
            [CatalogType.Live] = [], [CatalogType.Vod] = [],
            [CatalogType.Series] = Enumerable.Range(1, 30).Select(i => new CatalogItem(i.ToString(), $"Series {i}", CategoryId: "1", AddedAt: DateTimeOffset.FromUnixTimeSeconds(1700000000 + i))).ToArray()
        }, Secret, DateTimeOffset.UtcNow);
        await fixture.Database.Repository.SyncCategoriesAsync(fixture.Provider.Key, CatalogType.Series, [new("1", "Series", "SERIES")]);
        var jobs = new RebuildJobRepository(fixture.Database.Connections);
        await jobs.QueueAsync(fixture.Provider.Key);
        using var worker = new IndexRebuildWorker(jobs, fixture.Database.Repository, fixture.Factory, fixture.Secrets,
            new AtomicSearchIndex(fixture.Database.Paths), NullLogger<IndexRebuildWorker>.Instance, fixture.Snapshots);
        await worker.StartAsync(default);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        try
        {
            IndexJobSnapshot? snapshot;
            do
            {
                await Task.Delay(20, timeout.Token);
                snapshot = await jobs.LatestAsync(fixture.Provider.Key, timeout.Token);
            } while (snapshot?.Status is RebuildJobStatus.Queued or RebuildJobStatus.Running);
            Assert.Equal(RebuildJobStatus.Completed, snapshot?.Status);
        }
        finally { await worker.StopAsync(default); }
        var recent = (await fixture.SendAsync("home.content")).GetProperty("recentlyAddedSeries");
        Assert.Equal(20, recent.GetArrayLength());
        Assert.All(recent.EnumerateArray(), item => Assert.Equal(JsonValueKind.Null, item.GetProperty("imageUrl").ValueKind));
        Assert.Empty(fixture.Handler.Actions);
        Assert.Equal(0, fixture.Factory.Calls);
    }

    [Fact]
    public async Task CancelledLocalLookupAndCacheWriteDoNotContactProvider()
    {
        await using var fixture = await Fixture.CreateAsync();
        var service = new RecentSeriesArtwork(fixture.Cache);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.ApplyCachedAsync(fixture.Provider.Key, Secret, [Hit(fixture.Provider, "1")], cancellation.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.RememberDetailAsync(fixture.Provider.Key, "1", Poster, Secret, cancellation.Token));
        Assert.Empty(fixture.Handler.Actions);
    }

    [Fact]
    public async Task UnavailableLocalCacheDoesNotBreakDetailOrStartAnotherRequest()
    {
        await using var fixture = await Fixture.CreateAsync();
        await using var connection = fixture.Database.Connections.Create();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "DROP TABLE series_artwork";
        await command.ExecuteNonQueryAsync();
        await fixture.IndexAsync([Hit(fixture.Provider, "1")]);
        Assert.Equal(Poster, (await fixture.SendAsync("catalog.series.detail", "1")).GetProperty("poster").GetString());
        Assert.Null((await fixture.SendAsync("home.content")).GetProperty("recentlyAddedSeries")[0].GetProperty("imageUrl").GetString());
        Assert.Single(fixture.Handler.Actions);
    }

    [Theory]
    [InlineData("https://account:password@images.example.invalid/a.jpg")]
    [InlineData("https://images.example.invalid/a.jpg?%2574oken=opaque")]
    [InlineData("https://images.example.invalid/a.jpg?accessToken=opaque")]
    [InlineData("https://images.example.invalid/a.jpg?apiKey=opaque")]
    [InlineData("https://images.example.invalid/a.jpg?auth=opaque")]
    [InlineData("https://images.example.invalid/a.jpg?credentials=opaque")]
    [InlineData("https://images.example.invalid/a.jpg?username=someone")]
    [InlineData("https://images.example.invalid/a.jpg?password=opaque")]
    [InlineData("https://images.example.invalid/a.jpg?mac=opaque")]
    [InlineData("https://images.example.invalid/a.jpg?X-Amz-Signature=opaque")]
    [InlineData("https://images.example.invalid/series/a/b/image.jpg")]
    [InlineData("https://images.example.invalid/%256dovie/a/b/image.jpg")]
    [InlineData("https://images.example.invalid/live/a/b/image.jpg")]
    [InlineData("https://images.example.invalid/fixture-user/a.jpg")]
    [InlineData("https://images.example.invalid/a.jpg?w=fixture-password")]
    [InlineData("https://images.example.invalid/00%3A11%3A22%3A33%3A44%3A55/a.jpg")]
    [InlineData("file:///C:/a.jpg")]
    public void UnsafePosterIsRejected(string url) => Assert.Null(MediaArtwork.SafeImageUrl(url, Secret));

    [Theory]
    [InlineData("https://images.example.invalid/a.jpg?w=300&quality=80")]
    [InlineData("https://images.example.invalid/image/123")]
    public void PublicCdnPosterIsAllowedButBackdropPolicyStaysStrict(string url)
    {
        Assert.Equal(url, MediaArtwork.SafeImageUrl(url, Secret));
        Assert.Null(HomeArtwork.SafeUrl(url, Secret));
        Assert.Empty(HomeArtwork.Candidates(url, url, Secret));
    }

    [Theory]
    [InlineData("https://images.example.invalid/a.jpg?token=fixture-private")]
    [InlineData("https://images.example.invalid/fixture-password/cover.jpg")]
    public async Task UnsafeDetailPosterIsNeverReturnedOrPersisted(string url)
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Handler.DetailJson = JsonSerializer.Serialize(new { info = new { cover = url } });
        Assert.Null((await fixture.SendAsync("catalog.series.detail", "1")).GetProperty("poster").GetString());
        Assert.Null(await fixture.Cache.GetAsync(fixture.Provider.Key, "1", default));
        Assert.All(await fixture.Database.ReadRawSqliteStorageAsync(), content => Assert.DoesNotContain(url, content));
        Assert.Single(fixture.Handler.Actions);
    }

    private static SearchHit Hit(ProviderRecord provider, string id, string? image = null) => new(provider.Key, CatalogType.Series, id, "Series " + id, image, DateTimeOffset.FromUnixTimeSeconds(1700000000));

    private sealed class Fixture : IAsyncDisposable
    {
        public required TestDatabase Database { get; init; }
        public required ProviderRecord Provider { get; init; }
        public required InMemorySecretStore Secrets { get; init; }
        public required CountingFactory Factory { get; init; }
        public required Handler Handler { get; init; }
        public required SeriesArtworkRepository Cache { get; init; }
        public CatalogSnapshotRepository Snapshots => new(Database.Connections);
        public BridgeRouter Router { get; set; } = null!;
        public static async Task<Fixture> CreateAsync()
        {
            var database = await TestDatabase.CreateAsync();
            var secrets = new InMemorySecretStore();
            var provider = await database.AddProviderAsync(secretReference: await secrets.PutAsync(Secret));
            provider = provider with { Enabled = true };
            await database.Repository.UpdateAsync(provider);
            await new AppSettingsRepository(database.Connections).SaveAsync(new(ActiveProviderKey: provider.Key));
            var handler = new Handler();
            var factory = new CountingFactory(new ProviderClientFactory(new StubHttpClientFactory(handler), secrets));
            var fixture = new Fixture { Database = database, Provider = provider, Secrets = secrets, Factory = factory, Handler = handler, Cache = new(database.Connections) };
            fixture.Router = fixture.CreateRouter();
            return fixture;
        }
        public BridgeRouter CreateRouter() => new(Database.Repository, new LocalProviderClientFactory(Factory, Snapshots, Database.Repository), null!, null!,
            new AppSettingsRepository(Database.Connections), new RebuildJobRepository(Database.Connections),
            new SearchService(Database.Paths), new RecentSeriesArtwork(Cache), new PlaybackHistoryRepository(Database.Connections),
            Secrets, null!, NullLogger<BridgeRouter>.Instance, Snapshots,
            new CatalogRefreshService(Database.Repository, Factory, Secrets, Snapshots, new RebuildJobRepository(Database.Connections), TimeProvider.System),
            new MediaDetailService(Factory, Snapshots, Secrets, new RecentSeriesArtwork(Cache)));
        public Task IndexAsync(IEnumerable<SearchHit> documents) => new AtomicSearchIndex(Database.Paths).ReplaceAsync(Provider.Key, documents);
        public async Task<JsonElement> SendAsync(string method, string? mediaId = null)
        {
            using var response = JsonDocument.Parse(await Router.HandleAsync(JsonSerializer.Serialize(new
            {
                id = Guid.NewGuid().ToString("N"), method, @params = new { providerKey = Provider.Key, mediaId }
            })));
            Assert.True(response.RootElement.GetProperty("ok").GetBoolean(), response.RootElement.ToString());
            return response.RootElement.GetProperty("result").Clone();
        }
        public async ValueTask DisposeAsync() { Handler.Dispose(); await Database.DisposeAsync(); }
    }

    private sealed class CountingFactory(IProviderClientFactory inner) : IRemoteProviderClientFactory
    {
        public int Calls;
        public Task<IProviderClient> CreateAsync(ProviderRecord provider, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref Calls);
            return inner.CreateAsync(provider, cancellationToken);
        }
    }

    private sealed class Handler : HttpMessageHandler
    {
        public ConcurrentQueue<string> Actions { get; } = new();
        public string DetailJson { get; set; } = """{"info":{"cover":"https://images.example.invalid/cover.jpg"}}""";
        public string ListJson { get; set; } = "[]";
        public string? Error { get; set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var values = request.RequestUri!.Query.TrimStart('?').Split('&').Select(pair => pair.Split('=', 2)).ToDictionary(pair => pair[0], pair => Uri.UnescapeDataString(pair[1]));
            var action = values.GetValueOrDefault("action", "account");
            Actions.Enqueue(action);
            if (action == "get_series") return Task.FromResult(Response(ListJson));
            Assert.Equal("get_series_info", action); // All traffic is handled in-memory; there is no network fallback.
            if (Error == "timeout") throw new TaskCanceledException("Fixture timeout");
            if (Error == "network") throw new HttpRequestException("Fixture network failure");
            return Task.FromResult(Error is not null ? new HttpResponseMessage((HttpStatusCode)int.Parse(Error)) { Content = new StringContent("Fixture error") } : Response(DetailJson));
        }
        private static HttpResponseMessage Response(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    }
}
