using System.Diagnostics;
using IPTVExplorer.Core;

namespace IPTVExplorer.Providers;

public sealed record ProviderUpdateInput(string Name, string ServerUrl, string? Username, string? Password, string? MacAddress);

public sealed class ProviderManagementService(IProviderRepository providers, ISecretStore secrets, IProviderClientFactory clients)
{
    public async Task<ProviderDiagnostic> DiagnoseAsync(string providerKey, CancellationToken cancellationToken = default)
    {
        var provider = await RequiredProvider(providerKey, cancellationToken);
        var client = await clients.CreateAsync(provider, cancellationToken);
        var stopwatch = Stopwatch.StartNew();
        var test = await client.TestConnectionAsync(cancellationToken);
        if (!test.Success) return new ProviderDiagnostic(provider.Type, provider.ServerUri.Host, ApiName(provider), false, 0, 0, 0, stopwatch.ElapsedMilliseconds, test.Message, DateTimeOffset.UtcNow);
        var account = await client.GetAccountInfoAsync(cancellationToken);
        var live = await client.GetLiveCategoriesAsync(cancellationToken);
        var vod = await client.GetVodCategoriesAsync(cancellationToken);
        var series = await client.GetSeriesCategoriesAsync(cancellationToken);
        stopwatch.Stop();
        return new ProviderDiagnostic(provider.Type, provider.ServerUri.Host, ApiName(provider), account.Authenticated, live.Count(c => !c.Technical), vod.Count(c => !c.Technical), series.Count(c => !c.Technical), stopwatch.ElapsedMilliseconds, account.Status ?? (account.Authenticated ? "Active" : "Authentication rejected"), DateTimeOffset.UtcNow);
    }

    public async Task SyncCategoriesAsync(string providerKey, CancellationToken cancellationToken = default)
    {
        var provider = await RequiredProvider(providerKey, cancellationToken);
        var client = await clients.CreateAsync(provider, cancellationToken);
        await providers.SyncCategoriesAsync(providerKey, CatalogType.Live, await client.GetLiveCategoriesAsync(cancellationToken), cancellationToken);
        await providers.SyncCategoriesAsync(providerKey, CatalogType.Vod, await client.GetVodCategoriesAsync(cancellationToken), cancellationToken);
        await providers.SyncCategoriesAsync(providerKey, CatalogType.Series, await client.GetSeriesCategoriesAsync(cancellationToken), cancellationToken);
    }

    public async Task<ProviderRecord> UpdateAsync(string providerKey, ProviderUpdateInput input, CancellationToken cancellationToken = default)
    {
        var existing = await RequiredProvider(providerKey, cancellationToken);
        var currentSecret = await secrets.GetAsync(existing.SecretReference, cancellationToken) ?? throw new InvalidOperationException("Provider credentials are unavailable.");
        var uri = ProviderOnboardingService.NormalizeServerUri(input.ServerUrl);
        var updatedSecret = existing.Type == ProviderType.Xtream
            ? new ProviderSecret(string.IsNullOrWhiteSpace(input.Username) ? currentSecret.Username : input.Username.Trim(), string.IsNullOrEmpty(input.Password) ? currentSecret.Password : input.Password)
            : new ProviderSecret(MacAddress: string.IsNullOrWhiteSpace(input.MacAddress) ? currentSecret.MacAddress : input.MacAddress.Trim().ToUpperInvariant());
        ValidateSecret(existing.Type, updatedSecret);
        var newReference = await secrets.PutAsync(updatedSecret, cancellationToken);
        var updated = existing with { Name = input.Name.Trim(), ServerUri = uri, SecretReference = newReference };
        try
        {
            var client = await clients.CreateAsync(updated, cancellationToken);
            var result = await client.TestConnectionAsync(cancellationToken);
            if (!result.Success) throw new InvalidOperationException("The updated provider could not be authenticated.");
            await providers.UpdateAsync(updated, cancellationToken);
        }
        catch
        {
            await secrets.DeleteAsync(newReference, CancellationToken.None);
            throw;
        }
        await secrets.DeleteAsync(existing.SecretReference, CancellationToken.None);
        return updated;
    }

    public async Task SetEnabledAsync(string providerKey, bool enabled, CancellationToken cancellationToken = default)
    {
        var provider = await RequiredProvider(providerKey, cancellationToken);
        if (enabled)
        {
            var client = await clients.CreateAsync(provider, cancellationToken);
            var result = await client.TestConnectionAsync(cancellationToken);
            if (!result.Success) throw new InvalidOperationException("The provider cannot be enabled because its connection test failed.");
        }
        await providers.SetEnabledAsync(providerKey, enabled, cancellationToken);
    }

    public async Task DeleteAsync(string providerKey, bool removeLocalData, string? indexPath, CancellationToken cancellationToken = default)
    {
        var provider = await RequiredProvider(providerKey, cancellationToken);
        await secrets.DeleteAsync(provider.SecretReference, CancellationToken.None);
        await providers.DeleteAsync(providerKey, cancellationToken);
        if (removeLocalData && indexPath is not null) File.Delete(indexPath);
    }

    private async Task<ProviderRecord> RequiredProvider(string key, CancellationToken cancellationToken) => await providers.GetAsync(key, cancellationToken) ?? throw new KeyNotFoundException("Provider was not found.");
    private static string ApiName(ProviderRecord provider) => provider.Type == ProviderType.Xtream ? "player_api.php" : provider.PortalPath;
    private static void ValidateSecret(ProviderType type, ProviderSecret secret)
    {
        if (type == ProviderType.Xtream && (string.IsNullOrWhiteSpace(secret.Username) || string.IsNullOrEmpty(secret.Password))) throw new ArgumentException("Username and password are required.");
        if (type == ProviderType.Stalker && !ProviderOnboardingService.ValidMac().IsMatch(secret.MacAddress ?? string.Empty)) throw new ArgumentException("A valid MAC is required.");
    }
}
