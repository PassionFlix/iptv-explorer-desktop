using IPTVExplorer.Core;

namespace IPTVExplorer.Player;

public sealed class PlaybackProgressRecorder : IAsyncDisposable
{
    public static readonly TimeSpan PeriodicPositionInterval = TimeSpan.FromSeconds(10);

    private readonly IPlaybackHistoryRepository _history;
    private readonly TimeProvider _clock;
    private readonly object _stateLock = new();
    private readonly object _queueLock = new();
    private readonly SemaphoreSlim _switchGate = new(1, 1);
    private PlaybackProgress? _current;
    private TimeSpan _lastQueuedPosition;
    private DateTimeOffset _lastQueuedAt;
    private bool _active;
    private Task _writeTail = Task.CompletedTask;

    public PlaybackProgressRecorder(IPlaybackHistoryRepository history, TimeProvider? clock = null)
    {
        _history = history;
        _clock = clock ?? TimeProvider.System;
    }

    public async Task BeginAsync(PlaybackProgress? progress, CancellationToken cancellationToken = default)
    {
        await _switchGate.WaitAsync(cancellationToken);
        try
        {
            PlaybackProgress? previous;
            lock (_stateLock)
            {
                previous = SnapshotLocked();
                _current = null;
                _active = false;
            }
            if (previous is not null) await QueuePersistAsync(previous);
            cancellationToken.ThrowIfCancellationRequested();
            lock (_stateLock)
            {
                _current = progress;
                _lastQueuedPosition = progress?.Position ?? TimeSpan.Zero;
                _lastQueuedAt = _clock.GetUtcNow();
            }
        }
        finally
        {
            _switchGate.Release();
        }
    }

    public void UpdateMetadata(PlaybackProgress progress)
    {
        lock (_stateLock)
        {
            if (_current is null || !SameIdentity(_current, progress)) return;
            _current = progress with
            {
                Position = _current.Position,
                Duration = _current.Duration,
                UpdatedAt = _current.UpdatedAt
            };
        }
    }

    public void RecordPosition(TimeSpan position, TimeSpan? duration)
    {
        PlaybackProgress? snapshot = null;
        lock (_stateLock)
        {
            if (_current is null) return;
            var safePosition = position < TimeSpan.Zero ? TimeSpan.Zero : position;
            var safeDuration = duration is { } value && value > TimeSpan.Zero ? value : _current.Duration;
            _current = _current with { Position = safePosition, Duration = safeDuration, UpdatedAt = _clock.GetUtcNow() };
            if (!_active || !PlaybackProgressPolicy.HasStarted(safePosition)) return;
            var now = _clock.GetUtcNow();
            if ((safePosition - _lastQueuedPosition).Duration() < PeriodicPositionInterval && now - _lastQueuedAt < PeriodicPositionInterval) return;
            _lastQueuedPosition = safePosition;
            _lastQueuedAt = now;
            snapshot = SnapshotLocked();
        }
        if (snapshot is not null) _ = QueuePersistAsync(snapshot);
    }

    public Task RecordStateAsync(PlayerState state)
    {
        PlaybackProgress? snapshot = null;
        lock (_stateLock)
        {
            if (_current is null) return Task.CompletedTask;
            if (state is PlayerState.Playing or PlayerState.Paused) _active = true;
            if (state is PlayerState.Stopped or PlayerState.Idle or PlayerState.Error) _active = false;
            if (state is PlayerState.Paused or PlayerState.Stopped or PlayerState.Idle or PlayerState.Error)
            {
                _current = _current with { UpdatedAt = _clock.GetUtcNow() };
                snapshot = SnapshotLocked();
                _lastQueuedPosition = _current.Position;
                _lastQueuedAt = _current.UpdatedAt;
            }
        }
        return snapshot is null ? Task.CompletedTask : QueuePersistAsync(snapshot);
    }

    public async Task FlushAsync()
    {
        PlaybackProgress? snapshot;
        lock (_stateLock) snapshot = SnapshotLocked();
        if (snapshot is not null) await QueuePersistAsync(snapshot);
        Task tail;
        lock (_queueLock) tail = _writeTail;
        await tail;
    }

    private Task QueuePersistAsync(PlaybackProgress progress)
    {
        lock (_queueLock)
        {
            _writeTail = _writeTail.ContinueWith(
                _ => PersistSafelyAsync(progress),
                CancellationToken.None,
                TaskContinuationOptions.None,
                TaskScheduler.Default).Unwrap();
            return _writeTail;
        }
    }

    private async Task PersistSafelyAsync(PlaybackProgress progress)
    {
        try
        {
            if (PlaybackProgressPolicy.IsCompleted(progress.Position, progress.Duration))
            {
                await _history.DeleteAsync(progress.ProviderKey, progress.Catalog, progress.MediaId);
            }
            else if (PlaybackProgressPolicy.HasStarted(progress.Position))
            {
                await _history.UpsertAsync(progress);
            }
        }
        catch
        {
            // Playback remains available if the local history database is temporarily unavailable.
        }
    }

    private PlaybackProgress? SnapshotLocked() => _current;

    private static bool SameIdentity(PlaybackProgress left, PlaybackProgress right) =>
        string.Equals(left.ProviderKey, right.ProviderKey, StringComparison.Ordinal) &&
        left.Catalog == right.Catalog &&
        string.Equals(left.MediaId, right.MediaId, StringComparison.Ordinal);

    public async ValueTask DisposeAsync()
    {
        await FlushAsync();
        _switchGate.Dispose();
    }
}
