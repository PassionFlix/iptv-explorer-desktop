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
public sealed record AccountInfo(
    bool Authenticated,
    string? Status,
    DateTimeOffset? ExpiresAt,
    int? ActiveConnections = null,
    int? MaxConnections = null,
    IReadOnlyList<string>? AllowedOutputFormats = null);
public sealed record ConnectionTestResult(bool Success, ProviderType? DetectedType, string Message);
public sealed record CatalogItem(string Id, string Title, string? ImageUrl = null, string? Extension = null, JsonElement? Metadata = null, string? Year = null, double? Rating = null, DateTimeOffset? AddedAt = null, string? BackdropUrl = null,
    string? CategoryId = null, string? Plot = null, string? Genre = null, string? Director = null, string? Cast = null, string? Duration = null);
public sealed record CatalogPage<T>(IReadOnlyList<T> Items, int Page, int PageSize, int Total, int TotalPages);
public sealed record MediaRequest(CatalogType Catalog, string MediaId, string? SeriesId = null, string? Extension = null, string? CategoryId = null);
public sealed record ResolvedMedia(Uri Uri, IReadOnlyDictionary<string, string>? Headers = null);
public sealed record LiveCatalogRefreshResult(bool Updated, string State, DateTimeOffset? RefreshedAt);
public sealed record MediaReference(
    string ProviderKey,
    CatalogType MediaType,
    string MediaId,
    string? EpisodeId = null,
    string? Extension = null,
    string? Title = null,
    string? PosterUrl = null,
    string? SeriesTitle = null,
    int? Season = null,
    int? Episode = null,
    string? CategoryId = null);
public sealed record SearchHit(string ProviderKey, CatalogType Catalog, string RemoteId, string Title, string? ImageUrl, DateTimeOffset? AddedAt = null, string? BackdropUrl = null);
public sealed record VodDetails(string Id, string Title, string? Poster, string? Plot, string? Year, string? Genre, string? Director, string? Cast, string? Duration, double? Rating, string? Extension);
public sealed record EpisodeDetails(string Id, string Title, int? Season, int? Episode, string? Extension);
public sealed record SeasonDetails(int Number, string Title, IReadOnlyList<EpisodeDetails> Episodes);
public sealed record SeriesDetails(string Id, string Title, string? Poster, string? Plot, string? Year, string? Genre, string? Director, string? Cast, double? Rating, IReadOnlyList<SeasonDetails> Seasons);
public sealed record ProviderDiagnostic(
    ProviderType Type,
    string Host,
    string Api,
    bool AccountOk,
    int LiveCategories,
    int VodCategories,
    int SeriesCategories,
    long LatencyMs,
    string Message,
    DateTimeOffset CheckedAt,
    DateTimeOffset? ExpiresAt = null,
    string? AccountStatus = null,
    string? IdentityLabel = null,
    string? MaskedIdentity = null,
    string? CredentialState = null,
    int? ActiveConnections = null,
    int? MaxConnections = null,
    IReadOnlyList<string>? AllowedOutputFormats = null);
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

public sealed record PlaybackProgress(
    string ProviderKey,
    CatalogType Catalog,
    string MediaId,
    string? SeriesId,
    string Title,
    string? SeriesTitle,
    int? Season,
    int? Episode,
    string? PosterUrl,
    string? Extension,
    TimeSpan Position,
    TimeSpan? Duration,
    DateTimeOffset UpdatedAt);

public static class PlaybackProgressPolicy
{
    public static readonly TimeSpan MinimumPosition = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan NearEndThreshold = TimeSpan.FromMinutes(2);
    public static readonly TimeSpan NearEndMinimumDuration = TimeSpan.FromMinutes(10);
    public const double CompletionRatio = 0.95;

    public static bool HasStarted(TimeSpan position) => position >= MinimumPosition;

    public static bool IsCompleted(TimeSpan position, TimeSpan? duration)
    {
        if (duration is not { } total || total <= TimeSpan.Zero) return false;
        var clamped = TimeSpan.FromTicks(Math.Clamp(position.Ticks, 0, total.Ticks));
        return clamped.TotalSeconds / total.TotalSeconds >= CompletionRatio ||
            total >= NearEndMinimumDuration && total - clamped <= NearEndThreshold;
    }

    public static int Percentage(TimeSpan position, TimeSpan? duration)
    {
        if (duration is not { } total || total <= TimeSpan.Zero) return 0;
        return (int)Math.Clamp(Math.Round(position.TotalSeconds * 100 / total.TotalSeconds), 0, 100);
    }

    public static bool ShouldList(PlaybackProgress progress) =>
        HasStarted(progress.Position) && !IsCompleted(progress.Position, progress.Duration);
}

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
