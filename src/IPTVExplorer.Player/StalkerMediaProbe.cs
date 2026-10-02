using System.Buffers;
using System.Diagnostics;
using System.Globalization;
using IPTVExplorer.Core;
using Microsoft.Extensions.Logging;

namespace IPTVExplorer.Player;

public interface IStalkerMediaProbe
{
    bool Enabled { get; }
    Task ProbeAsync(ResolvedMedia media, CancellationToken cancellationToken = default);
}

public sealed class NullStalkerMediaProbe : IStalkerMediaProbe
{
    public static NullStalkerMediaProbe Instance { get; } = new();
    private NullStalkerMediaProbe() { }
    public bool Enabled => false;
    public Task ProbeAsync(ResolvedMedia media, CancellationToken cancellationToken = default) => Task.CompletedTask;
}

public sealed class StalkerMediaProbe : IStalkerMediaProbe
{
    internal const int MaximumBytes = 256 * 1024;
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(3);

    private readonly HttpClient _http;
    private readonly ILogger<StalkerMediaProbe> _logger;
    private readonly TimeSpan _timeout;

    public StalkerMediaProbe(HttpClient http, ILogger<StalkerMediaProbe> logger)
        : this(http, logger, IsOptInEnabled(Environment.GetEnvironmentVariable("IPTVEXPLORER_STALKER_MEDIA_PROBE")), DefaultTimeout) { }

    internal StalkerMediaProbe(HttpClient http, ILogger<StalkerMediaProbe> logger, bool enabled, TimeSpan? timeout = null)
    {
        _http = http;
        _logger = logger;
        _timeout = timeout ?? DefaultTimeout;
        Enabled = enabled;
    }

    public bool Enabled { get; }

    public async Task ProbeAsync(ResolvedMedia media, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(media);
        if (!Enabled) return;

        var stopwatch = Stopwatch.StartNew();
        var statusCode = 0;
        var success = false;
        var contentType = "none";
        var bytesReceived = 0;
        string? exceptionType = null;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_timeout);

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, media.Uri);
            if (media.Headers is not null)
            {
                foreach (var (name, value) in media.Headers)
                {
                    if (!request.Headers.TryAddWithoutValidation(name, value))
                        throw new InvalidOperationException("A resolved media header cannot be applied to the probe request.");
                }
            }

            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            statusCode = (int)response.StatusCode;
            success = response.IsSuccessStatusCode;
            contentType = SafePlaybackDiagnosticData.ContentType(response.Content.Headers.ContentType?.MediaType);
            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            var buffer = ArrayPool<byte>.Shared.Rent(32 * 1024);
            try
            {
                while (bytesReceived < MaximumBytes)
                {
                    var remaining = MaximumBytes - bytesReceived;
                    var read = await stream.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, remaining)), timeout.Token).ConfigureAwait(false);
                    if (read == 0) break;
                    bytesReceived += read;
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }
        catch (Exception exception)
        {
            success = false;
            exceptionType = exception.GetType().Name;
            if (exception is OperationCanceledException && cancellationToken.IsCancellationRequested) throw;
        }
        finally
        {
            stopwatch.Stop();
            if (exceptionType is null)
            {
                _logger.LogInformation(
                    "STALKER MEDIA PROBE status_code={StatusCode} success={Success} content_type={ContentType} bytes_received={BytesReceived} elapsed_ms={ElapsedMs}",
                    statusCode.ToString(CultureInfo.InvariantCulture), SafePlaybackDiagnosticData.Bool(success), contentType,
                    bytesReceived.ToString(CultureInfo.InvariantCulture), stopwatch.ElapsedMilliseconds.ToString(CultureInfo.InvariantCulture));
            }
            else
            {
                _logger.LogInformation(
                    "STALKER MEDIA PROBE status_code={StatusCode} success={Success} content_type={ContentType} bytes_received={BytesReceived} exception_type={ExceptionType} elapsed_ms={ElapsedMs}",
                    statusCode.ToString(CultureInfo.InvariantCulture), SafePlaybackDiagnosticData.Bool(success), contentType,
                    bytesReceived.ToString(CultureInfo.InvariantCulture), exceptionType,
                    stopwatch.ElapsedMilliseconds.ToString(CultureInfo.InvariantCulture));
            }
        }
    }

    internal static bool IsOptInEnabled(string? value) => string.Equals(value, "1", StringComparison.Ordinal);
}
