using System.Net;
using System.Text;
using IPTVExplorer.Core;
using IPTVExplorer.Providers;

namespace IPTVExplorer.Tests;

public sealed class StalkerLiveCategoryFallbackTests
{
    private static readonly ProviderRecord Provider = new("stalker-category-fixture", ProviderType.Stalker, "Fixture", new Uri("https://provider.invalid"), "fixture", Enabled: true);
    private static readonly ProviderSecret Secret = new(MacAddress: "00:1A:79:AA:BB:CC");

    [Fact]
    public async Task BulkCategoryNeverUsesOrderedListFallback()
    {
        var handler = new CategoryHandler();
        using var http = new HttpClient(handler);
        var channels = await new StalkerProviderClient(Provider, Secret, http).GetLiveAsync("100");

        Assert.Equal(["bulk-1"], channels.Select(item => item.Id));
        Assert.Empty(handler.OrderedRequests);
    }

    [Fact]
    public async Task MissingBulkCategoryUsesOnlyTargetedSequentialPagesAndCachesResult()
    {
        var handler = new CategoryHandler();
        using var http = new HttpClient(handler);
        var client = new StalkerProviderClient(Provider, Secret, http);

        var first = await client.GetLiveAsync("900");
        var second = await client.GetLiveAsync("900");

        Assert.Equal(["adult-1", "adult-2", "adult-3"], first.Select(item => item.Id));
        Assert.Equal(first.Select(item => item.Id), second.Select(item => item.Id));
        Assert.Equal([("900", "1"), ("900", "2")], handler.OrderedRequests);
        Assert.DoesNotContain(handler.OrderedRequests, request => request.Genre != "900");
        Assert.Equal(1, handler.MaxConcurrentOrderedRequests);
    }

    private sealed class CategoryHandler : HttpMessageHandler
    {
        private int _activeOrderedRequests;
        public int MaxConcurrentOrderedRequests { get; private set; }
        public List<(string Genre, string Page)> OrderedRequests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var action = Parameter(request.RequestUri, "action");
            if (action == "handshake") return Json("{\"js\":{\"token\":\"fixture-token\"}}");
            if (action == "get_all_channels") return Json("{\"js\":{\"data\":[{\"id\":\"bulk-1\",\"name\":\"Bulk\",\"tv_genre_id\":\"100\",\"cmd\":\"ffmpeg https://stream.example.invalid/bulk\"}]}}");
            if (action != "get_ordered_list") return Json("{\"js\":{}}");

            var genre = Parameter(request.RequestUri, "genre") ?? string.Empty;
            var page = Parameter(request.RequestUri, "p") ?? string.Empty;
            OrderedRequests.Add((genre, page));
            Assert.Equal("0", Parameter(request.RequestUri, "fav"));
            Assert.Equal("0", Parameter(request.RequestUri, "from_ch_id"));
            var active = Interlocked.Increment(ref _activeOrderedRequests);
            MaxConcurrentOrderedRequests = Math.Max(MaxConcurrentOrderedRequests, active);
            try
            {
                await Task.Yield();
                return page == "1"
                    ? Json("{\"js\":{\"data\":[{\"id\":\"adult-1\",\"name\":\"One\",\"cmd\":\"ffmpeg https://stream.example.invalid/one\"},{\"id\":\"adult-2\",\"name\":\"Two\",\"cmd\":\"ffmpeg https://stream.example.invalid/two\"}],\"total_items\":3,\"max_page_items\":2}}")
                    : Json("{\"js\":{\"data\":[{\"id\":\"adult-3\",\"name\":\"Three\",\"cmd\":\"ffmpeg https://stream.example.invalid/three\"}],\"total_items\":3,\"max_page_items\":2}}");
            }
            finally { Interlocked.Decrement(ref _activeOrderedRequests); }
        }
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

    private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
}
