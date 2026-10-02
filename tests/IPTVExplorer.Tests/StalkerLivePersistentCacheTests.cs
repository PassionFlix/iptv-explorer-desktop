using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using IPTVExplorer.Core;
using IPTVExplorer.Infrastructure;
using IPTVExplorer.Providers;

namespace IPTVExplorer.Tests;

public sealed class StalkerLivePersistentCacheTests
{
    private static readonly ProviderSecret Secret = new(MacAddress: "00:1A:79:AA:BB:CC");

    [Fact]
    public async Task FreshSixHourCacheSurvivesRestartWithoutAnotherBulkRequest()
    {
        await using var fixture = await Fixture.CreateAsync();
        Assert.Single(await fixture.Client().GetLiveAsync("100"));
        Assert.Equal(1, fixture.Http.Count("get_all_channels"));

        fixture.Clock.Now += TimeSpan.FromHours(5) + TimeSpan.FromMinutes(59);
        var restarted = fixture.Client();
        var cached = Assert.Single(await restarted.GetLiveAsync("100"));

        Assert.Equal("cached-1", cached.Id);
        Assert.Equal("100", cached.CategoryId);
        Assert.Equal(1, fixture.Http.Count("get_all_channels"));
        Assert.Equal(TimeSpan.FromHours(6), StalkerProviderClient.LiveCatalogCacheDuration);
    }

    [Fact]
    public async Task ExpiredCacheMakesOneBulkRequestAndPublishesNewGeneration()
    {
        await using var fixture = await Fixture.CreateAsync();
        _ = await fixture.Client().GetLiveAsync("100");
        var firstGeneration = await fixture.Snapshots.GenerationAsync(fixture.Provider.Key);

        fixture.Clock.Now += TimeSpan.FromHours(6);
        fixture.Http.Title = "Updated channel";
        var updated = Assert.Single(await fixture.Client().GetLiveAsync("100"));

        Assert.Equal("Updated channel", updated.Title);
        Assert.Equal(2, fixture.Http.Count("get_all_channels"));
        Assert.Equal(firstGeneration + 1, await fixture.Snapshots.GenerationAsync(fixture.Provider.Key));
    }

    [Fact]
    public async Task ManualRefreshReplacesOnlyLiveAndRetainsOldCacheOnFailure()
    {
        await using var fixture = await Fixture.CreateAsync();
        var client = fixture.Client();
        _ = await client.GetLiveAsync("100");
        var before = await fixture.Snapshots.RefreshedAtAsync(fixture.Provider.Key);

        fixture.Clock.Now += TimeSpan.FromMinutes(1);
        fixture.Http.Title = "Manual refresh";
        var refreshed = await client.RefreshLiveCatalogAsync();
        Assert.True(refreshed.Updated);
        Assert.Equal("Manual refresh", Assert.Single(await fixture.Snapshots.ReadAsync(fixture.Provider.Key, CatalogType.Live)).Title);
        Assert.Equal("Keep film", Assert.Single(await fixture.Snapshots.ReadAsync(fixture.Provider.Key, CatalogType.Vod)).Title);

        fixture.Clock.Now += TimeSpan.FromMinutes(1);
        fixture.Http.FailBulk = true;
        var failed = await client.RefreshLiveCatalogAsync();
        Assert.False(failed.Updated);
        Assert.Equal("retained", failed.State);
        Assert.Equal(refreshed.RefreshedAt, failed.RefreshedAt);
        Assert.NotEqual(before, failed.RefreshedAt);
        Assert.Equal("Manual refresh", Assert.Single(await fixture.Snapshots.ReadAsync(fixture.Provider.Key, CatalogType.Live)).Title);
        Assert.Equal(3, fixture.Http.Count("get_all_channels"));
    }

    [Fact]
    public async Task PersistentLiveRowsContainNoCommandSessionOrProviderSecretsAndKeepRealCategoryNames()
    {
        await using var fixture = await Fixture.CreateAsync();
        _ = await fixture.Client().GetLiveAsync("100");

        var category = Assert.Single(await fixture.Database.Repository.ListCategoriesAsync(fixture.Provider.Key, CatalogType.Live));
        Assert.Equal("Vraie catégorie", category.Name);
        var item = Assert.Single(await fixture.Snapshots.ReadAsync(fixture.Provider.Key, CatalogType.Live));
        Assert.Null(item.Metadata);
        Assert.DoesNotContain(Secret.MacAddress!, item.Title, StringComparison.OrdinalIgnoreCase);

        foreach (var content in await fixture.Database.ReadRawSqliteStorageAsync())
            foreach (var sensitive in new[] { Secret.MacAddress!, "session-token-fixture", "stale-token", "fresh-token", "ffmpeg ", "/play/live.php?" })
                Assert.DoesNotContain(sensitive, content, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PlaybackAfterRestartReacquiresOnlyRequestedCategoryCommand()
    {
        await using var fixture = await Fixture.CreateAsync();
        _ = await fixture.Client().GetLiveAsync("100");
        fixture.Http.Actions.Clear();

        var restarted = fixture.Client();
        var cached = Assert.Single(await restarted.GetLiveAsync("100"));
        var media = await restarted.ResolveMediaAsync(new MediaRequest(CatalogType.Live, cached.Id, CategoryId: cached.CategoryId));

        Assert.DoesNotContain("get_all_channels", fixture.Http.Actions);
        Assert.Equal(1, fixture.Http.Count("get_ordered_list"));
        Assert.Equal("100", fixture.Http.OrderedGenres.Single());
        Assert.Equal(1, fixture.Http.Count("create_link"));
        Assert.Equal("cached-1", Parameter(media.Uri, "stream"));
        Assert.NotNull(media.Headers);
        Assert.Contains("play_token=fresh-token", media.Headers!["Cookie"], StringComparison.Ordinal);
    }

    [Fact]
    public async Task FailedInitialBulkIsNotRepeatedByNormalNavigationInSameSession()
    {
        var handler = new LiveHttp { FailBulk = true };
        using var http = new HttpClient(handler);
        var provider = new ProviderRecord("no-cache-fixture", ProviderType.Stalker, "Fixture",
            new Uri("https://provider.invalid"), "fixture-secret", Enabled: true);
        var client = new StalkerProviderClient(provider, Secret, http);

        await Assert.ThrowsAnyAsync<Exception>(() => client.GetLiveAsync("100"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetLiveAsync("100"));

        Assert.Equal(1, handler.Count("get_all_channels"));
    }

    private sealed class Fixture : IAsyncDisposable
    {
        public required TestDatabase Database { get; init; }
        public required ProviderRecord Provider { get; init; }
        public required CatalogSnapshotRepository Snapshots { get; init; }
        public required LiveHttp Http { get; init; }
        public FakeClock Clock { get; } = new();

        public StalkerProviderClient Client() => new(Provider, Secret, new HttpClient(Http, disposeHandler: false), Snapshots, Clock);

        public static async Task<Fixture> CreateAsync()
        {
            var database = await TestDatabase.CreateAsync();
            var provider = (await database.AddProviderAsync(ProviderType.Stalker)) with { Enabled = true };
            await database.Repository.UpdateAsync(provider);
            await database.Repository.SyncCategoriesAsync(provider.Key, CatalogType.Live,
                [new ProviderCategory("100", "Vraie catégorie", "VRAIE CATEGORIE")]);
            var snapshots = new CatalogSnapshotRepository(database.Connections);
            await snapshots.ReplaceAsync(provider.Key, new Dictionary<CatalogType, IReadOnlyList<CatalogItem>>
            {
                [CatalogType.Live] = [],
                [CatalogType.Vod] = [new("vod-1", "Keep film", CategoryId: "200")],
                [CatalogType.Series] = []
            }, null, new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero));
            return new Fixture { Database = database, Provider = provider, Snapshots = snapshots, Http = new LiveHttp() };
        }

        public ValueTask DisposeAsync() => Database.DisposeAsync();
    }

    private sealed class FakeClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class LiveHttp : HttpMessageHandler
    {
        public ConcurrentQueue<string> Actions { get; } = new();
        public List<string> OrderedGenres { get; } = [];
        public string Title { get; set; } = "Channel 00:1A:79:AA:BB:CC";
        public bool FailBulk { get; set; }
        public int Count(string action) => Actions.Count(value => value == action);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var action = Parameter(request.RequestUri, "action") ?? string.Empty;
            Actions.Enqueue(action);
            if (action == "handshake") return Task.FromResult(Json("{\"js\":{\"token\":\"session-token-fixture\"}}"));
            if (action == "get_all_channels")
            {
                if (FailBulk) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { Content = new StringContent("fixture failure") });
                return Task.FromResult(Json(JsonSerializer.Serialize(new
                {
                    js = new
                    {
                        data = new[]
                        {
                            new
                            {
                                id = "cached-1", name = Title, tv_genre_id = "100",
                                logo = "https://images.example.invalid/channel.png",
                                cmd = "ffmpeg https://stream.example.invalid/play/live.php?mac=00:1A:79:AA:BB:CC&stream=cached-1&play_token=stale-token"
                            }
                        }
                    }
                })));
            }
            if (action == "get_ordered_list")
            {
                OrderedGenres.Add(Parameter(request.RequestUri, "genre") ?? string.Empty);
                return Task.FromResult(Json("{\"js\":{\"data\":[{\"id\":\"cached-1\",\"name\":\"Targeted\",\"cmd\":\"ffmpeg https://stream.example.invalid/play/live.php?mac=00:1A:79:AA:BB:CC&stream=cached-1&play_token=stale-token\"}],\"total_items\":1,\"max_page_items\":1}}"));
            }
            if (action == "create_link")
                return Task.FromResult(Json("{\"js\":{\"cmd\":\"ffmpeg https://stream.example.invalid/play/live.php?stream=&play_token=fresh-token\"}}"));
            return Task.FromResult(Json("{\"js\":{}}"));
        }

        private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
    }

    private static string? Parameter(Uri? uri, string name)
    {
        if (uri is null) return null;
        foreach (var pair in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var values = pair.Split('=', 2);
            if (Uri.UnescapeDataString(values[0]) == name) return values.Length == 2 ? Uri.UnescapeDataString(values[1]) : string.Empty;
        }
        return null;
    }
}
