using IPTVExplorer.Core;

namespace IPTVExplorer.Providers;

internal static class ProviderDiagnosticSafety
{
    public static string? MeaningfulStatus(string? status)
    {
        var value = status?.Trim();
        return string.IsNullOrWhiteSpace(value) || value.All(char.IsDigit) ? null : value;
    }

    public static (string Label, string Value, string? CredentialState) Identity(ProviderType type, ProviderSecret secret) => type switch
    {
        ProviderType.Xtream => ("Identifiant", MaskUsername(secret.Username), string.IsNullOrEmpty(secret.Password) ? "Absent" : "Configuré"),
        ProviderType.Stalker => ("MAC", MaskMac(secret.MacAddress), null),
        _ => ("Compte", "Non fourni", null)
    };

    private static string MaskUsername(string? username)
    {
        var value = username?.Trim() ?? string.Empty;
        if (value.Length == 0) return "Non fourni";
        if (value.Length == 1) return "••";
        if (value.Length <= 4) return $"{value[0]}••{value[^1]}";
        return $"{value[..2]}••••{value[^2..]}";
    }

    private static string MaskMac(string? mac)
    {
        var parts = (mac ?? string.Empty).Trim().ToUpperInvariant().Split(':');
        return parts.Length == 6 && parts.All(part => part.Length == 2)
            ? $"{parts[0]}:{parts[1]}:{parts[2]}:••:••:••"
            : "Non fournie";
    }
}
