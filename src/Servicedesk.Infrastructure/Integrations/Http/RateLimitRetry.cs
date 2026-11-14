using System.Net;
using Microsoft.Extensions.Logging;

namespace Servicedesk.Infrastructure.Integrations.Http;

/// HTTP 429 handling for integration API calls: honour Retry-After (delta or
/// date), else back off 1 s / 2 s / 4 s; at most <c>maxRetries</c> retries,
/// after which the 429 response itself is returned to the caller.
public static class RateLimitRetry
{
    public const int DefaultMaxRetries = 3;

    /// Sends a fresh request from <paramref name="buildRequest"/> on every
    /// attempt (a request message can't be sent twice).
    public static async Task<HttpResponseMessage> SendAsync(
        HttpClient client,
        Func<HttpRequestMessage> buildRequest,
        ILogger logger,
        string serviceName,
        CancellationToken ct,
        int maxRetries = DefaultMaxRetries,
        TimeProvider? clock = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        clock ??= TimeProvider.System;
        delay ??= Task.Delay;
        for (var attempt = 0; ; attempt++)
        {
            var resp = await client.SendAsync(buildRequest(), HttpCompletionOption.ResponseHeadersRead, ct);
            if (resp.StatusCode != HttpStatusCode.TooManyRequests || attempt >= maxRetries)
                return resp;

            var wait = DelayFor(resp, attempt, clock.GetUtcNow());
            resp.Dispose();
            logger.LogWarning("{Service} 429 rate-limited; retrying in {Delay}s (attempt {Attempt}/{Max}).",
                serviceName, wait.TotalSeconds, attempt + 1, maxRetries);
            await delay(wait, ct);
        }
    }

    /// Wait before the next attempt: Retry-After when present, else 2^attempt s.
    public static TimeSpan DelayFor(HttpResponseMessage resp, int attempt, DateTimeOffset nowUtc)
    {
        if (resp.Headers.RetryAfter?.Delta is { } d) return d;
        if (resp.Headers.RetryAfter?.Date is { } when && when - nowUtc > TimeSpan.Zero) return when - nowUtc;
        return TimeSpan.FromSeconds(Math.Pow(2, attempt));
    }
}
