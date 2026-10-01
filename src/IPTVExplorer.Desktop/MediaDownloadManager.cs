using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using IPTVExplorer.Core;
using IPTVExplorer.Infrastructure;
using IPTVExplorer.Providers;
using Microsoft.Extensions.Logging;

namespace IPTVExplorer.Desktop;

public sealed record DownloadStatusSnapshot(
    string DownloadId,
    string FileName,
    string Status,
    long BytesReceived,
    long? TotalBytes,
    long BytesPerSecond,
    string? Error);

public sealed class MediaDownloadManager : IAsyncDisposable
{
    private static readonly TimeSpan DefaultRetention = TimeSpan.FromSeconds(10);
    private readonly ILogger<MediaDownloadManager> _logger;
    private readonly Func<HttpMessageHandler> _handlerFactory;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _terminalRetention;
    private readonly ConcurrentDictionary<string, DownloadOperation> _downloads = new(StringComparer.Ordinal);
    private int _stopping;

    public MediaDownloadManager(ILogger<MediaDownloadManager> logger)
        : this(logger, CreateHandler, TimeProvider.System, DefaultRetention)
    {
    }

    internal MediaDownloadManager(
        ILogger<MediaDownloadManager> logger,
        Func<HttpMessageHandler> handlerFactory,
        TimeProvider timeProvider,
        TimeSpan terminalRetention)
    {
        _logger = logger;
        _handlerFactory = handlerFactory;
        _timeProvider = timeProvider;
        _terminalRetention = terminalRetention;
    }

    public DownloadStatusSnapshot Start(string fileName, string destinationPath, ResolvedMedia media)
    {
        if (Volatile.Read(ref _stopping) != 0) throw new InvalidOperationException("L’application est en cours de fermeture.");
        var id = Guid.NewGuid().ToString("N");
        var operation = new DownloadOperation(id, fileName, destinationPath, destinationPath + ".part-" + id);
        if (!_downloads.TryAdd(id, operation)) throw new InvalidOperationException("Impossible de créer le téléchargement.");
        if (Volatile.Read(ref _stopping) != 0)
        {
            _downloads.TryRemove(id, out _);
            operation.DisposeCancellation();
            throw new InvalidOperationException("L’application est en cours de fermeture.");
        }

        _ = RunDownloadAsync(operation, media);
        return Snapshot(operation);
    }

    public DownloadStatusSnapshot? Get(string downloadId) =>
        _downloads.TryGetValue(downloadId, out var operation) ? Snapshot(operation) : null;

    public bool Cancel(string downloadId)
    {
        if (!_downloads.TryGetValue(downloadId, out var operation)) return false;
        lock (operation.Gate)
        {
            if (operation.Status is "completed" or "failed" or "cancelled") return operation.Status == "cancelled";
        }

        try { operation.Cancellation.Cancel(); }
        catch (ObjectDisposedException) { }
        return true;
    }

    public async Task ShutdownAsync(TimeSpan timeout)
    {
        Interlocked.Exchange(ref _stopping, 1);
        var operations = _downloads.Values.ToArray();
        foreach (var operation in operations)
        {
            lock (operation.Gate)
            {
                if (operation.Status is "completed" or "failed" or "cancelled") continue;
            }
            try { operation.Cancellation.Cancel(); }
            catch (ObjectDisposedException) { }
        }

        if (operations.Length == 0) return;
        try
        {
            await Task.WhenAll(operations.Select(operation => operation.Completion.Task))
                .WaitAsync(timeout).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // Application shutdown stays bounded even if an OS/network handler ignores cancellation.
        }
    }

    public async ValueTask DisposeAsync() => await ShutdownAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);

    internal int OperationCount => _downloads.Count;
    internal bool CancellationDisposed(string id) => _downloads.TryGetValue(id, out var operation) && operation.CancellationIsDisposed;

    private async Task RunDownloadAsync(DownloadOperation operation, ResolvedMedia media)
    {
        try
        {
            using var http = new HttpClient(_handlerFactory()) { Timeout = Timeout.InfiniteTimeSpan };
            using var request = new HttpRequestMessage(HttpMethod.Get, media.Uri);
            if (media.Headers is not null)
            {
                foreach (var (name, value) in media.Headers) request.Headers.TryAddWithoutValidation(name, value);
            }
            if (!request.Headers.Contains("User-Agent")) request.Headers.TryAddWithoutValidation("User-Agent", ProviderHttpRegistration.MediaUserAgent);
            if (!request.Headers.Contains("Accept")) request.Headers.TryAddWithoutValidation("Accept", "*/*");

            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, operation.Cancellation.Token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            lock (operation.Gate)
            {
                operation.TotalBytes = response.Content.Headers.ContentLength;
                operation.Status = "running";
            }

            await using (var source = await response.Content.ReadAsStreamAsync(operation.Cancellation.Token).ConfigureAwait(false))
            await using (var target = new FileStream(operation.TemporaryPath, FileMode.Create, FileAccess.Write, FileShare.None, 1024 * 128, useAsync: true))
            {
                var buffer = new byte[1024 * 128];
                while (true)
                {
                    var read = await source.ReadAsync(buffer, operation.Cancellation.Token).ConfigureAwait(false);
                    if (read == 0) break;
                    await target.WriteAsync(buffer.AsMemory(0, read), operation.Cancellation.Token).ConfigureAwait(false);
                    lock (operation.Gate) operation.BytesReceived += read;
                }
                await target.FlushAsync(operation.Cancellation.Token).ConfigureAwait(false);
            }

            operation.Cancellation.Token.ThrowIfCancellationRequested();
            File.Move(operation.TemporaryPath, operation.DestinationPath, true);
            lock (operation.Gate)
            {
                operation.Status = "completed";
                if (operation.TotalBytes is null) operation.TotalBytes = operation.BytesReceived;
            }
        }
        catch (OperationCanceledException)
        {
            lock (operation.Gate) operation.Status = "cancelled";
        }
        catch (Exception exception)
        {
            _logger.LogWarning("Media download failed: {SafeError}", LogRedactor.Redact(exception.Message));
            lock (operation.Gate)
            {
                operation.Status = "failed";
                operation.Error = "Le téléchargement a échoué.";
            }
        }
        finally
        {
            if (File.Exists(operation.TemporaryPath))
            {
                try { File.Delete(operation.TemporaryPath); } catch { }
            }
            operation.DisposeCancellation();
            operation.Completion.TrySetResult();
            _ = RemoveAfterRetentionAsync(operation);
        }
    }

    private async Task RemoveAfterRetentionAsync(DownloadOperation operation)
    {
        if (_terminalRetention > TimeSpan.Zero)
            await Task.Delay(_terminalRetention, _timeProvider, CancellationToken.None).ConfigureAwait(false);
        if (_downloads.TryGetValue(operation.Id, out var current) && ReferenceEquals(current, operation))
            _downloads.TryRemove(operation.Id, out _);
    }

    private static DownloadStatusSnapshot Snapshot(DownloadOperation operation)
    {
        lock (operation.Gate)
        {
            var elapsed = Math.Max(0.001, operation.Timer.Elapsed.TotalSeconds);
            var bytesPerSecond = operation.Status == "cancelled" ? 0L : (long)Math.Max(0, operation.BytesReceived / elapsed);
            return new(operation.Id, operation.FileName, operation.Status, operation.BytesReceived,
                operation.TotalBytes, bytesPerSecond, operation.Error);
        }
    }

    private static HttpMessageHandler CreateHandler() => new SocketsHttpHandler
    {
        AutomaticDecompression = DecompressionMethods.None,
        AllowAutoRedirect = true,
        UseCookies = false,
        ConnectTimeout = TimeSpan.FromSeconds(20)
    };

    private sealed class DownloadOperation(string id, string fileName, string destinationPath, string temporaryPath)
    {
        private int _cancellationDisposed;
        public object Gate { get; } = new();
        public string Id { get; } = id;
        public string FileName { get; } = fileName;
        public string DestinationPath { get; } = destinationPath;
        public string TemporaryPath { get; } = temporaryPath;
        public CancellationTokenSource Cancellation { get; } = new();
        public TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Stopwatch Timer { get; } = Stopwatch.StartNew();
        public string Status { get; set; } = "starting";
        public long BytesReceived { get; set; }
        public long? TotalBytes { get; set; }
        public string? Error { get; set; }
        public bool CancellationIsDisposed => Volatile.Read(ref _cancellationDisposed) != 0;

        public void DisposeCancellation()
        {
            if (Interlocked.Exchange(ref _cancellationDisposed, 1) == 0) Cancellation.Dispose();
        }
    }
}
