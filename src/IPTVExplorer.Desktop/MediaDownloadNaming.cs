using System.IO;

namespace IPTVExplorer.Desktop;

internal static class MediaDownloadNaming
{
    private static readonly HashSet<string> ServerScriptExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        "php", "php3", "php4", "php5", "php7", "php8", "phtml",
        "asp", "aspx", "jsp", "cgi", "pl", "py", "html", "htm", "json", "xml"
    };

    internal static string ResolveExtension(string? requestedExtension, Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);
        return NormalizeExtension(requestedExtension)
            ?? ExtensionFromQuery(uri, "stream")
            ?? NormalizeExtension(Path.GetExtension(uri.AbsolutePath))
            ?? "mp4";
    }

    internal static string? NormalizeExtension(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var extension = value.Trim().TrimStart('.').ToLowerInvariant();
        if (extension.Length is < 1 or > 12 || !extension.All(char.IsLetterOrDigit)) return null;
        return ServerScriptExtensions.Contains(extension) ? null : extension;
    }

    private static string? ExtensionFromQuery(Uri uri, string key)
    {
        if (string.IsNullOrWhiteSpace(uri.Query)) return null;
        foreach (var segment in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = segment.IndexOf('=');
            var rawKey = separator >= 0 ? segment[..separator] : segment;
            if (!string.Equals(SafeUnescape(rawKey), key, StringComparison.OrdinalIgnoreCase)) continue;

            var rawValue = separator >= 0 ? segment[(separator + 1)..] : string.Empty;
            var value = SafeUnescape(rawValue);
            if (Uri.TryCreate(value, UriKind.Absolute, out var nested))
            {
                if (NormalizeExtension(Path.GetExtension(nested.AbsolutePath)) is { } nestedExtension) return nestedExtension;
                continue;
            }

            var end = value.Length;
            var query = value.IndexOf('?');
            if (query >= 0) end = Math.Min(end, query);
            var fragment = value.IndexOf('#');
            if (fragment >= 0) end = Math.Min(end, fragment);
            if (NormalizeExtension(Path.GetExtension(value[..end])) is { } extension) return extension;
        }
        return null;
    }

    private static string SafeUnescape(string value)
    {
        try { return Uri.UnescapeDataString(value); }
        catch (UriFormatException) { return value; }
    }
}
