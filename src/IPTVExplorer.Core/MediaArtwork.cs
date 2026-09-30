using System.Text.RegularExpressions;

namespace IPTVExplorer.Core;

/// <summary>Public card/detail images. Decoration still uses the stricter HomeArtwork policy.</summary>
public static partial class MediaArtwork
{
    public static string? SafeImageUrl(string? value, ProviderSecret? secret = null)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 2048 ||
            !Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") ||
            uri.UserInfo.Length != 0 || uri.Fragment.Length != 0) return null;

        var decoded = uri.AbsoluteUri;
        for (var pass = 0; pass < 3; pass++) decoded = Uri.UnescapeDataString(decoded);
        if (decoded.Contains('%') || decoded.Any(char.IsControl) || SensitivePart().IsMatch(decoded) || MacAddress().IsMatch(decoded)) return null;
        if (secret is not null && new[] { secret.Username, secret.Password, secret.MacAddress, secret.MacAddress?.Replace(":", "").Replace("-", "") }
            .Any(part => !string.IsNullOrWhiteSpace(part) && decoded.Contains(part, StringComparison.OrdinalIgnoreCase))) return null;
        // CDN resizing/format queries and extensionless public image endpoints are allowed.
        return uri.AbsoluteUri;
    }

    [GeneratedRegex(@"(?:^|[^a-z0-9])(?:user(?:name)?|pass(?:word|wd)?|pwd|(?:access[_-]?|refresh[_-]?)?token|auth(?:orization|entication)?|bearer|credentials?|mac|api[_-]?key|key|signature|sig|session(?:[_-]?id)?|jwt|secret|x-amz[^=&#/]*|x-goog[^=&#/]*)(?:[^a-z0-9]|$)|/(?:movie|series|live)/|/(?:player_api|portal)\.php", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SensitivePart();

    [GeneratedRegex(@"(?:[0-9a-f]{2}[:-]){5}[0-9a-f]{2}", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex MacAddress();
}
