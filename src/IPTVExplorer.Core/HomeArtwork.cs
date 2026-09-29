using System.Text.RegularExpressions;

namespace IPTVExplorer.Core;

/// <summary>Only public, static artwork is eligible for the index and home decoration.</summary>
public static partial class HomeArtwork
{
    public static string? SafeUrl(string? value, ProviderSecret? secret = null)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 2048 ||
            !Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") ||
            uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0) return null;

        // Reject signed/query-based images altogether; decorating the home never needs credentials.
        var decoded = uri.AbsoluteUri;
        for (var pass = 0; pass < 3; pass++) decoded = Uri.UnescapeDataString(decoded);
        if (decoded.Contains('%') || SensitivePart().IsMatch(decoded) || MacAddress().IsMatch(decoded)) return null;
        var extension = Path.GetExtension(uri.AbsolutePath).ToLowerInvariant();
        if (extension is not (".jpg" or ".jpeg" or ".png" or ".webp" or ".avif" or ".gif")) return null;
        if (secret is not null && new[] { secret.Username, secret.Password, secret.MacAddress, secret.MacAddress?.Replace(":", "").Replace("-", "") }
            .Any(part => !string.IsNullOrWhiteSpace(part) && decoded.Contains(part, StringComparison.OrdinalIgnoreCase))) return null;
        return uri.AbsoluteUri;
    }

    public static string? SelectBackground(string? backdrop, string? poster, ProviderSecret? secret = null) =>
        SafeUrl(backdrop, secret) ?? SafeUrl(poster, secret);

    public static IReadOnlyList<string> Candidates(string? backdrop, string? poster, ProviderSecret? secret = null) =>
        new[] { SelectBackground(backdrop, poster, secret), SafeUrl(poster, secret) }
            .OfType<string>().Distinct(StringComparer.Ordinal).ToArray();

    [GeneratedRegex(@"(?:^|[^a-z0-9])(?:username|password|passwd|token|access_token|auth|authorization|credential|credentials|mac|api_key|apikey|signature)(?:[^a-z0-9]|$)|/(?:movie|series|live)/", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SensitivePart();

    [GeneratedRegex(@"(?:[0-9a-f]{2}[:-]){5}[0-9a-f]{2}", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex MacAddress();
}
