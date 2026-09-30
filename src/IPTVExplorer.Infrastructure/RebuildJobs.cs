using IPTVExplorer.Core;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace IPTVExplorer.Infrastructure;

public sealed record RebuildJob(long Id, string ProviderKey, RebuildJobStatus Status, long Current, long Total, string Label);

public sealed class RebuildJobRepository(SqliteConnectionFactory connections)
{
    public async Task<int> RecoverInterruptedAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = connections.Create();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE rebuild_jobs SET status='interrupted',finished_at=$now,progress_label='Interrupted during previous session'
            WHERE status='running'
            """;
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<long> QueueAsync(string providerKey, CancellationToken cancellationToken = default)
    {
        if (!ProviderKey.IsValid(providerKey)) throw new ArgumentException("Invalid provider key.", nameof(providerKey));
        await using var connection = connections.Create();
        await connection.OpenAsync(cancellationToken);
        await using (var active = connection.CreateCommand())
        {
            active.CommandText = "SELECT id FROM rebuild_jobs WHERE provider_key=$key AND status IN ('queued','running') ORDER BY id DESC LIMIT 1";
            active.Parameters.AddWithValue("$key", providerKey);
            var existing = await active.ExecuteScalarAsync(cancellationToken);
            if (existing is not null) return Convert.ToInt64(existing, System.Globalization.CultureInfo.InvariantCulture);
        }
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO rebuild_jobs(provider_key,status,progress_label,created_at) VALUES($key,'queued','Waiting',$now);
            SELECT last_insert_rowid();
            """;
        command.Parameters.AddWithValue("$key", providerKey);
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken), System.Globalization.CultureInfo.InvariantCulture);
    }

    public async Task<RebuildJob?> ClaimNextAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = connections.Create();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(deferred: false);
        long? id;
        await using (var select = connection.CreateCommand())
        {
            select.Transaction = transaction;
            select.CommandText = "SELECT id FROM rebuild_jobs WHERE status='queued' ORDER BY id LIMIT 1";
            var value = await select.ExecuteScalarAsync(cancellationToken);
            id = value is null ? null : Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture);
        }
        if (id is null) { await transaction.CommitAsync(cancellationToken); return null; }
        await using (var claim = connection.CreateCommand())
        {
            claim.Transaction = transaction;
            claim.CommandText = "UPDATE rebuild_jobs SET status='running',started_at=$now,progress_label='Preparing' WHERE id=$id AND status='queued'";
            claim.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O")); claim.Parameters.AddWithValue("$id", id.Value);
            if (await claim.ExecuteNonQueryAsync(cancellationToken) != 1) { await transaction.RollbackAsync(cancellationToken); return null; }
        }
        RebuildJob job;
        await using (var get = connection.CreateCommand())
        {
            get.Transaction = transaction; get.CommandText = "SELECT id,provider_key,status,progress_current,progress_total,progress_label FROM rebuild_jobs WHERE id=$id"; get.Parameters.AddWithValue("$id", id.Value);
            await using var reader = await get.ExecuteReaderAsync(cancellationToken); await reader.ReadAsync(cancellationToken);
            job = new RebuildJob(reader.GetInt64(0), reader.GetString(1), RebuildJobStatus.Running, reader.GetInt64(3), reader.GetInt64(4), reader.GetString(5));
        }
        await transaction.CommitAsync(cancellationToken);
        return job;
    }

    public async Task ReportAsync(long id, long current, long total, string label, long vodItems, long seriesItems, CancellationToken cancellationToken = default)
    {
        await UpdateAsync("UPDATE rebuild_jobs SET progress_current=$current,progress_total=$total,progress_label=$label,vod_items=$vod,series_items=$series WHERE id=$id AND status='running'", id, current, total, label, vodItems, seriesItems, cancellationToken);
    }

    public async Task CompleteAsync(long id, long vodItems, long seriesItems, CancellationToken cancellationToken = default)
    {
        await using var connection = connections.Create(); await connection.OpenAsync(cancellationToken); await using var transaction = connection.BeginTransaction();
        var now = DateTimeOffset.UtcNow.ToString("O");
        string? providerKey;
        await using (var lookup = connection.CreateCommand())
        {
            lookup.Transaction = transaction; lookup.CommandText = "SELECT provider_key FROM rebuild_jobs WHERE id=$id"; lookup.Parameters.AddWithValue("$id", id);
            providerKey = Convert.ToString(await lookup.ExecuteScalarAsync(cancellationToken), System.Globalization.CultureInfo.InvariantCulture);
        }
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "UPDATE rebuild_jobs SET status='completed',finished_at=$now,progress_current=progress_total,progress_label='Completed',vod_items=$vod,series_items=$series WHERE id=$id AND status='running'";
            command.Parameters.AddWithValue("$now", now); command.Parameters.AddWithValue("$vod", vodItems); command.Parameters.AddWithValue("$series", seriesItems); command.Parameters.AddWithValue("$id", id);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        if (providerKey is not null)
        {
            await using var policy = connection.CreateCommand(); policy.Transaction = transaction;
            policy.CommandText = "UPDATE provider_category_policy SET index_dirty=0,index_synced_at=$now,updated_at=$now WHERE provider_key=$key AND catalog_type IN ('vod','series')";
            policy.Parameters.AddWithValue("$now", now); policy.Parameters.AddWithValue("$key", providerKey); await policy.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task FailAsync(long id, string safeError, CancellationToken cancellationToken = default)
    {
        await using var connection = connections.Create(); await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE rebuild_jobs SET status='failed',finished_at=$now,progress_label='Failed',safe_error=$error WHERE id=$id AND status='running'";
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O")); command.Parameters.AddWithValue("$error", LogRedactor.Redact(safeError)); command.Parameters.AddWithValue("$id", id);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task UpdateAsync(string sql, long id, long current, long total, string label, long vodItems, long seriesItems, CancellationToken cancellationToken)
    {
        await using var connection = connections.Create(); await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand(); command.CommandText = sql;
        command.Parameters.AddWithValue("$id", id); command.Parameters.AddWithValue("$current", current); command.Parameters.AddWithValue("$total", total); command.Parameters.AddWithValue("$label", label); command.Parameters.AddWithValue("$vod", vodItems); command.Parameters.AddWithValue("$series", seriesItems);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IndexJobSnapshot?> LatestAsync(string providerKey, CancellationToken cancellationToken = default)
    {
        if (!ProviderKey.IsValid(providerKey)) throw new ArgumentException("Invalid provider key.", nameof(providerKey));
        await using var connection = connections.Create(); await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT id,provider_key,status,progress_current,progress_total,progress_label,vod_items,series_items,safe_error,created_at FROM rebuild_jobs WHERE provider_key=$key ORDER BY id DESC LIMIT 1";
        command.Parameters.AddWithValue("$key", providerKey);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        return new IndexJobSnapshot(reader.GetInt64(0), reader.GetString(1), Enum.Parse<RebuildJobStatus>(reader.GetString(2), true), reader.GetInt64(3), reader.GetInt64(4), reader.GetString(5), reader.GetInt64(6), reader.GetInt64(7), reader.IsDBNull(8) ? null : reader.GetString(8), DateTimeOffset.Parse(reader.GetString(9), System.Globalization.CultureInfo.InvariantCulture));
    }
}

public sealed class IndexRebuildWorker(
    RebuildJobRepository jobs,
    IProviderRepository providers,
    IProviderClientFactory clients,
    ISecretStore secrets,
    AtomicSearchIndex indexes,
    ILogger<IndexRebuildWorker> logger,
    CatalogSnapshotRepository snapshots) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await jobs.RecoverInterruptedAsync(stoppingToken);
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
        while (!stoppingToken.IsCancellationRequested)
        {
            var job = await jobs.ClaimNextAsync(stoppingToken);
            if (job is null) { await timer.WaitForNextTickAsync(stoppingToken); continue; }
            try { await RebuildAsync(job, stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { throw; }
            catch (Exception exception)
            {
                logger.LogError("Index rebuild {JobId} failed: {SafeError}", job.Id, LogRedactor.Redact(exception.Message));
                await jobs.FailAsync(job.Id, exception.Message, stoppingToken);
            }
        }
    }

    private async Task RebuildAsync(RebuildJob job, CancellationToken cancellationToken)
    {
        var provider = await providers.GetAsync(job.ProviderKey, cancellationToken) ?? throw new InvalidOperationException("Provider is no longer available.");
        if (provider.Type == ProviderType.Xtream)
        {
            var generation = await snapshots.GenerationAsync(provider.Key, cancellationToken);
            var local = await snapshots.SearchDocumentsAsync(provider.Key, cancellationToken);
            var localVod = local.Count(item => item.Catalog == CatalogType.Vod);
            var localSeries = local.Count - localVod;
            await jobs.ReportAsync(job.Id, local.Count, local.Count, "Construction depuis le snapshot local", localVod, localSeries, cancellationToken);
            await indexes.ReplaceAsync(provider.Key, local, cancellationToken);
            await jobs.CompleteAsync(job.Id, localVod, localSeries, cancellationToken);
            // A concurrent successful refresh may have coalesced into this running job.
            // Never leave the new snapshot with the old index: queue another LOCAL pass.
            if (generation != await snapshots.GenerationAsync(provider.Key, cancellationToken))
                await jobs.QueueAsync(provider.Key, cancellationToken);
            return;
        }
        // Stalker/MAG keeps its existing category/page protocol; no speculative bulk endpoint.
        var client = await clients.CreateAsync(provider, cancellationToken);
        var secret = await secrets.GetAsync(provider.SecretReference, cancellationToken);
        var documents = new List<SearchHit>();
        long vod = 0, series = 0, categoryNumber = 0;
        var work = new[] { CatalogType.Vod, CatalogType.Series };
        var selected = new Dictionary<CatalogType, IReadOnlyList<ProviderCategory>>();
        foreach (var catalog in work) selected[catalog] = (await providers.ListCategoriesAsync(provider.Key, catalog, false, cancellationToken)).Where(c => c.Selected && !c.Technical).ToArray();
        var totalCategories = selected.Values.Sum(c => c.Count);
        long estimatedTotal = 0;
        foreach (var catalog in work)
        {
            foreach (var category in selected[catalog])
            {
                categoryNumber++;
                await jobs.ReportAsync(job.Id, categoryNumber - 1, totalCategories, $"{catalog} category {categoryNumber}/{totalCategories}", vod, series, cancellationToken);
                var page = 1;
                while (true)
                {
                    var result = catalog == CatalogType.Vod
                        ? await client.GetVodPageAsync(category.RemoteId, page, cancellationToken)
                        : await client.GetSeriesPageAsync(category.RemoteId, page, cancellationToken);
                    if (page == 1) estimatedTotal += result.Total;
                    documents.AddRange(result.Items.Select(item => new SearchHit(provider.Key, catalog, item.Id, item.Title,
                        MediaArtwork.SafeImageUrl(item.ImageUrl, secret), item.AddedAt, HomeArtwork.SafeUrl(item.BackdropUrl, secret))));
                    if (catalog == CatalogType.Vod) vod += result.Items.Count; else series += result.Items.Count;
                    await jobs.ReportAsync(job.Id, documents.Count, Math.Max(documents.Count, estimatedTotal), $"{catalog} — category {categoryNumber}/{totalCategories}", vod, series, cancellationToken);
                    if (result.Items.Count == 0 || page >= result.TotalPages) break;
                    page++;
                }
            }
        }
        await jobs.ReportAsync(job.Id, documents.Count, Math.Max(documents.Count, estimatedTotal), "Validating atomic index", vod, series, cancellationToken);
        await indexes.ReplaceAsync(provider.Key, documents, cancellationToken);
        await jobs.CompleteAsync(job.Id, vod, series, cancellationToken);
    }
}
