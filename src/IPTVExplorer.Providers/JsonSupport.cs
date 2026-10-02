using System.Net;
using System.Globalization;
using System.Text.Json;
using IPTVExplorer.Core;

namespace IPTVExplorer.Providers;

internal static class JsonSupport
{
    public static string? Text(this JsonElement element, params string[] names)
    {
        foreach (var name in names)
            if (element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.String or JsonValueKind.Number)
                return value.ToString();
        return null;
    }

    public static JsonElement Unwrap(this JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("js", out var js)) return js;
        return root;
    }

    public static IReadOnlyList<ProviderCategory> Categories(JsonElement root)
    {
        root = root.Unwrap();
        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("data", out var data)) root = data;
        if (root.ValueKind != JsonValueKind.Array) return [];
        return ReadCategories(root.EnumerateArray());
    }

    public static bool TryStalkerCategories(JsonElement root, out IReadOnlyList<ProviderCategory> categories, out string structure)
    {
        structure = DescribeStalkerPayload(root);
        if (!TryStalkerRecords(root, out var records))
        {
            categories = [];
            return false;
        }

        categories = ReadCategories(records);
        return records.Count == 0 || categories.Count > 0;
    }

    private static IReadOnlyList<ProviderCategory> ReadCategories(IEnumerable<JsonElement> items)
    {
        var result = new List<ProviderCategory>();
        foreach (var item in items)
        {
            var id = item.Text("category_id", "id", "genre_id");
            var name = item.Text("category_name", "name", "title");
            if (!string.IsNullOrWhiteSpace(id) && !string.IsNullOrWhiteSpace(name))
            {
                var displayName = WebUtility.HtmlDecode(name);
                var normalized = InfrastructureCompatibleNormalize(displayName);
                var technical = normalized is "ALL" or "ALL CHANNELS" or "ALL MOVIES" or "ALL SERIES" || id is "*" or "0";
                var parent = NormalizeParent(item.Text("parent_id", "category_parent_id", "genre_parent_id", "parent_category_id"), id);
                result.Add(new ProviderCategory(id, displayName, normalized, Technical: technical, ParentRemoteId: parent));
            }
        }
        return result;
    }

    private static string? NormalizeParent(string? value, string id)
    {
        var parent = value?.Trim();
        if (string.IsNullOrEmpty(parent) || parent == "0" || parent == "*" || string.Equals(parent, id, StringComparison.Ordinal)) return null;
        return parent.Length <= 180 ? parent : null;
    }

    private static bool TryStalkerRecords(JsonElement root, out IReadOnlyList<JsonElement> records)
    {
        var payload = root;
        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("js", out var js)) payload = js;
        if (payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty("data", out var data)) payload = data;

        if (payload.ValueKind == JsonValueKind.Array)
        {
            var values = payload.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.Object).Select(item => item.Clone()).ToArray();
            records = values;
            return payload.GetArrayLength() == 0 || values.Length > 0;
        }

        if (payload.ValueKind == JsonValueKind.Object)
        {
            var properties = payload.EnumerateObject().ToArray();
            var values = properties.Where(property => property.Value.ValueKind == JsonValueKind.Object).Select(property => property.Value.Clone()).ToArray();
            records = values;
            return properties.Length == 0 || values.Length > 0;
        }

        records = [];
        return false;
    }

    private static string DescribeStalkerPayload(JsonElement root)
    {
        var details = new List<string> { $"root={Kind(root)}" };
        if (root.ValueKind == JsonValueKind.Object)
        {
            details.Add($"root_fields={Names(root.EnumerateObject().Select(property => property.Name))}");
            if (root.TryGetProperty("js", out var js))
            {
                details.Add($"js={Kind(js)}");
                DescribeCollection(js, "js", details);
            }
            else
            {
                DescribeCollection(root, "root", details);
            }
        }
        else
        {
            DescribeCollection(root, "root", details);
        }
        return string.Join(", ", details);
    }

    private static void DescribeCollection(JsonElement value, string label, List<string> details)
    {
        if (value.ValueKind == JsonValueKind.Object && value.TryGetProperty("data", out var data))
        {
            details.Add($"{label}_data={Kind(data)}");
            value = data;
            label += "_data";
        }

        if (value.ValueKind == JsonValueKind.Array)
        {
            details.Add($"count={value.GetArrayLength()}");
            var first = value.EnumerateArray().FirstOrDefault(item => item.ValueKind == JsonValueKind.Object);
            if (first.ValueKind == JsonValueKind.Object) details.Add($"fields={Names(first.EnumerateObject().Select(property => property.Name))}");
        }
        else if (value.ValueKind == JsonValueKind.Object)
        {
            var properties = value.EnumerateObject().ToArray();
            details.Add($"{label}_fields={Names(properties.Select(property => property.Name))}");
            var first = properties.Select(property => property.Value).FirstOrDefault(item => item.ValueKind == JsonValueKind.Object);
            if (first.ValueKind == JsonValueKind.Object) details.Add($"fields={Names(first.EnumerateObject().Select(property => property.Name))}");
        }
    }

    private static string Kind(JsonElement element) => element.ValueKind.ToString().ToLowerInvariant();
    private static string Names(IEnumerable<string> names) => string.Join('|', names.Take(12).Select(name => new string(name.Where(character => char.IsLetterOrDigit(character) || character is '_' or '-').Take(40).ToArray())).Where(name => name.Length > 0));

    public static CatalogItem Item(JsonElement item, CatalogType catalog, bool useSeriesModifiedDate = false)
    {
        var id = catalog switch
        {
            CatalogType.Live => item.Text("stream_id", "id"),
            CatalogType.Vod => item.Text("stream_id", "movie_id", "id"),
            CatalogType.Series => item.Text("series_id", "id"),
            _ => item.Text("id")
        } ?? string.Empty;
        var title = item.Text("name", "title") ?? "Untitled";
        var image = catalog == CatalogType.Series
            ? new[] { "cover", "movie_image", "stream_icon", "screenshot_uri", "logo" }
                .Select(field => item.Text(field)).FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))
            : item.Text("stream_icon", "cover", "screenshot_uri", "logo");
        var extension = item.Text("container_extension");
        var year = item.Text("year", "releaseDate", "releasedate");
        double? rating = double.TryParse(item.Text("rating", "rating_5based", "kinopoisk_rating"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var parsedRating) ? parsedRating : null;
        var added = new[] { "added", "added_at", "created_at" }
            .Select(field => ParseAddedAt(item.Text(field))).FirstOrDefault(date => date is not null);
        // Xtream exposes last_modified for series. This is provider recency (including updates),
        // never the release year, local indexing time, list position or an inferred creation date.
        if (added is null && catalog == CatalogType.Series && useSeriesModifiedDate)
            added = ParseAddedAt(item.Text("last_modified"));
        return new CatalogItem(id, title, image, extension, item.Clone(), year, rating, added, Backdrop(item),
            item.Text("category_id"), item.Text("plot", "description"), item.Text("genre"), item.Text("director"), item.Text("cast", "actors"), item.Text("duration", "duration_secs"));
    }

    private static string? Backdrop(JsonElement item)
    {
        foreach (var field in new[] { "backdrop_path", "backdrop" })
        {
            if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty(field, out var value)) continue;
            var candidates = value.ValueKind == JsonValueKind.Array ? value.EnumerateArray().ToArray() : [value];
            foreach (var candidate in candidates)
                if (candidate.ValueKind == JsonValueKind.String && HomeArtwork.SafeUrl(candidate.GetString()) is { } safe) return safe;
        }
        return null;
    }

    private static DateTimeOffset? ParseAddedAt(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        DateTimeOffset parsed;
        if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var epoch))
        {
            try { parsed = epoch > 10_000_000_000 ? DateTimeOffset.FromUnixTimeMilliseconds(epoch) : DateTimeOffset.FromUnixTimeSeconds(epoch); }
            catch (ArgumentOutOfRangeException) { return null; }
        }
        else if (!DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out parsed))
        {
            return null;
        }

        var utc = parsed.ToUniversalTime();
        return utc.Year >= 2000 && utc <= DateTimeOffset.UtcNow.AddDays(1) ? utc : null;
    }

    private static string InfrastructureCompatibleNormalize(string value)
    {
        var decomposed = value.Trim().Normalize(System.Text.NormalizationForm.FormD);
        var chars = decomposed.Where(c => System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c) != System.Globalization.UnicodeCategory.NonSpacingMark);
        return new string(chars.ToArray()).Normalize(System.Text.NormalizationForm.FormC).ToUpperInvariant();
    }
}

public sealed class JsonFieldMapping(IReadOnlyDictionary<string, string> paths)
{
    public string? Read(JsonElement source, string logicalField, params string[] fallbacks)
    {
        if (paths.TryGetValue(logicalField, out var path))
        {
            var current = source;
            foreach (var segment in path.Split('.', StringSplitOptions.RemoveEmptyEntries))
                if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(segment, out current)) return null;
            if (current.ValueKind is JsonValueKind.String or JsonValueKind.Number) return current.ToString();
        }
        return source.Text(fallbacks);
    }
}
