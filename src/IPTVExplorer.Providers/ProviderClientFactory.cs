using IPTVExplorer.Core;

namespace IPTVExplorer.Providers;

public sealed class ProviderClientFactory(IHttpClientFactory httpClients, ISecretStore secrets) : IRemoteProviderClientFactory
{
    private readonly SemaphoreSlim _xtreamRequests = new(1, 1);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, StalkerClientCacheEntry> _stalkerClients = new(StringComparer.Ordinal);

    public async Task<IProviderClient> CreateAsync(ProviderRecord provider, CancellationToken cancellationToken = default)
    {
        var secret = await secrets.GetAsync(provider.SecretReference, cancellationToken) ?? throw new InvalidOperationException("Provider credentials are unavailable.");
        return provider.Type switch
        {
            ProviderType.Xtream => new XtreamProviderClient(provider, secret, httpClients.CreateClient("providers"), _xtreamRequests),
            ProviderType.Stalker => StalkerClient(provider, secret),
            _ => throw new NotSupportedException("Provider type is not supported.")
        };
    }

    private StalkerProviderClient StalkerClient(ProviderRecord provider, ProviderSecret secret)
    {
        // This factory is a singleton: keep one client for the lifetime of a provider configuration
        // so consecutive catalog/detail RPCs share its session-only metadata and command caches.
        var signature = new StalkerClientSignature(provider.ServerUri, provider.PortalPath, provider.SecretReference);
        if (_stalkerClients.TryGetValue(provider.Key, out var cached) && cached.Signature == signature) return cached.Client;

        var client = new StalkerProviderClient(provider, secret, httpClients.CreateClient("providers"));
        return _stalkerClients.AddOrUpdate(
            provider.Key,
            _ => new StalkerClientCacheEntry(signature, client),
            (_, current) => current.Signature == signature ? current : new StalkerClientCacheEntry(signature, client)).Client;
    }

    private sealed record StalkerClientSignature(Uri ServerUri, string PortalPath, string SecretReference);
    private sealed record StalkerClientCacheEntry(StalkerClientSignature Signature, StalkerProviderClient Client);
}

public static class ProviderHttpRegistration
{
    public const string AppUserAgent = "IPTVExplorerDesktop/1.0";
    public const string MediaUserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) IPTVExplorerDesktop/1.0";

    public static void Configure(HttpClient client)
    {
        client.Timeout = TimeSpan.FromSeconds(30);
        client.DefaultRequestHeaders.AcceptEncoding.ParseAdd("gzip, deflate, br");
        client.DefaultRequestHeaders.UserAgent.ParseAdd(AppUserAgent);
    }
}
