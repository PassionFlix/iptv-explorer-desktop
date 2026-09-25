using IPTVExplorer.Core;
using IPTVExplorer.Infrastructure;
using IPTVExplorer.Providers;
using Microsoft.Data.Sqlite;

namespace IPTVExplorer.Tests;

public sealed class SecurityAndRecoveryTests
{
    [Fact]
    public void OnboardingRejectsInvalidJson() => Assert.Throws<FormatException>(() => BridgeProtocol.Parse("{ definitely-not-json"));

    [Fact]
    public void SensitiveUrlsAreRedacted()
    {
        var value = "https://example.invalid/movie/user-demo/password-demo/42.mp4?token=session-token-demo";
        var redacted = LogRedactor.Redact(value);
        Assert.DoesNotContain("user-demo", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("password-demo", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("session-token-demo", redacted, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StalkerTokenNotPersisted()
    {
        await using var database = await TestDatabase.CreateAsync();
        var provider = await database.AddProviderAsync(ProviderType.Stalker);
        var client = new StalkerProviderClient(provider, new ProviderSecret(MacAddress: "00:00:00:00:00:00"), new HttpClient(new StalkerFixtureHandler()));
        Assert.True((await client.TestConnectionAsync()).Success);
        var printable = System.Text.Encoding.UTF8.GetString(await File.ReadAllBytesAsync(database.Paths.Database));
        Assert.DoesNotContain("session-token-demo", printable, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RebuildJobRecoveryAfterInterruption()
    {
        await using var database = await TestDatabase.CreateAsync(); await database.AddProviderAsync();
        await using (var connection = database.Connections.Create())
        {
            await connection.OpenAsync(); await using var command = connection.CreateCommand();
            command.CommandText = "INSERT INTO rebuild_jobs(provider_key,status,created_at,started_at) VALUES('fixture-provider','running',$now,$now)";
            command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O")); await command.ExecuteNonQueryAsync();
        }
        var jobs = new RebuildJobRepository(database.Connections);
        Assert.Equal(1, await jobs.RecoverInterruptedAsync());
        await using var verify = database.Connections.Create(); await verify.OpenAsync(); await using var select = verify.CreateCommand(); select.CommandText = "SELECT status FROM rebuild_jobs LIMIT 1";
        Assert.Equal("interrupted", Convert.ToString(await select.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture));
    }
}
