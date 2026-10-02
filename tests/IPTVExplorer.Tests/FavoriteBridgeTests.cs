using System.Text.Json;
using IPTVExplorer.Desktop;
using IPTVExplorer.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;

namespace IPTVExplorer.Tests;

public sealed class FavoriteBridgeTests
{
    [Fact]
    public async Task SetListAndRemoveFavoritesWithoutProviderNetworkAccess()
    {
        await using var database = await TestDatabase.CreateAsync();
        await database.AddProviderAsync();
        var repository = new FavoriteRepository(database.Connections);
        var bridge = new FavoriteBridge(database.Repository, repository, NullLogger<FavoriteBridge>.Instance);

        var set = await bridge.TryHandleAsync("""{"id":"f1","method":"favorites.set","params":{"providerKey":"fixture-provider","mediaId":"101","mediaType":"live","title":"Channel","categoryId":"10"}}""");
        Assert.True(ReadOk(set));

        var list = await bridge.TryHandleAsync("""{"id":"f2","method":"favorites.list","params":{"providerKey":"fixture-provider"}}""");
        using (var document = JsonDocument.Parse(list!))
        {
            Assert.True(document.RootElement.GetProperty("ok").GetBoolean());
            var result = document.RootElement.GetProperty("result");
            var item = Assert.Single(result.EnumerateArray());
            Assert.Equal("live", item.GetProperty("mediaType").GetString());
            Assert.Equal("101", item.GetProperty("mediaId").GetString());
        }

        var remove = await bridge.TryHandleAsync("""{"id":"f3","method":"favorites.remove","params":{"providerKey":"fixture-provider","mediaId":"101","mediaType":"live"}}""");
        Assert.True(ReadOk(remove));
        Assert.Empty(await repository.ListAsync("fixture-provider"));
    }

    [Fact]
    public async Task NonFavoriteMethodsAreIgnored()
    {
        await using var database = await TestDatabase.CreateAsync();
        var bridge = new FavoriteBridge(database.Repository, new FavoriteRepository(database.Connections), NullLogger<FavoriteBridge>.Instance);
        Assert.Null(await bridge.TryHandleAsync("""{"id":"x","method":"player.open","params":{}}"""));
    }

    [Fact]
    public async Task InvalidFavoriteReferenceReturnsGenericSafeError()
    {
        await using var database = await TestDatabase.CreateAsync();
        await database.AddProviderAsync();
        var bridge = new FavoriteBridge(database.Repository, new FavoriteRepository(database.Connections), NullLogger<FavoriteBridge>.Instance);

        var response = await bridge.TryHandleAsync("""{"id":"f4","method":"favorites.set","params":{"providerKey":"fixture-provider","mediaId":"https://example.invalid/secret","mediaType":"live","title":"Bad"}}""");
        using var document = JsonDocument.Parse(response!);
        Assert.False(document.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal("Impossible de mettre à jour les favoris.", document.RootElement.GetProperty("error").GetString());
        Assert.DoesNotContain("example.invalid", response, StringComparison.OrdinalIgnoreCase);
    }

    private static bool ReadOk(string? response)
    {
        Assert.NotNull(response);
        using var document = JsonDocument.Parse(response);
        return document.RootElement.GetProperty("ok").GetBoolean();
    }
}
