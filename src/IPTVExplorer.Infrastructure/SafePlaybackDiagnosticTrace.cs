using System.Text.RegularExpressions;
using IPTVExplorer.Core;
using Microsoft.Extensions.Logging;

namespace IPTVExplorer.Infrastructure;

public sealed partial class SafePlaybackDiagnosticTrace : IPlaybackDiagnosticTrace
{
    private readonly ILogger<SafePlaybackDiagnosticTrace> _logger;

    public SafePlaybackDiagnosticTrace(ILogger<SafePlaybackDiagnosticTrace> logger)
        : this(logger, IsOptInEnabled(Environment.GetEnvironmentVariable("IPTVEXPLORER_STALKER_TRACE"))) { }

    internal SafePlaybackDiagnosticTrace(ILogger<SafePlaybackDiagnosticTrace> logger, bool enabled)
    {
        _logger = logger;
        Enabled = enabled;
    }

    public bool Enabled { get; }

    public void Write(string eventName, params PlaybackDiagnosticField[] fields)
    {
        if (!Enabled) return;
        var safeEvent = EventName().IsMatch(eventName) ? eventName : "INVALID TRACE EVENT";
        var safeFields = fields.Select(field => $"{SafeFieldName(field.Name)}={SafeValue(field.Value)}");
        var message = $"{safeEvent} {string.Join(' ', safeFields)}".TrimEnd();
        _logger.LogInformation("{SafePlaybackDiagnostic}", LogRedactor.Redact(message));
    }

    internal static bool IsOptInEnabled(string? value) => string.Equals(value, "1", StringComparison.Ordinal);

    private static string SafeFieldName(string value) => FieldName().IsMatch(value) ? value : "invalid_field";

    private static string SafeValue(string value)
    {
        if (string.IsNullOrEmpty(value)) return "none";
        if (value.Length > 512 || value.Any(char.IsControl) || value.Contains("://", StringComparison.Ordinal) || value.Contains(';'))
            return "[REDACTED]";
        return LogRedactor.Redact(value);
    }

    [GeneratedRegex("^[A-Z0-9 _-]{3,80}$", RegexOptions.CultureInvariant)]
    private static partial Regex EventName();
    [GeneratedRegex("^[a-z][a-z0-9_]{0,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex FieldName();
}
