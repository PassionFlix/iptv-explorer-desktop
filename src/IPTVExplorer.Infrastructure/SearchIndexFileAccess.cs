using System.Collections.Concurrent;
using Microsoft.Data.Sqlite;

namespace IPTVExplorer.Infrastructure;

internal static class SearchIndexFileAccess
{
    private static readonly TimeSpan[] RetryDelays =
    [
        TimeSpan.FromMilliseconds(25),
        TimeSpan.FromMilliseconds(50),
        TimeSpan.FromMilliseconds(75)
    ];

    private static readonly ConcurrentDictionary<string, AsyncReaderWriterGate> Gates = new(
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

    public static Task<IDisposable> EnterReadAsync(string path, CancellationToken cancellationToken) =>
        Gate(path).EnterReadAsync(cancellationToken);

    public static Task<IDisposable> EnterWriteAsync(string path, CancellationToken cancellationToken) =>
        Gate(path).EnterWriteAsync(cancellationToken);

    public static string ReadOnlyConnectionString(string path)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = Normalize(path),
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = true
        };
        return builder.ConnectionString;
    }

    public static void ClearReadPool(string path)
    {
        using var poolKey = new SqliteConnection(ReadOnlyConnectionString(path));
        SqliteConnection.ClearPool(poolKey);
    }

    public static async Task DeleteAsync(string path, CancellationToken cancellationToken) =>
        await DeleteAsync(path, static (candidate, _) =>
        {
            File.Delete(candidate);
            return Task.CompletedTask;
        }, static (delay, token) => Task.Delay(delay, token), cancellationToken).ConfigureAwait(false);

    internal static async Task DeleteAsync(
        string path,
        Func<string, CancellationToken, Task> deleteFile,
        Func<TimeSpan, CancellationToken, Task> delay,
        CancellationToken cancellationToken)
    {
        path = Normalize(path);
        using var lease = await EnterWriteAsync(path, cancellationToken).ConfigureAwait(false);
        ClearReadPool(path);
        await DeleteFamilyAsync(path, includeDatabase: true, deleteFile, delay,
            () => ClearReadPool(path), cancellationToken).ConfigureAwait(false);
    }

    internal static Task DeleteFamilyAsync(string path, bool includeDatabase, CancellationToken cancellationToken) =>
        DeleteFamilyAsync(path, includeDatabase,
            static (candidate, _) =>
            {
                File.Delete(candidate);
                return Task.CompletedTask;
            },
            static (delay, token) => Task.Delay(delay, token),
            clearPool: null,
            cancellationToken);

    private static async Task DeleteFamilyAsync(
        string path,
        bool includeDatabase,
        Func<string, CancellationToken, Task> deleteFile,
        Func<TimeSpan, CancellationToken, Task> delay,
        Action? clearPool,
        CancellationToken cancellationToken)
    {
        await DeleteWithRetryAsync(path + "-wal", deleteFile, delay, clearPool, cancellationToken).ConfigureAwait(false);
        await DeleteWithRetryAsync(path + "-shm", deleteFile, delay, clearPool, cancellationToken).ConfigureAwait(false);
        await DeleteWithRetryAsync(path + "-journal", deleteFile, delay, clearPool, cancellationToken).ConfigureAwait(false);
        // Keep the usable main database until all transient sidecars have been removed.
        if (includeDatabase) await DeleteWithRetryAsync(path, deleteFile, delay, clearPool, cancellationToken).ConfigureAwait(false);
    }

    private static async Task DeleteWithRetryAsync(
        string path,
        Func<string, CancellationToken, Task> deleteFile,
        Func<TimeSpan, CancellationToken, Task> delay,
        Action? clearPool,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                await deleteFile(path, cancellationToken).ConfigureAwait(false);
                return;
            }
            catch (IOException) when (attempt < RetryDelays.Length)
            {
                clearPool?.Invoke();
                await delay(RetryDelays[attempt], cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static AsyncReaderWriterGate Gate(string path) => Gates.GetOrAdd(Normalize(path), static _ => new());
    private static string Normalize(string path) => Path.GetFullPath(path);

    private sealed class AsyncReaderWriterGate
    {
        private readonly SemaphoreSlim _turnstile = new(1, 1);
        private readonly SemaphoreSlim _readersMutex = new(1, 1);
        private readonly SemaphoreSlim _roomEmpty = new(1, 1);
        private int _readers;

        public async Task<IDisposable> EnterReadAsync(CancellationToken cancellationToken)
        {
            await _turnstile.WaitAsync(cancellationToken).ConfigureAwait(false);
            _turnstile.Release();

            await _readersMutex.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (_readers == 0) await _roomEmpty.WaitAsync(cancellationToken).ConfigureAwait(false);
                _readers++;
                return new Lease(ExitRead);
            }
            finally
            {
                _readersMutex.Release();
            }
        }

        public async Task<IDisposable> EnterWriteAsync(CancellationToken cancellationToken)
        {
            await _turnstile.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await _roomEmpty.WaitAsync(cancellationToken).ConfigureAwait(false);
                return new Lease(ExitWrite);
            }
            catch
            {
                _turnstile.Release();
                throw;
            }
        }

        private void ExitRead()
        {
            _readersMutex.Wait();
            try
            {
                _readers--;
                if (_readers == 0) _roomEmpty.Release();
            }
            finally
            {
                _readersMutex.Release();
            }
        }

        private void ExitWrite()
        {
            _roomEmpty.Release();
            _turnstile.Release();
        }
    }

    private sealed class Lease(Action release) : IDisposable
    {
        private Action? _release = release;

        public void Dispose() => Interlocked.Exchange(ref _release, null)?.Invoke();
    }
}

public sealed class ProviderLocalDataStore : IPTVExplorer.Core.IProviderLocalData
{
    private readonly AppPaths _paths;
    private readonly Func<string, CancellationToken, Task>? _deleteFile;
    private readonly Func<TimeSpan, CancellationToken, Task>? _delay;

    public ProviderLocalDataStore(AppPaths paths) => _paths = paths;

    internal ProviderLocalDataStore(
        AppPaths paths,
        Func<string, CancellationToken, Task> deleteFile,
        Func<TimeSpan, CancellationToken, Task> delay)
    {
        _paths = paths;
        _deleteFile = deleteFile;
        _delay = delay;
    }

    public Task DeleteSearchIndexAsync(string providerKey, CancellationToken cancellationToken = default)
    {
        var path = _paths.SearchIndex(providerKey);
        return _deleteFile is null || _delay is null
            ? SearchIndexFileAccess.DeleteAsync(path, cancellationToken)
            : SearchIndexFileAccess.DeleteAsync(path, _deleteFile, _delay, cancellationToken);
    }
}
