using System.Net;
using System.Text;
using IPTVExplorer.Core;
using IPTVExplorer.Providers;

namespace IPTVExplorer.Tests;

public sealed class StalkerLiveResolutionTests
{
    private static readonly ProviderRecord Provider = new(
        "stalker-live-fixture",
        ProviderType.Stalker,
        "Stalker live fixture",
        new Uri("https://portal.example.invalid"),
        "fixture-reference",
        Enabled: true);

    private static readonly ProviderSecret Secret = new(MacAddress: "00:1A:79:AA:BB:CC");

    [Fact]
    public async Task LivePlaybackUsesItvCreateLinkAndRebuildsEmptyStreamResponse()
    {
        var handler = new RoutingHandler();
        using var http = new HttpClient(handler);
        var client = new StalkerProviderClient(Provider, Secret, http);

        var live = await client.GetLiveAsync("100");
        Assert.Single(live);

        var liveMedia = await client.ResolveMediaAsync(new MediaRequest(CatalogType.Live, "live-1", null, null));

        Assert.Equal("/play/live.php", liveMedia.Uri.AbsolutePath);
        Assert.Equal("live-1", Parameter(liveMedia.Uri, "stream"));
        Assert.Null(Parameter(liveMedia.Uri, "play_token"));
        Assert.Contains(handler.CreateLinkRequests, request => request.Type == "itv" && request.Action == "create_link");

        Assert.NotNull(liveMedia.Headers);
        Assert.Equal("Bearer legacy-token", liveMedia.Headers!["Authorization"]);
        Assert.Contains("play_token=fresh-live-token", liveMedia.Headers["Cookie"], StringComparison.Ordinal);
        Assert.Contains("token=legacy-token", liveMedia.Headers["Cookie"], StringComparison.Ordinal);
        Assert.DoesNotContain("stale-live-token", liveMedia.Headers["Cookie"], StringComparison.Ordinal);

        var vodMedia = await client.ResolveMediaAsync(new MediaRequest(CatalogType.Vod, "vod-1", null, "mkv"));
        Assert.Equal("/play/movie.php", vodMedia.Uri.AbsolutePath);
        Assert.Null(vodMedia.Headers);
        Assert.Contains(handler.CreateLinkRequests, request => request.Type == "vod" && request.Action == "create_link");
    }

    [Fact]
    public async Task CompleteLiveCreateLinkRemainsDirect()
    {
        var handler = new RoutingHandler { ReturnCompleteLiveStream = true };
        using var http = new HttpClient(handler);
        var client = new StalkerProviderClient(Provider, Secret, http);

        _ = await client.GetLiveAsync("100");
        var media = await client.ResolveMediaAsync(new MediaRequest(CatalogType.Live, "live-1", null, null));

        Assert.Equal("live-direct", Parameter(media.Uri, "stream"));
        Assert.Equal("direct-token", Parameter(media.Uri, "play_token"));
        Assert.Null(media.Headers);
    }

    [Fact]
    public async Task LiveHybridTraceContainsOnlySafeStructuredMetadata()
    {
        var handler = new RoutingHandler();
        using var http = new HttpClient(handler);
        var trace = new RecordingPlaybackDiagnosticTrace();
        var client = new StalkerProviderClient(Provider, Secret, http, diagnosticTrace: trace);

        _ = await client.GetLiveAsync("100");
        _ = await client.ResolveMediaAsync(new MediaRequest(CatalogType.Live, "live-1", null, null));

        Assert.Contains("STALKER PLAYBACK RESOLVED", trace.Text, StringComparison.Ordinal);
        Assert.Contains("hybrid=true", trace.Text, StringComparison.Ordinal);
        Assert.Contains("header_names=Accept,Authorization,Cookie,Referer,User-Agent,X-User-Agent", trace.Text, StringComparison.Ordinal);
        Assert.Contains("cookie_names=mac,stb_lang,timezone,token,play_token", trace.Text, StringComparison.Ordinal);
        Assert.Contains("final_query_keys=extension,mac,stream", trace.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("https://", trace.Text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("00:1A:79:AA:BB:CC", trace.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("legacy-token", trace.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("fresh-live-token", trace.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("stale-live-token", trace.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("Bearer", trace.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("ffmpeg", trace.Text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DisabledLiveTraceProducesNoEvents()
    {
        var handler = new RoutingHandler();
        using var http = new HttpClient(handler);
        var trace = new RecordingPlaybackDiagnosticTrace(enabled: false);
        var client = new StalkerProviderClient(Provider, Secret, http, diagnosticTrace: trace);

        _ = await client.GetLiveAsync("100");
        _ = await client.ResolveMediaAsync(new MediaRequest(CatalogType.Live, "live-1", null, null));

        Assert.Empty(trace.Events);
    }

    private sealed class RoutingHandler : HttpMessageHandler
    {
        public List<(string? Type, string? Action)> CreateLinkRequests { get; } = [];
        public bool ReturnCompleteLiveStream { get; init; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var type = Parameter(request.RequestUri, "type");
            var action = Parameter(request.RequestUri, "action");

            if (action == "handshake")
                return Task.FromResult(Json("{\"js\":{\"token\":\"legacy-token\"}}"));

            if (type == "itv" && action == "get_all_channels")
            {
                return Task.FromResult(Json("{\"js\":{\"data\":[{\"id\":\"live-1\",\"name\":\"Fixture Live\",\"tv_genre_id\":\"100\",\"cmd\":\"ffmpeg https://stream.example.invalid/play/live.php?mac=00:1A:79:AA:BB:CC&stream=live-1&extension=ts&play_token=stale-live-token\"}]}}"));
            }

            if (type == "vod" && action == "get_ordered_list")
            {
                return Task.FromResult(Json("{\"js\":{\"data\":[{\"id\":\"vod-1\",\"name\":\"Fixture Movie\",\"cmd\":\"ffmpeg http://origin.example.invalid/movie/fixture\",\"container_extension\":\"mkv\"}]}}"));
            }

            if (action == "create_link")
            {
                CreateLinkRequests.Add((type, action));
                return type switch
                {
                    "itv" when ReturnCompleteLiveStream => Task.FromResult(Json("{\"js\":{\"cmd\":\"ffmpeg https://stream.example.invalid/play/live.php?stream=live-direct&extension=ts&play_token=direct-token\"}}")),
                    "itv" => Task.FromResult(Json("{\"js\":{\"cmd\":\"ffmpeg https://stream.example.invalid/play/live.php?mac=00:1A:79:AA:BB:CC&stream=&extension=ts&play_token=fresh-live-token\"}}")),
                    "vod" => Task.FromResult(Json("{\"js\":{\"cmd\":\"ffmpeg https://stream.example.invalid/play/movie.php?stream=vod-1.mkv\"}}")),
                    _ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest))
                };
            }

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
            var parts = pair.Split('=', 2);
            if (Uri.UnescapeDataString(parts[0]) != name) continue;
            return parts.Length == 2 ? Uri.UnescapeDataString(parts[1]) : string.Empty;
        }
        return null;
    }
}
