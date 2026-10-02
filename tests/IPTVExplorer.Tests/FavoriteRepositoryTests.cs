using IPTVExplorer.Core;
using IPTVExplorer.Infrastructure;

namespace IPTVExplorer.Tests;

public sealed class FavoriteRepositoryTests
{
    [Fact]
    public async Task FavoritesAreProviderScopedAndFilterByCatalog()
    {
        await using var database = await TestDatabase.CreateAsync();
        var first = await database.AddProviderAsync();
        var second = first with { Key = "second-provider", Name = "Second" };
        await database.Repository.AddAsync(second);
        var favorites = new FavoriteRepository(database.Connections);

        await favorites.UpsertAsync(new(first.Key, CatalogType.Live, "101", "Live One", CategoryId: "10"));
        await favorites.UpsertAsync(new(first.Key, CatalogType.Vod, "202", "Movie One", Extension: "mkv"));
        await favorites.UpsertAsync(new(second.Key, CatalogType.Series, "303", "Series Two"));

        Assert.Equal(2, (await favorites.ListAsync(first.Key)).Count);
        var live = Assert.Single(await favorites.ListAsync(first.Key, CatalogType.Live));
        Assert.Equal("101", live.MediaId);
        Assert.Equal("10", live.CategoryId);
        Assert.Single(await favorites.ListAsync(second.Key));
    }

    [Fact]
    public async Task UpsertUpdatesMetadataWithoutDuplicatingFavorite()
    {
        await using var database = await TestDatabase.CreateAsync();
        var provider = await database.AddProviderAsync();
        var favorites = new FavoriteRepository(database.Connections);

        await favorites.UpsertAsync(new(provider.Key, CatalogType.Vod, "movie-1", "Old title", Extension: "mp4"));
        var original = Assert.Single(await favorites.ListAsync(provider.Key));
        await Task.Delay(5);
        await favorites.UpsertAsync(new(provider.Key, CatalogType.Vod, "movie-1", "New title", Extension: "mkv"));
        var updated = Assert.Single(await favorites.ListAsync(provider.Key));

        Assert.Equal("New title", updated.Title);
        Assert.Equal("mkv", updated.Extension);
        Assert.Equal(original.CreatedAt, updated.CreatedAt);
        Assert.True(updated.UpdatedAt >= original.UpdatedAt);
    }

    [Fact]
    public async Task UnsafeArtworkAndTextAreSanitizedBeforePersistence()
    {
        await using var database = await TestDatabase.CreateAsync();
        var provider = await database.AddProviderAsync();
        var favorites = new FavoriteRepository(database.Connections);

        await favorites.UpsertAsync(new(
            provider.Key,
            CatalogType.Series,
            "series-1",
            "Series https://credentials.invalid/private",
            "https://images.invalid/poster.jpg?token=secret-demo"));

        var item = Assert.Single(await favorites.ListAsync(provider.Key));
        Assert.Null(item.ImageUrl);
        Assert.DoesNotContain("credentials.invalid", item.Title, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secret-demo", string.Join('|', await database.ReadRawSqliteStorageAsync()), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RemovingAndDeletingProviderClearFavoritesLocally()
    {
        await using var database = await TestDatabase.CreateAsync();
        var provider = await database.AddProviderAsync();
        var favorites = new FavoriteRepository(database.Connections);
        await favorites.UpsertAsync(new(provider.Key, CatalogType.Live, "live-1", "Channel"));
        await favorites.UpsertAsync(new(provider.Key, CatalogType.Vod, "vod-1", "Movie"));

        await favorites.RemoveAsync(provider.Key, CatalogType.Live, "live-1");
        Assert.False(await favorites.ContainsAsync(provider.Key, CatalogType.Live, "live-1"));
        Assert.Single(await favorites.ListAsync(provider.Key));

        await database.Repository.DeleteAsync(provider.Key);
        Assert.Empty(await favorites.ListAsync(provider.Key));
    }

    [Fact]
    public async Task AbsoluteMediaUrlsAreRejected()
    {
        await using var database = await TestDatabase.CreateAsync();
        var provider = await database.AddProviderAsync();
        var favorites = new FavoriteRepository(database.Connections);

        await Assert.ThrowsAsync<ArgumentException>(() => favorites.UpsertAsync(new(
            provider.Key,
            CatalogType.Live,
            "https://example.invalid/live/secret",
            "Bad")));
    }
}
