using System.Net;
using System.Text;
using IPTVExplorer.Core;
using IPTVExplorer.Infrastructure;
using IPTVExplorer.Providers;
using Microsoft.Data.Sqlite;

namespace IPTVExplorer.Tests;

public sealed class HomeMediaTests
{
    private const string Poster = "https://images.example.invalid/poster.jpg";
    private const string Backdrop = "https://images.example.invalid/landscape.jpg";

    [Theory]
    [InlineData(CatalogType.Vod)]
    [InlineData(CatalogType.Series)]
    public async Task RecentListsAreSortedLimitedAndIsolatedByCatalogAndProvider(CatalogType catalog)
    {
        await using var database = await TestDatabase.CreateAsync();
        var index = new AtomicSearchIndex(database.Paths);
        var search = new SearchService(database.Paths);
        var date = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var documents = new[] { CatalogType.Vod, CatalogType.Series }.SelectMany(type => Enumerable.Range(1, 25)
            .Select(i => new SearchHit("fixture-provider", type, i.ToString(), $"Title {i}", Poster, date.AddDays(i), Backdrop)))
            .Append(new("fixture-provider", catalog, "unknown", "No date", Poster));
        await index.ReplaceAsync("fixture-provider", documents);
        await index.ReplaceAsync("other-provider", [new("other-provider", catalog, "other", "Other", Poster, date.AddDays(50))]);

        var recent = await search.RecentlyAddedAsync("fixture-provider", catalog, 1000);

        Assert.Equal(20, recent.Count);
        Assert.Equal(Enumerable.Range(6, 20).Reverse().Select(i => i.ToString()), recent.Select(item => item.RemoteId));
        Assert.All(recent, item => { Assert.Equal(catalog, item.Catalog); Assert.Equal("fixture-provider", item.ProviderKey); Assert.Equal(Backdrop, item.BackdropUrl); });
        Assert.Equal(3, (await search.RecentlyAddedAsync("fixture-provider", catalog, 3)).Count);
        Assert.Empty(await search.RecentlyAddedAsync("missing-provider", catalog, 20));
    }

    [Fact]
    public async Task UndatedCatalogsNeverBecomeRecentAtIndexingTime()
    {
        await using var database = await TestDatabase.CreateAsync();
        await new AtomicSearchIndex(database.Paths).ReplaceAsync("fixture-provider",
            [new("fixture-provider", CatalogType.Vod, "1", "Film", Poster), new("fixture-provider", CatalogType.Series, "2", "Series", Poster)]);
        var search = new SearchService(database.Paths);
        Assert.Empty(await search.RecentlyAddedAsync("fixture-provider", CatalogType.Vod, 20));
        Assert.Empty(await search.RecentlyAddedAsync("fixture-provider", CatalogType.Series, 20));
    }

    [Fact]
    public async Task LegacyIndexStillReadsThenAtomicallyRebuildsWithBackdrop()
    {
        await using var database = await TestDatabase.CreateAsync();
        var path = database.Paths.SearchIndex("fixture-provider");
        await using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString()))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE search_documents(catalog_type TEXT,remote_id TEXT,title TEXT,normalized_title TEXT,image_url TEXT,added_at TEXT);
                INSERT INTO search_documents VALUES('vod','old','Old','OLD',$poster,'2026-01-01T00:00:00.0000000+00:00');
                """;
            command.Parameters.AddWithValue("$poster", Poster);
            await command.ExecuteNonQueryAsync();
        }
        var search = new SearchService(database.Paths);
        var old = Assert.Single(await search.RecentlyAddedAsync("fixture-provider", CatalogType.Vod, 20));
        Assert.Null(old.BackdropUrl);
        Assert.Equal(Poster, HomeArtwork.SelectBackground(old.BackdropUrl, old.ImageUrl));

        await new AtomicSearchIndex(database.Paths).ReplaceAsync("fixture-provider",
            [new("fixture-provider", CatalogType.Series, "new", "New", Poster, DateTimeOffset.UtcNow, Backdrop)]);
        Assert.Empty(await search.RecentlyAddedAsync("fixture-provider", CatalogType.Vod, 20));
        var updated = Assert.Single(await search.RecentlyAddedAsync("fixture-provider", CatalogType.Series, 20));
        Assert.Equal(Backdrop, updated.BackdropUrl);
        Assert.Single((await search.SearchAsync("fixture-provider", CatalogType.Series, "new", 1, 20)).Items);
        Assert.False(File.Exists(path + ".previous"));
    }

    [Fact]
    public async Task ArtworkMigrationInvalidatesExistingIndexesOnlyOnce()
    {
        await using var database = await TestDatabase.CreateAsync();
        await database.AddProviderAsync();
        await using var connection = database.Connections.Create();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM schema_migrations WHERE version=4; UPDATE provider_category_policy SET index_dirty=0";
        await command.ExecuteNonQueryAsync();
        var initializer = new DatabaseInitializer(database.Paths, database.Connections);
        await initializer.InitializeAsync();
        var summaries = await database.Repository.GetCategorySummariesAsync("fixture-provider");
        Assert.All(summaries.Where(item => item.Catalog != CatalogType.Live), item => Assert.True(item.IndexDirty));
        Assert.False(summaries.Single(item => item.Catalog == CatalogType.Live).IndexDirty);
        command.CommandText = "UPDATE provider_category_policy SET index_dirty=0";
        await command.ExecuteNonQueryAsync();
        await initializer.InitializeAsync();
        Assert.All(await database.Repository.GetCategorySummariesAsync("fixture-provider"), item => Assert.False(item.IndexDirty));
    }

    [Theory]
    [InlineData("https://fixture-user:fixture-pass@images.example.invalid/image.jpg")]
    [InlineData("https://images.example.invalid/image.jpg?username=fixture-user")]
    [InlineData("https://images.example.invalid/image.jpg?%74oken=fixture-value")]
    [InlineData("https://images.example.invalid/image.jpg#Authorization=fixture-value")]
    [InlineData("https://images.example.invalid/%2574oken/fixture-value.jpg")]
    [InlineData("https://images.example.invalid/movie/fixture-user/fixture-pass/image.jpg")]
    [InlineData("https://images.example.invalid/00%3A00%3A00%3A00%3A00%3A00/image.jpg")]
    [InlineData("file:///C:/fixture/image.jpg")]
    [InlineData("javascript:alert(1)")]
    [InlineData("https://images.example.invalid/player_api.php")]
    public void SensitiveOrNonStaticArtworkFallsBackToPublicPoster(string unsafeUrl)
    {
        Assert.Null(HomeArtwork.SafeUrl(unsafeUrl));
        Assert.Equal(Poster, HomeArtwork.SelectBackground(unsafeUrl, Poster));
        Assert.Equal([new HomeArtworkCandidate(Poster, HomeArtworkKind.Poster)], HomeArtwork.Candidates(unsafeUrl, Poster));
        Assert.Empty(HomeArtwork.Candidates(unsafeUrl, unsafeUrl));
    }

    [Fact]
    public void ArtworkPrefersBackdropAndRejectsProviderSecretsInUnlabelledPaths()
    {
        var secret = new ProviderSecret("fixture-account", "fixture-pass");
        Assert.Equal(Backdrop, HomeArtwork.SelectBackground(Backdrop, Poster, secret));
        Assert.Equal(
            [new HomeArtworkCandidate(Backdrop, HomeArtworkKind.Backdrop), new HomeArtworkCandidate(Poster, HomeArtworkKind.Poster)],
            HomeArtwork.Candidates(Backdrop, Poster, secret));
        Assert.Null(HomeArtwork.SafeUrl("https://images.example.invalid/fixture-account/image.jpg", secret));
        Assert.Null(HomeArtwork.SafeUrl("https://images.example.invalid/fixture-pass.jpg", secret));
        Assert.Null(HomeArtwork.SafeUrl("https://images.example.invalid/001122334455/image.jpg", new(MacAddress: "00:11:22:33:44:55")));
    }

    [Fact]
    public void IdenticalBackdropAndPosterKeepsTheBackdropTypeOnly()
    {
        Assert.Equal([new HomeArtworkCandidate(Backdrop, HomeArtworkKind.Backdrop)], HomeArtwork.Candidates(Backdrop, Backdrop));
    }

    [Fact]
    public async Task IndexNeverPersistsSensitiveArtwork()
    {
        await using var database = await TestDatabase.CreateAsync();
        await new AtomicSearchIndex(database.Paths).ReplaceAsync("fixture-provider",
            [new("fixture-provider", CatalogType.Vod, "1", "Film", Poster + "?password=fixture-sensitive", DateTimeOffset.UtcNow, Backdrop + "?token=fixture-sensitive")]);
        var item = Assert.Single(await new SearchService(database.Paths).RecentlyAddedAsync("fixture-provider", CatalogType.Vod, 20));
        Assert.Null(item.ImageUrl);
        Assert.Null(item.BackdropUrl);
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = database.Paths.SearchIndex("fixture-provider"), Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        await connection.OpenAsync();
        await using var query = connection.CreateCommand();
        query.CommandText = "SELECT COUNT(*) FROM search_documents WHERE image_url IS NOT NULL OR backdrop_url IS NOT NULL";
        Assert.Equal(0L, await query.ExecuteScalarAsync());
    }

    [Fact]
    public async Task XtreamSeriesUsesRealModifiedDateAndFirstSafeBackdrop()
    {
        using var http = new HttpClient(new JsonHandler("""
            [{"series_id":1,"name":"Fixture series","cover":"https://images.example.invalid/poster.jpg","added":"bad-date","last_modified":"1700000000",
              "backdrop_path":["https://images.example.invalid/private.jpg?token=fixture-sensitive","https://images.example.invalid/landscape.jpg"]},
             {"series_id":2,"name":"Undated series","last_modified":"invalid"},
             {"series_id":3,"name":"Future series","last_modified":"9999999999999"}]
            """));
        var provider = new ProviderRecord("fixture-provider", ProviderType.Xtream, "Fixture", new Uri("https://example.invalid"), "fixture-reference");
        var items = await new XtreamProviderClient(provider, new("fixture-user", "fixture-pass"), http).GetAllSeriesAsync();
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1700000000), items[0].AddedAt);
        Assert.Equal(Poster, items[0].ImageUrl);
        Assert.Equal(Backdrop, items[0].BackdropUrl);
        Assert.Null(items[1].AddedAt);
        Assert.Null(items[2].AddedAt);
    }

    [Fact]
    public async Task XtreamFilmUsesAddedDateButDoesNotSubstituteModifiedDate()
    {
        using var http = new HttpClient(new JsonHandler("""
            [{"stream_id":1,"name":"Film","added":"1700000000000","backdrop":"https://images.example.invalid/landscape.jpg"},
             {"stream_id":2,"name":"Film without added","last_modified":"1700000000"}]
            """));
        var provider = new ProviderRecord("fixture-provider", ProviderType.Xtream, "Fixture", new Uri("https://example.invalid"), "fixture-reference");
        var items = await new XtreamProviderClient(provider, new("fixture-user", "fixture-pass"), http).GetAllVodAsync();
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1700000000), items[0].AddedAt);
        Assert.Equal(Backdrop, items[0].BackdropUrl);
        Assert.Null(items[1].AddedAt);
    }

    [Fact]
    public async Task StalkerDoesNotInferRecentSeriesFromModifiedDateOrListOrder()
    {
        // The same fixture envelope supports the handshake and one bounded ordered-list page.
        using var http = new HttpClient(new JsonHandler("""
            {"js":{"token":"fixture-session","data":[{"id":"1","name":"Fixture series","last_modified":"1700000000"}],"total_items":1,"max_page_items":14}}
            """));
        var provider = new ProviderRecord("fixture-provider", ProviderType.Stalker, "Fixture", new Uri("https://example.invalid"), "fixture-reference");
        var page = await new StalkerProviderClient(provider, new(MacAddress: "00:00:00:00:00:00"), http).GetSeriesPageAsync("1", 1);
        Assert.Null(Assert.Single(page.Items).AddedAt);
    }

    private sealed class JsonHandler(string json) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") });
    }
}
