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
    public async Task LivePlaybackUsesItvCreateLinkWhileVodUsesVodCreateLink()
    {
        var handler = new RoutingHandler();
        using var http = new HttpClient(handler);
        var client = new StalkerProviderClient(Provider, Secret, http);

        var live = await client.GetLiveAsync("100");
        Assert.Single(live);

        var liveMedia = await client.ResolveMediaAsync(new MediaRequest(CatalogType.Live, "live-1", null, null));
        Assert.Equal("/play/live.php", liveMedia.Uri.AbsolutePath);
        Assert.Contains(handler.CreateLinkRequests, request => request.Type == "itv" && request.Action == "create_link");

        var vodMedia = await client.ResolveMediaAsync(new MediaRequest(CatalogType.Vod, "vod-1", null, "mkv"));
        Assert.Equal("/play/movie.php", vodMedia.Uri.AbsolutePath);
        Assert.Contains(handler.CreateLinkRequests, request => request.Type == "vod" && request.Action == "create_link");
    }

    private sealed class RoutingHandler : HttpMessageHandler
    {
        public List<(string? Type, string? Action)> CreateLinkRequests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var type = Parameter(request.RequestUri, "type");
            var action = Parameter(request.RequestUri, "action");

            if (action == "handshake")
                return Task.FromResult(Json("{\"js\":{\"token\":\"legacy-token\"}}"));

            if (type == "itv" && action == "get_all_channels")
            {
                return Task.FromResult(Json("{\"js\":{\"data\":[{\"id\":\"live-1\",\"name\":\"Fixture Live\",\"tv_genre_id\":\"100\",\"cmd\":\"ffmpeg http://origin.example.invalid/live/fixture\"}]}}"));
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
                    "itv" => Task.FromResult(Json("{\"js\":{\"cmd\":\"ffmpeg https://stream.example.invalid/play/live.php?stream=live-1\"}}")),
                    "vod" => Task.FromResult(Json("{\"js\":{\"cmd\":\"ffmpeg https://stream.example.invalid/play/movie.php?stream=vod-1.mkv\"}}")),
                    _ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest))
                };
            }

            return Task.FromResult(Json("{\"js\":{}}"));
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

        private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
    }
}
