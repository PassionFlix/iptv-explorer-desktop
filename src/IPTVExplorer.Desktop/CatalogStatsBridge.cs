using System.Text.Json;
using IPTVExplorer.Core;
using IPTVExplorer.Infrastructure;
using Microsoft.Extensions.Logging;

namespace IPTVExplorer.Desktop;

public sealed class CatalogStatsBridge(
    IProviderRepository providers,
    LocalCatalogStatsRepository stats,
    ILogger<CatalogStatsBridge> logger)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<string?> TryHandleAsync(string message, CancellationToken cancellationToken = default)
    {
        BridgeRequest request;
        try { request = BridgeProtocol.Parse(message); }
        catch (FormatException) { return null; }
        if (request.Method != "catalog.stats") return null;

        try
        {
            var input = request.Params?.Deserialize<ProviderKeyRequest>(Json)
                ?? throw new InvalidOperationException("Fournisseur invalide.");
            if (!ProviderKey.IsValid(input.ProviderKey)) throw new InvalidOperationException("Fournisseur invalide.");
            var provider = await providers.GetAsync(input.ProviderKey, cancellationToken)
                ?? throw new InvalidOperationException("Fournisseur introuvable.");
            var local = await stats.ReadAsync(input.ProviderKey, cancellationToken);
            var completeSnapshot = provider.Type == ProviderType.Xtream && local.SnapshotAvailable;
            var liveComplete = local.SnapshotAvailable;

            var result = new
            {
                providerType = provider.Type.ToString().ToLowerInvariant(),
                snapshotAvailable = local.SnapshotAvailable,
                live = liveComplete ? local.LiveItems : (long?)null,
                vod = completeSnapshot ? local.VodItems : (long?)null,
                series = completeSnapshot ? local.SeriesItems : (long?)null,
                completeSnapshot,
                indexedVod = local.IndexedVodItems,
                indexedSeries = local.IndexedSeriesItems,
                indexStatus = local.IndexStatus
            };
            return BridgeProtocol.Serialize(new BridgeResponse(request.Id, true, result));
        }
        catch (OperationCanceledException)
        {
            return BridgeProtocol.Serialize(new BridgeResponse(request.Id, false, Error: "Opération annulée."));
        }
        catch
        {
            logger.LogWarning("Local catalog stats could not be read.");
            return BridgeProtocol.Serialize(new BridgeResponse(request.Id, false, Error: "Statistiques locales indisponibles."));
        }
    }

    private sealed record ProviderKeyRequest(string ProviderKey);
}
