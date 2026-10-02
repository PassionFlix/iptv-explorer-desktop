using System.Text.Json;
using IPTVExplorer.Core;
using IPTVExplorer.Infrastructure;
using Microsoft.Extensions.Logging;

namespace IPTVExplorer.Desktop;

public sealed class FavoriteBridge(
    IProviderRepository providers,
    FavoriteRepository favorites,
    ILogger<FavoriteBridge> logger)
{
    private static readonly JsonSerializerOptions Json = CreateJsonOptions();

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

        if (request.Method is not ("favorites.list" or "favorites.set" or "favorites.remove")) return null;

        try
        {
            object result = request.Method switch
            {
                "favorites.list" => await ListAsync(RequireList(request), cancellationToken),
                "favorites.set" => await SetAsync(RequireAction(request), cancellationToken),
                "favorites.remove" => await RemoveAsync(RequireAction(request), cancellationToken),
                _ => throw new InvalidOperationException("Action favori inconnue.")
            };
            return BridgeProtocol.Serialize(new BridgeResponse(request.Id, true, result));
        }
        catch (OperationCanceledException)
        {
            return BridgeProtocol.Serialize(new BridgeResponse(request.Id, false, Error: "Opération annulée."));
        }
        catch (Exception exception)
        {
            logger.LogWarning("Favorite action {Method} failed: {SafeError}", request.Method, LogRedactor.Redact(exception.Message));
            return BridgeProtocol.Serialize(new BridgeResponse(request.Id, false, Error: "Impossible de mettre à jour les favoris."));
        }
    }

    private async Task<object> ListAsync(FavoriteListRequest input, CancellationToken cancellationToken)
    {
        await RequireProviderAsync(input.ProviderKey, cancellationToken);
        var catalog = string.IsNullOrWhiteSpace(input.MediaType) ? (CatalogType?)null : ParseCatalog(input.MediaType);
        var items = await favorites.ListAsync(input.ProviderKey, catalog, 1000, cancellationToken);
        return items.Select(item => new
        {
            providerKey = item.ProviderKey,
            mediaType = Db(item.Catalog),
            mediaId = item.MediaId,
            title = item.Title,
            imageUrl = item.ImageUrl,
            extension = item.Extension,
            categoryId = item.CategoryId,
            createdAt = item.CreatedAt
        }).ToArray();
    }

    private async Task<object> SetAsync(FavoriteActionRequest input, CancellationToken cancellationToken)
    {
        await RequireProviderAsync(input.ProviderKey, cancellationToken);
        var catalog = ParseCatalog(input.MediaType);
        await favorites.UpsertAsync(new FavoriteItem(
            input.ProviderKey,
            catalog,
            input.MediaId,
            input.Title ?? "Contenu",
            input.ImageUrl,
            input.Extension,
            input.CategoryId), cancellationToken);
        return new { favorite = true };
    }

    private async Task<object> RemoveAsync(FavoriteActionRequest input, CancellationToken cancellationToken)
    {
        await RequireProviderAsync(input.ProviderKey, cancellationToken);
        await favorites.RemoveAsync(input.ProviderKey, ParseCatalog(input.MediaType), input.MediaId, cancellationToken);
        return new { favorite = false };
    }

    private async Task RequireProviderAsync(string providerKey, CancellationToken cancellationToken)
    {
        if (!ProviderKey.IsValid(providerKey)) throw new InvalidOperationException("Fournisseur invalide.");
        _ = await providers.GetAsync(providerKey, cancellationToken) ?? throw new InvalidOperationException("Fournisseur introuvable.");
    }

    private static CatalogType ParseCatalog(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "live" => CatalogType.Live,
        "vod" => CatalogType.Vod,
        "series" => CatalogType.Series,
        _ => throw new InvalidOperationException("Type de média invalide.")
    };

    private static string Db(CatalogType catalog) => catalog.ToString().ToLowerInvariant();

    private static FavoriteListRequest RequireList(BridgeRequest request) =>
        request.Params?.Deserialize<FavoriteListRequest>(Json)
        ?? throw new InvalidOperationException("La demande de favoris est invalide.");

    private static FavoriteActionRequest RequireAction(BridgeRequest request) =>
        request.Params?.Deserialize<FavoriteActionRequest>(Json)
        ?? throw new InvalidOperationException("La référence favorite est invalide.");

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }

    private sealed record FavoriteListRequest(string ProviderKey, string? MediaType = null);
    private sealed record FavoriteActionRequest(
        string ProviderKey,
        string MediaId,
        string MediaType,
        string? Title = null,
        string? ImageUrl = null,
        string? Extension = null,
        string? CategoryId = null);
}
