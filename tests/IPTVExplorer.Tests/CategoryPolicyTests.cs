using IPTVExplorer.Core;

namespace IPTVExplorer.Tests;

public sealed class CategoryPolicyTests
{
    [Fact] public void CategoryPolicyAll() => Assert.True(CategorySelection.Includes(new(CategoryPolicyMode.All, new HashSet<string>()), "news"));
    [Fact] public void CategoryPolicyNone() => Assert.False(CategorySelection.Includes(new(CategoryPolicyMode.None, new HashSet<string> { "news" }), "news"));
    [Fact]
    public void CategoryPolicyCustom()
    {
        var policy = new CategoryPolicy(CategoryPolicyMode.Custom, new HashSet<string> { "movies" });
        Assert.True(CategorySelection.Includes(policy, "movies"));
        Assert.False(CategorySelection.Includes(policy, "sports"));
    }

    [Fact]
    public async Task CategorySelectionIsPersisted()
    {
        await using var database = await TestDatabase.CreateAsync(); await database.AddProviderAsync();
        await database.Repository.SyncCategoriesAsync("fixture-provider", CatalogType.Vod, [new("1", "Drama", ""), new("2", "Comedy", "")]);
        await database.Repository.SaveCategoryPolicyAsync("fixture-provider", CatalogType.Vod, new(CategoryPolicyMode.Custom, new HashSet<string> { "2" }));
        var categories = await database.Repository.ListCategoriesAsync("fixture-provider", CatalogType.Vod);
        Assert.False(categories.Single(c => c.RemoteId == "1").Selected);
        Assert.True(categories.Single(c => c.RemoteId == "2").Selected);
    }
}
