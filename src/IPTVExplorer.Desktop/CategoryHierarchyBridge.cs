using System.Text.Json;
using IPTVExplorer.Core;
using Microsoft.Extensions.Logging;

namespace IPTVExplorer.Desktop;

public sealed class CategoryHierarchyBridge(
    IProviderRepository providers,
    ILogger<CategoryHierarchyBridge> logger)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<string?> TryHandleAsync(string message, CancellationToken cancellationToken = default)
    {
        BridgeRequest request;
        try { request = BridgeProtocol.Parse(message); }
        catch (FormatException) { return null; }
        if (request.Method != "categories.hierarchy") return null;

        try
        {
            var input = request.Params?.Deserialize<Request>(Json)
                ?? throw new InvalidOperationException("Catégories invalides.");
            if (!ProviderKey.IsValid(input.ProviderKey)) throw new InvalidOperationException("Fournisseur invalide.");
            _ = await providers.GetAsync(input.ProviderKey, cancellationToken)
                ?? throw new InvalidOperationException("Fournisseur introuvable.");
            var catalog = ParseCatalog(input.CatalogType);
            var categories = await providers.ListCategoriesAsync(input.ProviderKey, catalog, true, cancellationToken);
            var result = categories.Where(category => !category.Technical).Select(category => new
            {
                id = category.RemoteId,
                name = category.Name,
                parentId = category.ParentRemoteId,
                category.Selected,
                category.Present
            }).ToArray();
            return BridgeProtocol.Serialize(new BridgeResponse(request.Id, true, new { categories = result }));
        }
        catch (OperationCanceledException)
        {
            return BridgeProtocol.Serialize(new BridgeResponse(request.Id, false, Error: "Opération annulée."));
        }
        catch
        {
            logger.LogWarning("Local category hierarchy could not be read.");
            return BridgeProtocol.Serialize(new BridgeResponse(request.Id, false, Error: "Hiérarchie locale indisponible."));
        }
    }

    private static CatalogType ParseCatalog(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "live" => CatalogType.Live,
        "vod" => CatalogType.Vod,
        "series" => CatalogType.Series,
        _ => throw new InvalidOperationException("Type de catalogue invalide.")
    };

    private sealed record Request(string ProviderKey, string CatalogType);
}
