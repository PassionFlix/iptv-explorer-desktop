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

    [Theory]
    [InlineData("https://cdn.invalid/file.jpg?username=secret-user&width=800", "secret-user")]
    [InlineData("https://cdn.invalid/file.jpg?PASSWD=secret-pass", "secret-pass")]
    [InlineData("https://cdn.invalid/file.jpg?access_token=secret-token", "secret-token")]
    [InlineData("https://cdn.invalid/file.jpg?Authorization=secret-auth", "secret-auth")]
    [InlineData("https://cdn.invalid/file.jpg?auth=secret-auth-short", "secret-auth-short")]
    [InlineData("https://cdn.invalid/file.jpg?credential=secret-credential", "secret-credential")]
    [InlineData("https://cdn.invalid/file.jpg?credentials=secret-credentials", "secret-credentials")]
    [InlineData("https://cdn.invalid/file.jpg?mac=secret-mac", "secret-mac")]
    [InlineData("https://cdn.invalid/file.jpg?api_key=secret-api-key", "secret-api-key")]
    [InlineData("https://cdn.invalid/file.jpg?apikey=secret-apikey", "secret-apikey")]
    [InlineData("https://cdn.invalid/file.jpg?signature=secret-signature", "secret-signature")]
    [InlineData("https://cdn.invalid/file.jpg?sig=secret-sig", "secret-sig")]
    [InlineData("https://cdn.invalid/file.jpg?X-Amz-Credential=secret-amz&X-Amz-Date=secret-date", "secret-amz")]
    [InlineData("https://cdn.invalid/file.jpg?X-Goog-Signature=secret-goog", "secret-goog")]
    [InlineData("https://cdn.invalid/file.jpg?access%5Ftoken=secret-encoded-key", "secret-encoded-key")]
    [InlineData("https://cdn.invalid/file.jpg?access%255Ftoken%253Dsecret-double%2526width%253D800", "secret-double")]
    [InlineData("Request failed. Authorization: Bearer secret-bearer", "secret-bearer")]
    public void SensitiveAssignmentsAndSignedUrlsAreRedacted(string input, string secret)
    {
        var redacted = LogRedactor.Redact(input);

        Assert.DoesNotContain(secret, redacted, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("[REDACTED]", redacted, StringComparison.Ordinal);
    }

    [Fact]
    public void PublicUrlWithoutSensitiveParametersRemainsReadable()
    {
        const string value = "https://cdn.invalid/poster.jpg?width=800&height=1200&format=webp";

        Assert.Equal(value, LogRedactor.Redact(value));
    }

    [Fact]
    public async Task StalkerTokenNotPersisted()
    {
        await using var database = await TestDatabase.CreateAsync();
        var provider = await database.AddProviderAsync(ProviderType.Stalker);
        var client = new StalkerProviderClient(provider, new ProviderSecret(MacAddress: "00:00:00:00:00:00"), new HttpClient(new StalkerFixtureHandler()));
        Assert.True((await client.TestConnectionAsync()).Success);
        var storage = await database.ReadRawSqliteStorageAsync();
        Assert.NotEmpty(storage);
        foreach (var printable in storage) Assert.DoesNotContain("session-token-demo", printable, StringComparison.Ordinal);
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
