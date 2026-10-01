using System.Net;
using System.Text;
using IPTVExplorer.Core;
using IPTVExplorer.Infrastructure;
using IPTVExplorer.Providers;

namespace IPTVExplorer.Tests;

public sealed class StalkerCompatibilityTests
{
    private const string Mac = "00:1A:79:AA:BB:CC";
    private static readonly ProviderRecord Provider = new("stalker-fixture", ProviderType.Stalker, "Stalker fixture", new Uri("https://portal.example.invalid"), "fixture-reference", Enabled: true);
    private static readonly ProviderSecret Secret = new(MacAddress: Mac);

    [Fact]
    public async Task ValidLegacyHandshakeDoesNotSendMagFallback()
    {
        var handler = new CompatibilityHandler();
        using var http = new HttpClient(handler);
        var account = await new StalkerProviderClient(Provider, Secret, http).GetAccountInfoAsync();

        Assert.True(account.Authenticated);
        Assert.Equal(1, handler.LegacyHandshakes);
        Assert.Equal(0, handler.MagHandshakes);
        Assert.All(handler.Requests, request => Assert.Equal("Mozilla/5.0 MAG254 stbapp", request.UserAgent));
    }

    [Theory]
    [InlineData(HandshakeFailure.EmptyBody)]
    [InlineData(HandshakeFailure.MissingToken)]
    [InlineData(HandshakeFailure.InvalidJson)]
    public async Task CompatibleLegacyFailuresFallBackOnceToMag(HandshakeFailure failure)
    {
        var handler = new CompatibilityHandler(failure);
        using var http = new HttpClient(handler);
        var client = new StalkerProviderClient(Provider, Secret, http);

        var account = await client.GetAccountInfoAsync();
        _ = await client.GetLiveCategoriesAsync();

        Assert.True(account.Authenticated);
        Assert.Equal(1, handler.LegacyHandshakes);
        Assert.Equal(1, handler.MagHandshakes);
        var magHandshake = Assert.Single(handler.Requests, request => request.Action == "handshake" && request.IsMag);
        Assert.Contains("QtEmbedded", magHandshake.UserAgent, StringComparison.Ordinal);
        Assert.Equal("application/json, text/javascript, */*; q=0.01", magHandshake.Accept);
        Assert.Equal("Model: MAG254; Link: Ethernet", magHandshake.XUserAgent);
        Assert.Equal("XMLHttpRequest", magHandshake.XRequestedWith);
        Assert.Equal("https://portal.example.invalid/c/", magHandshake.Referer);
        Assert.Equal($"mac={Mac}; stb_lang=en; timezone=Europe/Paris", magHandshake.Cookie);
        Assert.DoesNotContain("%3A", magHandshake.Cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Null(magHandshake.Authorization);
        Assert.All(handler.Requests.Where(request => request.Action is "get_profile" or "get_main_info" or "get_genres"), request =>
        {
            Assert.True(request.IsMag);
            Assert.Equal("Bearer mag-token-1", request.Authorization);
        });
    }

    [Fact]
    public async Task ExpiredMagSessionReauthenticatesOnceWithoutRepeatingDetection()
    {
        var handler = new CompatibilityHandler(HandshakeFailure.EmptyBody) { RejectFirstProfileToken = true };
        using var http = new HttpClient(handler);
        var account = await new StalkerProviderClient(Provider, Secret, http).GetAccountInfoAsync();

        Assert.True(account.Authenticated);
        Assert.Equal(1, handler.LegacyHandshakes);
        Assert.Equal(2, handler.MagHandshakes);
        Assert.Equal(["Bearer mag-token-1", "Bearer mag-token-2"], handler.Requests.Where(request => request.Action == "get_profile").Select(request => request.Authorization));
    }

    [Fact]
    public async Task CrossHostRedirectIsRejectedWithoutSendingFallbackToDestination()
    {
        var handler = new CompatibilityHandler { CrossHostLegacyRedirect = true };
        using var http = new HttpClient(handler);
        var result = await new StalkerProviderClient(Provider, Secret, http).TestConnectionAsync();

        Assert.False(result.Success);
        Assert.Equal(1, handler.LegacyHandshakes);
        Assert.Equal(0, handler.MagHandshakes);
        Assert.DoesNotContain(handler.Requests, request => request.Host == "other.example.invalid");
    }

    [Fact]
    public async Task DualHandshakeFailureProducesSafeDiagnostic()
    {
        var handler = new CompatibilityHandler(HandshakeFailure.EmptyBody) { RejectMagHandshake = true };
        using var http = new HttpClient(handler);
        var result = await new StalkerProviderClient(Provider, Secret, http).TestConnectionAsync();

        Assert.False(result.Success);
        Assert.Contains("Legacy", result.Message, StringComparison.Ordinal);
        Assert.Contains("MAG254-compatible", result.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(Mac, result.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("mag-token", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GenericProfileWithZeroIdIsAuthenticated()
    {
        var handler = new CompatibilityHandler { ProfileJson = "{\"js\":{\"id\":0,\"blocked\":0,\"status\":1}}" };
        using var http = new HttpClient(handler);
        var account = await new StalkerProviderClient(Provider, Secret, http).GetAccountInfoAsync();

        Assert.True(account.Authenticated);
        Assert.Equal("1", account.Status);
    }

    [Fact]
    public async Task MissingMainInfoKeepsProfileUsable()
    {
        var handler = new CompatibilityHandler { MainInfoStatus = HttpStatusCode.NotFound };
        using var http = new HttpClient(handler);
        var account = await new StalkerProviderClient(Provider, Secret, http).GetAccountInfoAsync();

        Assert.True(account.Authenticated);
        Assert.Equal("Enabled", account.Status);
    }

    [Fact]
    public async Task InvalidMainInfoJsonKeepsProfileUsable()
    {
        var handler = new CompatibilityHandler { MainInfoJson = "not-json" };
        using var http = new HttpClient(handler);
        var account = await new StalkerProviderClient(Provider, Secret, http).GetAccountInfoAsync();

        Assert.True(account.Authenticated);
        Assert.Equal("Enabled", account.Status);
    }

    [Fact]
    public async Task StandardMainInfoExpirationIsPreferred()
    {
        var handler = new CompatibilityHandler { MainInfoJson = "{\"js\":{\"status\":\"Paid\",\"expire_billing_date\":\"2027-05-08 18:25:00\"}}" };
        using var http = new HttpClient(handler);
        var account = await new StalkerProviderClient(Provider, Secret, http).GetAccountInfoAsync();

        Assert.Equal("Paid", account.Status);
        Assert.Equal(new DateTimeOffset(2027, 5, 8, 18, 25, 0, TimeSpan.Zero), account.ExpiresAt);
    }

    [Fact]
    public async Task ZeroExpirationIsIgnored()
    {
        var handler = new CompatibilityHandler { MainInfoJson = "{\"js\":{\"expire_billing_date\":\"0000-00-00 00:00:00\"}}" };
        using var http = new HttpClient(handler);
        var account = await new StalkerProviderClient(Provider, Secret, http).GetAccountInfoAsync();

        Assert.Null(account.ExpiresAt);
    }

    [Fact]
    public async Task MatchingMacAllowsClearlyDatedPhoneFallback()
    {
        var handler = new CompatibilityHandler { MainInfoJson = $"{{\"js\":{{\"mac\":\"{Mac}\",\"phone\":\"May 8, 2027, 6:25 pm\"}}}}" };
        using var http = new HttpClient(handler);
        var account = await new StalkerProviderClient(Provider, Secret, http).GetAccountInfoAsync();

        Assert.Equal(new DateTimeOffset(2027, 5, 8, 18, 25, 0, TimeSpan.Zero), account.ExpiresAt);
    }

    [Theory]
    [InlineData("00:1A:79:AA:BB:CC", "+1 514 555 0100")]
    [InlineData("00:1A:79:11:22:33", "May 8, 2027, 6:25 pm")]
    public async Task PhoneFallbackRejectsPhoneNumbersAndMismatchedMacs(string responseMac, string phone)
    {
        var handler = new CompatibilityHandler { MainInfoJson = $"{{\"js\":{{\"mac\":\"{responseMac}\",\"phone\":\"{phone}\"}}}}" };
        using var http = new HttpClient(handler);
        var account = await new StalkerProviderClient(Provider, Secret, http).GetAccountInfoAsync();

        Assert.Null(account.ExpiresAt);
    }

    [Fact]
    public async Task AccountResultIsReusedAcrossConnectionTestAndAccountRead()
    {
        var handler = new CompatibilityHandler();
        using var http = new HttpClient(handler);
        var client = new StalkerProviderClient(Provider, Secret, http);

        Assert.True((await client.TestConnectionAsync()).Success);
        Assert.True((await client.GetAccountInfoAsync()).Authenticated);

        Assert.Equal(1, handler.ProfileRequests);
        Assert.Equal(1, handler.MainInfoRequests);
    }

    [Fact]
    public async Task SyntheticCategoryFixturesKeepAllTechnicalAndRealCategoriesVisible()
    {
        using var http = new HttpClient(new CompatibilityHandler());
        var client = new StalkerProviderClient(Provider, Secret, http);

        var live = await client.GetLiveCategoriesAsync();
        var vod = await client.GetVodCategoriesAsync();
        var series = await client.GetSeriesCategoriesAsync();

        Assert.Collection(live,
            all => Assert.Equal(("*", "All", true), (all.RemoteId, all.Name, all.Technical)),
            category => Assert.Equal(("100", "FR| GENERAL", false), (category.RemoteId, category.Name, category.Technical)));
        Assert.Collection(vod,
            all => Assert.Equal(("*", "All", true), (all.RemoteId, all.Name, all.Technical)),
            category => Assert.Equal(("200", "|FR| FILMS", false), (category.RemoteId, category.Name, category.Technical)));
        Assert.Collection(series,
            all => Assert.Equal(("*", "All", true), (all.RemoteId, all.Name, all.Technical)),
            category => Assert.Equal(("300", "|FR| SERIES", false), (category.RemoteId, category.Name, category.Technical)));
    }

    [Fact]
    public async Task OnboardingReusesAccountResultAndKeepsRealCategoriesVisible()
    {
        await using var database = await TestDatabase.CreateAsync();
        var handler = new CompatibilityHandler();
        var secretStore = new InMemorySecretStore();
        var factory = new ProviderClientFactory(new StubHttpClientFactory(handler), secretStore);
        var service = new ProviderOnboardingService(secretStore, database.Repository, factory);
        var draft = service.AddDraft(new ProviderDraftInput("Stalker fixture", "stalker", "https://portal.example.invalid/c/", null, null, Mac));

        var tested = await service.TestAsync(draft.Id);

        Assert.Equal(ProviderType.Stalker, tested.DetectedType);
        Assert.Equal(1, handler.ProfileRequests);
        Assert.Equal(1, handler.MainInfoRequests);
        Assert.Equal("MAC", tested.Diagnostic?.IdentityLabel);
        Assert.Equal("00:1A:79:••:••:••", tested.Diagnostic?.MaskedIdentity);
        Assert.DoesNotContain(Mac, System.Text.Json.JsonSerializer.Serialize(tested.Diagnostic), StringComparison.Ordinal);
        Assert.Equal(["100"], tested.Categories["live"].Select(category => category.RemoteId));
        Assert.Equal(["200"], tested.Categories["vod"].Select(category => category.RemoteId));
        Assert.Equal(["300"], tested.Categories["series"].Select(category => category.RemoteId));
    }

    public enum HandshakeFailure { None, EmptyBody, MissingToken, InvalidJson }

    private sealed class CompatibilityHandler(HandshakeFailure legacyFailure = HandshakeFailure.None) : HttpMessageHandler
    {
        public int LegacyHandshakes { get; private set; }
        public int MagHandshakes { get; private set; }
        public int ProfileRequests { get; private set; }
        public int MainInfoRequests { get; private set; }
        public bool RejectMagHandshake { get; init; }
        public bool RejectFirstProfileToken { get; init; }
        public bool CrossHostLegacyRedirect { get; init; }
        public string ProfileJson { get; init; } = "{\"js\":{\"id\":\"profile-1\",\"auth\":1,\"blocked\":0,\"status\":\"Enabled\"}}";
        public string MainInfoJson { get; init; } = "{\"js\":{}}";
        public HttpStatusCode MainInfoStatus { get; init; } = HttpStatusCode.OK;
        public List<RequestSnapshot> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var snapshot = RequestSnapshot.From(request);
            Requests.Add(snapshot);
            var action = Parameter(request.RequestUri, "action");
            if (action == "handshake") return Task.FromResult(Handshake(snapshot));
            if (action == "get_profile")
            {
                ProfileRequests++;
                if (RejectFirstProfileToken && snapshot.Authorization == "Bearer mag-token-1") return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized));
                return Task.FromResult(Json(ProfileJson));
            }
            if (action == "get_main_info")
            {
                MainInfoRequests++;
                return Task.FromResult(new HttpResponseMessage(MainInfoStatus) { Content = new StringContent(MainInfoJson, Encoding.UTF8, "application/json") });
            }
            if (action == "get_genres") return Task.FromResult(Json("{\"js\":[{\"id\":\"*\",\"title\":\"All\"},{\"id\":\"100\",\"title\":\"FR| GENERAL\"}]}"));
            if (action == "get_categories" && Parameter(request.RequestUri, "type") == "vod") return Task.FromResult(Json("{\"js\":[{\"id\":\"*\",\"title\":\"All\"},{\"id\":\"200\",\"title\":\"|FR| FILMS\"}]}"));
            if (action == "get_categories" && Parameter(request.RequestUri, "type") == "series") return Task.FromResult(Json("{\"js\":[{\"id\":\"*\",\"title\":\"All\"},{\"id\":\"300\",\"title\":\"|FR| SERIES\"}]}"));
            return Task.FromResult(Json("{\"js\":{}}"));
        }

        private HttpResponseMessage Handshake(RequestSnapshot request)
        {
            if (request.IsMag)
            {
                MagHandshakes++;
                return RejectMagHandshake ? new HttpResponseMessage(HttpStatusCode.Forbidden) : Json($"{{\"js\":{{\"token\":\"mag-token-{MagHandshakes}\"}}}}");
            }

            LegacyHandshakes++;
            if (CrossHostLegacyRedirect) return new HttpResponseMessage(HttpStatusCode.Found) { Headers = { Location = new Uri("https://other.example.invalid/portal.php") } };
            return legacyFailure switch
            {
                HandshakeFailure.EmptyBody => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(string.Empty) },
                HandshakeFailure.MissingToken => Json("{\"js\":{}}"),
                HandshakeFailure.InvalidJson => Json("not-json"),
                _ => Json("{\"js\":{\"token\":\"legacy-token\"}}")
            };
        }
    }

    private sealed record RequestSnapshot(string? Action, string Host, string UserAgent, string? Accept, string? XUserAgent, string? XRequestedWith, string? Referer, string? Cookie, string? Authorization)
    {
        public bool IsMag => UserAgent.Contains("QtEmbedded", StringComparison.Ordinal);

        public static RequestSnapshot From(HttpRequestMessage request) => new(
            Parameter(request.RequestUri, "action"),
            request.RequestUri?.Host ?? string.Empty,
            Header(request, "User-Agent") ?? string.Empty,
            Header(request, "Accept", ", "),
            Header(request, "X-User-Agent"),
            Header(request, "X-Requested-With"),
            request.Headers.Referrer?.AbsoluteUri,
            Header(request, "Cookie"),
            request.Headers.Authorization is { } authorization ? $"{authorization.Scheme} {authorization.Parameter}" : null);
    }

    private static string? Header(HttpRequestMessage request, string name, string separator = " ") => request.Headers.TryGetValues(name, out var values) ? string.Join(separator, values) : null;

    private static string? Parameter(Uri? uri, string name)
    {
        if (uri is null) return null;
        foreach (var pair in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = pair.Split('=', 2);
            if (Uri.UnescapeDataString(parts[0]) == name) return parts.Length == 2 ? Uri.UnescapeDataString(parts[1]) : string.Empty;
        }
        return null;
    }

    private static HttpResponseMessage Json(string value) => new(HttpStatusCode.OK) { Content = new StringContent(value, Encoding.UTF8, "application/json") };
}
