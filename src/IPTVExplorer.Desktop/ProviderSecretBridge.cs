using System.Text.Json;
using IPTVExplorer.Core;
using IPTVExplorer.Infrastructure;
using Microsoft.Extensions.Logging;

namespace IPTVExplorer.Desktop;

public interface INativeSecretPresenter
{
    Task ShowMacAsync(string providerName, string macAddress, CancellationToken cancellationToken = default);
}

public sealed class ProviderSecretBridge(
    IProviderRepository providers,
    ISecretStore secrets,
    INativeSecretPresenter presenter,
    ILogger<ProviderSecretBridge> logger)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<string?> TryHandleAsync(string message, CancellationToken cancellationToken = default)
    {
        BridgeRequest request;
        try
        {
            request = BridgeProtocol.Parse(message);
        }
        catch (FormatException)
        {
            return null;
        }

        if (request.Method != "providers.showFullMac") return null;

        try
        {
            var input = request.Params?.Deserialize<ProviderKeyRequest>(Json)
                ?? throw new InvalidOperationException("Fournisseur invalide.");
            if (!ProviderKey.IsValid(input.ProviderKey)) throw new InvalidOperationException("Fournisseur invalide.");
            var provider = await providers.GetAsync(input.ProviderKey, cancellationToken)
                ?? throw new InvalidOperationException("Fournisseur introuvable.");
            if (provider.Type != ProviderType.Stalker) throw new InvalidOperationException("Cette action est réservée aux fournisseurs Stalker/MAG.");
            var secret = await secrets.GetAsync(provider.SecretReference, cancellationToken)
                ?? throw new InvalidOperationException("Secret fournisseur introuvable.");
            if (string.IsNullOrWhiteSpace(secret.MacAddress)) throw new InvalidOperationException("MAC non configurée.");

            await presenter.ShowMacAsync(provider.Name, secret.MacAddress, cancellationToken);
            return BridgeProtocol.Serialize(new BridgeResponse(request.Id, true, new { shown = true }));
        }
        catch (OperationCanceledException)
        {
            return BridgeProtocol.Serialize(new BridgeResponse(request.Id, false, Error: "Opération annulée."));
        }
        catch
        {
            logger.LogWarning("Native provider identity action failed without exposing credential data.");
            return BridgeProtocol.Serialize(new BridgeResponse(request.Id, false, Error: "Impossible d’afficher la MAC localement."));
        }
    }

    private sealed record ProviderKeyRequest(string ProviderKey);
}
