using System.Net;
using System.Text;
using IPTVExplorer.Core;
using IPTVExplorer.Infrastructure;
using IPTVExplorer.Providers;

namespace IPTVExplorer.Tests;

public sealed class CategoryHierarchyTests
{
    [Fact]
    public async Task ProviderParserUsesOnlyExplicitParentFields()
    {
        using var http = new HttpClient(new CategoryHandler());
        var provider = new ProviderRecord("fixture", ProviderType.Xtream, "Fixture", new Uri("https://example.invalid"), "secret", Enabled: true);
        var client = new XtreamProviderClient(provider, new ProviderSecret("user", "pass"), http);

        var categories = await client.GetLiveCategoriesAsync();

        Assert.Null(categories.Single(category => category.RemoteId == "10").ParentRemoteId);
        Assert.Equal("10", categories.Single(category => category.RemoteId == "11").ParentRemoteId);
        Assert.Equal("10", categories.Single(category => category.RemoteId == "12").ParentRemoteId);
        Assert.Null(categories.Single(category => category.RemoteId == "13").ParentRemoteId);
    }

    [Fact]
    public async Task HierarchyWrapperPersistsAndReturnsExplicitRelations()
    {
        await using var database = await TestDatabase.CreateAsync();
        var provider = await database.AddProviderAsync();
        var hierarchy = new CategoryHierarchyRepository(database.Connections);
        IProviderRepository repository = new HierarchyProviderRepository(database.Repository, hierarchy);
        var categories = new ProviderCategory[]
        {
            new("10", "Parent", "PARENT"),
            new("11", "Child", "CHILD", ParentRemoteId: "10"),
            new("12", "Looks nested", "LOOKS NESTED")
        };

        await repository.SyncCategoriesAsync(provider.Key, CatalogType.Live, categories);
        var stored = await repository.ListCategoriesAsync(provider.Key, CatalogType.Live);

        Assert.Equal("10", stored.Single(category => category.RemoteId == "11").ParentRemoteId);
        Assert.Null(stored.Single(category => category.RemoteId == "12").ParentRemoteId);
        var raw = await hierarchy.ReadAsync(provider.Key, CatalogType.Live);
        Assert.Single(raw);
        Assert.Equal("10", raw["11"]);
    }

    [Fact]
    public async Task ResyncRemovesRelationsProviderNoLongerReports()
    {
        await using var database = await TestDatabase.CreateAsync();
        var provider = await database.AddProviderAsync();
        var hierarchy = new CategoryHierarchyRepository(database.Connections);
        IProviderRepository repository = new HierarchyProviderRepository(database.Repository, hierarchy);

        await repository.SyncCategoriesAsync(provider.Key, CatalogType.Series,
            [new ProviderCategory("1", "Parent", "PARENT"), new ProviderCategory("2", "Child", "CHILD", ParentRemoteId: "1")]);
        await repository.SyncCategoriesAsync(provider.Key, CatalogType.Series,
            [new ProviderCategory("1", "Parent", "PARENT"), new ProviderCategory("2", "Child", "CHILD")]);

        Assert.Empty(await hierarchy.ReadAsync(provider.Key, CatalogType.Series));
        Assert.Null((await repository.ListCategoriesAsync(provider.Key, CatalogType.Series)).Single(category => category.RemoteId == "2").ParentRemoteId);
    }

    private sealed class CategoryHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var json = """
                [
                  {"category_id":"10","category_name":"Parent","parent_id":"0"},
                  {"category_id":"11","category_name":"Child","parent_id":"10"},
                  {"category_id":"12","category_name":"Alternate child","category_parent_id":"10"},
                  {"category_id":"13","category_name":"### Looks nested"}
                ]
                """;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            });
        }
    }
}
