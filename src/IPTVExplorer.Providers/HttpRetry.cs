using System.Net;

namespace IPTVExplorer.Providers;

internal static class HttpRetry
{
    public static async Task<HttpResponseMessage> SendAsync(HttpClient client, Func<HttpRequestMessage> requestFactory, CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            using var request = requestFactory();
            HttpResponseMessage response;
            try
            {
                response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            }
            catch (HttpRequestException) when (attempt == 0)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken);
                continue;
            }
            if (attempt > 0 || !IsTransient(response.StatusCode)) return response;
            var delay = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromMilliseconds(250);
            response.Dispose();
            await Task.Delay(delay > TimeSpan.FromSeconds(1) ? TimeSpan.FromSeconds(1) : delay, cancellationToken);
        }
    }

    private static bool IsTransient(HttpStatusCode status) => status is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests || (int)status >= 500;
}
