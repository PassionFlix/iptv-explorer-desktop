using System.Net;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using IPTVExplorer.Core;

namespace IPTVExplorer.Providers;

public sealed class StalkerProviderClient(ProviderRecord provider, ProviderSecret secret, HttpClient http) : IProviderClient
{
    private static readonly TimeSpan LiveCatalogCacheDuration = TimeSpan.FromMinutes(5);
    private readonly SemaphoreSlim _handshake = new(1, 1);
    private readonly SemaphoreSlim _liveCatalogLock = new(1, 1);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<(CatalogType Catalog, string Id), string> _commands = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, VodDetails> _vodDetails = new(StringComparer.Ordinal);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _categoryStructures = new(StringComparer.Ordinal);
    private IReadOnlyList<LiveChannel>? _liveChannels;
    private DateTimeOffset _liveChannelsExpiresAt;
    private string? _token; // Session-only by design. Never expose or persist this value.
    private RequestProfile? _requestProfile;
    private AccountInfo? _accountInfo;

    public ProviderType Type => ProviderType.Stalker;
    internal string? CategoryDiagnostic
    {
        get
        {
            var values = new[] { "itv", "vod", "series" }.Where(_categoryStructures.ContainsKey).Select(type => _categoryStructures[type]).ToArray();
            return values.Length == 0 ? null : string.Join(" ; ", values);
        }
    }

    public async Task<ConnectionTestResult> TestConnectionAsync(CancellationToken cancellationToken = default)
    {
        try { var account = await GetAccountInfoAsync(cancellationToken); return new(account.Authenticated, Type, account.Authenticated ? "Connection successful." : "Profile rejected."); }
        catch (StalkerResponseException exception) { return new(false, Type, exception.Diagnostic); }
        catch (HttpRequestException) { return new(false, Type, "Provider endpoint is unavailable."); }
        catch (JsonException) { return new(false, Type, "Provider returned invalid JSON."); }
        catch (InvalidDataException) { return new(false, Type, "Provider returned an incompatible Stalker response."); }
        catch (StalkerSessionExpiredException) { return new(false, Type, "Provider rejected the Stalker session."); }
    }

    public async Task<AccountInfo> GetAccountInfoAsync(CancellationToken cancellationToken = default)
    {
        if (_accountInfo is not null) return _accountInfo;

        using var profileDocument = await PortalAsync("stb", "get_profile", [], cancellationToken);
        var profile = profileDocument.RootElement.Unwrap();
        var rejected = profile.Text("auth") == "0" || profile.Text("blocked") == "1";
        var recognized = profile.ValueKind == JsonValueKind.Object &&
            new[] { "id", "user_id", "mac", "status", "blocked" }.Any(field => profile.TryGetProperty(field, out _));
        if (rejected || !recognized)
            return _accountInfo = new AccountInfo(false, profile.Text("status"), ParseExpiration(profile));

        JsonElement mainInfo = default;
        try
        {
            using var mainDocument = await PortalAsync("account_info", "get_main_info", [], cancellationToken);
            mainInfo = mainDocument.RootElement.Unwrap().Clone();
        }
        catch (StalkerResponseException)
        {
            // get_main_info is an optional compatibility endpoint. A valid profile remains usable.
        }

        var status = mainInfo.Text("status") ?? profile.Text("status");
        var expiration = ParseExpiration(mainInfo) ?? ParseExpiration(profile) ?? ParsePhoneExpiration(mainInfo);
        return _accountInfo = new AccountInfo(true, status, expiration);
    }

    public Task<IReadOnlyList<ProviderCategory>> GetLiveCategoriesAsync(CancellationToken cancellationToken = default) => GetCategories("itv", cancellationToken);
    public Task<IReadOnlyList<ProviderCategory>> GetVodCategoriesAsync(CancellationToken cancellationToken = default) => GetCategories("vod", cancellationToken);
    public Task<IReadOnlyList<ProviderCategory>> GetSeriesCategoriesAsync(CancellationToken cancellationToken = default) => GetCategories("series", cancellationToken);

    public async Task<IReadOnlyList<CatalogItem>> GetLiveAsync(string categoryId, CancellationToken cancellationToken = default)
    {
        var channels = await GetAllLiveChannelsAsync(cancellationToken);
        return channels.Where(channel => string.Equals(channel.GenreId, categoryId, StringComparison.Ordinal)).Select(channel => channel.Item).ToArray();
    }

    public Task<CatalogPage<CatalogItem>> GetVodPageAsync(string categoryId, int page, CancellationToken cancellationToken = default) => OrderedList("vod", CatalogType.Vod, categoryId, page, cancellationToken);
    public Task<CatalogPage<CatalogItem>> GetSeriesPageAsync(string categoryId, int page, CancellationToken cancellationToken = default) => OrderedList("series", CatalogType.Series, categoryId, page, cancellationToken);

    public async Task<CatalogItem> GetVodInfoAsync(string id, CancellationToken cancellationToken = default)
    {
        using var document = await PortalAsync("vod", "get_ordered_list", [("movie_id", id), ("p", "1")], cancellationToken);
        var item = FindMediaRecord(document.RootElement, CatalogType.Vod, id);
        if (item.ValueKind == JsonValueKind.Undefined) throw ContentNotFound();
        var result = JsonSupport.Item(item, CatalogType.Vod);
        CacheCommand(item, CatalogType.Vod, result.Id);
        return result;
    }

    public async Task<CatalogItem> GetSeriesInfoAsync(string id, CancellationToken cancellationToken = default)
    {
        using var document = await PortalAsync("series", "get_ordered_list", [("movie_id", id), ("p", "1")], cancellationToken);
        return ReadItems(document.RootElement, CatalogType.Series).FirstOrDefault() ?? new CatalogItem(id, "Untitled");
    }

    public async Task<VodDetails> GetVodDetailsAsync(string id, CancellationToken cancellationToken = default)
    {
        if (_vodDetails.TryGetValue(id, out var cached)) return cached;

        using var document = await PortalAsync("vod", "get_ordered_list", [("movie_id", id), ("p", "1")], cancellationToken);
        var item = FindMediaRecord(document.RootElement, CatalogType.Vod, id);
        if (item.ValueKind == JsonValueKind.Undefined) throw ContentNotFound();
        CacheCommand(item, CatalogType.Vod, id);
        var details = NormalizeVodDetails(item, id);
        _vodDetails[id] = details;
        return details;
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
        if (first.ValueKind == JsonValueKind.Undefined) throw ContentNotFound();
        var seasons = MapSeriesSeasons(records);
        return new SeriesDetails(id, first.Text("name", "title") ?? "Untitled", first.Text("screenshot_uri", "cover"), first.Text("description", "plot"), first.Text("year"), first.Text("genres_str", "genre"), first.Text("director"), first.Text("actors", "cast"), ParseRating(first.Text("rating")), seasons);
    }

    public async Task<ResolvedMedia> ResolveMediaAsync(MediaRequest request, CancellationToken cancellationToken = default)
    {
        var command = await ResolveCommandAsync(request, cancellationToken);
        var parameters = new List<(string, string)> { ("cmd", command) };
        if (request.Catalog == CatalogType.Series) parameters.Add(("series", "1"));
        var portalType = request.Catalog == CatalogType.Live ? "itv" : "vod";
        // Episodes intentionally use vod/create_link with series=1; live channels use itv/create_link.
        using var document = await PortalAsync(portalType, "create_link", parameters, cancellationToken);
        var root = document.RootElement.Unwrap();
        var resolvedCommand = root.ValueKind == JsonValueKind.Object ? root.Text("cmd", "url") : root.ToString();
        var url = ExtractHttpUri(resolvedCommand);
        return url is null ? throw new InvalidDataException("Provider did not return a playable link.") : new ResolvedMedia(url);
    }

    private async Task<IReadOnlyList<ProviderCategory>> GetCategories(string type, CancellationToken cancellationToken)
    {
        var action = type == "itv" ? "get_genres" : "get_categories";
        using var document = await PortalAsync(type, action, [], cancellationToken);
        if (!JsonSupport.TryStalkerCategories(document.RootElement, out var categories, out var structure))
            throw new StalkerPayloadStructureException(action, structure);
        _categoryStructures[type] = $"{type}/{action}: {structure}";
        return categories;
    }

    private async Task<IReadOnlyList<LiveChannel>> GetAllLiveChannelsAsync(CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        if (_liveChannels is not null && now < _liveChannelsExpiresAt) return _liveChannels;

        await _liveCatalogLock.WaitAsync(cancellationToken);
        try
        {
            now = DateTimeOffset.UtcNow;
            if (_liveChannels is not null && now < _liveChannelsExpiresAt) return _liveChannels;
            using var document = await PortalAsync("itv", "get_all_channels", [], cancellationToken);
            var channels = new List<LiveChannel>();
            foreach (var raw in ReadRawItems(document.RootElement))
            {
                var genreId = raw.Text("tv_genre_id");
                if (string.IsNullOrWhiteSpace(genreId)) continue;
                var item = JsonSupport.Item(raw, CatalogType.Live);
                if (item.Id.Length == 0) continue;
                CacheCommand(raw, CatalogType.Live, item.Id);
                channels.Add(new LiveChannel(genreId, item));
            }
            _liveChannels = channels;
            _liveChannelsExpiresAt = now.Add(LiveCatalogCacheDuration);
            return channels;
        }
        finally
        {
            _liveCatalogLock.Release();
        }
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
            if (catalog == CatalogType.Vod) _vodDetails[item.Id] = NormalizeVodDetails(raw, item.Id);
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

    private static JsonElement FindMediaRecord(JsonElement root, CatalogType catalog, string id)
    {
        foreach (var item in ReadRawItems(root))
        {
            var fields = catalog switch
            {
                CatalogType.Vod => new[] { "id", "movie_id", "stream_id" },
                CatalogType.Series => new[] { "id", "movie_id", "series_id" },
                _ => new[] { "id", "stream_id" }
            };
            if (fields.Any(field => string.Equals(item.Text(field), id, StringComparison.Ordinal))) return item;
        }
        return default;
    }

    private static VodDetails NormalizeVodDetails(JsonElement item, string id) => new(
        id,
        item.Text("name", "title") ?? "Untitled",
        item.Text("screenshot_uri", "cover", "logo"),
        item.Text("description", "plot"),
        item.Text("year"),
        item.Text("genres_str", "genre"),
        item.Text("director"),
        item.Text("actors", "cast"),
        item.Text("time", "duration"),
        ParseRating(item.Text("rating")),
        item.Text("container_extension"));

    private IReadOnlyList<SeasonDetails> MapSeriesSeasons(IReadOnlyList<JsonElement> records)
    {
        var episodes = new Dictionary<(int Season, string Id), EpisodeDetails>();
        for (var recordIndex = 0; recordIndex < records.Count; recordIndex++)
        {
            var record = records[recordIndex];
            var defaultSeason = PositiveNumber(record.Text("season", "season_number"), 1);
            if (record.ValueKind == JsonValueKind.Object && record.TryGetProperty("series", out var series))
            {
                ReadSeriesNode(series, defaultSeason, record, episodes);
                continue;
            }

            if (records.Count > 1 || record.Text("episode", "episode_num", "episode_number") is not null)
                AddEpisode(record, defaultSeason, recordIndex + 1, record, episodes);
        }

        return episodes.Values
            .GroupBy(episode => episode.Season ?? 1)
            .OrderBy(group => group.Key)
            .Select(group => new SeasonDetails(group.Key, $"Saison {group.Key}", group.OrderBy(episode => episode.Episode ?? int.MaxValue).ThenBy(episode => episode.Title, StringComparer.Ordinal).ToArray()))
            .ToArray();
    }

    private void ReadSeriesNode(JsonElement node, int defaultSeason, JsonElement parent, Dictionary<(int Season, string Id), EpisodeDetails> episodes)
    {
        if (node.ValueKind == JsonValueKind.Object)
        {
            if (LooksLikeEpisode(node))
            {
                AddEpisode(node, defaultSeason, episodes.Count + 1, parent, episodes);
                return;
            }

            foreach (var property in node.EnumerateObject())
            {
                var season = PositiveNumber(property.Name, defaultSeason);
                ReadSeasonValues(property.Value, season, parent, episodes);
            }
            return;
        }

        ReadSeasonValues(node, defaultSeason, parent, episodes);
    }

    private void ReadSeasonValues(JsonElement values, int season, JsonElement parent, Dictionary<(int Season, string Id), EpisodeDetails> episodes)
    {
        if (values.ValueKind == JsonValueKind.Array)
        {
            var index = 0;
            foreach (var value in values.EnumerateArray())
            {
                index++;
                AddEpisode(value, season, index, parent, episodes);
            }
            return;
        }

        if (values.ValueKind == JsonValueKind.Object && !LooksLikeEpisode(values))
        {
            foreach (var property in values.EnumerateObject())
                AddEpisode(property.Value, season, PositiveNumber(property.Name, episodes.Count + 1), parent, episodes);
            return;
        }

        AddEpisode(values, season, episodes.Count + 1, parent, episodes);
    }

    private void AddEpisode(JsonElement value, int season, int fallbackNumber, JsonElement parent, Dictionary<(int Season, string Id), EpisodeDetails> episodes)
    {
        var record = value.ValueKind == JsonValueKind.Object ? value : default;
        var scalarNumber = value.ValueKind is JsonValueKind.Number or JsonValueKind.String ? value.ToString() : null;
        var number = PositiveNumber(record.Text("episode_num", "episode_number", "episode") ?? scalarNumber, fallbackNumber);
        season = Math.Max(1, PositiveNumber(record.Text("season", "season_number"), season));
        var id = record.Text("id", "episode_id");
        if (string.IsNullOrWhiteSpace(id)) id = $"s{season}e{number}";
        var title = record.Text("name", "title") ?? $"Épisode {number}";
        var extension = record.Text("container_extension") ?? parent.Text("container_extension");
        episodes[(season, id)] = new EpisodeDetails(id, title, season, number, extension);

        var command = record.Text("cmd", "command") ?? parent.Text("cmd", "command");
        if (!string.IsNullOrWhiteSpace(command)) _commands[(CatalogType.Series, id)] = command;
    }

    private static bool LooksLikeEpisode(JsonElement value) => value.ValueKind == JsonValueKind.Object &&
        (value.Text("id", "episode_id", "episode", "episode_num", "episode_number", "cmd", "command") is not null);

    private static int PositiveNumber(string? value, int fallback) => int.TryParse(value, out var number) && number > 0 ? number : Math.Max(1, fallback);

    private static KeyNotFoundException ContentNotFound() => new("Contenu introuvable.");

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
        try { return await SendAsync(type, action, parameters, includeToken: true, _requestProfile!.Value, cancellationToken); }
        catch (StalkerSessionExpiredException)
        {
            await EnsureTokenAsync(cancellationToken);
            return await SendAsync(type, action, parameters, includeToken: true, _requestProfile!.Value, cancellationToken);
        }
    }

    private async Task EnsureTokenAsync(CancellationToken cancellationToken)
    {
        if (_token is not null) return;
        await _handshake.WaitAsync(cancellationToken);
        try
        {
            if (_token is not null) return;
            if (_requestProfile is { } selected)
            {
                _token = await HandshakeAsync(selected, cancellationToken);
                return;
            }

            Exception legacyFailure;
            try
            {
                var token = await HandshakeAsync(RequestProfile.Legacy, cancellationToken);
                _requestProfile = RequestProfile.Legacy;
                _token = token;
                return;
            }
            catch (Exception exception) when (IsFallbackCompatible(exception))
            {
                legacyFailure = exception;
            }

            try
            {
                var token = await HandshakeAsync(RequestProfile.Mag254Compatible, cancellationToken);
                _requestProfile = RequestProfile.Mag254Compatible;
                _token = token;
            }
            catch (Exception magFailure) when (IsFallbackCompatible(magFailure))
            {
                throw StalkerResponseException.HandshakeProfilesFailed(legacyFailure, magFailure);
            }
        }
        finally { _handshake.Release(); }
    }

    private async Task<string> HandshakeAsync(RequestProfile profile, CancellationToken cancellationToken)
    {
        using var document = await SendAsync("stb", "handshake", [("token", string.Empty)], includeToken: false, profile, cancellationToken);
        var candidate = document.RootElement.Unwrap().Text("token");
        if (string.IsNullOrWhiteSpace(candidate)) throw new InvalidDataException("handshake token absent");
        return candidate;
    }

    private static bool IsFallbackCompatible(Exception exception) => exception is StalkerResponseException or InvalidDataException;

    private async Task<JsonDocument> SendAsync(string type, string action, IReadOnlyList<(string Key, string Value)> parameters, bool includeToken, RequestProfile profile, CancellationToken cancellationToken)
    {
        var mac = NormalizeMac(secret.MacAddress);
        var values = new List<(string, string)> { ("type", type), ("action", action), ("JsHttpRequest", "1-xml") };
        values.AddRange(parameters);
        var endpoint = new Uri(provider.ServerUri.GetLeftPart(UriPartial.Authority) + "/" + provider.PortalPath.TrimStart('/'));
        var uri = XtreamProviderClient.BuildUri(endpoint, values);
        using var response = await SendWithRedirectsAsync(uri, mac, includeToken, profile, cancellationToken);
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            _token = null;
            _accountInfo = null;
            if (includeToken) throw new StalkerSessionExpiredException();
            throw StalkerResponseException.For(action, response);
        }
        if (!response.IsSuccessStatusCode) throw StalkerResponseException.For(action, response);
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        try { return await JsonDocument.ParseAsync(stream, new JsonDocumentOptions { MaxDepth = 512 }, cancellationToken); }
        catch (JsonException exception) { throw StalkerResponseException.For(action, response, "JSON incompatible", exception); }
    }

    private async Task<HttpResponseMessage> SendWithRedirectsAsync(Uri initialUri, string mac, bool includeToken, RequestProfile profile, CancellationToken cancellationToken)
    {
        const int maxRedirects = 3;
        var uri = initialUri;
        for (var redirect = 0; ; redirect++)
        {
            var response = await HttpRetry.SendAsync(http, () => CreateRequest(uri, mac, includeToken, profile), cancellationToken);
            if (!IsRedirect(response.StatusCode) || response.Headers.Location is null) return response;
            if (redirect >= maxRedirects)
            {
                response.Dispose();
                throw new HttpRequestException("Stalker endpoint exceeded the redirect limit.");
            }

            var destination = response.Headers.Location.IsAbsoluteUri ? response.Headers.Location : new Uri(uri, response.Headers.Location);
            response.Dispose();
            if (destination.Scheme is not ("http" or "https") || !string.Equals(destination.IdnHost, initialUri.IdnHost, StringComparison.OrdinalIgnoreCase))
                throw new HttpRequestException("Stalker endpoint redirected outside its server.");
            uri = destination;
        }
    }

    private HttpRequestMessage CreateRequest(Uri uri, string mac, bool includeToken, RequestProfile profile)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.TryAddWithoutValidation("Accept", "application/json, text/javascript, */*; q=0.01");
        request.Headers.TryAddWithoutValidation("X-User-Agent", "Model: MAG254; Link: Ethernet");
        if (profile == RequestProfile.Mag254Compatible)
        {
            request.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (QtEmbedded; U; Linux; C) AppleWebKit/533.3 (KHTML, like Gecko) MAG254 stbapp ver: 2 rev: 250 Safari/533.3");
            request.Headers.TryAddWithoutValidation("X-Requested-With", "XMLHttpRequest");
            request.Headers.Referrer = new Uri(provider.ServerUri.GetLeftPart(UriPartial.Authority) + "/c/");
            request.Headers.TryAddWithoutValidation("Cookie", $"mac={mac}; stb_lang=en; timezone=Europe/Paris");
        }
        else
        {
            request.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 MAG254 stbapp");
            request.Headers.TryAddWithoutValidation("Cookie", $"mac={Uri.EscapeDataString(mac)}; stb_lang=fr; timezone={Uri.EscapeDataString("Europe/Paris")}");
        }
        if (includeToken && _token is not null) request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _token);
        return request;
    }

    private static bool IsRedirect(HttpStatusCode status) => status is HttpStatusCode.MovedPermanently or HttpStatusCode.Found or HttpStatusCode.SeeOther or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect;

    private static DateTimeOffset? ParseExpiration(JsonElement source)
    {
        if (source.ValueKind != JsonValueKind.Object) return null;
        foreach (var field in new[] { "expire_billing_date", "tariff_expired_date", "expire_date", "expiration_date", "expires_at", "end_date", "exp_date" })
            if (ParseDate(source.Text(field)) is { } parsed) return parsed;
        return null;
    }

    private DateTimeOffset? ParsePhoneExpiration(JsonElement mainInfo)
    {
        if (mainInfo.ValueKind != JsonValueKind.Object) return null;
        var responseMac = mainInfo.Text("mac");
        string configuredMac;
        try { configuredMac = NormalizeMac(secret.MacAddress); }
        catch (InvalidOperationException) { return null; }
        if (!string.Equals(responseMac?.Trim(), configuredMac, StringComparison.OrdinalIgnoreCase)) return null;

        var phone = mainInfo.Text("phone");
        if (string.IsNullOrWhiteSpace(phone) || !Regex.IsMatch(phone, @"\b(?:19|20|21)\d{2}\b", RegexOptions.CultureInvariant)) return null;
        return ParseDate(phone);
    }

    private static DateTimeOffset? ParseDate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        value = value.Trim();
        if (value is "0000-00-00" or "0000-00-00 00:00:00") return null;
        if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var epoch))
        {
            if (epoch <= 0) return null;
            try { return epoch > 10_000_000_000 ? DateTimeOffset.FromUnixTimeMilliseconds(epoch) : DateTimeOffset.FromUnixTimeSeconds(epoch); }
            catch (ArgumentOutOfRangeException) { return null; }
        }

        // Stalker dates without an explicit offset are interpreted as UTC. This avoids
        // machine-local differences; explicit offsets are respected, then normalized to UTC.
        return DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture,
            DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed)
            ? parsed
            : null;
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
    private enum RequestProfile { Legacy, Mag254Compatible }
    private sealed record LiveChannel(string GenreId, CatalogItem Item);
    private sealed class StalkerSessionExpiredException : Exception { }
    private sealed class StalkerResponseException(string diagnostic, Exception? innerException = null) : Exception(diagnostic, innerException)
    {
        public string Diagnostic { get; } = diagnostic;

        public static StalkerResponseException For(string action, HttpResponseMessage response, string? detail = null, Exception? innerException = null)
        {
            var phase = action switch { "handshake" => "handshake", "get_profile" => "profile", _ => action };
            var contentType = response.Content.Headers.ContentType?.MediaType ?? "type inconnu";
            var suffix = detail is null ? string.Empty : $", {detail}";
            return new StalkerResponseException($"{phase} HTTP {(int)response.StatusCode} ({contentType}){suffix}", innerException);
        }

        public static StalkerResponseException HandshakeProfilesFailed(Exception legacy, Exception mag) =>
            new($"handshake rejected both safe request profiles (Legacy: {SafeReason(legacy)}; MAG254-compatible: {SafeReason(mag)})");

        private static string SafeReason(Exception exception) => exception switch
        {
            StalkerResponseException response => response.Diagnostic,
            InvalidDataException => "token absent",
            _ => "incompatible response"
        };
    }
}

internal sealed class StalkerPayloadStructureException(string action, string structure)
    : Exception($"{action} returned an unsupported Stalker payload structure. Structure: {structure}");
