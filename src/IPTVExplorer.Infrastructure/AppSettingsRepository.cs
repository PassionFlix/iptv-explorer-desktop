using System.Text.Json;
using IPTVExplorer.Core;

namespace IPTVExplorer.Infrastructure;

public sealed class AppSettingsRepository(SqliteConnectionFactory connections) : IAppSettingsRepository
{
    private const string Key = "app.preferences";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<AppPreferences> GetAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = connections.Create(); await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand(); command.CommandText = "SELECT value_json FROM app_settings WHERE setting_key=$key"; command.Parameters.AddWithValue("$key", Key);
        var raw = Convert.ToString(await command.ExecuteScalarAsync(cancellationToken), System.Globalization.CultureInfo.InvariantCulture);
        if (string.IsNullOrWhiteSpace(raw)) return new AppPreferences();
        try { return JsonSerializer.Deserialize<AppPreferences>(raw, Json) ?? new AppPreferences(); }
        catch (JsonException) { return new AppPreferences(); }
    }

    public async Task SaveAsync(AppPreferences preferences, CancellationToken cancellationToken = default)
    {
        var json = JsonSerializer.Serialize(preferences, Json);
        await using var connection = connections.Create(); await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO app_settings(setting_key,value_json,updated_at) VALUES($key,$value,$now)
            ON CONFLICT(setting_key) DO UPDATE SET value_json=excluded.value_json,updated_at=excluded.updated_at
            """;
        command.Parameters.AddWithValue("$key", Key); command.Parameters.AddWithValue("$value", json); command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
