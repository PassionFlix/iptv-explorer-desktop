using System.Collections.Concurrent;
using Microsoft.Data.Sqlite;

namespace IPTVExplorer.Infrastructure;

internal static class SearchIndexFileAccess
{
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
