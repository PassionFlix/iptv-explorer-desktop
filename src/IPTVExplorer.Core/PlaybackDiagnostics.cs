using System.Text.RegularExpressions;

namespace IPTVExplorer.Core;

public readonly record struct PlaybackDiagnosticField(string Name, string Value);

public interface IPlaybackDiagnosticTrace
{
    bool Enabled { get; }
    void Write(string eventName, params PlaybackDiagnosticField[] fields);
}

public sealed class NullPlaybackDiagnosticTrace : IPlaybackDiagnosticTrace
{
    public static NullPlaybackDiagnosticTrace Instance { get; } = new();
    private NullPlaybackDiagnosticTrace() { }
    public bool Enabled => false;
    public void Write(string eventName, params PlaybackDiagnosticField[] fields) { }
}

/// <summary>Builds diagnostic fragments without retaining URI or header values.</summary>
public static partial class SafePlaybackDiagnosticData
{
    private static readonly HashSet<string> KnownPathNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "live.php", "movie.php", "series.php", "play.php", "load.php", "portal.php", "index.m3u8", "master.m3u8"
    };
    private static readonly HashSet<string> MediaExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".ts", ".m3u8", ".mp4", ".mkv", ".avi", ".mov", ".webm", ".mpd"
    };
    private static readonly string[] ExpectedCookieNames = ["mac", "stb_lang", "timezone", "token", "play_token"];

    public static string Bool(bool value) => value ? "true" : "false";

    public static string OpaqueId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 180 || value.Any(char.IsControl) || Uri.TryCreate(value, UriKind.Absolute, out _))
            return "<invalid>";
        return value;
    }

    public static string Scheme(Uri? uri) => uri?.Scheme is { Length: > 0 } scheme && SafeName().IsMatch(scheme)
        ? scheme.ToLowerInvariant()
        : "none";

    public static string PathBaseName(Uri? uri)
    {
        if (uri is null) return "none";
        var basename = Path.GetFileName(uri.AbsolutePath.TrimEnd('/'));
        if (string.IsNullOrEmpty(basename) || !SafePathName().IsMatch(basename)) return "<opaque>";
        if (KnownPathNames.Contains(basename)) return basename;
        var extension = Path.GetExtension(basename);
        return MediaExtensions.Contains(extension) ? $"<opaque>{extension.ToLowerInvariant()}" : "<opaque>";
    }

    public static string QueryKeyNames(Uri? uri)
    {
        if (uri is null || string.IsNullOrEmpty(uri.Query)) return "none";
        var names = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = pair.IndexOf('=');
            var raw = separator < 0 ? pair : pair[..separator];
            string decoded;
            try { decoded = Uri.UnescapeDataString(raw.Replace("+", " ", StringComparison.Ordinal)); }
            catch (UriFormatException) { continue; }
            if (SafeQueryName().IsMatch(decoded)) names.Add(decoded);
        }
        return names.Count == 0 ? "none" : string.Join(',', names);
    }

    public static string HeaderNames(IReadOnlyDictionary<string, string>? headers) =>
        NameList(headers?.Keys);

    public static string CookieNames(IReadOnlyDictionary<string, string>? headers) =>
        headers?.Keys.Any(name => string.Equals(name, "Cookie", StringComparison.OrdinalIgnoreCase)) == true
            ? string.Join(',', ExpectedCookieNames)
            : "none";

    public static string NameList(IEnumerable<string>? names)
    {
        if (names is null) return "none";
        var safe = names.Where(name => !string.IsNullOrWhiteSpace(name) && SafeHeaderName().IsMatch(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return safe.Length == 0 ? "none" : string.Join(',', safe);
    }

    [GeneratedRegex("^[A-Za-z][A-Za-z0-9+.-]{0,20}$", RegexOptions.CultureInvariant)]
    private static partial Regex SafeName();
    [GeneratedRegex("^[A-Za-z0-9._-]{1,80}$", RegexOptions.CultureInvariant)]
    private static partial Regex SafePathName();
    [GeneratedRegex("^[A-Za-z][A-Za-z0-9_.-]{0,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex SafeQueryName();
    [GeneratedRegex("^[A-Za-z0-9_-]{1,64}$", RegexOptions.CultureInvariant)]
    private static partial Regex SafeHeaderName();
}
