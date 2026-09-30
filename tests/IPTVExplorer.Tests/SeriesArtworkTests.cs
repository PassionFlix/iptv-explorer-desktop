using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using IPTVExplorer.Core;
using IPTVExplorer.Infrastructure;
using IPTVExplorer.Providers;
using Microsoft.Extensions.Logging.Abstractions;

namespace IPTVExplorer.Tests;

public sealed class SeriesArtworkTests
{
    private const string Poster = "https://images.example.invalid/cover.jpg";
    private const string Broken = "https://images.example.invalid/old-cover.jpg";
    private static readonly ProviderSecret Secret = new("fixture-user", "fixture-password");

    [Fact]
    public async Task ListCoverSurvivesIndexAndNeedsNoDetailRequest()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Handler.ListJson = """[{"series_id":1,"name":"Series","cover":"https://images.example.invalid/image/1?w=300","last_modified":"1700000000"}]""";
        var client = await fixture.Factory.CreateAsync(fixture.Provider);
        var list = await client.GetSeriesPageAsync("1", 1);
        var item = Assert.Single(list.Items);
        await new AtomicSearchIndex(fixture.Database.Paths).ReplaceAsync(fixture.Provider.Key,
            [Hit(fixture.Provider, item.Id, item.ImageUrl) with { AddedAt = item.AddedAt }]);
        var recent = await new SearchService(fixture.Database.Paths).RecentlyAddedAsync(fixture.Provider.Key, CatalogType.Series, 20);
        Assert.Equal(item.ImageUrl, Assert.Single(await fixture.Service.EnrichAsync(fixture.Provider, Secret, recent)).ImageUrl);
        Assert.Empty(fixture.Handler.DetailIds);
    }

    [Theory]
    [InlineData("cover")]
    [InlineData("movie_image")]
    public async Task MissingCoverIsEnrichedPersistedAndReusedAfterServiceRestart(string field)
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Handler.DetailJson = _ => field == "cover" ? """{"info":{"cover":"https://images.example.invalid/cover.jpg"}}"""
            : """{"info":{"cover":"","movie_image":"https://images.example.invalid/cover.jpg"}}""";
        var hits = new[] { Hit(fixture.Provider, "1") };
        Assert.Equal(Poster, Assert.Single(await fixture.Service.EnrichAsync(fixture.Provider, Secret, hits)).ImageUrl);
        Assert.Single(fixture.Handler.DetailIds);
        var restarted = new RecentSeriesArtwork(new SeriesArtworkRepository(fixture.Database.Connections), fixture.Factory);
        Assert.Equal(Poster, Assert.Single(await restarted.EnrichAsync(fixture.Provider, Secret, hits)).ImageUrl);
        Assert.Single(fixture.Handler.DetailIds);
        Assert.Equal(Poster, (await fixture.Cache.GetAsync(fixture.Provider.Key, "1", default))?.ImageUrl);
    }

    [Fact]
    public async Task BrowserReported404IsRecoveredAndOverridesStaleIndexOnNextHome()
    {
        await using var fixture = await Fixture.CreateAsync();
        var hits = new[] { Hit(fixture.Provider, "1", Broken) };
        Assert.Equal(Broken, Assert.Single(await fixture.Service.EnrichAsync(fixture.Provider, Secret, hits)).ImageUrl);
        Assert.Empty(fixture.Handler.DetailIds);
        var recovered = await fixture.Service.EnrichAsync(fixture.Provider, Secret, hits, failedImages: new Dictionary<string, string> { ["1"] = Broken });
        Assert.Equal(Poster, Assert.Single(recovered).ImageUrl);
        Assert.Equal(Poster, Assert.Single(await fixture.Service.EnrichAsync(fixture.Provider, Secret, hits)).ImageUrl);
        Assert.Single(fixture.Handler.DetailIds);
    }

    [Fact]
    public async Task BrokenOrMissingDetailPosterLeavesPlaceholderAndDoesNotLoop()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Handler.DetailJson = _ => """{"info":{}}""";
        var hits = new[] { Hit(fixture.Provider, "1") };
        Assert.Null(Assert.Single(await fixture.Service.EnrichAsync(fixture.Provider, Secret, hits)).ImageUrl);
        Assert.Null(Assert.Single(await fixture.Service.EnrichAsync(fixture.Provider, Secret, hits)).ImageUrl);
        Assert.Single(fixture.Handler.DetailIds);
    }

    [Fact]
    public async Task DetailReturningSameBrokenUrlDoesNotRetryOnEveryHome()
    {
        await using var fixture = await Fixture.CreateAsync();
        var hits = new[] { Hit(fixture.Provider, "1", Poster) };
        var failed = new Dictionary<string, string> { ["1"] = Poster };
        Assert.Null(Assert.Single(await fixture.Service.EnrichAsync(fixture.Provider, Secret, hits, failedImages: failed)).ImageUrl);
        Assert.Null(Assert.Single(await fixture.Service.EnrichAsync(fixture.Provider, Secret, hits, failedImages: failed)).ImageUrl);
        Assert.Single(fixture.Handler.DetailIds);
    }

    [Fact]
    public async Task ProviderFailureDoesNotFailHomeAndHasShortRetryCooldown()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Handler.Fail = true;
        var hits = new[] { Hit(fixture.Provider, "1") };
        Assert.Null(Assert.Single(await fixture.Service.EnrichAsync(fixture.Provider, Secret, hits)).ImageUrl);
        Assert.Null(Assert.Single(await fixture.Service.EnrichAsync(fixture.Provider, Secret, hits)).ImageUrl);
        Assert.Single(fixture.Handler.DetailIds);
    }

    [Fact]
    public async Task CacheIsProviderIsolatedAndDeletedWithProvider()
    {
        await using var fixture = await Fixture.CreateAsync();
        var other = fixture.Provider with { Key = "other-provider" };
        await fixture.Database.Repository.AddAsync(other);
        await fixture.Service.EnrichAsync(fixture.Provider, Secret, [Hit(fixture.Provider, "1")]);
        Assert.Null(await fixture.Cache.GetAsync(other.Key, "1", default));
        await fixture.Service.EnrichAsync(other, Secret, [Hit(other, "1")]);
        Assert.Equal(2, fixture.Handler.DetailIds.Count);
        await fixture.Database.Repository.DeleteAsync(fixture.Provider.Key);
        Assert.Null(await fixture.Cache.GetAsync(fixture.Provider.Key, "1", default));
        Assert.NotNull(await fixture.Cache.GetAsync(other.Key, "1", default));
    }

    [Fact]
    public async Task OnlyNewest20SeriesAreHydratedWithAtMost3ConcurrentRequests()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Handler.DelayMs = 25;
        var hits = Enumerable.Range(1, 14000).Select(i => Hit(fixture.Provider, i.ToString()) with { AddedAt = DateTimeOffset.UnixEpoch.AddDays(i) })
            .Append(Hit(fixture.Provider, "undated") with { AddedAt = null })
            .Append(Hit(fixture.Provider, "film") with { Catalog = CatalogType.Vod, AddedAt = DateTimeOffset.MaxValue })
            .Append(Hit(fixture.Provider, "foreign") with { ProviderKey = "other-provider", AddedAt = DateTimeOffset.MaxValue }).ToArray();
        var result = await fixture.Service.EnrichAsync(fixture.Provider, Secret, hits);
        Assert.Equal(20, result.Count);
        Assert.All(result, item => Assert.Equal(Poster, item.ImageUrl));
        Assert.Equal(Enumerable.Range(13981, 20).Select(i => i.ToString()).Order(), fixture.Handler.DetailIds.Order());
        Assert.InRange(fixture.Handler.Peak, 2, 3);
    }

    [Fact]
    public async Task ConcurrentHomesShareSuccessfulResultsAndKeepGlobalConcurrencyBounded()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Handler.DelayMs = 25;
        var hits = Enumerable.Range(1, 20).Select(i => Hit(fixture.Provider, i.ToString())).ToArray();
        var results = await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => fixture.Service.EnrichAsync(fixture.Provider, Secret, hits)));
        Assert.All(results.SelectMany(result => result), item => Assert.Equal(Poster, item.ImageUrl));
        Assert.Equal(20, fixture.Handler.DetailIds.Count);
        Assert.InRange(fixture.Handler.Peak, 1, 3);
    }

    [Fact]
    public async Task FailedImageCannotHydrateIdsOutsideRecent20OrUnrelatedUrls()
    {
        await using var fixture = await Fixture.CreateAsync();
        var hits = new[] { Hit(fixture.Provider, "1", Poster) };
        await fixture.Service.EnrichAsync(fixture.Provider, Secret, hits, failedImages: new Dictionary<string, string> { ["outside"] = Poster });
        await fixture.Service.EnrichAsync(fixture.Provider, Secret, hits, failedImages: new Dictionary<string, string> { ["1"] = Broken });
        Assert.Empty(fixture.Handler.DetailIds);
    }

    [Fact]
    public async Task CallerCancellationStopsProviderWork()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Handler.DelayMs = 30000;
        using var cancellation = new CancellationTokenSource();
        var work = fixture.Service.EnrichAsync(fixture.Provider, Secret, [Hit(fixture.Provider, "1")], cancellation.Token);
        await fixture.Handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => work);
        Assert.Null(await fixture.Cache.GetAsync(fixture.Provider.Key, "1", default));
    }

    [Fact]
    public async Task SlowProviderCannotHoldHomeBeyondEnrichmentBudget()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Handler.DelayMs = 30000;
        var result = await fixture.Service.EnrichAsync(fixture.Provider, Secret, [Hit(fixture.Provider, "1")])
            .WaitAsync(RecentSeriesArtwork.TimeBudget + TimeSpan.FromSeconds(5));
        Assert.Null(Assert.Single(result).ImageUrl);
        Assert.Single(fixture.Handler.DetailIds);
    }

    [Fact]
    public async Task CacheActivityDoesNotLockAtomicIndexReplacementAndSurvivesRebuild()
    {
        await using var fixture = await Fixture.CreateAsync();
        var index = new AtomicSearchIndex(fixture.Database.Paths);
        var search = new SearchService(fixture.Database.Paths);
        var hits = new[] { Hit(fixture.Provider, "1") };
        await index.ReplaceAsync(fixture.Provider.Key, hits);
        var recent = await search.RecentlyAddedAsync(fixture.Provider.Key, CatalogType.Series, 20);
        fixture.Handler.DelayMs = 100;
        var enrichment = fixture.Service.EnrichAsync(fixture.Provider, Secret, recent);
        await fixture.Handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await index.ReplaceAsync(fixture.Provider.Key, [hits[0] with { Title = "Rebuilt series" }]);
        await enrichment;
        var reopened = await search.RecentlyAddedAsync(fixture.Provider.Key, CatalogType.Series, 20);
        Assert.Equal("Rebuilt series", Assert.Single(reopened).Title);
        Assert.Equal(Poster, Assert.Single(await fixture.Service.EnrichAsync(fixture.Provider, Secret, reopened)).ImageUrl);
        Assert.Single(fixture.Handler.DetailIds);
        Assert.False(File.Exists(fixture.Database.Paths.SearchIndex(fixture.Provider.Key) + ".previous"));
        Assert.False(File.Exists(fixture.Database.Paths.SearchIndex(fixture.Provider.Key) + "-wal"));
    }

    [Fact]
    public async Task RebuildWorkerEnrichesOnlyRecent20BeforeAtomicInstall()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Handler.ListJson = JsonSerializer.Serialize(Enumerable.Range(1, 30).Select(i => new { series_id = i, name = $"Series {i}", last_modified = (1700000000 + i).ToString() }));
        await fixture.Database.Repository.SyncCategoriesAsync(fixture.Provider.Key, CatalogType.Series, [new("1", "Series", "SERIES")]);
        var jobs = new RebuildJobRepository(fixture.Database.Connections);
        await jobs.QueueAsync(fixture.Provider.Key);
        using var worker = new IndexRebuildWorker(jobs, fixture.Database.Repository, fixture.Factory, fixture.Secrets,
            new AtomicSearchIndex(fixture.Database.Paths), fixture.Service, NullLogger<IndexRebuildWorker>.Instance);
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
        var recent = await new SearchService(fixture.Database.Paths).RecentlyAddedAsync(fixture.Provider.Key, CatalogType.Series, 20);
        Assert.Equal(20, recent.Count);
        Assert.All(recent, item => Assert.Equal(Poster, item.ImageUrl));
        Assert.Equal(20, fixture.Handler.DetailIds.Count);
        await fixture.Service.EnrichAsync(fixture.Provider, Secret, recent);
        Assert.Equal(20, fixture.Handler.DetailIds.Count);
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

    [Fact]
    public async Task UnsafeEnrichmentIsNeverReturnedOrPersisted()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Handler.DetailJson = _ => """{"info":{"cover":"https://images.example.invalid/a.jpg?token=fixture-private"}}""";
        Assert.Null(Assert.Single(await fixture.Service.EnrichAsync(fixture.Provider, Secret, [Hit(fixture.Provider, "1")])).ImageUrl);
        Assert.Null((await fixture.Cache.GetAsync(fixture.Provider.Key, "1", default))?.ImageUrl);
        Assert.All(await fixture.Database.ReadRawSqliteStorageAsync(), content => Assert.DoesNotContain("fixture-private", content));
    }

    private static SearchHit Hit(ProviderRecord provider, string id, string? image = null) => new(provider.Key, CatalogType.Series, id, "Series " + id, image, DateTimeOffset.FromUnixTimeSeconds(1700000000));

    private sealed class Fixture : IAsyncDisposable
    {
        public required TestDatabase Database { get; init; }
        public required ProviderRecord Provider { get; init; }
        public required InMemorySecretStore Secrets { get; init; }
        public required ProviderClientFactory Factory { get; init; }
        public required Handler Handler { get; init; }
        public required SeriesArtworkRepository Cache { get; init; }
        public required RecentSeriesArtwork Service { get; init; }
        public static async Task<Fixture> CreateAsync()
        {
            var database = await TestDatabase.CreateAsync();
            var secrets = new InMemorySecretStore();
            var provider = await database.AddProviderAsync(secretReference: await secrets.PutAsync(Secret));
            provider = provider with { Enabled = true };
            await database.Repository.UpdateAsync(provider);
            var handler = new Handler();
            var factory = new ProviderClientFactory(new StubHttpClientFactory(handler), secrets);
            var cache = new SeriesArtworkRepository(database.Connections);
            return new Fixture { Database = database, Provider = provider, Secrets = secrets, Factory = factory, Handler = handler, Cache = cache, Service = new(cache, factory) };
        }
        public async ValueTask DisposeAsync() { Handler.Dispose(); await Database.DisposeAsync(); }
    }

    private sealed class Handler : HttpMessageHandler
    {
        public ConcurrentBag<string> DetailIds { get; } = [];
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Func<string, string> DetailJson { get; set; } = _ => """{"info":{"cover":"https://images.example.invalid/cover.jpg"}}""";
        public string ListJson { get; set; } = "[]";
        public bool Fail { get; set; }
        public int DelayMs { get; set; }
        public int Peak;
        private int _active;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var values = request.RequestUri!.Query.TrimStart('?').Split('&').Select(pair => pair.Split('=', 2)).ToDictionary(pair => pair[0], pair => Uri.UnescapeDataString(pair[1]));
            if (values["action"] != "get_series_info") return Response(ListJson);
            var id = values["series_id"];
            DetailIds.Add(id);
            var active = Interlocked.Increment(ref _active);
            int peak;
            do { peak = Peak; } while (active > peak && Interlocked.CompareExchange(ref Peak, active, peak) != peak);
            Started.TrySetResult();
            try
            {
                if (DelayMs > 0) await Task.Delay(DelayMs, cancellationToken);
                return Fail ? new(HttpStatusCode.BadRequest) { Content = new StringContent("Unavailable") } : Response(DetailJson(id));
            }
            finally { Interlocked.Decrement(ref _active); }
        }
        private static HttpResponseMessage Response(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    }
}
