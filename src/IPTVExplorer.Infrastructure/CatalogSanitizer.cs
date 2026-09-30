using System.Text.RegularExpressions;
using IPTVExplorer.Core;

namespace IPTVExplorer.Infrastructure;

/// <summary>Allowlisted domain data only. Never persist provider JSON, commands or playback URLs.</summary>
public static partial class CatalogSanitizer
{
    public const string Uncategorized = "_uncategorized";

    public static string Id(string value, ProviderSecret? secret)
    {
        if (!Identifier().IsMatch(value) || ContainsSecret(value, secret)) throw new InvalidDataException("Invalid catalog identifier.");
        return value;
    }

    public static string? Text(string? value, ProviderSecret? secret)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var result = value;
        for (var pass = 0; pass < 3; pass++) result = Uri.UnescapeDataString(result);
        foreach (var part in SecretParts(secret)) result = result.Replace(part, "[REDACTED]", StringComparison.OrdinalIgnoreCase);
        return LogRedactor.Redact(Mac().Replace(Url().Replace(result, "[URL]"), "[REDACTED]"));
    }

    public static CatalogItem Item(CatalogItem item, ProviderSecret? secret) => item with
    {
        Id = Id(item.Id, secret), CategoryId = Id(string.IsNullOrWhiteSpace(item.CategoryId) ? Uncategorized : item.CategoryId, secret),
        Title = Text(item.Title, secret) ?? "Untitled", Metadata = null,
        ImageUrl = MediaArtwork.SafeImageUrl(item.ImageUrl, secret), BackdropUrl = HomeArtwork.SafeUrl(item.BackdropUrl, secret),
        Extension = Extension(item.Extension), Year = Text(item.Year, secret), Plot = Text(item.Plot, secret),
        Genre = Text(item.Genre, secret), Director = Text(item.Director, secret), Cast = Text(item.Cast, secret), Duration = Text(item.Duration, secret)
    };

    public static VodDetails Vod(VodDetails detail, ProviderSecret? secret) => detail with
    {
        Id = Id(detail.Id, secret), Title = Text(detail.Title, secret) ?? "Untitled", Poster = MediaArtwork.SafeImageUrl(detail.Poster, secret),
        Plot = Text(detail.Plot, secret), Year = Text(detail.Year, secret), Genre = Text(detail.Genre, secret), Director = Text(detail.Director, secret),
        Cast = Text(detail.Cast, secret), Duration = Text(detail.Duration, secret), Extension = Extension(detail.Extension)
    };

    public static SeriesDetails Series(SeriesDetails detail, ProviderSecret? secret) => detail with
    {
        Id = Id(detail.Id, secret), Title = Text(detail.Title, secret) ?? "Untitled", Poster = MediaArtwork.SafeImageUrl(detail.Poster, secret),
        Plot = Text(detail.Plot, secret), Year = Text(detail.Year, secret), Genre = Text(detail.Genre, secret), Director = Text(detail.Director, secret), Cast = Text(detail.Cast, secret),
        Seasons = detail.Seasons.Select(season => season with
        {
            Title = Text(season.Title, secret) ?? $"Season {season.Number}",
            Episodes = season.Episodes.Select(episode => episode with
            {
                Id = Id(episode.Id, secret), Title = Text(episode.Title, secret) ?? "Episode", Extension = Extension(episode.Extension)
            }).ToArray()
        }).ToArray()
    };

    public static VodDetails FromCatalog(CatalogItem item) => new(item.Id, item.Title, item.ImageUrl, item.Plot, item.Year,
        item.Genre, item.Director, item.Cast, item.Duration, item.Rating, item.Extension);
    public static bool HasVodDetail(CatalogItem item) => !string.IsNullOrWhiteSpace(item.Plot) && !string.IsNullOrWhiteSpace(item.Extension);
    private static string? Extension(string? value) => value is not null && ExtensionPattern().IsMatch(value) ? value : null;
    private static IEnumerable<string> SecretParts(ProviderSecret? secret) => new[] { secret?.Username, secret?.Password, secret?.MacAddress, secret?.MacAddress?.Replace(":", "").Replace("-", "") }.Where(value => !string.IsNullOrWhiteSpace(value)).Cast<string>();
    private static bool ContainsSecret(string value, ProviderSecret? secret) => SecretParts(secret).Any(part => value.Contains(part, StringComparison.OrdinalIgnoreCase));
    [GeneratedRegex(@"^[a-zA-Z0-9_-]{1,180}$")] private static partial Regex Identifier();
    [GeneratedRegex(@"^[a-zA-Z0-9]{1,10}$")] private static partial Regex ExtensionPattern();
    [GeneratedRegex(@"(?i)(?:https?://|file:)[^\s<>]+")]
    private static partial Regex Url();
    [GeneratedRegex(@"(?i)(?:[0-9a-f]{2}[:-]){5}[0-9a-f]{2}")]
    private static partial Regex Mac();
}
