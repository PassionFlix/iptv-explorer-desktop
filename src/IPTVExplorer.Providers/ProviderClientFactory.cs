using IPTVExplorer.Core;

namespace IPTVExplorer.Providers;

public sealed class ProviderClientFactory(IHttpClientFactory httpClients, ISecretStore secrets) : IProviderClientFactory
{
    public async Task<IProviderClient> CreateAsync(ProviderRecord provider, CancellationToken cancellationToken = default)
    {
        var secret = await secrets.GetAsync(provider.SecretReference, cancellationToken) ?? throw new InvalidOperationException("Provider credentials are unavailable.");
        var http = httpClients.CreateClient("providers");
        return provider.Type switch
        {
            ProviderType.Xtream => new XtreamProviderClient(provider, secret, http),
            ProviderType.Stalker => new StalkerProviderClient(provider, secret, http),
            _ => throw new NotSupportedException("Provider type is not supported.")
        };
    }
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
