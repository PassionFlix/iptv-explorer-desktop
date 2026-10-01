using System.Diagnostics;
using System.Net;
using System.Text;
using IPTVExplorer.Core;
using IPTVExplorer.Infrastructure;
using IPTVExplorer.Providers;

namespace IPTVExplorer.Tests;

public sealed class ProviderFixtureTests
{
    private static readonly ProviderRecord XtreamProvider = new("fixture-provider", ProviderType.Xtream, "Fixture", new Uri("https://example.invalid"), "fixture-reference", Enabled: true);
    private static readonly ProviderRecord StalkerProvider = new("fixture-provider", ProviderType.Stalker, "Fixture", new Uri("https://example.invalid"), "fixture-reference", Enabled: true);
    private static readonly ProviderSecret XtreamSecret = new("user-demo", "password-demo");
    private static readonly ProviderSecret StalkerSecret = new(MacAddress: "00:00:00:00:00:00");

    [Fact]
    public async Task XtreamFixtureCoversAuthenticationCatalogsAndDetails()
    {
        using var http = new HttpClient(new XtreamFixtureHandler());
        var client = new XtreamProviderClient(XtreamProvider, XtreamSecret, http);
        Assert.True((await client.TestConnectionAsync()).Success);
        Assert.Single(await client.GetLiveCategoriesAsync());
        Assert.Single(await client.GetVodCategoriesAsync());
        Assert.Single(await client.GetSeriesCategoriesAsync());
        var live = await client.GetAllLiveAsync();
        Assert.Equal("101", Assert.Single(live).Id);
        var vod = await client.GetAllVodAsync();
        Assert.Equal(2, vod.Count);
        Assert.Equal(8.4, vod[0].Rating);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1_700_000_000), vod[0].AddedAt);
        var detail = await client.GetVodDetailsAsync("201");
        Assert.Equal("Fixture Film", detail.Title);
        Assert.Equal("Fixture Director", detail.Director);
        var series = await client.GetSeriesDetailsAsync("301");
        var episode = Assert.Single(Assert.Single(series.Seasons).Episodes);
        Assert.Equal("episode-501", episode.Id);
        var resolved = await client.ResolveMediaAsync(new MediaRequest(CatalogType.Series, episode.Id, series.Id, "mkv"));
        Assert.Contains("/series/user-demo/password-demo/episode-501.mkv", resolved.Uri.AbsoluteUri, StringComparison.Ordinal);
        Assert.Equal(ProviderHttpRegistration.MediaUserAgent, resolved.Headers?["User-Agent"]);
        Assert.Equal("*/*", resolved.Headers?["Accept"]);
        Assert.DoesNotContain(resolved.Headers!, pair => pair.Value.Contains(XtreamSecret.Password!, StringComparison.Ordinal));
        Assert.DoesNotContain(resolved.Headers!, pair => pair.Key.Contains("token", StringComparison.OrdinalIgnoreCase));
        var account = await client.GetAccountInfoAsync();
        Assert.Equal(2, account.ActiveConnections);
        Assert.Equal(4, account.MaxConnections);
        Assert.Equal(["ts", "m3u8"], account.AllowedOutputFormats);
        Assert.NotNull(account.ExpiresAt);
    }

    [Fact]
    public async Task XtreamDiagnosticIncludesAccountDetailsButOnlyMaskedCredentials()
    {
        await using var database = await TestDatabase.CreateAsync();
        var secrets = new InMemorySecretStore();
        var secretReference = await secrets.PutAsync(new ProviderSecret("nicolas", "fixture-password"));
        var provider = await database.AddProviderAsync(ProviderType.Xtream, secretReference);
        using var handler = new StaticJsonHandler("{\"user_info\":{\"auth\":1,\"status\":\"Active\",\"exp_date\":1800000000,\"active_cons\":2,\"max_connections\":4,\"allowed_output_formats\":[\"ts\",\"m3u8\"]}}");
        var factory = new ProviderClientFactory(new StubHttpClientFactory(handler), secrets);
        var service = new ProviderManagementService(database.Repository, secrets, factory, new ProviderLocalDataStore(database.Paths));

        var diagnostic = await service.DiagnoseAsync(provider.Key);
        var serialized = System.Text.Json.JsonSerializer.Serialize(diagnostic);

        Assert.True(diagnostic.AccountOk);
        Assert.Equal("Compte valide", diagnostic.Message);
        Assert.Equal("Active", diagnostic.AccountStatus);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1_800_000_000), diagnostic.ExpiresAt);
        Assert.Equal("Identifiant", diagnostic.IdentityLabel);
        Assert.Equal("ni••••as", diagnostic.MaskedIdentity);
        Assert.Equal("Configuré", diagnostic.CredentialState);
        Assert.Equal(2, diagnostic.ActiveConnections);
        Assert.Equal(4, diagnostic.MaxConnections);
        Assert.Equal(["ts", "m3u8"], diagnostic.AllowedOutputFormats);
        Assert.DoesNotContain("nicolas", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("fixture-password", serialized, StringComparison.Ordinal);
    }

    [Fact]
    public async Task XtreamAuthenticationFailureIsReported()
    {
        using var http = new HttpClient(new StaticJsonHandler("{\"user_info\":{\"auth\":0,\"status\":\"Disabled\"}}"));
        var client = new XtreamProviderClient(XtreamProvider, XtreamSecret, http);
        Assert.False((await client.TestConnectionAsync()).Success);
    }

    [Fact]
    public async Task XtreamMalformedJsonIsRejected()
    {
        using var http = new HttpClient(new StaticJsonHandler("{broken"));
        var client = new XtreamProviderClient(XtreamProvider, XtreamSecret, http);
        Assert.False((await client.TestConnectionAsync()).Success);
    }

    [Fact]
    public async Task StalkerPaginationFetchesOnlyRequestedPage()
    {
        var handler = new StalkerFixtureHandlerV2(); using var http = new HttpClient(handler);
        var client = new StalkerProviderClient(StalkerProvider, StalkerSecret, http);
        var page = await client.GetSeriesPageAsync("30", 7);
        Assert.Equal(2684, page.Total);
        Assert.Equal(7, page.Page);
        Assert.Single(page.Items);
        Assert.Equal(["7"], handler.OrderedPages);
    }

    [Fact]
    public async Task StalkerSeriesDetailAndCreateLinkStayInsideClient()
    {
        var handler = new StalkerFixtureHandlerV2(); using var http = new HttpClient(handler);
        var client = new StalkerProviderClient(StalkerProvider, StalkerSecret, http);
        var detail = await client.GetSeriesDetailsAsync("series-1");
        var episode = Assert.Single(Assert.Single(detail.Seasons).Episodes);
        Assert.Equal("episode-7", episode.Id);
        var resolved = await client.ResolveMediaAsync(new MediaRequest(CatalogType.Series, episode.Id, detail.Id));
        Assert.Equal("https://media.example.invalid/stream/episode-7", resolved.Uri.AbsoluteUri);
        Assert.True(handler.CreateLinkUsedSeriesFlag);
    }

    [Fact]
    public async Task StalkerLiveCategoryFiltering()
    {
        var handler = new StalkerRegressionHandler();
        using var http = new HttpClient(handler);
        var client = new StalkerProviderClient(StalkerProvider, StalkerSecret, http);

        var category1 = await client.GetLiveAsync("1");
        var category2 = await client.GetLiveAsync("2");

        Assert.Equal(["channel-a", "channel-b"], category1.Select(item => item.Id));
        Assert.Equal(["channel-c", "channel-d"], category2.Select(item => item.Id));
        Assert.Empty(category1.Select(item => item.Id).Intersect(category2.Select(item => item.Id), StringComparer.Ordinal));
        Assert.Equal(1, handler.AllChannelsRequests);
        Assert.False(handler.GenreParameterWasSent);
    }

    [Fact]
    public async Task StalkerVodLoadedPageReturnsDifferentDetailsWhenMovieIdIsIgnored()
    {
        var handler = new StalkerRegressionHandler();
        using var http = new HttpClient(handler);
        var client = new StalkerProviderClient(StalkerProvider, StalkerSecret, http);
        var page = await client.GetVodPageAsync("20", 1);

        var filmA = await client.GetVodDetailsAsync("101");
        var filmB = await client.GetVodDetailsAsync("202");

        Assert.Equal(["101", "202"], page.Items.Select(item => item.Id));
        Assert.Equal(("Film A", "poster-a", "Plot A"), (filmA.Title, filmA.Poster, filmA.Plot));
        Assert.Equal(("Film B", "poster-b", "Plot B"), (filmB.Title, filmB.Poster, filmB.Plot));
        Assert.NotEqual(filmA.Id, filmB.Id);
        Assert.Equal(0, handler.VodDetailRequests);
    }

    [Fact]
    public async Task StalkerVodDetailCacheIsMediaSpecific()
    {
        using var http = new HttpClient(new StalkerRegressionHandler());
        var client = new StalkerProviderClient(StalkerProvider, StalkerSecret, http);
        _ = await client.GetVodPageAsync("20", 1);

        var filmA = await client.ResolveMediaAsync(new MediaRequest(CatalogType.Vod, "101"));
        var filmB = await client.ResolveMediaAsync(new MediaRequest(CatalogType.Vod, "202"));

        Assert.EndsWith("/film-a", filmA.Uri.AbsoluteUri, StringComparison.Ordinal);
        Assert.EndsWith("/film-b", filmB.Uri.AbsoluteUri, StringComparison.Ordinal);
        Assert.NotEqual(filmA.Uri, filmB.Uri);
    }

    [Fact]
    public async Task StalkerVodUncachedIdRejectsIgnoredMovieIdResponse()
    {
        var handler = new StalkerRegressionHandler();
        using var http = new HttpClient(handler);
        var client = new StalkerProviderClient(StalkerProvider, StalkerSecret, http);

        var exception = await Assert.ThrowsAsync<KeyNotFoundException>(() => client.GetVodDetailsAsync("missing"));

        Assert.Equal("Contenu introuvable.", exception.Message);
        Assert.Equal(1, handler.VodDetailRequests);
    }

    [Fact]
    public async Task StalkerSeriesMapsDistinctSeasons()
    {
        using var http = new HttpClient(new StalkerRegressionHandler());
        var client = new StalkerProviderClient(StalkerProvider, StalkerSecret, http);

        var detail = await client.GetSeriesDetailsAsync("series-1");

        Assert.Equal([1, 2], detail.Seasons.Select(season => season.Number));
        Assert.Equal(["Saison 1", "Saison 2"], detail.Seasons.Select(season => season.Title));
    }

    [Fact]
    public async Task StalkerSeriesMapsEpisodes()
    {
        using var http = new HttpClient(new StalkerRegressionHandler());
        var client = new StalkerProviderClient(StalkerProvider, StalkerSecret, http);

        var detail = await client.GetSeriesDetailsAsync("series-1");

        Assert.Equal(["s1e1", "s1e2"], detail.Seasons[0].Episodes.Select(episode => episode.Id));
        Assert.Equal("s2e1", Assert.Single(detail.Seasons[1].Episodes).Id);
    }

    [Fact]
    public async Task StalkerEpisodeMediaReferenceUsesEpisodeIdentity()
    {
        using var http = new HttpClient(new StalkerRegressionHandler());
        var client = new StalkerProviderClient(StalkerProvider, StalkerSecret, http);
        var detail = await client.GetSeriesDetailsAsync("series-1");
        var episodes = detail.Seasons.SelectMany(season => season.Episodes).ToArray();

        var first = new MediaReference(StalkerProvider.Key, CatalogType.Series, detail.Id, episodes[0].Id, episodes[0].Extension);
        var second = new MediaReference(StalkerProvider.Key, CatalogType.Series, detail.Id, episodes[1].Id, episodes[1].Extension);

        Assert.Equal("series-1", first.MediaId);
        Assert.Equal("s1e1", first.EpisodeId);
        Assert.Equal("s1e2", second.EpisodeId);
        Assert.NotEqual(first, second);
        Assert.DoesNotContain("http", first.EpisodeId!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task StalkerHtmlEntitiesAreDisplayDecoded()
    {
        using var http = new HttpClient(new StalkerRegressionHandler());
        var client = new StalkerProviderClient(StalkerProvider, StalkerSecret, http);

        var category = Assert.Single(await client.GetLiveCategoriesAsync());

        Assert.Equal("live-html", category.RemoteId);
        Assert.Equal("CHILE & BOLIVIA", category.Name);
    }

    [Fact]
    public async Task SharedChangesDoNotRegressXtreamFixtures()
    {
        using var http = new HttpClient(new XtreamFixtureHandler());
        var client = new XtreamProviderClient(XtreamProvider, XtreamSecret, http);

        Assert.Equal("101", Assert.Single(await client.GetAllLiveAsync()).Id);
        Assert.Equal(["201", "202"], (await client.GetAllVodAsync()).Select(item => item.Id));
        Assert.Equal("Fixture Film", (await client.GetVodDetailsAsync("201")).Title);
        Assert.Equal("episode-501", Assert.Single(Assert.Single((await client.GetSeriesDetailsAsync("301")).Seasons).Episodes).Id);
    }

    [Fact]
    public async Task StalkerExpiredTokenTriggersSingleReauthentication()
    {
        var handler = new ExpiringTokenHandler(); using var http = new HttpClient(handler);
        var client = new StalkerProviderClient(StalkerProvider, StalkerSecret, http);
        var account = await client.GetAccountInfoAsync();
        Assert.True(account.Authenticated);
        Assert.Equal(2, handler.Handshakes);
    }

    [Theory]
    [InlineData("application/json")]
    [InlineData("text/javascript")]
    [InlineData("text/plain")]
    [InlineData("application/javascript")]
    public async Task StalkerCategoriesAcceptValidJsonRegardlessOfContentType(string mediaType)
    {
        using var http = new HttpClient(new StalkerCategoriesHandler("{\"js\":[{\"id\":\"10\",\"title\":\"Fixture category\"}]}", mediaType));
        var client = new StalkerProviderClient(StalkerProvider, StalkerSecret, http);

        var category = Assert.Single(await client.GetLiveCategoriesAsync());

        Assert.Equal("10", category.RemoteId);
    }

    [Theory]
    [InlineData("{\"js\":[{\"id\":\"10\",\"title\":\"Wrapped\"}]}")]
    [InlineData("[{\"id\":\"10\",\"title\":\"Direct\"}]")]
    [InlineData("{\"js\":{\"data\":[{\"id\":\"10\",\"title\":\"Nested data\"}]}}")]
    [InlineData("{\"js\":{\"10\":{\"id\":\"10\",\"title\":\"Indexed object\"}}}")]
    public async Task StalkerCategoriesAcceptWebCompatiblePayloadShapes(string payload)
    {
        using var http = new HttpClient(new StalkerCategoriesHandler(payload, "text/javascript"));
        var client = new StalkerProviderClient(StalkerProvider, StalkerSecret, http);

        Assert.Single(await client.GetLiveCategoriesAsync());
    }

    [Fact]
    public async Task StalkerJsonDepthMatchesWebDecoderLimit()
    {
        var nestedMetadata = Enumerable.Range(1, 80).Aggregate("\"leaf\"", (value, _) => $"{{\"nested\":{value}}}");
        var payload = $"{{\"js\":[{{\"id\":\"10\",\"title\":\"Deep fixture\",\"metadata\":{nestedMetadata}}}]}}";
        using var http = new HttpClient(new StalkerCategoriesHandler(payload, "text/javascript"));
        var client = new StalkerProviderClient(StalkerProvider, StalkerSecret, http);

        Assert.Single(await client.GetLiveCategoriesAsync());
    }

    [Theory]
    [InlineData("{\"unexpected\":\"value\"}")]
    [InlineData("{\"js\":\"wrong type\"}")]
    [InlineData("{\"js\":[1,true,null]}")]
    public async Task StalkerCategoriesRejectUnsupportedPayloadStructures(string payload)
    {
        using var http = new HttpClient(new StalkerCategoriesHandler(payload, "text/javascript"));
        var client = new StalkerProviderClient(StalkerProvider, StalkerSecret, http);

        var exception = await Assert.ThrowsAnyAsync<Exception>(() => client.GetLiveCategoriesAsync());

        Assert.StartsWith("get_genres returned an unsupported Stalker payload structure.", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(payload, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StalkerCategoriesRejectMalformedJavascriptBodyAsJsonIncompatible()
    {
        using var http = new HttpClient(new StalkerCategoriesHandler("not-json", "text/javascript"));
        var client = new StalkerProviderClient(StalkerProvider, StalkerSecret, http);

        var exception = await Assert.ThrowsAnyAsync<Exception>(() => client.GetLiveCategoriesAsync());

        Assert.Contains("get_genres HTTP 200 (text/javascript), JSON incompatible", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("not-json", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StalkerUsesWebActionsForAllCategoryFamilies()
    {
        var handler = new StalkerCategoriesHandler("{\"js\":[{\"id\":\"10\",\"title\":\"Fixture category\"}]}", "application/json");
        using var http = new HttpClient(handler);
        var client = new StalkerProviderClient(StalkerProvider, StalkerSecret, http);

        Assert.Single(await client.GetLiveCategoriesAsync());
        Assert.Single(await client.GetVodCategoriesAsync());
        Assert.Single(await client.GetSeriesCategoriesAsync());

        Assert.Contains(("itv", "get_genres"), handler.CategoryRequests);
        Assert.Contains(("vod", "get_categories"), handler.CategoryRequests);
        Assert.Contains(("series", "get_categories"), handler.CategoryRequests);
    }

    [Fact]
    public async Task StalkerHandshakeFollowsSameServerRedirectAndKeepsRequiredHeaders()
    {
        var handler = new StrictStalkerOnboardingHandler("/portal.php", redirectHandshake: true);
        using var http = new HttpClient(handler);
        var client = new StalkerProviderClient(StalkerProvider, StalkerSecret, http);

        var result = await client.TestConnectionAsync();

        Assert.True(result.Success);
        Assert.Equal(2, handler.HandshakePaths.Count);
        Assert.True(handler.SawCompatibleHandshake);
        Assert.True(handler.SawCookieAndBearerAfterHandshake);
    }

    [Fact]
    public async Task StalkerMacIsTrimmedUppercasedAndCookieEncoded()
    {
        const string normalizedMac = "AA:BB:CC:DD:EE:FF";
        var handler = new StrictStalkerOnboardingHandler("/portal.php", expectedMac: normalizedMac);
        using var http = new HttpClient(handler);
        var client = new StalkerProviderClient(StalkerProvider, new ProviderSecret(MacAddress: "  aa:bb:cc:dd:ee:ff  "), http);

        var result = await client.TestConnectionAsync();

        Assert.True(result.Success);
        Assert.True(handler.SawCompatibleHandshake);
        Assert.True(handler.SawCookieAndBearerAfterHandshake);
    }

    [Theory]
    [InlineData("")]
    [InlineData("/c")]
    [InlineData("/c/")]
    public async Task StalkerOnboardingNormalizesPortalEntryUrls(string suffix)
    {
        await using var database = await TestDatabase.CreateAsync();
        var handler = new StrictStalkerOnboardingHandler("/portal.php");
        var secretStore = new InMemorySecretStore();
        var factory = new ProviderClientFactory(new StubHttpClientFactory(handler), secretStore);
        var service = new ProviderOnboardingService(secretStore, database.Repository, factory);
        var draft = service.AddDraft(new ProviderDraftInput("Stalker Fixture", "stalker", $"https://example.invalid{suffix}", null, null, StalkerSecret.MacAddress));

        var tested = await service.TestAsync(draft.Id);

        Assert.Equal(ProviderType.Stalker, tested.DetectedType);
        Assert.Equal("https://example.invalid", tested.ServerUrl);
        Assert.True(handler.SawCompatibleHandshake);
        Assert.True(handler.SawCookieAndBearerAfterHandshake);
    }

    [Theory]
    [InlineData("/portal.php", 1)]
    [InlineData("/server/load.php", 3)]
    [InlineData("/stalker_portal/server/load.php", 5)]
    public async Task AutomaticOnboardingFallsBackAcrossStandardStalkerEndpoints(string acceptedPath, int expectedHandshakeAttempts)
    {
        await using var database = await TestDatabase.CreateAsync();
        var handler = new StrictStalkerOnboardingHandler(acceptedPath);
        var secretStore = new InMemorySecretStore();
        var factory = new ProviderClientFactory(new StubHttpClientFactory(handler), secretStore);
        var service = new ProviderOnboardingService(secretStore, database.Repository, factory);
        var draft = service.AddDraft(new ProviderDraftInput("Automatic Fixture", "auto", "https://example.invalid/c/", null, null, StalkerSecret.MacAddress));

        var tested = await service.TestAsync(draft.Id);

        Assert.Equal(ProviderType.Stalker, tested.DetectedType);
        Assert.Equal(acceptedPath, handler.SuccessfulPath);
        Assert.Equal(expectedHandshakeAttempts, handler.HandshakePaths.Count);
        Assert.True(handler.SawCompatibleHandshake);
        Assert.True(handler.SawCookieAndBearerAfterHandshake);
        Assert.Contains("itv/get_genres: root=object", tested.Diagnostic?.ProtocolDetails, StringComparison.Ordinal);
        Assert.Contains("vod/get_categories: root=object", tested.Diagnostic?.ProtocolDetails, StringComparison.Ordinal);
        Assert.Contains("series/get_categories: root=object", tested.Diagnostic?.ProtocolDetails, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AutomaticOnboardingSkipsHttp200WithIncompatibleProfile()
    {
        await using var database = await TestDatabase.CreateAsync();
        var handler = new StrictStalkerOnboardingHandler("/server/load.php", incompatibleProfilePath: "/portal.php");
        var secretStore = new InMemorySecretStore();
        var factory = new ProviderClientFactory(new StubHttpClientFactory(handler), secretStore);
        var service = new ProviderOnboardingService(secretStore, database.Repository, factory);
        var draft = service.AddDraft(new ProviderDraftInput("Profile Fallback Fixture", "auto", "https://example.invalid", null, null, StalkerSecret.MacAddress));

        var tested = await service.TestAsync(draft.Id);

        Assert.Equal(ProviderType.Stalker, tested.DetectedType);
        Assert.Equal("/server/load.php", handler.SuccessfulPath);
        Assert.Equal(["/portal.php", "/server/load.php"], handler.HandshakePaths);
    }

    [Fact]
    public async Task FailedStalkerOnboardingReturnsOnlySafeFrenchDiagnostics()
    {
        await using var database = await TestDatabase.CreateAsync();
        var handler = new StrictStalkerOnboardingHandler("/unsupported.php");
        var secretStore = new InMemorySecretStore();
        var factory = new ProviderClientFactory(new StubHttpClientFactory(handler), secretStore);
        var service = new ProviderOnboardingService(secretStore, database.Repository, factory);
        var draft = service.AddDraft(new ProviderDraftInput("Rejected Fixture", "stalker", "https://example.invalid/c", null, null, StalkerSecret.MacAddress));

        var tested = await service.TestAsync(draft.Id);

        Assert.Null(tested.DetectedType);
        Assert.StartsWith("Impossible d’établir une session Stalker/MAG", tested.Message, StringComparison.Ordinal);
        Assert.Contains("/portal.php → handshake rejected both safe request profiles", tested.Message, StringComparison.Ordinal);
        Assert.Contains("/server/load.php → handshake rejected both safe request profiles", tested.Message, StringComparison.Ordinal);
        Assert.Contains("/stalker_portal/server/load.php → handshake rejected both safe request profiles", tested.Message, StringComparison.Ordinal);
        Assert.Contains("Legacy: handshake HTTP 404", tested.Message, StringComparison.Ordinal);
        Assert.Contains("MAG254-compatible: handshake HTTP 404", tested.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(StalkerSecret.MacAddress!, tested.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("fixture-token", tested.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExplicitCategorySyncAfterOnboardingHandlesLargeFixtureWithoutPersistingSecrets()
    {
        await using var database = await TestDatabase.CreateAsync();
        var secretStore = new InMemorySecretStore();
        var factory = new ProviderClientFactory(new StubHttpClientFactory(new LargeXtreamFixtureHandler()), secretStore);
        var service = new ProviderOnboardingService(secretStore, database.Repository, factory);
        var draft = service.AddDraft(new ProviderDraftInput("Large Fixture", "xtream", "https://example.invalid", "user-demo", "password-demo", null));
        var watch = Stopwatch.StartNew();
        var tested = await service.TestAsync(draft.Id);
        watch.Stop();
        Assert.Equal(0, tested.Diagnostic?.Live);
        Assert.Equal(0, tested.Diagnostic?.Vod);
        Assert.Equal(0, tested.Diagnostic?.Series);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5));
        var provider = await service.SaveAsync(draft.Id, new ProviderSaveOptions(false));
        await new ProviderManagementService(database.Repository, secretStore, factory, new ProviderLocalDataStore(database.Paths)).SyncCategoriesAsync(provider.Key);
        await database.Repository.SaveCategoryPolicyAsync(provider.Key, CatalogType.Vod, new(CategoryPolicyMode.Custom, new HashSet<string> { "vod-2" }));
        await database.Repository.SaveCategoryPolicyAsync(provider.Key, CatalogType.Series, new(CategoryPolicyMode.None, new HashSet<string>()));
        Assert.False(provider.Enabled);
        Assert.Equal(500, (await database.Repository.ListCategoriesAsync(provider.Key, CatalogType.Live)).Count);
        Assert.Equal(200, (await database.Repository.ListCategoriesAsync(provider.Key, CatalogType.Vod)).Count);
        Assert.Equal(100, (await database.Repository.ListCategoriesAsync(provider.Key, CatalogType.Series)).Count);
        Assert.Single(await database.Repository.ListCategoriesAsync(provider.Key, CatalogType.Vod), category => category.Selected);
        var storage = await database.ReadRawSqliteStorageAsync();
        Assert.NotEmpty(storage);
        foreach (var printable in storage) Assert.DoesNotContain("password-demo", printable, StringComparison.Ordinal);
    }

    private sealed class StaticJsonHandler(string json) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(Json(json));
    }

    private sealed class XtreamFixtureHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var action = Parameter(request.RequestUri, "action");
            var json = action switch
            {
                null => "{\"user_info\":{\"auth\":1,\"status\":\"Active\",\"exp_date\":1999999999,\"active_cons\":2,\"max_connections\":4,\"allowed_output_formats\":[\"ts\",\"m3u8\"]},\"server_info\":{\"url\":\"example.invalid\"}}",
                "get_live_categories" => "[{\"category_id\":\"10\",\"category_name\":\"Live Fixture\"}]",
                "get_vod_categories" => "[{\"category_id\":\"20\",\"category_name\":\"Films Fixture\"}]",
                "get_series_categories" => "[{\"category_id\":\"30\",\"category_name\":\"Series Fixture\"}]",
                "get_live_streams" => "[{\"stream_id\":101,\"name\":\"Fixture Channel\",\"stream_icon\":\"https://images.example.invalid/live.png\"}]",
                "get_vod_streams" => "[{\"stream_id\":201,\"name\":\"Fixture Film\",\"year\":\"2026\",\"rating\":\"8.4\",\"added\":\"1700000000\",\"container_extension\":\"mkv\",\"audio_tracks\":[\"fra\",\"eng\"]},{\"stream_id\":202,\"name\":\"Second Fixture\"}]",
                "get_series" => "[{\"series_id\":301,\"name\":\"Fixture Series\",\"rating\":\"7.9\"}]",
                "get_vod_info" => "{\"movie_data\":{\"stream_id\":201,\"name\":\"Fixture Film\",\"container_extension\":\"mkv\"},\"info\":{\"plot\":\"Fixture plot\",\"director\":\"Fixture Director\",\"cast\":\"Actor One\",\"genre\":\"Drama\",\"year\":\"2026\",\"rating\":\"8.4\"}}",
                "get_series_info" => "{\"info\":{\"name\":\"Fixture Series\",\"plot\":\"Series plot\"},\"episodes\":{\"1\":[{\"id\":\"episode-501\",\"episode_num\":1,\"title\":\"Pilot\",\"container_extension\":\"mkv\"}]}}",
                _ => "[]"
            };
            return Task.FromResult(Json(json));
        }
    }

    private sealed class LargeXtreamFixtureHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var action = Parameter(request.RequestUri, "action");
            if (action is null) return Task.FromResult(Json("{\"user_info\":{\"auth\":1,\"status\":\"Active\"},\"server_info\":{}}"));
            var (prefix, count) = action switch { "get_live_categories" => ("live", 500), "get_vod_categories" => ("vod", 200), "get_series_categories" => ("series", 100), _ => ("none", 0) };
            var values = Enumerable.Range(1, count).Select(index => $"{{\"category_id\":\"{prefix}-{index}\",\"category_name\":\"{prefix} category {index}\"}}");
            return Task.FromResult(Json("[" + string.Join(',', values) + "]"));
        }
    }

    private sealed class StalkerFixtureHandlerV2 : HttpMessageHandler
    {
        public List<string> OrderedPages { get; } = [];
        public bool CreateLinkUsedSeriesFlag { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var action = Parameter(request.RequestUri, "action");
            var type = Parameter(request.RequestUri, "type");
            if (action == "handshake") return Task.FromResult(Json("{\"js\":{\"token\":\"fixture-token\"}}"));
            if (action == "get_profile") return Task.FromResult(Json("{\"js\":{\"id\":\"profile-1\",\"auth\":\"1\",\"status\":\"Active\"}}"));
            if (action == "get_genres") return Task.FromResult(Json("{\"js\":[{\"id\":\"30\",\"title\":\"Fixture Genre\"}]}"));
            if (action == "get_all_channels") return Task.FromResult(Json("{\"js\":{\"data\":[{\"id\":\"channel-1\",\"name\":\"Fixture Channel\",\"cmd\":\"ffmpeg http://internal.invalid/live\"}]}}"));
            if (action == "create_link") { CreateLinkUsedSeriesFlag = Parameter(request.RequestUri, "series") == "1"; return Task.FromResult(Json("{\"js\":{\"cmd\":\"ffmpeg https://media.example.invalid/stream/episode-7\"}}")); }
            if (action == "get_ordered_list" && Parameter(request.RequestUri, "movie_id") is not null) return Task.FromResult(Json("{\"js\":{\"data\":[{\"id\":\"episode-7\",\"name\":\"Episode Seven\",\"season\":1,\"episode\":7,\"cmd\":\"ffmpeg http://internal.invalid/episode-7\"}],\"total_items\":1,\"max_page_items\":14,\"cur_page\":1}}"));
            if (action == "get_ordered_list") { var page = Parameter(request.RequestUri, "p") ?? "1"; OrderedPages.Add(page); var idField = type == "series" ? "series_id" : "id"; return Task.FromResult(Json($"{{\"js\":{{\"data\":[{{\"{idField}\":\"item-{page}\",\"name\":\"Fixture {page}\",\"cmd\":\"ffmpeg http://internal.invalid/{page}\"}}],\"total_items\":2684,\"max_page_items\":14,\"cur_page\":{page}}}}}")); }
            return Task.FromResult(Json("{\"js\":{}}"));
        }
    }

    private sealed class StalkerRegressionHandler : HttpMessageHandler
    {
        public int AllChannelsRequests { get; private set; }
        public bool GenreParameterWasSent { get; private set; }
        public int VodDetailRequests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var action = Parameter(request.RequestUri, "action");
            var type = Parameter(request.RequestUri, "type");
            if (action == "handshake") return Task.FromResult(Json("{\"js\":{\"token\":\"fixture-token\"}}"));
            if (action == "get_genres") return Task.FromResult(Json("{\"js\":[{\"id\":\"live-html\",\"title\":\"CHILE &amp; BOLIVIA\"}]}"));
            if (action == "get_all_channels")
            {
                AllChannelsRequests++;
                GenreParameterWasSent |= Parameter(request.RequestUri, "genre") is not null;
                return Task.FromResult(Json("{\"js\":{\"data\":[" +
                    "{\"id\":\"channel-a\",\"name\":\"Channel A\",\"tv_genre_id\":\"1\"}," +
                    "{\"id\":\"channel-b\",\"name\":\"Channel B\",\"tv_genre_id\":1}," +
                    "{\"id\":\"channel-c\",\"name\":\"Channel C\",\"tv_genre_id\":\"2\"}," +
                    "{\"id\":\"channel-d\",\"name\":\"Channel D\",\"tv_genre_id\":2}]}}"));
            }
            if (action == "get_ordered_list" && type == "series")
                return Task.FromResult(Json("{\"js\":{\"data\":[{\"id\":\"series-1\",\"name\":\"Fixture Series\",\"cover\":\"series-poster\",\"cmd\":\"ffmpeg http://internal.invalid/series\",\"series\":{\"1\":[1,2],\"2\":[1]}}],\"total_items\":1,\"max_page_items\":14,\"cur_page\":1}}"));
            if (action == "get_ordered_list" && type == "vod")
            {
                var requested = Parameter(request.RequestUri, "movie_id");
                var filmA = "{\"id\":\"101\",\"name\":\"Film A\",\"screenshot_uri\":\"poster-a\",\"description\":\"Plot A\",\"cmd\":\"ffmpeg http://internal.invalid/film-a\"}";
                var filmB = "{\"movie_id\":\"202\",\"name\":\"Film B\",\"screenshot_uri\":\"poster-b\",\"description\":\"Plot B\",\"cmd\":\"ffmpeg http://internal.invalid/film-b\"}";
                if (requested is null) return Task.FromResult(Json($"{{\"js\":{{\"data\":[{filmA},{filmB}],\"total_items\":2,\"max_page_items\":14,\"cur_page\":1}}}}"));
                VodDetailRequests++;
                return Task.FromResult(Json("{\"js\":{\"data\":[{\"id\":\"999\",\"name\":\"Wrong Film\",\"screenshot_uri\":\"wrong-poster\",\"description\":\"Wrong plot\"}],\"total_items\":1,\"max_page_items\":14,\"cur_page\":1}}"));
            }
            if (action == "create_link")
            {
                var command = Parameter(request.RequestUri, "cmd") ?? string.Empty;
                var suffix = command.Contains("film-a", StringComparison.Ordinal) ? "film-a" : command.Contains("film-b", StringComparison.Ordinal) ? "film-b" : "series";
                return Task.FromResult(Json($"{{\"js\":{{\"cmd\":\"ffmpeg https://media.example.invalid/{suffix}\"}}}}"));
            }
            return Task.FromResult(Json("{\"js\":{}}"));
        }
    }

    private sealed class ExpiringTokenHandler : HttpMessageHandler
    {
        public int Handshakes { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (Parameter(request.RequestUri, "action") == "handshake") { Handshakes++; return Task.FromResult(Json($"{{\"js\":{{\"token\":\"token-{Handshakes}\"}}}}")); }
            if (request.Headers.Authorization?.Parameter == "token-1") return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized));
            return Task.FromResult(Json("{\"js\":{\"id\":\"profile-1\",\"auth\":\"1\"}}"));
        }
    }

    private sealed class StalkerCategoriesHandler(string payload, string mediaType) : HttpMessageHandler
    {
        public List<(string Type, string Action)> CategoryRequests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var action = Parameter(request.RequestUri, "action");
            if (action == "handshake") return Task.FromResult(Json("{\"js\":{\"token\":\"fixture-token\"}}"));
            if (action == "get_profile") return Task.FromResult(Json("{\"js\":{\"id\":\"profile-1\",\"auth\":\"1\"}}"));
            if (action is "get_genres" or "get_categories")
            {
                CategoryRequests.Add((Parameter(request.RequestUri, "type") ?? string.Empty, action));
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(payload, Encoding.UTF8, mediaType) });
            }
            return Task.FromResult(Json("{\"js\":{}}"));
        }
    }

    private sealed class StrictStalkerOnboardingHandler(string acceptedPath, bool redirectHandshake = false, string expectedMac = "00:00:00:00:00:00", string? incompatibleProfilePath = null) : HttpMessageHandler
    {
        public List<string> HandshakePaths { get; } = [];
        public string? SuccessfulPath { get; private set; }
        public bool SawCompatibleHandshake { get; private set; }
        public bool SawCookieAndBearerAfterHandshake { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath ?? string.Empty;
            var action = Parameter(request.RequestUri, "action");
            if (action == "handshake") HandshakePaths.Add(path);
            var isAcceptedPath = string.Equals(path, acceptedPath, StringComparison.Ordinal);
            var hasIncompatibleProfile = string.Equals(path, incompatibleProfilePath, StringComparison.Ordinal);
            if (!isAcceptedPath && !hasIncompatibleProfile) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            if (action == "handshake" && redirectHandshake && Parameter(request.RequestUri, "redirected") != "1")
            {
                var destination = new UriBuilder(request.RequestUri!) { Query = request.RequestUri!.Query.TrimStart('?') + "&redirected=1" };
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Found) { Headers = { Location = destination.Uri } });
            }

            var expectedCookie = $"mac={Uri.EscapeDataString(expectedMac)}; stb_lang=fr; timezone=Europe%2FParis";
            var hasCookie = request.Headers.TryGetValues("Cookie", out var cookies) && string.Join("; ", cookies) == expectedCookie;
            var hasAccept = request.Headers.TryGetValues("Accept", out var accept) && string.Join(", ", accept).Contains("application/json", StringComparison.Ordinal);
            var hasUserAgent = request.Headers.TryGetValues("User-Agent", out var agents) && string.Join(" ", agents) == "Mozilla/5.0 MAG254 stbapp";
            var hasXUserAgent = request.Headers.TryGetValues("X-User-Agent", out var xAgents) && string.Join(" ", xAgents) == "Model: MAG254; Link: Ethernet";
            var commonHeadersAreCompatible = hasCookie && hasAccept && hasUserAgent && hasXUserAgent;

            if (action == "handshake")
            {
                SawCompatibleHandshake = commonHeadersAreCompatible && Parameter(request.RequestUri, "token") == string.Empty && request.Headers.Authorization is null;
                return Task.FromResult(SawCompatibleHandshake ? Json("{\"js\":{\"token\":\"fixture-token\"}}") : new HttpResponseMessage(HttpStatusCode.Forbidden));
            }

            var hasBearer = request.Headers.Authorization?.Scheme == "Bearer" && request.Headers.Authorization.Parameter == "fixture-token";
            if (!commonHeadersAreCompatible || !hasBearer) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden));
            if (hasIncompatibleProfile && action == "get_profile") return Task.FromResult(Json("{\"js\":{\"auth\":\"1\"}}"));
            SawCookieAndBearerAfterHandshake = true;
            SuccessfulPath = path;
            var json = action switch
            {
                "get_profile" => "{\"js\":{\"id\":\"profile-1\",\"auth\":\"1\",\"status\":\"Active\"}}",
                "get_genres" or "get_categories" => "{\"js\":[{\"id\":\"10\",\"title\":\"Fixture category\"}]}",
                _ => "{\"js\":{}}"
            };
            return Task.FromResult(Json(json));
        }
    }

    private static string? Parameter(Uri? uri, string name)
    {
        if (uri is null) return null;
        foreach (var pair in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = pair.Split('=', 2); if (Uri.UnescapeDataString(parts[0]) == name) return parts.Length == 2 ? Uri.UnescapeDataString(parts[1]) : string.Empty;
        }
        return null;
    }
    private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
}
