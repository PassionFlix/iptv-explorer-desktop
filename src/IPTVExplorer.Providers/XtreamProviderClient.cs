using System.Net.Http.Json;
using System.Text.Json;
using IPTVExplorer.Core;

namespace IPTVExplorer.Providers;

public sealed class XtreamProviderClient(ProviderRecord provider, ProviderSecret secret, HttpClient http) : IProviderClient
{
    public ProviderType Type => ProviderType.Xtream;

    public async Task<ConnectionTestResult> TestConnectionAsync(CancellationToken cancellationToken = default)
    {
        try { var account = await GetAccountInfoAsync(cancellationToken); return new(account.Authenticated, Type, account.Authenticated ? "Connection successful." : "Authentication rejected."); }
        catch (HttpRequestException) { return new(false, Type, "Provider endpoint is unavailable."); }
        catch (JsonException) { return new(false, Type, "Provider returned invalid JSON."); }
    }

    public async Task<AccountInfo> GetAccountInfoAsync(CancellationToken cancellationToken = default)
    {
        using var document = await GetAsync(null, cancellationToken);
        if (!document.RootElement.TryGetProperty("user_info", out var user)) return new(false, null, null);
        var authenticated = user.Text("auth") == "1";
        DateTimeOffset? expires = long.TryParse(user.Text("exp_date"), out var epoch) && epoch > 0 ? DateTimeOffset.FromUnixTimeSeconds(epoch) : null;
        return new AccountInfo(authenticated, user.Text("status"), expires);
    }

    public Task<IReadOnlyList<ProviderCategory>> GetLiveCategoriesAsync(CancellationToken cancellationToken = default) => Categories("get_live_categories", cancellationToken);
    public Task<IReadOnlyList<ProviderCategory>> GetVodCategoriesAsync(CancellationToken cancellationToken = default) => Categories("get_vod_categories", cancellationToken);
    public Task<IReadOnlyList<ProviderCategory>> GetSeriesCategoriesAsync(CancellationToken cancellationToken = default) => Categories("get_series_categories", cancellationToken);

    public async Task<IReadOnlyList<CatalogItem>> GetLiveAsync(string categoryId, CancellationToken cancellationToken = default) => await Items("get_live_streams", CatalogType.Live, categoryId, cancellationToken);
    public async Task<CatalogPage<CatalogItem>> GetVodPageAsync(string categoryId, int page, CancellationToken cancellationToken = default) => Page(await Items("get_vod_streams", CatalogType.Vod, categoryId, cancellationToken), page);
    public async Task<CatalogPage<CatalogItem>> GetSeriesPageAsync(string categoryId, int page, CancellationToken cancellationToken = default) => Page(await Items("get_series", CatalogType.Series, categoryId, cancellationToken), page);

    public async Task<CatalogItem> GetVodInfoAsync(string id, CancellationToken cancellationToken = default)
    {
        using var document = await GetAsync("get_vod_info", cancellationToken, ("vod_id", id));
        var root = document.RootElement;
        var info = root.TryGetProperty("movie_data", out var movie) ? movie : root.TryGetProperty("info", out var detail) ? detail : root;
        return JsonSupport.Item(info, CatalogType.Vod) with { Id = id };
    }

    public async Task<CatalogItem> GetSeriesInfoAsync(string id, CancellationToken cancellationToken = default)
    {
        using var document = await GetAsync("get_series_info", cancellationToken, ("series_id", id));
        var root = document.RootElement;
        var info = root.TryGetProperty("info", out var detail) ? detail : root;
        return JsonSupport.Item(info, CatalogType.Series, useSeriesModifiedDate: true) with { Id = id };
    }

    public async Task<VodDetails> GetVodDetailsAsync(string id, CancellationToken cancellationToken = default)
    {
        using var document = await GetAsync("get_vod_info", cancellationToken, ("vod_id", id));
        var root = document.RootElement;
        var movie = root.TryGetProperty("movie_data", out var movieData) ? movieData : root;
        var info = root.TryGetProperty("info", out var infoData) ? infoData : movie;
        return new VodDetails(
            id,
            movie.Text("name", "title") ?? info.Text("name", "title") ?? "Untitled",
            movie.Text("stream_icon", "cover") ?? info.Text("movie_image", "cover"),
            info.Text("plot", "description"),
            info.Text("year", "releasedate", "releaseDate"),
            info.Text("genre"),
            info.Text("director"),
            info.Text("cast", "actors"),
            info.Text("duration", "duration_secs"),
            ParseRating(info.Text("rating", "rating_5based")),
            movie.Text("container_extension") ?? info.Text("container_extension"));
    }

    public async Task<SeriesDetails> GetSeriesDetailsAsync(string id, CancellationToken cancellationToken = default)
    {
        using var document = await GetAsync("get_series_info", cancellationToken, ("series_id", id));
        var root = document.RootElement;
        var info = root.TryGetProperty("info", out var infoData) ? infoData : root;
        var seasons = new List<SeasonDetails>();
        if (root.TryGetProperty("episodes", out var episodes) && episodes.ValueKind == JsonValueKind.Object)
        {
            foreach (var seasonProperty in episodes.EnumerateObject())
            {
                if (seasonProperty.Value.ValueKind != JsonValueKind.Array) continue;
                var seasonNumber = int.TryParse(seasonProperty.Name, out var parsedSeason) ? parsedSeason : 0;
                var list = new List<EpisodeDetails>();
                foreach (var episode in seasonProperty.Value.EnumerateArray())
                {
                    var episodeId = episode.Text("id"); // Never substitute series_id here.
                    if (string.IsNullOrWhiteSpace(episodeId)) continue;
                    int? episodeNumber = int.TryParse(episode.Text("episode_num", "episode"), out var parsedEpisode) ? parsedEpisode : null;
                    list.Add(new EpisodeDetails(episodeId, episode.Text("title", "name") ?? $"Episode {episodeNumber}", seasonNumber, episodeNumber, episode.Text("container_extension")));
                }
                seasons.Add(new SeasonDetails(seasonNumber, $"Season {seasonNumber}", list));
            }
        }
        var poster = new[] { info.Text("cover"), info.Text("movie_image") }.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
        return new SeriesDetails(id, info.Text("name", "title") ?? "Untitled", poster, info.Text("plot", "description"), info.Text("year", "releaseDate"), info.Text("genre"), info.Text("director"), info.Text("cast", "actors"), ParseRating(info.Text("rating", "rating_5based")), seasons.OrderBy(s => s.Number).ToArray());
    }

    public Task<ResolvedMedia> ResolveMediaAsync(MediaRequest request, CancellationToken cancellationToken = default)
    {
        RequireCredentials();
        if (string.IsNullOrWhiteSpace(request.MediaId)) throw new ArgumentException("A media id is required.", nameof(request));
        var folder = request.Catalog switch { CatalogType.Live => "live", CatalogType.Vod => "movie", CatalogType.Series => "series", _ => throw new ArgumentOutOfRangeException(nameof(request)) };
        var fallbackExtension = request.Catalog == CatalogType.Live ? "ts" : "mp4";
        var extension = string.IsNullOrWhiteSpace(request.Extension) ? fallbackExtension : request.Extension.Trim().TrimStart('.');
        // MediaId is deliberately the episode id for series playback, never the series id.
        var relative = $"{folder}/{Uri.EscapeDataString(secret.Username!)}/{Uri.EscapeDataString(secret.Password!)}/{Uri.EscapeDataString(request.MediaId)}.{Uri.EscapeDataString(extension)}";
        return Task.FromResult(new ResolvedMedia(new Uri(EnsureTrailingSlash(provider.ServerUri), relative)));
    }

    private async Task<IReadOnlyList<ProviderCategory>> Categories(string action, CancellationToken cancellationToken)
    {
        using var document = await GetAsync(action, cancellationToken);
        return JsonSupport.Categories(document.RootElement);
    }

    private async Task<IReadOnlyList<CatalogItem>> Items(string action, CatalogType catalog, string categoryId, CancellationToken cancellationToken)
    {
        using var document = await GetAsync(action, cancellationToken, ("category_id", categoryId));
        if (document.RootElement.ValueKind != JsonValueKind.Array) throw new JsonException("Expected a catalog array.");
        return document.RootElement.EnumerateArray().Select(i => JsonSupport.Item(i, catalog, useSeriesModifiedDate: true)).Where(i => i.Id.Length > 0).ToArray();
    }

    private async Task<JsonDocument> GetAsync(string? action, CancellationToken cancellationToken, params (string Key, string Value)[] parameters)
    {
        RequireCredentials();
        var query = new List<(string, string)> { ("username", secret.Username!), ("password", secret.Password!) };
        if (action is not null) query.Add(("action", action));
        query.AddRange(parameters);
        var uri = BuildUri(new Uri(EnsureTrailingSlash(provider.ServerUri), "player_api.php"), query);
        using var response = await HttpRetry.SendAsync(http, () => new HttpRequestMessage(HttpMethod.Get, uri), cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
    }

    private void RequireCredentials()
    {
        if (string.IsNullOrWhiteSpace(secret.Username) || string.IsNullOrEmpty(secret.Password)) throw new InvalidOperationException("Xtream credentials are incomplete.");
    }
    private static Uri EnsureTrailingSlash(Uri uri) => uri.AbsoluteUri.EndsWith("/", StringComparison.Ordinal) ? uri : new Uri(uri.AbsoluteUri + "/");
    internal static Uri BuildUri(Uri endpoint, IEnumerable<(string Key, string Value)> values)
    {
        var builder = new UriBuilder(endpoint) { Query = string.Join("&", values.Select(v => $"{Uri.EscapeDataString(v.Key)}={Uri.EscapeDataString(v.Value)}")) };
        return builder.Uri;
    }
    private static CatalogPage<CatalogItem> Page(IReadOnlyList<CatalogItem> items, int page)
    {
        const int size = 100; page = Math.Max(1, page); var totalPages = (int)Math.Ceiling(items.Count / (double)size);
        return new CatalogPage<CatalogItem>(items.Skip((page - 1) * size).Take(size).ToArray(), page, size, items.Count, totalPages);
    }
    private static double? ParseRating(string? value) => double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var rating) ? rating : null;
}
