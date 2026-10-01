using IPTVExplorer.Core;

namespace IPTVExplorer.Infrastructure;

public sealed record CatalogRefreshResult(bool Updated, string State, DateTimeOffset? RefreshedAt);

public sealed class CatalogRefreshService(IProviderRepository providers, IRemoteProviderClientFactory remote,
    ISecretStore secrets, CatalogSnapshotRepository snapshots, RebuildJobRepository jobs, TimeProvider clock) : IDisposable
{
    public static readonly TimeSpan StartupCooldown = TimeSpan.FromMinutes(30);
    private readonly object _gate = new();
    private readonly Dictionary<string, Task<CatalogRefreshResult>> _session = new(StringComparer.Ordinal);
    private readonly SingleFlight<string, CatalogRefreshResult> _refreshes = new();
    private readonly CancellationTokenSource _lifetime = new();

    public Task<CatalogRefreshResult> EnsureSessionAsync(string providerKey, CancellationToken token = default)
    {
        Task<CatalogRefreshResult> task;
        lock (_gate)
        {
            if (!_session.TryGetValue(providerKey, out task!))
            {
                task = Task.Run(() => AutomaticAsync(providerKey));
                _session.Add(providerKey, task); // Failures count as the one session attempt too.
            }
        }
        return task.WaitAsync(token);
    }

    public Task<CatalogRefreshResult> RefreshManualAsync(string providerKey, CancellationToken token = default)
    {
        var task = _refreshes.RunAsync(providerKey, () => RefreshAsync(providerKey), _lifetime.Token);
        lock (_gate) _session.TryAdd(providerKey, task);
        return task.WaitAsync(token);
    }

    private async Task<CatalogRefreshResult> AutomaticAsync(string key)
    {
        var provider = await providers.GetAsync(key, _lifetime.Token);
        if (provider is null || !provider.Enabled || provider.Type != ProviderType.Xtream) return new(false, "notApplicable", null);
        var refreshedAt = await snapshots.RefreshedAtAsync(key, _lifetime.Token);
        if (refreshedAt is { } last && clock.GetUtcNow() - last < StartupCooldown) return new(false, "recent", last);
        return await _refreshes.RunAsync(key, () => RefreshAsync(key), _lifetime.Token);
    }

    private async Task<CatalogRefreshResult> RefreshAsync(string key)
    {
        var token = _lifetime.Token;
        var provider = await providers.GetAsync(key, token) ?? throw new KeyNotFoundException("Provider was not found.");
        if (!provider.Enabled || provider.Type != ProviderType.Xtream) throw new InvalidOperationException("Bulk refresh requires an enabled Xtream provider.");
        var previous = await snapshots.RefreshedAtAsync(key, token);
        DateTimeOffset refreshedAt;
        try
        {
            var client = await remote.CreateAsync(provider, token);
            // Deliberately sequential, all-or-nothing. No category_id and no retry, including failures.
            var live = await client.GetAllLiveAsync(token);
            var vod = await client.GetAllVodAsync(token);
            var series = await client.GetAllSeriesAsync(token);
            var secret = await secrets.GetAsync(provider.SecretReference, token);
            refreshedAt = clock.GetUtcNow();
            await snapshots.ReplaceAsync(key, new Dictionary<CatalogType, IReadOnlyList<CatalogItem>>
            {
                [CatalogType.Live] = live, [CatalogType.Vod] = vod, [CatalogType.Series] = series
            }, secret, refreshedAt, token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception) { return new(false, "retained", previous); } // No provider URL/exception is persisted or sent to the UI.
        try { await jobs.QueueAsync(key, token); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception) { return new(true, "updatedIndexPending", refreshedAt); }
        return new(true, "updated", refreshedAt);
    }

    public void Dispose() { _lifetime.Cancel(); _lifetime.Dispose(); }
}
