using System.Text.Json;
using System.Text.RegularExpressions;

namespace IPTVExplorer.Core;

public enum ProviderType { Xtream, Stalker }
public enum CatalogType { Live, Vod, Series }
public enum CategoryPolicyMode { All, None, Custom }
public enum MediaTrackType { Audio, Video, Subtitle }
public enum RebuildJobStatus { Queued, Running, Completed, Failed, Interrupted }

public sealed record ProviderRecord(
    string Key,
    ProviderType Type,
    string Name,
    Uri ServerUri,
    string SecretReference,
    string PortalPath = "/portal.php",
    bool Enabled = false,
    string Status = "ready");

public sealed record ProviderSecret(string? Username = null, string? Password = null, string? MacAddress = null);
public sealed record ProviderCategory(string RemoteId, string Name, string NormalizedName, bool Selected = true, bool Present = true, bool NeedsReview = false, DateTimeOffset? LastSeen = null, bool Technical = false);
public sealed record CategoryPolicy(CategoryPolicyMode Mode, IReadOnlySet<string> SelectedIds);
public sealed record AccountInfo(bool Authenticated, string? Status, DateTimeOffset? ExpiresAt);
public sealed record ConnectionTestResult(bool Success, ProviderType? DetectedType, string Message);
public sealed record CatalogItem(string Id, string Title, string? ImageUrl = null, string? Extension = null, JsonElement? Metadata = null, string? Year = null, double? Rating = null);
public sealed record CatalogPage<T>(IReadOnlyList<T> Items, int Page, int PageSize, int Total, int TotalPages);
public sealed record MediaRequest(CatalogType Catalog, string MediaId, string? SeriesId = null, string? Extension = null);
public sealed record ResolvedMedia(Uri Uri, IReadOnlyDictionary<string, string>? Headers = null);
public sealed record MediaReference(string ProviderKey, CatalogType MediaType, string MediaId, string? EpisodeId = null, string? Extension = null);
public sealed record SearchHit(string ProviderKey, CatalogType Catalog, string RemoteId, string Title, string? ImageUrl);
public sealed record VodDetails(string Id, string Title, string? Poster, string? Plot, string? Year, string? Genre, string? Director, string? Cast, string? Duration, double? Rating, string? Extension);
public sealed record EpisodeDetails(string Id, string Title, int? Season, int? Episode, string? Extension);
public sealed record SeasonDetails(int Number, string Title, IReadOnlyList<EpisodeDetails> Episodes);
public sealed record SeriesDetails(string Id, string Title, string? Poster, string? Plot, string? Year, string? Genre, string? Director, string? Cast, double? Rating, IReadOnlyList<SeasonDetails> Seasons);
public sealed record ProviderDiagnostic(ProviderType Type, string Host, string Api, bool AccountOk, int LiveCategories, int VodCategories, int SeriesCategories, long LatencyMs, string Message, DateTimeOffset CheckedAt);
public sealed record CategorySummary(CatalogType Catalog, CategoryPolicyMode Mode, int Selected, int Total, int Missing, bool IndexDirty);
public sealed record IndexJobSnapshot(long Id, string ProviderKey, RebuildJobStatus Status, long Current, long Total, string Label, long VodItems, long SeriesItems, string? Error, DateTimeOffset CreatedAt);
public sealed record AppPreferences(string? ActiveProviderKey = null, string InterfaceLanguage = "auto", string Theme = "system", string AudioLanguage = "auto", string SecondaryAudioLanguage = "auto", string SubtitleLanguage = "auto", bool AutomaticForcedSubtitles = true);
public sealed record MediaTrack(
    long Id,
    MediaTrackType Type,
    string? Language,
    string? Title,
    string? Codec,
    bool Selected,
    bool Default,
    bool Forced,
    int? Channels = null,
    int? SampleRate = null,
    int? Width = null,
    int? Height = null,
    double? Fps = null,
    bool? Hdr = null);

public static partial class ProviderKey
{
    [GeneratedRegex("^[a-z0-9](?:[a-z0-9-]{0,62}[a-z0-9])?$")]
    private static partial Regex ValidPattern();

    public static bool IsValid(string value) => value is not null && ValidPattern().IsMatch(value);

    public static string FromName(string name)
    {
        var normalized = name.Trim().ToLowerInvariant().Normalize(System.Text.NormalizationForm.FormD);
        var chars = normalized.Where(c => System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c) != System.Globalization.UnicodeCategory.NonSpacingMark);
        var key = Regex.Replace(new string(chars.ToArray()), "[^a-z0-9]+", "-").Trim('-');
        if (key.Length > 64) key = key[..64].TrimEnd('-');
        return IsValid(key) ? key : $"provider-{Guid.NewGuid():N}"[..25];
    }
}

public static class CategorySelection
{
    public static bool Includes(CategoryPolicy policy, string remoteId) => policy.Mode switch
    {
        CategoryPolicyMode.All => true,
        CategoryPolicyMode.None => false,
        CategoryPolicyMode.Custom => policy.SelectedIds.Contains(remoteId),
        _ => false
    };
}
