using System.Text.RegularExpressions;

namespace IPTVExplorer.Infrastructure;

public static partial class LogRedactor
{
    [GeneratedRegex("(?i)(password|token|authorization|mac|username)=([^&\\s]+)")]
    private static partial Regex QuerySecrets();
    [GeneratedRegex("(?i)(bearer\\s+)[A-Za-z0-9._~+/-]+={0,2}")]
    private static partial Regex BearerSecret();
    [GeneratedRegex("(?i)(?:[0-9a-f]{2}:){5}[0-9a-f]{2}")]
    private static partial Regex MacAddress();
    [GeneratedRegex("(?i)(https?://)[^/@\\s]+:[^/@\\s]+@")]
    private static partial Regex UriUserInfo();
    [GeneratedRegex("(?i)(https?://[^/\\s]+/(?:live|movie|series)/)[^/\\s]+/[^/\\s]+/")]
    private static partial Regex MediaPathSecrets();

    public static string Redact(string? value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        var result = UriUserInfo().Replace(value, "$1[REDACTED]@");
        result = MediaPathSecrets().Replace(result, "$1[REDACTED]/[REDACTED]/");
        result = QuerySecrets().Replace(result, "$1=[REDACTED]");
        result = BearerSecret().Replace(result, "$1[REDACTED]");
        return MacAddress().Replace(result, "[REDACTED]");
    }
}
