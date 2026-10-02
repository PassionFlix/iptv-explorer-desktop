using System.Net;
using System.Net.Http.Headers;
using System.Diagnostics;
using IPTVExplorer.Core;
using IPTVExplorer.Player;
using Microsoft.Extensions.Logging;

namespace IPTVExplorer.Tests;

public sealed class StalkerMediaProbeTests
{
    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("0", false)]
    [InlineData("true", false)]
    [InlineData("1", true)]
    public void ProbeRequiresExactOptIn(string? value, bool expected) =>
        Assert.Equal(expected, StalkerMediaProbe.IsOptInEnabled(value));

    [Fact]
    public async Task DisabledProbeMakesNoRequestAndProducesNoTrace()
    {
        var handler = new RecordingHandler(_ => Response(new byte[32]));
        var logger = new RecordingLogger<StalkerMediaProbe>();
        var probe = new StalkerMediaProbe(new HttpClient(handler), logger, enabled: false);

        await probe.ProbeAsync(Media());

        Assert.Equal(0, handler.RequestCount);
        Assert.Empty(logger.Messages);
    }

    [Fact]
    public async Task OneShotProbeCopiesHeadersReadsAtMostLimitAndLogsOnlySafeSummary()
    {
        var stream = new CountingMemoryStream(new byte[StalkerMediaProbe.MaximumBytes + 64 * 1024]);
        var handler = new RecordingHandler(request => Response(stream, request));
        var logger = new RecordingLogger<StalkerMediaProbe>();
        var probe = new StalkerMediaProbe(new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan }, logger, enabled: true);

        await probe.ProbeAsync(Media());

        Assert.Equal(1, handler.RequestCount);
        Assert.Equal(HttpMethod.Get, handler.Method);
        Assert.Equal(
            ["Accept", "Authorization", "Cookie", "Referer", "User-Agent", "X-User-Agent"],
            handler.HeaderNames.Order(StringComparer.OrdinalIgnoreCase));
        Assert.Equal("Bearer private-session", handler.Headers["Authorization"]);
        Assert.Equal("mac=00:1A:79:AA:BB:CC; token=private-session; play_token=private-play-token", handler.Headers["Cookie"]);
        Assert.Equal(StalkerMediaProbe.MaximumBytes, stream.TotalRead);
        Assert.Contains("STALKER MEDIA PROBE status_code=200 success=true content_type=video/mp2t", logger.Text, StringComparison.Ordinal);
        Assert.Contains($"bytes_received={StalkerMediaProbe.MaximumBytes}", logger.Text, StringComparison.Ordinal);
        Assert.Contains("elapsed_ms=", logger.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("https://", logger.Text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("media.example.invalid", logger.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("00:1A:79:AA:BB:CC", logger.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("private-session", logger.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("private-play-token", logger.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("Authorization", logger.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("Cookie", logger.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProbeDoesNotRetryAfterTransportFailure()
    {
        var handler = new RecordingHandler(_ => throw new HttpRequestException("synthetic reset"));
        var logger = new RecordingLogger<StalkerMediaProbe>();
        var probe = new StalkerMediaProbe(new HttpClient(handler), logger, enabled: true);

        await probe.ProbeAsync(Media());

        Assert.Equal(1, handler.RequestCount);
        Assert.Contains("status_code=0 success=false", logger.Text, StringComparison.Ordinal);
        Assert.Contains("bytes_received=0", logger.Text, StringComparison.Ordinal);
        Assert.Contains("exception_type=HttpRequestException", logger.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("synthetic reset", logger.Text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.Found)]
    public async Task ProbeReportsHttpFailureWithoutFollowingOrRetrying(HttpStatusCode statusCode)
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(statusCode)
        {
            Content = new ByteArrayContent([]),
            Headers = { Location = new Uri("https://redirect.example.invalid/live.ts") }
        });
        var logger = new RecordingLogger<StalkerMediaProbe>();
        var probe = new StalkerMediaProbe(new HttpClient(handler), logger, enabled: true);

        await probe.ProbeAsync(Media());

        Assert.Equal(1, handler.RequestCount);
        Assert.Contains($"status_code={(int)statusCode} success=false", logger.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("exception_type=", logger.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("redirect.example.invalid", logger.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProbeUsesOneBoundedOverallTimeout()
    {
        var handler = new BlockingHandler();
        var logger = new RecordingLogger<StalkerMediaProbe>();
        var probe = new StalkerMediaProbe(new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan }, logger,
            enabled: true, timeout: TimeSpan.FromMilliseconds(25));
        var stopwatch = Stopwatch.StartNew();

        await probe.ProbeAsync(Media());

        stopwatch.Stop();
        Assert.Equal(1, handler.RequestCount);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(2));
        Assert.Contains("status_code=0 success=false", logger.Text, StringComparison.Ordinal);
        Assert.Contains("exception_type=", logger.Text, StringComparison.Ordinal);
    }

    private static ResolvedMedia Media() => new(
        new Uri("https://media.example.invalid/live.php?stream=private-stream&play_token=private-play-token"),
        new Dictionary<string, string>
        {
            ["User-Agent"] = "Synthetic MAG",
            ["X-User-Agent"] = "Model: Synthetic",
            ["Accept"] = "*/*",
            ["Referer"] = "https://portal.example.invalid/c/",
            ["Cookie"] = "mac=00:1A:79:AA:BB:CC; token=private-session; play_token=private-play-token",
            ["Authorization"] = "Bearer private-session"
        });

    private static HttpResponseMessage Response(Stream stream, HttpRequestMessage? request = null)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(stream),
            RequestMessage = request
        };
        response.Content.Headers.ContentType = new MediaTypeHeaderValue("video/mp2t");
        return response;
    }

    private static HttpResponseMessage Response(byte[] body) => Response(new MemoryStream(body));

    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public int RequestCount { get; private set; }
        public HttpMethod? Method { get; private set; }
        public Dictionary<string, string> Headers { get; } = new(StringComparer.OrdinalIgnoreCase);
        public IEnumerable<string> HeaderNames => Headers.Keys;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            Method = request.Method;
            Headers.Clear();
            foreach (var header in request.Headers) Headers[header.Key] = string.Join(", ", header.Value);
            return Task.FromResult(respond(request));
        }
    }

    private sealed class CountingMemoryStream(byte[] buffer) : MemoryStream(buffer)
    {
        public int TotalRead { get; private set; }

        public override ValueTask<int> ReadAsync(Memory<byte> destination, CancellationToken cancellationToken = default)
        {
            var read = base.Read(destination.Span);
            TotalRead += read;
            return ValueTask.FromResult(read);
        }
    }

    private sealed class BlockingHandler : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("Unreachable synthetic response.");
        }
    }

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<string> Messages { get; } = [];
        public string Text => string.Join(Environment.NewLine, Messages);
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Messages.Add(formatter(state, exception));
    }
}
