using IPTVExplorer.Core;
using IPTVExplorer.Infrastructure;
using IPTVExplorer.Player;

namespace IPTVExplorer.Tests;

public sealed class PlaybackProgressTests
{
    [Fact]
    public void PolicyDistinguishesStartedInProgressAndCompletedPlayback()
    {
        Assert.False(PlaybackProgressPolicy.HasStarted(TimeSpan.FromSeconds(29)));
        Assert.True(PlaybackProgressPolicy.HasStarted(TimeSpan.FromSeconds(30)));
        Assert.False(PlaybackProgressPolicy.IsCompleted(TimeSpan.FromMinutes(80), TimeSpan.FromMinutes(90)));
        Assert.True(PlaybackProgressPolicy.IsCompleted(TimeSpan.FromMinutes(86), TimeSpan.FromMinutes(90)));
        Assert.True(PlaybackProgressPolicy.IsCompleted(TimeSpan.FromMinutes(89), TimeSpan.FromMinutes(90)));
        Assert.False(PlaybackProgressPolicy.IsCompleted(TimeSpan.FromMinutes(3), null));
        Assert.Equal(50, PlaybackProgressPolicy.Percentage(TimeSpan.FromMinutes(45), TimeSpan.FromMinutes(90)));
    }

    [Fact]
    public async Task RepositoryReturnsOnlyCurrentProviderInMostRecentOrder()
    {
        await using var database = await TestDatabase.CreateAsync();
        await database.AddProviderAsync();
        await database.Repository.AddAsync(new ProviderRecord(
            "provider-two",
            ProviderType.Xtream,
            "Second provider",
            new Uri("https://second.example.invalid"),
            "second-secret"));
        var history = new PlaybackHistoryRepository(database.Connections);
        var now = DateTimeOffset.UtcNow;

        Assert.Empty(await history.ListInProgressAsync("fixture-provider", 6));

        await history.UpsertAsync(Progress("fixture-provider", "older", now.AddMinutes(-10)));
        await history.UpsertAsync(Progress("fixture-provider", "newer", now));
        await history.UpsertAsync(Progress("provider-two", "foreign", now.AddMinutes(1)));

        var items = await history.ListInProgressAsync("fixture-provider", 6);

        Assert.Equal(["newer", "older"], items.Select(item => item.MediaId));
        Assert.DoesNotContain(items, item => item.ProviderKey == "provider-two");
        Assert.Null(await history.GetAsync("fixture-provider", CatalogType.Vod, "foreign"));
    }

    [Fact]
    public async Task RepositoryStoresMetadataButRejectsCredentialBearingPosterUrl()
    {
        await using var database = await TestDatabase.CreateAsync();
        await database.AddProviderAsync();
        var history = new PlaybackHistoryRepository(database.Connections);
        var progress = Progress("fixture-provider", "movie-42", DateTimeOffset.UtcNow) with
        {
            Title = "Fixture film",
            PosterUrl = "https://images.example.invalid/poster.jpg?token=private-token",
            Extension = "mkv",
            Position = TimeSpan.FromMinutes(12),
            Duration = TimeSpan.FromMinutes(90)
        };

        await history.UpsertAsync(progress);

        var stored = Assert.IsType<PlaybackProgress>(await history.GetAsync("fixture-provider", CatalogType.Vod, "movie-42"));
        Assert.Equal("Fixture film", stored.Title);
        Assert.Equal("mkv", stored.Extension);
        Assert.Null(stored.PosterUrl);
        Assert.All(await database.ReadRawSqliteStorageAsync(), data => Assert.DoesNotContain("private-token", data, StringComparison.Ordinal));
    }

    [Fact]
    public async Task RecorderPersistsStartedPlaybackAndRemovesCompletedPlayback()
    {
        await using var database = await TestDatabase.CreateAsync();
        await database.AddProviderAsync();
        var history = new PlaybackHistoryRepository(database.Connections);
        await using var recorder = new PlaybackProgressRecorder(history);
        var progress = Progress("fixture-provider", "movie-42", DateTimeOffset.UtcNow) with
        {
            Position = TimeSpan.Zero,
            Duration = TimeSpan.FromMinutes(100)
        };

        await recorder.BeginAsync(progress);
        await recorder.RecordStateAsync(PlayerState.Playing);
        recorder.RecordPosition(TimeSpan.FromSeconds(29), progress.Duration);
        await recorder.FlushAsync();
        Assert.Null(await history.GetAsync("fixture-provider", CatalogType.Vod, "movie-42"));

        recorder.RecordPosition(TimeSpan.FromSeconds(35), progress.Duration);
        await recorder.FlushAsync();
        Assert.Equal(TimeSpan.FromSeconds(35), (await history.GetAsync("fixture-provider", CatalogType.Vod, "movie-42"))?.Position);

        recorder.RecordPosition(TimeSpan.FromMinutes(96), progress.Duration);
        await recorder.RecordStateAsync(PlayerState.Paused);
        await recorder.FlushAsync();
        Assert.Null(await history.GetAsync("fixture-provider", CatalogType.Vod, "movie-42"));
    }

    [Fact]
    public async Task RecentIndexUsesRealDatesAndDescendingOrder()
    {
        await using var database = await TestDatabase.CreateAsync();
        var index = new AtomicSearchIndex(database.Paths);
        var search = new SearchService(database.Paths);
        var oldDate = new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var newDate = new DateTimeOffset(2026, 4, 2, 0, 0, 0, TimeSpan.Zero);
        await index.ReplaceAsync("fixture-provider",
        [
            new("fixture-provider", CatalogType.Vod, "old", "Older film", null, oldDate),
            new("fixture-provider", CatalogType.Series, "new", "New series", null, newDate),
            new("fixture-provider", CatalogType.Vod, "unknown", "Unknown date", null)
        ]);

        var items = (await search.RecentlyAddedAsync("fixture-provider", CatalogType.Series, 12))
            .Concat(await search.RecentlyAddedAsync("fixture-provider", CatalogType.Vod, 12)).ToArray();

        Assert.Equal(["new", "old"], items.Select(item => item.RemoteId));
        Assert.All(items, item => Assert.NotNull(item.AddedAt));
    }

    private static PlaybackProgress Progress(string providerKey, string mediaId, DateTimeOffset updatedAt) => new(
        providerKey,
        CatalogType.Vod,
        mediaId,
        null,
        mediaId,
        null,
        null,
        null,
        "https://images.example.invalid/poster.jpg",
        "mkv",
        TimeSpan.FromMinutes(10),
        TimeSpan.FromMinutes(90),
        updatedAt);
}
