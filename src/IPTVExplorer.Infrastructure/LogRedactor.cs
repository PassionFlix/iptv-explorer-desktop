using System.Text.RegularExpressions;

namespace IPTVExplorer.Infrastructure;

public static partial class LogRedactor
{
    private static readonly HashSet<string> SensitiveKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "username", "password", "passwd", "token", "accesstoken", "authorization", "auth",
        "credential", "credentials", "mac", "apikey", "signature", "sig"
    };

    [GeneratedRegex("(?i)(?<prefix>^|[?&\\s;,]|%26|%2526)(?<key>(?:[a-z0-9_.\\-]|%[0-9a-f]{2}){2,128}?)(?<separator>%253d|%3d|=)(?<value>.*?)(?=(?:[&#\\s]|%26|%2526)(?:[a-z0-9_.\\-]|%[0-9a-f]{2}){2,128}?(?:%253d|%3d|=)|[\\s#]|$)")]
    private static partial Regex Assignments();
    [GeneratedRegex("(?i)(?<key>username|password|passwd|token|access[_-]?token|authorization|auth|credentials?|mac|api[_-]?key|signature|sig|x-amz-[a-z0-9-]+|x-goog-[a-z0-9-]+)(?<separator>\\s*:\\s*)(?<value>[^\\s,;&]+)")]
    private static partial Regex ColonSecrets();
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
        result = BearerSecret().Replace(result, "$1[REDACTED]");
        result = ColonSecrets().Replace(result, "${key}${separator}[REDACTED]");
        result = Assignments().Replace(result, match => IsSensitiveKey(match.Groups["key"].Value)
            ? match.Groups["prefix"].Value + match.Groups["key"].Value + match.Groups["separator"].Value + "[REDACTED]"
            : match.Value);
        return MacAddress().Replace(result, "[REDACTED]");
    }

    private static bool IsSensitiveKey(string value)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                var decoded = Uri.UnescapeDataString(value);
                if (decoded == value) break;
                value = decoded;
            }
            catch (UriFormatException)
            {
                break;
            }
        }

        var normalized = value.Replace("_", string.Empty, StringComparison.Ordinal)
            .Replace("-", string.Empty, StringComparison.Ordinal)
            .Replace(".", string.Empty, StringComparison.Ordinal);
        return SensitiveKeys.Contains(normalized)
            || normalized.StartsWith("xamz", StringComparison.OrdinalIgnoreCase)
            || normalized.StartsWith("xgoog", StringComparison.OrdinalIgnoreCase);
    }
}
