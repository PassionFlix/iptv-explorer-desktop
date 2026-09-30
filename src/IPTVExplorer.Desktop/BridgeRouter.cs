using System.Collections.Concurrent;
using System.Net.Http;
using System.Text.Json;
using IPTVExplorer.Core;
using IPTVExplorer.Infrastructure;
using IPTVExplorer.Player;
using IPTVExplorer.Providers;
using Microsoft.Extensions.Logging;

namespace IPTVExplorer.Desktop;

public sealed class BridgeRouter(
    IProviderRepository providers,
    IProviderClientFactory clients,
    ProviderOnboardingService onboarding,
    ProviderManagementService management,
    IAppSettingsRepository settings,
    RebuildJobRepository jobs,
    ISearchService search,
    RecentSeriesArtwork artwork,
    IPlaybackHistoryRepository playbackHistory,
    ISecretStore secrets,
    PlaybackCoordinator playback,
    AppPaths paths,
    ILogger<BridgeRouter> logger)
{
    private static readonly JsonSerializerOptions Json = CreateJson();
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _requests = new(StringComparer.Ordinal);

    private static JsonSerializerOptions CreateJson()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }

    public async Task<string> HandleAsync(string message, CancellationToken cancellationToken = default)
    {
        BridgeRequest? request = null;
        CancellationTokenSource? operation = null;
        try
        {
            request = BridgeProtocol.Parse(message);
            if (request.Method == "app.cancel")
            {
                var target = Require<CancelRequest>(request).RequestId;
                if (_requests.TryGetValue(target, out var pending)) pending.Cancel();
                return BridgeProtocol.Serialize(new BridgeResponse(request.Id, true, new { cancelled = true }));
            }
            operation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            if (!_requests.TryAdd(request.Id, operation)) throw new FormatException("Duplicate bridge request id.");
            var result = await DispatchAsync(request, operation.Token);
            return BridgeProtocol.Serialize(new BridgeResponse(request.Id, true, result));
        }
        catch (OperationCanceledException)
        {
            return BridgeProtocol.Serialize(new BridgeResponse(request?.Id ?? "invalid", false, Error: "Operation cancelled."));
        }
        catch (Exception exception)
        {
            logger.LogWarning("Bridge method {Method} failed: {SafeError}", request?.Method ?? "invalid", LogRedactor.Redact(exception.Message));
            return BridgeProtocol.Serialize(new BridgeResponse(request?.Id ?? "invalid", false, Error: UserMessage(exception)));
        }
        finally
        {
            if (request is not null && _requests.TryRemove(request.Id, out var source)) source.Dispose();
            else operation?.Dispose();
        }
    }

    private async Task<object?> DispatchAsync(BridgeRequest request, CancellationToken cancellationToken) => request.Method switch
    {
        "app.getState" => await AppState(cancellationToken),
        "app.setActiveProvider" => await SetActiveProvider(Require<ActiveProviderRequest>(request), cancellationToken),
        "settings.get" => await settings.GetAsync(cancellationToken),
        "settings.save" => await SaveSettings(Require<AppPreferences>(request), cancellationToken),
        "providers.list" => await ProviderList(cancellationToken),
        "providers.addDraft" => onboarding.AddDraft(Require<ProviderDraftInput>(request)),
        "providers.test" => await onboarding.TestAsync(Require<DraftRequest>(request).DraftId, cancellationToken),
        "providers.save" => SafeProvider(await SaveProvider(Require<SaveProviderRequest>(request), cancellationToken)),
        "providers.dashboard" => await Dashboard(Require<ProviderKeyRequest>(request).ProviderKey, cancellationToken),
        "providers.diagnose" => await management.DiagnoseAsync(Require<ProviderKeyRequest>(request).ProviderKey, cancellationToken),
        "providers.syncCategories" => await SyncCategories(Require<ProviderKeyRequest>(request).ProviderKey, cancellationToken),
        "providers.setEnabled" => await SetEnabled(Require<SetEnabledRequest>(request), cancellationToken),
        "providers.update" => SafeProvider(await UpdateProvider(Require<UpdateProviderRequest>(request), cancellationToken)),
        "providers.delete" => await DeleteProvider(Require<DeleteProviderRequest>(request), cancellationToken),
        "categories.list" => await CategoryList(Require<CategoryListRequest>(request), cancellationToken),
        "categories.save" => await CategorySave(Require<CategorySaveRequest>(request), cancellationToken),
        "catalog.live" => await LiveCatalog(Require<CatalogRequest>(request), cancellationToken),
        "catalog.vod.page" => await CatalogPage(Require<PagedCatalogRequest>(request), CatalogType.Vod, cancellationToken),
        "catalog.series.page" => await CatalogPage(Require<PagedCatalogRequest>(request), CatalogType.Series, cancellationToken),
        "catalog.vod.detail" => await VodDetail(Require<DetailRequest>(request), cancellationToken),
        "catalog.series.detail" => await SeriesDetail(Require<DetailRequest>(request), cancellationToken),
        "index.queue" => await QueueIndex(Require<ProviderKeyRequest>(request).ProviderKey, cancellationToken),
        "index.status" => await IndexStatus(Require<ProviderKeyRequest>(request).ProviderKey, cancellationToken),
        "search.query" => await Search(Require<SearchRequest>(request), cancellationToken),
        "home.content" => await HomeContent(Require<ProviderKeyRequest>(request).ProviderKey, cancellationToken),
        "home.seriesArtwork" => await RecoverSeriesArtwork(Require<SeriesArtworkRequest>(request), cancellationToken),
        "player.open" => await playback.OpenAsync(Require<MediaReference>(request), cancellationToken),
        "player.resume" => await ResumePlayback(Require<ResumeRequest>(request), cancellationToken),
        _ => throw new NotSupportedException("Unknown bridge method.")
    };

    private async Task<object> AppState(CancellationToken cancellationToken)
    {
        var list = await providers.ListAsync(cancellationToken);
        var preferences = await settings.GetAsync(cancellationToken);
        var active = list.FirstOrDefault(provider => provider.Enabled && provider.Key == preferences.ActiveProviderKey)?.Key ?? list.FirstOrDefault(provider => provider.Enabled)?.Key;
        if (active != preferences.ActiveProviderKey) await settings.SaveAsync(preferences with { ActiveProviderKey = active }, cancellationToken);
        var version = typeof(BridgeRouter).Assembly.GetName().Version?.ToString(3) ?? "unknown";
        return new { product = "IPTV Explorer Desktop", version, providerCount = list.Count, activeProviderKey = active, providers = list.Select(SafeProvider) };
    }

    private async Task<object> SetActiveProvider(ActiveProviderRequest input, CancellationToken cancellationToken)
    {
        if (input.ProviderKey is not null)
        {
            var provider = await RequiredProvider(input.ProviderKey, cancellationToken);
            if (!provider.Enabled) throw new InvalidOperationException("This provider is disabled.");
        }
        var current = await settings.GetAsync(cancellationToken);
        await settings.SaveAsync(current with { ActiveProviderKey = input.ProviderKey }, cancellationToken);
        return new { activeProviderKey = input.ProviderKey };
    }

    private async Task<object> SaveSettings(AppPreferences preferences, CancellationToken cancellationToken)
    {
        if (preferences.InterfaceLanguage.Length > 20 || preferences.Theme is not ("system" or "dark" or "light") ||
            preferences.AudioLanguage.Length > 40 || preferences.SecondaryAudioLanguage.Length > 40 || preferences.SubtitleLanguage.Length > 40)
            throw new ArgumentException("Invalid application preferences.");
        if (preferences.ActiveProviderKey is not null) _ = await RequiredProvider(preferences.ActiveProviderKey, cancellationToken);
        await settings.SaveAsync(preferences, cancellationToken);
        return new { saved = true };
    }

    private async Task<object> ProviderList(CancellationToken cancellationToken) => (await providers.ListAsync(cancellationToken)).Select(SafeProvider).ToArray();

    private async Task<ProviderRecord> SaveProvider(SaveProviderRequest input, CancellationToken cancellationToken)
    {
        var provider = await onboarding.SaveAsync(input.DraftId, new ProviderSaveOptions(input.Enable, input.Policies), cancellationToken);
        if (!provider.Enabled) return provider;

        var summaries = await providers.GetCategorySummariesAsync(provider.Key, cancellationToken);
        var hasSearchableSelection = summaries.Any(summary =>
            summary.Catalog is CatalogType.Vod or CatalogType.Series && summary.Selected > 0);
        var existingJob = await jobs.LatestAsync(provider.Key, cancellationToken);
        if (hasSearchableSelection && existingJob is null)
        {
            await jobs.QueueAsync(provider.Key, cancellationToken);
        }

        return provider;
    }

    private async Task<object> Dashboard(string providerKey, CancellationToken cancellationToken)
    {
        var provider = await RequiredProvider(providerKey, cancellationToken);
        var summaries = await providers.GetCategorySummariesAsync(providerKey, cancellationToken);
        var job = await jobs.LatestAsync(providerKey, cancellationToken);
        try
        {
            var client = await clients.CreateAsync(provider, cancellationToken);
            var account = await client.GetAccountInfoAsync(cancellationToken);
            return new { provider = SafeProvider(provider), available = account.Authenticated, account = new { account.Status, expiresAt = account.ExpiresAt }, categories = summaries, index = job, indexDirty = IndexDirty(summaries) };
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested && exception is HttpRequestException or JsonException or TaskCanceledException)
        {
            return new { provider = SafeProvider(provider), available = false, account = (object?)null, categories = summaries, index = job, indexDirty = IndexDirty(summaries) };
        }
    }

    private async Task<object> SyncCategories(string providerKey, CancellationToken cancellationToken)
    {
        await management.SyncCategoriesAsync(providerKey, cancellationToken);
        return new { synced = true, summaries = await providers.GetCategorySummariesAsync(providerKey, cancellationToken) };
    }

    private async Task<object> SetEnabled(SetEnabledRequest input, CancellationToken cancellationToken)
    {
        await management.SetEnabledAsync(input.ProviderKey, input.Enabled, cancellationToken);
        return new { saved = true, input.Enabled };
    }

    private async Task<ProviderRecord> UpdateProvider(UpdateProviderRequest input, CancellationToken cancellationToken)
    {
        if (input.Name.Trim().Length is < 1 or > 80) throw new ArgumentException("Provider name is invalid.");
        if (input.Username?.Length > 256 || input.Password?.Length > 1024 || input.MacAddress?.Length > 32) throw new ArgumentException("Provider credentials exceed the supported length.");
        return await management.UpdateAsync(input.ProviderKey, new ProviderUpdateInput(input.Name, input.ServerUrl, input.Username, input.Password, input.MacAddress), cancellationToken);
    }

    private async Task<object> DeleteProvider(DeleteProviderRequest input, CancellationToken cancellationToken)
    {
        if (!input.Confirmed) throw new InvalidOperationException("Explicit confirmation is required.");
        await management.DeleteAsync(input.ProviderKey, input.RemoveLocalData, paths.SearchIndex(input.ProviderKey), cancellationToken);
        var preferences = await settings.GetAsync(cancellationToken);
        if (preferences.ActiveProviderKey == input.ProviderKey) await settings.SaveAsync(preferences with { ActiveProviderKey = null }, cancellationToken);
        return new { deleted = true };
    }

    private async Task<object> CategoryList(CategoryListRequest input, CancellationToken cancellationToken)
    {
        var catalog = ParseCatalog(input.CatalogType);
        var policy = await providers.GetCategoryPolicyAsync(input.ProviderKey, catalog, cancellationToken);
        var categories = await providers.ListCategoriesAsync(input.ProviderKey, catalog, true, cancellationToken);
        return new { mode = policy.Mode.ToString().ToLowerInvariant(), categories = categories.Where(category => !category.Technical).Select(SafeCategory) };
    }

    private async Task<object> CategorySave(CategorySaveRequest input, CancellationToken cancellationToken)
    {
        var catalog = ParseCatalog(input.CatalogType);
        if (!Enum.TryParse<CategoryPolicyMode>(input.Mode, true, out var mode) || !Enum.IsDefined(mode)) throw new ArgumentException("Invalid category policy mode.");
        var existing = await providers.ListCategoriesAsync(input.ProviderKey, catalog, true, cancellationToken);
        var allowed = existing.Select(category => category.RemoteId).ToHashSet(StringComparer.Ordinal);
        if (input.SelectedIds.Length > 5000 || input.SelectedIds.Any(id => !allowed.Contains(id))) throw new ArgumentException("Category selection contains an unknown id.");
        await providers.SaveCategoryPolicyAsync(input.ProviderKey, catalog, new CategoryPolicy(mode, input.SelectedIds.ToHashSet(StringComparer.Ordinal)), cancellationToken);
        return new { saved = true };
    }

    private async Task<object> LiveCatalog(CatalogRequest input, CancellationToken cancellationToken)
    {
        ValidateCategoryId(input.CategoryId); var client = await EnabledClient(input.ProviderKey, cancellationToken);
        return (await client.GetLiveAsync(input.CategoryId, cancellationToken)).Select(SafeItem).ToArray();
    }

    private async Task<object> CatalogPage(PagedCatalogRequest input, CatalogType catalog, CancellationToken cancellationToken)
    {
        ValidateCategoryId(input.CategoryId); var page = Math.Clamp(input.Page, 1, 100000); var client = await EnabledClient(input.ProviderKey, cancellationToken);
        var result = catalog == CatalogType.Vod ? await client.GetVodPageAsync(input.CategoryId, page, cancellationToken) : await client.GetSeriesPageAsync(input.CategoryId, page, cancellationToken);
        return new { items = result.Items.Select(SafeItem), result.Page, result.PageSize, result.Total, result.TotalPages };
    }

    private async Task<object> VodDetail(DetailRequest input, CancellationToken cancellationToken)
    {
        ValidateMediaId(input.MediaId); var client = await EnabledClient(input.ProviderKey, cancellationToken); var detail = await client.GetVodDetailsAsync(input.MediaId, cancellationToken);
        return new { detail.Id, detail.Title, poster = SafeImage(detail.Poster), detail.Plot, detail.Year, detail.Genre, detail.Director, detail.Cast, detail.Duration, detail.Rating, detail.Extension };
    }
    private async Task<object> SeriesDetail(DetailRequest input, CancellationToken cancellationToken)
    {
        ValidateMediaId(input.MediaId); var client = await EnabledClient(input.ProviderKey, cancellationToken); var detail = await client.GetSeriesDetailsAsync(input.MediaId, cancellationToken);
        return new { detail.Id, detail.Title, poster = SafeImage(detail.Poster), detail.Plot, detail.Year, detail.Genre, detail.Director, detail.Cast, detail.Rating, detail.Seasons };
    }
    private async Task<object> QueueIndex(string providerKey, CancellationToken cancellationToken) { _ = await RequiredProvider(providerKey, cancellationToken); return new { jobId = await jobs.QueueAsync(providerKey, cancellationToken) }; }
    private async Task<object> IndexStatus(string providerKey, CancellationToken cancellationToken)
    {
        _ = await RequiredProvider(providerKey, cancellationToken);
        var summaries = await providers.GetCategorySummariesAsync(providerKey, cancellationToken);
        return new { job = await jobs.LatestAsync(providerKey, cancellationToken), dirty = IndexDirty(summaries) };
    }
    private async Task<object> Search(SearchRequest input, CancellationToken cancellationToken)
    {
        if (input.Query.Trim().Length < 3) throw new ArgumentException("Enter at least three characters.");
        var catalog = ParseCatalog(input.CatalogType); return await search.SearchAsync(input.ProviderKey, catalog, input.Query.Trim(), Math.Clamp(input.Page, 1, 100000), Math.Clamp(input.PageSize, 1, 100), cancellationToken);
    }

    private async Task<object> HomeContent(string providerKey, CancellationToken cancellationToken)
    {
        var provider = await RequiredProvider(providerKey, cancellationToken);
        if (!provider.Enabled) throw new InvalidOperationException("This provider is disabled.");
        var preferences = await settings.GetAsync(cancellationToken);
        if (!string.Equals(preferences.ActiveProviderKey, providerKey, StringComparison.Ordinal)) throw new InvalidOperationException("The requested provider is not active.");

        var inProgress = await playbackHistory.ListInProgressAsync(providerKey, 6, cancellationToken);
        var recentFilms = provider.Type == ProviderType.Xtream
            ? await search.RecentlyAddedAsync(providerKey, CatalogType.Vod, 20, cancellationToken)
            : [];
        var recentSeries = provider.Type == ProviderType.Xtream
            ? await search.RecentlyAddedAsync(providerKey, CatalogType.Series, 20, cancellationToken)
            : [];
        var secret = await secrets.GetAsync(provider.SecretReference, cancellationToken);
        recentSeries = await artwork.EnrichAsync(provider, secret, recentSeries, cancellationToken);
        object RecentItem(SearchHit item) => new
        {
            catalog = item.Catalog.ToString().ToLowerInvariant(),
            id = item.RemoteId,
            item.Title,
            imageUrl = MediaArtwork.SafeImageUrl(item.ImageUrl, secret),
            addedAt = item.AddedAt
        };
        var backgrounds = inProgress.Take(1).Select(progress => HomeArtwork.Candidates(null, progress.PosterUrl, secret))
            .Concat(recentFilms.Take(6).Select(item => HomeArtwork.Candidates(item.BackdropUrl, item.ImageUrl, secret)))
            .Concat(recentSeries.Take(6).Select(item => HomeArtwork.Candidates(item.BackdropUrl, item.ImageUrl, secret)))
            .Where(candidates => candidates.Count > 0).DistinctBy(candidates => candidates[0]).ToArray();
        return new
        {
            continueWatching = inProgress.Select(progress => new
            {
                providerKey = progress.ProviderKey,
                catalog = progress.Catalog.ToString().ToLowerInvariant(),
                mediaId = progress.MediaId,
                title = progress.Catalog == CatalogType.Series ? progress.SeriesTitle ?? progress.Title : progress.Title,
                episodeTitle = progress.Catalog == CatalogType.Series ? progress.Title : null,
                progress.Season,
                progress.Episode,
                posterUrl = HomeArtwork.SafeUrl(progress.PosterUrl, secret),
                positionSeconds = progress.Position.TotalSeconds,
                durationSeconds = progress.Duration?.TotalSeconds,
                percentage = PlaybackProgressPolicy.Percentage(progress.Position, progress.Duration),
                updatedAt = progress.UpdatedAt
            }),
            recentlyAddedFilms = recentFilms.Select(RecentItem),
            recentlyAddedSeries = recentSeries.Select(RecentItem),
            backgroundImages = backgrounds,
            recentSupported = provider.Type == ProviderType.Xtream
        };
    }

    private async Task<object> ResumePlayback(ResumeRequest input, CancellationToken cancellationToken)
    {
        var preferences = await settings.GetAsync(cancellationToken);
        if (!string.Equals(preferences.ActiveProviderKey, input.ProviderKey, StringComparison.Ordinal)) throw new InvalidOperationException("Activez le fournisseur associé avant de reprendre ce contenu.");
        return await playback.ResumeAsync(input.ProviderKey, ParseCatalog(input.CatalogType), input.MediaId, cancellationToken);
    }

    private async Task<object> RecoverSeriesArtwork(SeriesArtworkRequest input, CancellationToken cancellationToken)
    {
        ValidateMediaId(input.MediaId);
        var provider = await RequiredProvider(input.ProviderKey, cancellationToken);
        var preferences = await settings.GetAsync(cancellationToken);
        if (!provider.Enabled || provider.Type != ProviderType.Xtream || preferences.ActiveProviderKey != provider.Key)
            throw new InvalidOperationException("The requested provider is not active.");
        var recent = await search.RecentlyAddedAsync(provider.Key, CatalogType.Series, RecentSeriesArtwork.Limit, cancellationToken);
        var secret = await secrets.GetAsync(provider.SecretReference, cancellationToken);
        var result = await artwork.EnrichAsync(provider, secret, recent, cancellationToken,
            new Dictionary<string, string> { [input.MediaId] = input.FailedImageUrl });
        return new { imageUrl = MediaArtwork.SafeImageUrl(result.FirstOrDefault(item => item.RemoteId == input.MediaId)?.ImageUrl, secret) };
    }

    private async Task<IProviderClient> EnabledClient(string providerKey, CancellationToken cancellationToken)
    {
        var provider = await RequiredProvider(providerKey, cancellationToken); if (!provider.Enabled) throw new InvalidOperationException("This provider is disabled."); return await clients.CreateAsync(provider, cancellationToken);
    }
    private async Task<ProviderRecord> RequiredProvider(string key, CancellationToken cancellationToken) { if (!ProviderKey.IsValid(key)) throw new ArgumentException("Invalid provider key."); return await providers.GetAsync(key, cancellationToken) ?? throw new KeyNotFoundException("Provider was not found."); }
    private static object SafeProvider(ProviderRecord provider) => new { key = provider.Key, type = provider.Type.ToString().ToLowerInvariant(), name = provider.Name, serverUrl = provider.ServerUri.ToString().TrimEnd('/'), provider.Enabled, provider.Status };
    private static object SafeCategory(ProviderCategory category) => new { id = category.RemoteId, name = category.Name, category.Selected, category.Present, category.NeedsReview };
    private static object SafeItem(CatalogItem item) => new { id = item.Id, title = item.Title, imageUrl = SafeImage(item.ImageUrl), item.Extension, item.Year, item.Rating };
    private static bool IndexDirty(IEnumerable<CategorySummary> summaries) => summaries.Any(summary => summary.Catalog is CatalogType.Vod or CatalogType.Series && summary.IndexDirty);
    private static string? SafeImage(string? value) => MediaArtwork.SafeImageUrl(value);
    private static CatalogType ParseCatalog(string value) => Enum.TryParse<CatalogType>(value, true, out var catalog) ? catalog : throw new ArgumentException("Invalid catalog type.");
    private static void ValidateCategoryId(string value) { if (string.IsNullOrWhiteSpace(value) || value.Length > 180) throw new ArgumentException("Invalid category id."); }
    private static void ValidateMediaId(string value) { if (string.IsNullOrWhiteSpace(value) || value.Length > 512 || Uri.TryCreate(value, UriKind.Absolute, out _)) throw new ArgumentException("Invalid media id."); }
    private static T Require<T>(BridgeRequest request) => request.Params is { } value ? value.Deserialize<T>(Json) ?? throw new FormatException("Request parameters are invalid.") : throw new FormatException("Request parameters are required.");
    private static string UserMessage(Exception exception) => exception switch
    {
        HttpRequestException => "Unable to reach the provider.",
        JsonException => "The provider returned an invalid response.",
        TimeoutException => "The provider request timed out.",
        TaskCanceledException => "The operation was cancelled or timed out.",
        KeyNotFoundException => exception.Message,
        ArgumentException => exception.Message,
        InvalidOperationException => exception.Message,
        _ => "The operation could not be completed. See the local diagnostic log."
    };

    private sealed record CancelRequest(string RequestId);
    private sealed record DraftRequest(string DraftId);
    private sealed record SaveProviderRequest(string DraftId, bool Enable, Dictionary<string, CategoryPolicyInput>? Policies);
    private sealed record ProviderKeyRequest(string ProviderKey);
    private sealed record ActiveProviderRequest(string? ProviderKey);
    private sealed record SetEnabledRequest(string ProviderKey, bool Enabled);
    private sealed record UpdateProviderRequest(string ProviderKey, string Name, string ServerUrl, string? Username, string? Password, string? MacAddress);
    private sealed record DeleteProviderRequest(string ProviderKey, bool Confirmed, bool RemoveLocalData);
    private sealed record CategoryListRequest(string ProviderKey, string CatalogType);
    private sealed record CategorySaveRequest(string ProviderKey, string CatalogType, string Mode, string[] SelectedIds);
    private sealed record CatalogRequest(string ProviderKey, string CategoryId);
    private sealed record PagedCatalogRequest(string ProviderKey, string CategoryId, int Page);
    private sealed record DetailRequest(string ProviderKey, string MediaId);
    private sealed record SeriesArtworkRequest(string ProviderKey, string MediaId, string FailedImageUrl);
    private sealed record SearchRequest(string ProviderKey, string CatalogType, string Query, int Page, int PageSize);
    private sealed record ResumeRequest(string ProviderKey, string CatalogType, string MediaId);
}
