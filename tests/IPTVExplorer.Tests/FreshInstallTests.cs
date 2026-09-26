using IPTVExplorer.Infrastructure;

namespace IPTVExplorer.Tests;

public sealed class FreshInstallTests
{
    [Fact]
    public async Task FreshInstallHasNoProviders()
    {
        await using var database = await TestDatabase.CreateAsync();
        Assert.Empty(await database.Repository.ListAsync());
        Assert.Empty(Directory.EnumerateFiles(database.Paths.Indexes));
        Assert.Empty(Directory.EnumerateFiles(database.Paths.Secrets));
        await using var connection = database.Connections.Create();
        await connection.OpenAsync();
        foreach (var table in new[] { "providers", "provider_categories", "rebuild_jobs", "playback_history" })
        {
            await using var command = connection.CreateCommand();
            command.CommandText = $"SELECT COUNT(*) FROM {table}";
            Assert.Equal(0L, Convert.ToInt64(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture));
        }
    }

    [Fact]
    public async Task SecretsAreNotStoredInPlainText()
    {
        await using var database = await TestDatabase.CreateAsync();
        var store = new InMemorySecretStore();
        var reference = await store.PutAsync(new("user-demo", "password-demo", "00:00:00:00:00:00"));
        await database.AddProviderAsync(secretReference: reference);
        var storage = await database.ReadRawSqliteStorageAsync();
        Assert.NotEmpty(storage);
        foreach (var printable in storage)
        {
            Assert.DoesNotContain("password-demo", printable, StringComparison.Ordinal);
            Assert.DoesNotContain("00:00:00:00:00:00", printable, StringComparison.Ordinal);
        }
    }
}
