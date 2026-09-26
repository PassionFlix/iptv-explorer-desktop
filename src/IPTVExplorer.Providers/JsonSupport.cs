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
                var normalized = InfrastructureCompatibleNormalize(name);
                var technical = normalized is "ALL" or "ALL CHANNELS" or "ALL MOVIES" or "ALL SERIES" || id is "*" or "0";
                result.Add(new ProviderCategory(id, name, normalized, Technical: technical));
            }
        }
        return result;
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

    public static CatalogItem Item(JsonElement item, CatalogType catalog)
    {
        var id = catalog switch
        {
            CatalogType.Live => item.Text("stream_id", "id"),
            CatalogType.Vod => item.Text("stream_id", "movie_id", "id"),
            CatalogType.Series => item.Text("series_id", "id"),
            _ => item.Text("id")
        } ?? string.Empty;
        var title = item.Text("name", "title") ?? "Untitled";
        var image = item.Text("stream_icon", "cover", "screenshot_uri", "logo");
        var extension = item.Text("container_extension");
        var year = item.Text("year", "releaseDate", "releasedate");
        double? rating = double.TryParse(item.Text("rating", "rating_5based", "kinopoisk_rating"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var parsedRating) ? parsedRating : null;
        return new CatalogItem(id, title, image, extension, item.Clone(), year, rating);
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
