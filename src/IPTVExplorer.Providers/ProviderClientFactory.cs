using IPTVExplorer.Core;

namespace IPTVExplorer.Providers;

public sealed class ProviderClientFactory(IHttpClientFactory httpClients, ISecretStore secrets) : IProviderClientFactory
{
    private static readonly TimeSpan StalkerClientCacheDuration = TimeSpan.FromMinutes(5);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, StalkerClientCacheEntry> _stalkerClients = new(StringComparer.Ordinal);

    public async Task<IProviderClient> CreateAsync(ProviderRecord provider, CancellationToken cancellationToken = default)
    {
        var secret = await secrets.GetAsync(provider.SecretReference, cancellationToken) ?? throw new InvalidOperationException("Provider credentials are unavailable.");
        return provider.Type switch
        {
            ProviderType.Xtream => new XtreamProviderClient(provider, secret, httpClients.CreateClient("providers")),
            ProviderType.Stalker => StalkerClient(provider, secret),
            _ => throw new NotSupportedException("Provider type is not supported.")
        };
    }

    private StalkerProviderClient StalkerClient(ProviderRecord provider, ProviderSecret secret)
    {
        var signature = new StalkerClientSignature(provider.ServerUri, provider.PortalPath, provider.SecretReference);
        var now = DateTimeOffset.UtcNow;
        if (_stalkerClients.TryGetValue(provider.Key, out var cached) && cached.Signature == signature && now < cached.ExpiresAt) return cached.Client;

        var client = new StalkerProviderClient(provider, secret, httpClients.CreateClient("providers"));
        return _stalkerClients.AddOrUpdate(
            provider.Key,
            _ => new StalkerClientCacheEntry(signature, client, now.Add(StalkerClientCacheDuration)),
            (_, current) => current.Signature == signature && now < current.ExpiresAt ? current : new StalkerClientCacheEntry(signature, client, now.Add(StalkerClientCacheDuration))).Client;
    }

    private sealed record StalkerClientSignature(Uri ServerUri, string PortalPath, string SecretReference);
    private sealed record StalkerClientCacheEntry(StalkerClientSignature Signature, StalkerProviderClient Client, DateTimeOffset ExpiresAt);
}

public static class ProviderHttpRegistration
{
    public static void Configure(HttpClient client)
    {
        client.Timeout = TimeSpan.FromSeconds(30);
        client.DefaultRequestHeaders.AcceptEncoding.ParseAdd("gzip, deflate, br");
        client.DefaultRequestHeaders.UserAgent.ParseAdd("IPTVExplorerDesktop/0.2");
    }
}
