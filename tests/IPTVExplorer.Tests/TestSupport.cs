using IPTVExplorer.Core;
using IPTVExplorer.Infrastructure;
using Microsoft.Data.Sqlite;
using System.Collections.Concurrent;

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

    public async Task<IReadOnlyList<string>> ReadRawSqliteStorageAsync()
    {
        // Disposed pooled connections can retain the SQLite file handle on Windows.
        // Clear only this test database pool before inspecting its raw persisted bytes.
        ClearMainDatabasePool();

        var storage = new List<string>();
        foreach (var path in new[] { Paths.Database, Paths.Database + "-wal", Paths.Database + "-shm", Paths.Database + "-journal" })
        {
            if (!File.Exists(path)) continue;
            storage.Add(System.Text.Encoding.UTF8.GetString(await File.ReadAllBytesAsync(path)));
        }
        return storage;
    }

    public ValueTask DisposeAsync()
    {
        ClearMainDatabasePool();
        ClearIndexPools();
        if (Directory.Exists(Root)) Directory.Delete(Root, true);
        return ValueTask.CompletedTask;
    }

    private void ClearMainDatabasePool()
    {
        using var poolKey = Connections.Create();
        SqliteConnection.ClearPool(poolKey);
    }

    private void ClearIndexPools()
    {
        if (!Directory.Exists(Paths.Indexes)) return;
        foreach (var path in Directory.EnumerateFiles(Paths.Indexes, "*.sqlite", SearchOption.TopDirectoryOnly))
        {
            var builder = new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = true };
            using var poolKey = new SqliteConnection(builder.ConnectionString);
            SqliteConnection.ClearPool(poolKey);
        }
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

internal sealed class RecordingPlaybackDiagnosticTrace(bool enabled = true) : IPlaybackDiagnosticTrace
{
    public bool Enabled { get; } = enabled;
    public ConcurrentQueue<string> Events { get; } = new();

    public void Write(string eventName, params PlaybackDiagnosticField[] fields)
    {
        if (!Enabled) return;
        Events.Enqueue($"{eventName} {string.Join(' ', fields.Select(field => $"{field.Name}={field.Value}"))}".TrimEnd());
    }

    public string Text => string.Join(Environment.NewLine, Events);
}
