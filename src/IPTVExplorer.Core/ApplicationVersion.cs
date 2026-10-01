using System.Reflection;

namespace IPTVExplorer.Core;

public static class ApplicationVersion
{
    public static Version Current { get; } =
        typeof(ApplicationVersion).Assembly.GetName().Version ?? new Version(0, 0, 0);

    public static string Display => Current.ToString(3);

    public static bool IsNewerRelease(string? tag)
    {
        var normalized = tag?.Trim();
        if (string.IsNullOrWhiteSpace(normalized)) return false;
        if (normalized.StartsWith('v')) normalized = normalized[1..];
        return Version.TryParse(normalized, out var candidate) && candidate > Current;
    }
}
