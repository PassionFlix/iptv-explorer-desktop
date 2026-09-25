using IPTVExplorer.Core;
using IPTVExplorer.Infrastructure;

namespace IPTVExplorer.Tests;

internal sealed class TestDatabase : IAsyncDisposable
{
    private TestDatabase(string root, AppPaths paths, SqliteConnectionFactory connections)
    {
        Root = root; Paths = paths; Connections = connections; Repository = new ProviderRepository(connections);
    }
    public string Root { get; }
    public AppPaths Paths { get; }
    public SqliteConnectionFactory Connections { get; }
    public ProviderRepository Repository { get; }

    public static async Task<TestDatabase> CreateAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "iptv-explorer-tests", Guid.NewGuid().ToString("N"));
        var paths = new AppPaths(root); var connections = new SqliteConnectionFactory(paths);
        await new DatabaseInitializer(paths, connections).InitializeAsync();
        return new TestDatabase(root, paths, connections);
    }
    public async Task<ProviderRecord> AddProviderAsync(ProviderType type = ProviderType.Xtream, string secretReference = "fixture-secret-reference")
    {
        var provider = new ProviderRecord("fixture-provider", type, "Fixture Provider", new Uri("https://example.invalid"), secretReference, Enabled: false);
        await Repository.AddAsync(provider); return provider;
    }
    public ValueTask DisposeAsync()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(Root)) Directory.Delete(Root, true);
        return ValueTask.CompletedTask;
    }
}

internal sealed class StubHttpClientFactory(HttpMessageHandler? handler = null) : IHttpClientFactory
{
    public HttpClient CreateClient(string name) => handler is null ? new HttpClient(new NeverSendHandler()) : new HttpClient(handler, disposeHandler: false);
    private sealed class NeverSendHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => throw new InvalidOperationException("Network access is forbidden in unit tests.");
    }
}

internal sealed class StalkerFixtureHandler : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var query = request.RequestUri?.Query ?? string.Empty;
        var json = query.Contains("action=handshake", StringComparison.Ordinal) ? "{\"js\":{\"token\":\"session-token-demo\"}}" : "{\"js\":{\"id\":\"fixture-profile\",\"status\":\"active\"}}";
        return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json") });
    }
}
