using System.Net;
using System.Text.Json;
using IPTVExplorer.Core;

namespace IPTVExplorer.Providers;

public sealed class StalkerProviderClient(ProviderRecord provider, ProviderSecret secret, HttpClient http) : IProviderClient
{
    private readonly SemaphoreSlim _handshake = new(1, 1);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<(CatalogType Catalog, string Id), string> _commands = new();
    private string? _token; // Session-only by design. Never expose or persist this value.

    public ProviderType Type => ProviderType.Stalker;

    public async Task<ConnectionTestResult> TestConnectionAsync(CancellationToken cancellationToken = default)
    {
        try { var account = await GetAccountInfoAsync(cancellationToken); return new(account.Authenticated, Type, account.Authenticated ? "Connection successful." : "Profile rejected."); }
        catch (HttpRequestException) { return new(false, Type, "Provider endpoint is unavailable."); }
        catch (JsonException) { return new(false, Type, "Provider returned invalid JSON."); }
    }

    public async Task<AccountInfo> GetAccountInfoAsync(CancellationToken cancellationToken = default)
    {
        using var document = await PortalAsync("stb", "get_profile", [], cancellationToken);
        var root = document.RootElement.Unwrap();
        var authenticated = root.ValueKind == JsonValueKind.Object && (root.Text("auth") == "1" || !string.IsNullOrWhiteSpace(root.Text("id", "user_id")));
        return new AccountInfo(authenticated, root.Text("status"), null);
    }

    public Task<IReadOnlyList<ProviderCategory>> GetLiveCategoriesAsync(CancellationToken cancellationToken = default) => GetCategories("itv", cancellationToken);
    public Task<IReadOnlyList<ProviderCategory>> GetVodCategoriesAsync(CancellationToken cancellationToken = default) => GetCategories("vod", cancellationToken);
    public Task<IReadOnlyList<ProviderCategory>> GetSeriesCategoriesAsync(CancellationToken cancellationToken = default) => GetCategories("series", cancellationToken);

    public async Task<IReadOnlyList<CatalogItem>> GetLiveAsync(string categoryId, CancellationToken cancellationToken = default)
    {
        using var document = await PortalAsync("itv", "get_all_channels", [("genre", categoryId)], cancellationToken);
        return ReadItems(document.RootElement, CatalogType.Live);
    }

    public Task<CatalogPage<CatalogItem>> GetVodPageAsync(string categoryId, int page, CancellationToken cancellationToken = default) => OrderedList("vod", CatalogType.Vod, categoryId, page, cancellationToken);
    public Task<CatalogPage<CatalogItem>> GetSeriesPageAsync(string categoryId, int page, CancellationToken cancellationToken = default) => OrderedList("series", CatalogType.Series, categoryId, page, cancellationToken);

    public async Task<CatalogItem> GetVodInfoAsync(string id, CancellationToken cancellationToken = default)
    {
        using var document = await PortalAsync("vod", "get_ordered_list", [("movie_id", id), ("p", "1")], cancellationToken);
        return ReadItems(document.RootElement, CatalogType.Vod).FirstOrDefault() ?? new CatalogItem(id, "Untitled");
    }

    public async Task<CatalogItem> GetSeriesInfoAsync(string id, CancellationToken cancellationToken = default)
    {
        using var document = await PortalAsync("series", "get_ordered_list", [("movie_id", id), ("p", "1")], cancellationToken);
        return ReadItems(document.RootElement, CatalogType.Series).FirstOrDefault() ?? new CatalogItem(id, "Untitled");
    }

    public async Task<VodDetails> GetVodDetailsAsync(string id, CancellationToken cancellationToken = default)
    {
        using var document = await PortalAsync("vod", "get_ordered_list", [("movie_id", id), ("p", "1")], cancellationToken);
        var item = ReadRawItems(document.RootElement).FirstOrDefault();
        if (item.ValueKind == JsonValueKind.Undefined) return new VodDetails(id, "Untitled", null, null, null, null, null, null, null, null, null);
        CacheCommand(item, CatalogType.Vod, id);
        return new VodDetails(id, item.Text("name", "title") ?? "Untitled", item.Text("screenshot_uri", "cover", "logo"), item.Text("description", "plot"), item.Text("year"), item.Text("genres_str", "genre"), item.Text("director"), item.Text("actors", "cast"), item.Text("time", "duration"), ParseRating(item.Text("rating", "kinopoisk_rating")), item.Text("container_extension"));
    }

    public async Task<SeriesDetails> GetSeriesDetailsAsync(string id, CancellationToken cancellationToken = default)
    {
        var records = new List<JsonElement>();
        var page = 1;
        var totalPages = 1;
        do
        {
            using var document = await PortalAsync("series", "get_ordered_list", [("movie_id", id), ("p", page.ToString(System.Globalization.CultureInfo.InvariantCulture))], cancellationToken);
            var root = document.RootElement.Unwrap();
            records.AddRange(ReadRawItems(root));
            if (page == 1 && root.ValueKind == JsonValueKind.Object)
            {
                var total = int.TryParse(root.Text("total_items", "total"), out var itemCount) ? itemCount : records.Count;
                var size = int.TryParse(root.Text("max_page_items"), out var pageSize) && pageSize > 0 ? pageSize : Math.Max(1, records.Count);
                totalPages = Math.Clamp((int)Math.Ceiling(total / (double)size), 1, 10000);
            }
            page++;
        }
        while (page <= totalPages);
        var first = records.FirstOrDefault();
        var grouped = new SortedDictionary<int, List<EpisodeDetails>>();
        foreach (var record in records)
        {
            var episodeId = record.Text("id", "episode_id");
            if (string.IsNullOrWhiteSpace(episodeId)) continue;
            var season = int.TryParse(record.Text("season", "season_number"), out var seasonNumber) ? seasonNumber : 1;
            int? episode = int.TryParse(record.Text("episode", "episode_num"), out var episodeNumber) ? episodeNumber : null;
            if (!grouped.TryGetValue(season, out var list)) grouped[season] = list = [];
            list.Add(new EpisodeDetails(episodeId, record.Text("name", "title") ?? $"Episode {episode}", season, episode, record.Text("container_extension")));
            CacheCommand(record, CatalogType.Series, episodeId);
        }
        var seasons = grouped.Select(pair => new SeasonDetails(pair.Key, $"Season {pair.Key}", pair.Value)).ToArray();
        return new SeriesDetails(id, first.ValueKind == JsonValueKind.Undefined ? "Untitled" : first.Text("name", "title") ?? "Untitled", first.ValueKind == JsonValueKind.Undefined ? null : first.Text("screenshot_uri", "cover"), first.ValueKind == JsonValueKind.Undefined ? null : first.Text("description", "plot"), first.ValueKind == JsonValueKind.Undefined ? null : first.Text("year"), first.ValueKind == JsonValueKind.Undefined ? null : first.Text("genres_str", "genre"), first.ValueKind == JsonValueKind.Undefined ? null : first.Text("director"), first.ValueKind == JsonValueKind.Undefined ? null : first.Text("actors", "cast"), first.ValueKind == JsonValueKind.Undefined ? null : ParseRating(first.Text("rating")), seasons);
    }

    public async Task<ResolvedMedia> ResolveMediaAsync(MediaRequest request, CancellationToken cancellationToken = default)
    {
        var command = await ResolveCommandAsync(request, cancellationToken);
        var parameters = new List<(string, string)> { ("cmd", command) };
        if (request.Catalog == CatalogType.Series) parameters.Add(("series", "1"));
        // Episode resolution intentionally uses vod/create_link with series=1.
        using var document = await PortalAsync("vod", "create_link", parameters, cancellationToken);
        var root = document.RootElement.Unwrap();
        var resolvedCommand = root.ValueKind == JsonValueKind.Object ? root.Text("cmd", "url") : root.ToString();
        var url = ExtractHttpUri(resolvedCommand);
        return url is null ? throw new InvalidDataException("Provider did not return a playable link.") : new ResolvedMedia(url);
    }

    private async Task<IReadOnlyList<ProviderCategory>> GetCategories(string type, CancellationToken cancellationToken)
    {
        using var document = await PortalAsync(type, "get_genres", [], cancellationToken);
        return JsonSupport.Categories(document.RootElement);
    }

    private async Task<CatalogPage<CatalogItem>> OrderedList(string type, CatalogType catalog, string categoryId, int page, CancellationToken cancellationToken)
    {
        page = Math.Max(1, page);
        using var document = await PortalAsync(type, "get_ordered_list", [("category", categoryId), ("p", page.ToString(System.Globalization.CultureInfo.InvariantCulture)), ("sortby", "added")], cancellationToken);
        var root = document.RootElement.Unwrap();
        var items = ReadItems(root, catalog);
        var total = root.ValueKind == JsonValueKind.Object && int.TryParse(root.Text("total_items", "total"), out var count) ? count : items.Count;
        var size = root.ValueKind == JsonValueKind.Object && int.TryParse(root.Text("max_page_items"), out var pageSize) && pageSize > 0 ? pageSize : Math.Max(1, items.Count);
        var pages = Math.Max(page, (int)Math.Ceiling(total / (double)size));
        return new CatalogPage<CatalogItem>(items, page, size, total, pages);
    }

    private IReadOnlyList<CatalogItem> ReadItems(JsonElement root, CatalogType catalog)
    {
        var result = new List<CatalogItem>();
        foreach (var raw in ReadRawItems(root))
        {
            var item = JsonSupport.Item(raw, catalog);
            if (item.Id.Length == 0) continue;
            CacheCommand(raw, catalog, item.Id);
            result.Add(item);
        }
        return result;
    }

    private static IReadOnlyList<JsonElement> ReadRawItems(JsonElement root)
    {
        root = root.Unwrap();
        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("data", out var data)) root = data;
        if (root.ValueKind != JsonValueKind.Array) return [];
        return root.EnumerateArray().Select(value => value.Clone()).ToArray();
    }

    private void CacheCommand(JsonElement item, CatalogType catalog, string fallbackId)
    {
        var id = item.Text(catalog == CatalogType.Series ? "episode_id" : "id", "id") ?? fallbackId;
        var command = item.Text("cmd", "command");
        if (!string.IsNullOrWhiteSpace(id) && !string.IsNullOrWhiteSpace(command)) _commands[(catalog, id)] = command;
    }

    private async Task<string> ResolveCommandAsync(MediaRequest request, CancellationToken cancellationToken)
    {
        if (_commands.TryGetValue((request.Catalog, request.MediaId), out var cached)) return cached;
        if (request.Catalog is CatalogType.Vod or CatalogType.Series)
        {
            if (request.Catalog == CatalogType.Series)
            {
                _ = await GetSeriesDetailsAsync(request.SeriesId ?? request.MediaId, cancellationToken);
            }
            else
            {
                using var document = await PortalAsync("vod", "get_ordered_list", [("movie_id", request.MediaId), ("p", "1")], cancellationToken);
                _ = ReadItems(document.RootElement, request.Catalog);
            }
            if (_commands.TryGetValue((request.Catalog, request.MediaId), out cached)) return cached;
        }
        throw new InvalidDataException("The media command is unavailable; reopen its catalog and try again.");
    }

    private async Task<JsonDocument> PortalAsync(string type, string action, IReadOnlyList<(string Key, string Value)> parameters, CancellationToken cancellationToken)
    {
        await EnsureTokenAsync(cancellationToken);
        try { return await SendAsync(type, action, parameters, includeToken: true, cancellationToken); }
        catch (StalkerSessionExpiredException)
        {
            await EnsureTokenAsync(cancellationToken);
            return await SendAsync(type, action, parameters, includeToken: true, cancellationToken);
        }
    }

    private async Task EnsureTokenAsync(CancellationToken cancellationToken)
    {
        if (_token is not null) return;
        await _handshake.WaitAsync(cancellationToken);
        try
        {
            if (_token is not null) return;
            using var document = await SendAsync("stb", "handshake", [("token", string.Empty)], includeToken: false, cancellationToken);
            var candidate = document.RootElement.Unwrap().Text("token");
            if (string.IsNullOrWhiteSpace(candidate)) throw new InvalidDataException("Stalker handshake did not return a token.");
            _token = candidate;
        }
        finally { _handshake.Release(); }
    }

    private async Task<JsonDocument> SendAsync(string type, string action, IReadOnlyList<(string Key, string Value)> parameters, bool includeToken, CancellationToken cancellationToken)
    {
        var mac = NormalizeMac(secret.MacAddress);
        var values = new List<(string, string)> { ("type", type), ("action", action), ("JsHttpRequest", "1-xml") };
        values.AddRange(parameters);
        var endpoint = new Uri(provider.ServerUri.GetLeftPart(UriPartial.Authority) + "/" + provider.PortalPath.TrimStart('/'));
        var uri = XtreamProviderClient.BuildUri(endpoint, values);
        using var response = await HttpRetry.SendAsync(http, () =>
        {
            var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.TryAddWithoutValidation("Cookie", $"mac={Uri.EscapeDataString(mac)}; stb_lang=en; timezone=UTC");
            request.Headers.Referrer = new Uri(provider.ServerUri.GetLeftPart(UriPartial.Authority) + "/c/");
            request.Headers.TryAddWithoutValidation("X-User-Agent", "Model: MAG254; Link: Ethernet");
            if (includeToken && _token is not null) request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _token);
            return request;
        }, cancellationToken);
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            _token = null;
            throw new StalkerSessionExpiredException();
        }
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
    }

    private static string NormalizeMac(string? mac)
    {
        var value = mac?.Trim().ToUpperInvariant();
        if (value is null || !System.Text.RegularExpressions.Regex.IsMatch(value, "^(?:[0-9A-F]{2}:){5}[0-9A-F]{2}$")) throw new InvalidOperationException("A valid Stalker MAC is required.");
        return value;
    }

    private static Uri? ExtractHttpUri(string? command)
    {
        if (string.IsNullOrWhiteSpace(command)) return null;
        var start = command.IndexOf("http", StringComparison.OrdinalIgnoreCase);
        if (start < 0) return null;
        var value = command[start..].Split(' ', '\t', '\r', '\n').First();
        return Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" ? uri : null;
    }
    private static double? ParseRating(string? value) => double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var rating) ? rating : null;
    private sealed class StalkerSessionExpiredException : Exception { }
}
