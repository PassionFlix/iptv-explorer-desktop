using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using IPTVExplorer.Core;
using IPTVExplorer.Desktop;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace IPTVExplorer.Tests;

public sealed class MediaDownloadManagerTests
{
    private static readonly TimeSpan LongRetention = TimeSpan.FromMinutes(1);

    [Fact]
    public async Task CompletedDownloadDisposesResourcesAndRemovesTemporaryFile()
    {
        using var files = new DownloadFiles();
        await using var manager = CreateManager(() => SuccessHandler("complete"));
        var operation = manager.Start("video.ts", files.Final, Media());

        var status = await WaitForTerminalAsync(manager, operation.DownloadId);

        Assert.Equal("completed", status.Status);
        Assert.True(manager.CancellationDisposed(operation.DownloadId));
        Assert.False(File.Exists(files.Part(operation.DownloadId)));
    }

    [Fact]
    public async Task CancelledDownloadDisposesCancellationSource()
    {
        using var files = new DownloadFiles();
        var stream = new ControlledStream();
        await using var manager = CreateManager(() => StreamHandler(stream));
        var operation = manager.Start("video.ts", files.Final, Media());
        await stream.FirstRead.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.True(manager.Cancel(operation.DownloadId));
        Assert.Equal("cancelled", (await WaitForTerminalAsync(manager, operation.DownloadId)).Status);
        Assert.True(manager.CancellationDisposed(operation.DownloadId));
    }

    [Fact]
    public async Task FailedDownloadDisposesCancellationSource()
    {
        using var files = new DownloadFiles();
        await using var manager = CreateManager(() => new DelegateHandler((_, _) => throw new HttpRequestException("fixture failure")));
        var operation = manager.Start("video.ts", files.Final, Media());

        Assert.Equal("failed", (await WaitForTerminalAsync(manager, operation.DownloadId)).Status);
        Assert.True(manager.CancellationDisposed(operation.DownloadId));
        Assert.False(File.Exists(files.Final));
    }

    [Fact]
    public async Task TerminalOperationIsRemovedAfterRetention()
    {
        using var files = new DownloadFiles();
        await using var manager = CreateManager(() => SuccessHandler("retained"), TimeSpan.FromMilliseconds(200));
        var operation = manager.Start("video.ts", files.Final, Media());
        await WaitUntilAsync(() => manager.CancellationDisposed(operation.DownloadId));

        await WaitUntilAsync(() => manager.Get(operation.DownloadId) is null);

        Assert.Equal(0, manager.OperationCount);
    }

    [Fact]
    public async Task ShutdownCancelsActiveDownloadAndPreventsNewStarts()
    {
        using var files = new DownloadFiles();
        var stream = new ControlledStream();
        await using var manager = CreateManager(() => StreamHandler(stream));
        var operation = manager.Start("video.ts", files.Final, Media());
        await stream.FirstRead.Task.WaitAsync(TimeSpan.FromSeconds(2));

        await manager.ShutdownAsync(TimeSpan.FromSeconds(2));

        Assert.Equal("cancelled", manager.Get(operation.DownloadId)?.Status);
        Assert.Throws<InvalidOperationException>(() => manager.Start("other.ts", files.Final + ".other", Media()));
    }

    [Fact]
    public async Task ShutdownWaitIsBoundedWhenStreamIgnoresCancellation()
    {
        using var files = new DownloadFiles();
        var stream = new ControlledStream(ignoreCancellation: true);
        await using var manager = CreateManager(() => StreamHandler(stream));
        manager.Start("video.ts", files.Final, Media());
        await stream.FirstRead.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var timer = Stopwatch.StartNew();
        await manager.ShutdownAsync(TimeSpan.FromMilliseconds(40));
        timer.Stop();
        stream.Release();

        Assert.InRange(timer.Elapsed, TimeSpan.Zero, TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task CancellationDeletesOwnedPartFile()
    {
        using var files = new DownloadFiles();
        var stream = new ControlledStream();
        await using var manager = CreateManager(() => StreamHandler(stream));
        var operation = manager.Start("video.ts", files.Final, Media());
        await stream.FirstRead.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await WaitUntilAsync(() => File.Exists(files.Part(operation.DownloadId)));

        manager.Cancel(operation.DownloadId);
        await WaitForTerminalAsync(manager, operation.DownloadId);

        Assert.False(File.Exists(files.Part(operation.DownloadId)));
        Assert.False(File.Exists(files.Final));
    }

    [Fact]
    public async Task SuccessKeepsValidatedFinalFile()
    {
        using var files = new DownloadFiles();
        await using var manager = CreateManager(() => SuccessHandler("validated-media"));
        var operation = manager.Start("video.ts", files.Final, Media());

        await WaitForTerminalAsync(manager, operation.DownloadId);

        Assert.Equal("validated-media", await File.ReadAllTextAsync(files.Final));
        Assert.False(File.Exists(files.Part(operation.DownloadId)));
    }

    [Fact]
    public async Task TwoDownloadsCompleteIndependently()
    {
        using var files = new DownloadFiles();
        var responses = new ConcurrentQueue<string>(["first", "second"]);
        await using var manager = CreateManager(() => SuccessHandler(responses.TryDequeue(out var body) ? body : "unexpected"));
        var first = manager.Start("first.ts", files.Final, Media());
        var secondPath = files.Final + ".second";
        var second = manager.Start("second.ts", secondPath, Media());

        Assert.Equal("completed", (await WaitForTerminalAsync(manager, first.DownloadId)).Status);
        Assert.Equal("completed", (await WaitForTerminalAsync(manager, second.DownloadId)).Status);
        Assert.True(File.Exists(files.Final));
        Assert.True(File.Exists(secondPath));
    }

    [Fact]
    public async Task CancellingOneDownloadDoesNotCancelAnother()
    {
        using var files = new DownloadFiles();
        var firstStream = new ControlledStream();
        var handlers = new ConcurrentQueue<HttpMessageHandler>([StreamHandler(firstStream), SuccessHandler("second")]);
        await using var manager = CreateManager(() => handlers.TryDequeue(out var handler) ? handler : SuccessHandler("extra"));
        var first = manager.Start("first.ts", files.Final, Media());
        await firstStream.FirstRead.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var secondPath = files.Final + ".second";
        var second = manager.Start("second.ts", secondPath, Media());

        manager.Cancel(first.DownloadId);

        Assert.Equal("cancelled", (await WaitForTerminalAsync(manager, first.DownloadId)).Status);
        Assert.Equal("completed", (await WaitForTerminalAsync(manager, second.DownloadId)).Status);
        Assert.True(File.Exists(secondPath));
    }

    [Fact]
    public async Task ShutdownCancelsAllActiveDownloads()
    {
        using var files = new DownloadFiles();
        var firstStream = new ControlledStream();
        var secondStream = new ControlledStream();
        var handlers = new ConcurrentQueue<HttpMessageHandler>([StreamHandler(firstStream), StreamHandler(secondStream)]);
        await using var manager = CreateManager(() => handlers.TryDequeue(out var handler) ? handler : SuccessHandler("extra"));
        var first = manager.Start("first.ts", files.Final, Media());
        var second = manager.Start("second.ts", files.Final + ".second", Media());
        await Task.WhenAll(firstStream.FirstRead.Task, secondStream.FirstRead.Task).WaitAsync(TimeSpan.FromSeconds(2));

        await manager.ShutdownAsync(TimeSpan.FromSeconds(2));

        Assert.Equal("cancelled", manager.Get(first.DownloadId)?.Status);
        Assert.Equal("cancelled", manager.Get(second.DownloadId)?.Status);
    }

    [Fact]
    public async Task StatusPollingDuringTerminationDoesNotThrow()
    {
        using var files = new DownloadFiles();
        var stream = new ControlledStream();
        await using var manager = CreateManager(() => StreamHandler(stream));
        var operation = manager.Start("video.ts", files.Final, Media());
        await stream.FirstRead.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var polling = Task.Run(async () =>
        {
            for (var index = 0; index < 100; index++)
            {
                _ = manager.Get(operation.DownloadId);
                await Task.Yield();
            }
        });
        manager.Cancel(operation.DownloadId);

        await polling;
        Assert.Equal("cancelled", (await WaitForTerminalAsync(manager, operation.DownloadId)).Status);
        Assert.True(manager.Cancel(operation.DownloadId));
    }

    [Fact]
    public async Task ProviderCredentialNeverAppearsInExposedOrLoggedError()
    {
        using var files = new DownloadFiles();
        var logger = new CapturingLogger();
        await using var manager = CreateManager(
            () => new DelegateHandler((_, _) => throw new HttpRequestException("failed https://provider.invalid/live/user/password/1.ts?access_token=secret-token")),
            logger: logger);
        var operation = manager.Start("video.ts", files.Final, Media());

        var status = await WaitForTerminalAsync(manager, operation.DownloadId);

        Assert.Equal("Le téléchargement a échoué.", status.Error);
        Assert.DoesNotContain("user", logger.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("password", logger.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-token", logger.Message, StringComparison.Ordinal);
    }

    private static MediaDownloadManager CreateManager(
        Func<HttpMessageHandler> factory,
        TimeSpan? retention = null,
        ILogger<MediaDownloadManager>? logger = null) =>
        new(logger ?? NullLogger<MediaDownloadManager>.Instance, factory, TimeProvider.System, retention ?? LongRetention);

    private static ResolvedMedia Media() => new(new Uri("https://fixture.invalid/media.ts"));

    private static DelegateHandler SuccessHandler(string body) => new((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
    {
        Content = new StringContent(body)
    }));

    private static DelegateHandler StreamHandler(Stream stream) => new((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
    {
        Content = new StreamContent(stream)
    }));

    private static async Task<DownloadStatusSnapshot> WaitForTerminalAsync(MediaDownloadManager manager, string id)
    {
        DownloadStatusSnapshot? status = null;
        await WaitUntilAsync(() =>
        {
            status = manager.Get(id);
            return status?.Status is "completed" or "cancelled" or "failed";
        });
        return status!;
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(3);
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline) throw new TimeoutException("The local fixture did not reach the expected state.");
            await Task.Delay(10);
        }
    }

    private sealed class DelegateHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
    }

    private sealed class ControlledStream(bool ignoreCancellation = false) : Stream
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _reads;
        public TaskCompletionSource FirstRead { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Release() => _release.TrySetResult();
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref _reads) == 1)
            {
                buffer.Span[0] = 42;
                FirstRead.TrySetResult();
                return 1;
            }

            if (ignoreCancellation) await _release.Task.ConfigureAwait(false);
            else await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
            return 0;
        }
    }

    private sealed class DownloadFiles : IDisposable
    {
        public DownloadFiles()
        {
            Root = Path.Combine(Path.GetTempPath(), "iptv-explorer-download-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
            Final = Path.Combine(Root, "video.ts");
        }
        public string Root { get; }
        public string Final { get; }
        public string Part(string id) => Final + ".part-" + id;
        public void Dispose()
        {
            if (Directory.Exists(Root)) Directory.Delete(Root, true);
        }
    }

    private sealed class CapturingLogger : ILogger<MediaDownloadManager>
    {
        public string Message { get; private set; } = string.Empty;
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Message = formatter(state, exception);
    }
}
