using System.Net;
using System.Net.Http.Headers;
using Microsoft.Extensions.Logging.Abstractions;
using Servicedesk.Infrastructure.Integrations.Http;
using Xunit;

namespace Servicedesk.Api.Tests;

/// Shared HTTP plumbing for integrations (Sophos, Veeam VSPC): the 429
/// retry policy and the credential-keyed bearer-token cache.
public sealed class IntegrationHttpTests
{
    private sealed class FakeClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class ScriptedHandler(params HttpStatusCode[] codes) : HttpMessageHandler
    {
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var code = codes[Math.Min(Calls, codes.Length - 1)];
            Calls++;
            return Task.FromResult(new HttpResponseMessage(code));
        }
    }

    private static readonly DateTimeOffset T0 = new(2026, 3, 10, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Delay_uses_retry_after_delta_then_date_then_exponential_backoff()
    {
        var delta = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        delta.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(7));
        Assert.Equal(TimeSpan.FromSeconds(7), RateLimitRetry.DelayFor(delta, 0, T0));

        var date = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        date.Headers.RetryAfter = new RetryConditionHeaderValue(T0.AddSeconds(30));
        Assert.Equal(TimeSpan.FromSeconds(30), RateLimitRetry.DelayFor(date, 0, T0));

        var none = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        Assert.Equal(TimeSpan.FromSeconds(1), RateLimitRetry.DelayFor(none, 0, T0));
        Assert.Equal(TimeSpan.FromSeconds(4), RateLimitRetry.DelayFor(none, 2, T0));
    }

    [Fact]
    public async Task Retries_429_and_returns_the_first_non_429_response()
    {
        var handler = new ScriptedHandler(HttpStatusCode.TooManyRequests, HttpStatusCode.TooManyRequests, HttpStatusCode.OK);
        var waits = new List<TimeSpan>();
        using var resp = await RateLimitRetry.SendAsync(
            new HttpClient(handler), () => new HttpRequestMessage(HttpMethod.Get, "https://api.example.test/x"),
            NullLogger.Instance, "Test", default,
            delay: (d, _) => { waits.Add(d); return Task.CompletedTask; });

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal(3, handler.Calls);
        Assert.Equal(new[] { TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2) }, waits);
    }

    [Fact]
    public async Task Gives_up_after_max_retries_and_returns_the_429()
    {
        var handler = new ScriptedHandler(HttpStatusCode.TooManyRequests);
        using var resp = await RateLimitRetry.SendAsync(
            new HttpClient(handler), () => new HttpRequestMessage(HttpMethod.Get, "https://api.example.test/x"),
            NullLogger.Instance, "Test", default, delay: (_, _) => Task.CompletedTask);

        Assert.Equal(HttpStatusCode.TooManyRequests, resp.StatusCode);
        Assert.Equal(RateLimitRetry.DefaultMaxRetries + 1, handler.Calls);
    }

    [Fact]
    public async Task Token_is_reused_until_shortly_before_expiry()
    {
        var clock = new FakeClock(T0);
        var cache = new BearerTokenCache(clock);
        var fetches = 0;
        Task<(string, int)> Fetch(CancellationToken _) => Task.FromResult(($"token-{++fetches}", 3600));
        var key = BearerTokenCache.KeyFor("client-a", "secret-a");

        Assert.Equal("token-1", await cache.GetAsync(key, Fetch, default));
        clock.Now = T0.AddMinutes(58);
        Assert.Equal("token-1", await cache.GetAsync(key, Fetch, default));
        clock.Now = T0.AddMinutes(59).AddSeconds(1); // past expires_in − 60 s
        Assert.Equal("token-2", await cache.GetAsync(key, Fetch, default));
    }

    [Fact]
    public async Task Changed_credentials_never_reuse_the_previous_token()
    {
        var cache = new BearerTokenCache(new FakeClock(T0));
        var fetches = 0;
        Task<(string, int)> Fetch(CancellationToken _) => Task.FromResult(($"token-{++fetches}", 3600));

        Assert.Equal("token-1", await cache.GetAsync(BearerTokenCache.KeyFor("client-a", "secret-a"), Fetch, default));
        Assert.Equal("token-2", await cache.GetAsync(BearerTokenCache.KeyFor("client-a", "secret-b"), Fetch, default));
        Assert.Equal("token-3", await cache.GetAsync(BearerTokenCache.KeyFor("client-b", "secret-b"), Fetch, default));
    }

    [Fact]
    public void Key_is_a_fingerprint_not_the_secret()
    {
        var key = BearerTokenCache.KeyFor("client-a", "very-secret");
        Assert.DoesNotContain("very-secret", key);
        Assert.Equal(64, key.Length);
        Assert.NotEqual(key, BearerTokenCache.KeyFor("client-av", "ery-secret"));
    }
}
