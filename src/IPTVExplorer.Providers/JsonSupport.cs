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
        var result = new List<ProviderCategory>();
        foreach (var item in root.EnumerateArray())
        {
            var id = item.Text("category_id", "id");
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
