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
        var live = await client.GetLiveAsync("10");
        Assert.Equal("101", Assert.Single(live).Id);
        var vod = await client.GetVodPageAsync("20", 1);
        Assert.Equal(2, vod.Total);
        Assert.Equal(8.4, vod.Items[0].Rating);
        var detail = await client.GetVodDetailsAsync("201");
        Assert.Equal("Fixture Film", detail.Title);
        Assert.Equal("Fixture Director", detail.Director);
        var series = await client.GetSeriesDetailsAsync("301");
        var episode = Assert.Single(Assert.Single(series.Seasons).Episodes);
        Assert.Equal("episode-501", episode.Id);
        var resolved = await client.ResolveMediaAsync(new MediaRequest(CatalogType.Series, episode.Id, series.Id, "mkv"));
        Assert.Contains("/series/user-demo/password-demo/episode-501.mkv", resolved.Uri.AbsoluteUri, StringComparison.Ordinal);
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
    public async Task StalkerExpiredTokenTriggersSingleReauthentication()
    {
        var handler = new ExpiringTokenHandler(); using var http = new HttpClient(handler);
        var client = new StalkerProviderClient(StalkerProvider, StalkerSecret, http);
        var account = await client.GetAccountInfoAsync();
        Assert.True(account.Authenticated);
        Assert.Equal(2, handler.Handshakes);
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
    [InlineData("/server/load.php", 2)]
    [InlineData("/stalker_portal/server/load.php", 3)]
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
        Assert.Contains("/portal.php → handshake HTTP 404", tested.Message, StringComparison.Ordinal);
        Assert.Contains("/server/load.php → handshake HTTP 404", tested.Message, StringComparison.Ordinal);
        Assert.Contains("/stalker_portal/server/load.php → handshake HTTP 404", tested.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(StalkerSecret.MacAddress!, tested.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("fixture-token", tested.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OnboardingHandlesLargeCategoryFixtureWithoutPersistingSecrets()
    {
        await using var database = await TestDatabase.CreateAsync();
        var secretStore = new InMemorySecretStore();
        var factory = new ProviderClientFactory(new StubHttpClientFactory(new LargeXtreamFixtureHandler()), secretStore);
        var service = new ProviderOnboardingService(secretStore, database.Repository, factory);
        var draft = service.AddDraft(new ProviderDraftInput("Large Fixture", "xtream", "https://example.invalid", "user-demo", "password-demo", null));
        var watch = Stopwatch.StartNew();
        var tested = await service.TestAsync(draft.Id);
        watch.Stop();
        Assert.Equal(500, tested.Diagnostic?.Live);
        Assert.Equal(200, tested.Diagnostic?.Vod);
        Assert.Equal(100, tested.Diagnostic?.Series);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5));
        var policies = new Dictionary<string, CategoryPolicyInput>
        {
            ["live"] = new("all", []),
            ["vod"] = new("custom", ["vod-2"]),
            ["series"] = new("none", [])
        };
        var provider = await service.SaveAsync(draft.Id, new ProviderSaveOptions(false, policies));
        Assert.False(provider.Enabled);
        Assert.Equal(500, (await database.Repository.ListCategoriesAsync(provider.Key, CatalogType.Live)).Count);
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
                null => "{\"user_info\":{\"auth\":1,\"status\":\"Active\",\"exp_date\":1999999999},\"server_info\":{\"url\":\"example.invalid\"}}",
                "get_live_categories" => "[{\"category_id\":\"10\",\"category_name\":\"Live Fixture\"}]",
                "get_vod_categories" => "[{\"category_id\":\"20\",\"category_name\":\"Films Fixture\"}]",
                "get_series_categories" => "[{\"category_id\":\"30\",\"category_name\":\"Series Fixture\"}]",
                "get_live_streams" => "[{\"stream_id\":101,\"name\":\"Fixture Channel\",\"stream_icon\":\"https://images.example.invalid/live.png\"}]",
                "get_vod_streams" => "[{\"stream_id\":201,\"name\":\"Fixture Film\",\"year\":\"2026\",\"rating\":\"8.4\",\"container_extension\":\"mkv\",\"audio_tracks\":[\"fra\",\"eng\"]},{\"stream_id\":202,\"name\":\"Second Fixture\"}]",
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
                "get_genres" => "{\"js\":[{\"id\":\"10\",\"title\":\"Fixture category\"}]}",
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
