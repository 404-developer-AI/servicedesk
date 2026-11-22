using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Servicedesk.Api.Tests.TestInfrastructure;
using Servicedesk.Infrastructure.Integrations.M365;
using Servicedesk.Infrastructure.Secrets;
using Servicedesk.Infrastructure.Settings;
using Xunit;

namespace Servicedesk.Api.Tests;

/// Roadmap #13: the M365 reader uses the shared token cache (per tenant, keyed
/// on the MSP credentials) and the shared 429 handling — including throttled
/// sub-requests inside a $batch, which used to be read as "no mailbox".
public sealed class M365GraphReaderTests
{
    private const string TenantA = "00000000-0000-0000-0000-00000000000a";
    private const string TenantB = "00000000-0000-0000-0000-00000000000b";

    /// Answers Graph by URL; `Batch` decides each $batch sub-response.
    private sealed class FakeGraph : HttpMessageHandler
    {
        public int TokenRequests;
        public int BatchRequests;
        public readonly List<string> SecretsSeen = new();
        public int UsersThrottledTimes;
        public Func<int, string, (int Status, string? Purpose, int? RetryAfter)> Batch =
            (_, _) => (200, "user", null);

        private static readonly string[] UserIds = { "u1", "u2" };

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var url = request.RequestUri!.ToString();
            if (url.Contains("/oauth2/v2.0/token"))
            {
                TokenRequests++;
                var form = await request.Content!.ReadAsStringAsync(ct);
                SecretsSeen.Add(form.Split('&').First(p => p.StartsWith("client_secret=")));
                return Json(new { access_token = $"token-{TokenRequests}", expires_in = 3600 });
            }
            if (url.EndsWith("/subscribedSkus"))
                return Json(new { value = Array.Empty<object>() });
            if (url.Contains("/users?"))
            {
                if (UsersThrottledTimes > 0)
                {
                    UsersThrottledTimes--;
                    var throttled = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
                    throttled.Headers.RetryAfter = new global::System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(1));
                    return throttled;
                }
                return Json(new
                {
                    value = UserIds.Select(id => new { id, displayName = $"User {id}", userPrincipalName = $"{id}@example.test", assignedLicenses = Array.Empty<object>() }),
                });
            }
            if (url.EndsWith("/$batch"))
            {
                var attempt = BatchRequests++;
                using var doc = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
                var responses = doc.RootElement.GetProperty("requests").EnumerateArray().Select(r =>
                {
                    var id = r.GetProperty("id").GetString()!;
                    var userId = r.GetProperty("url").GetString()!.Split('/')[2];
                    var (status, purpose, retryAfter) = Batch(attempt, userId);
                    return new Dictionary<string, object?>
                    {
                        ["id"] = id,
                        ["status"] = status,
                        ["headers"] = retryAfter is { } s ? new Dictionary<string, string> { ["Retry-After"] = s.ToString() } : null,
                        ["body"] = status == 200 ? new { userPurpose = purpose } : new { },
                    };
                });
                return Json(new { responses });
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        private static HttpResponseMessage Json(object body) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
        };
    }

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private static (M365GraphReader Reader, InMemorySecretStore Secrets, List<TimeSpan> Waits) Reader(FakeGraph graph)
    {
        var settings = new InMemorySettingsService();
        settings.Set(SettingKeys.M365.ClientId, "11111111-1111-1111-1111-111111111111");
        var secrets = new InMemorySecretStore();
        secrets.Set(ProtectedSecretKeys.M365ClientSecret, "first-secret");
        var waits = new List<TimeSpan>();
        var reader = new M365GraphReader(new Factory(graph), settings, secrets,
            NullLogger<M365GraphReader>.Instance, (wait, _) => { waits.Add(wait); return Task.CompletedTask; });
        return (reader, secrets, waits);
    }

    [Fact]
    public async Task A_throttled_batch_sub_request_is_retried_instead_of_dropping_the_mailbox()
    {
        var graph = new FakeGraph
        {
            // u2 is throttled on the first batch, then answers.
            Batch = (attempt, user) => user == "u2" && attempt == 0 ? (429, null, 7) : (200, "user", null),
        };
        var (reader, _, waits) = Reader(graph);

        var result = await reader.ReadMailboxesAsync(TenantA, default);

        Assert.True(result.Success, result.Error);
        Assert.Equal(new[] { "u1", "u2" }, result.Mailboxes.Select(m => m.ObjectId).Order().ToArray());
        Assert.Equal(2, graph.BatchRequests);
        Assert.Equal(new[] { TimeSpan.FromSeconds(7) }, waits);
    }

    [Fact]
    public async Task Persistent_throttling_is_a_transient_failure_not_fewer_mailboxes()
    {
        var graph = new FakeGraph { Batch = (_, user) => user == "u2" ? (429, null, null) : (200, "user", null) };
        var (reader, _, waits) = Reader(graph);

        var result = await reader.ReadMailboxesAsync(TenantA, default);

        Assert.False(result.Success);
        Assert.False(result.AuthFailure);
        Assert.Empty(result.Mailboxes);
        Assert.Equal(3, waits.Count); // 1 s, 2 s, 4 s back-off without Retry-After
        Assert.Equal(4, graph.BatchRequests);
    }

    [Fact]
    public async Task A_throttled_graph_call_is_retried_with_its_retry_after()
    {
        var graph = new FakeGraph { UsersThrottledTimes = 1 };
        var (reader, _, waits) = Reader(graph);

        var result = await reader.ReadMailboxesAsync(TenantA, default);

        Assert.True(result.Success, result.Error);
        Assert.Equal(new[] { TimeSpan.FromSeconds(1) }, waits);
    }

    [Fact]
    public async Task Tokens_are_cached_per_tenant_and_never_reused_after_a_secret_change()
    {
        var graph = new FakeGraph();
        var (reader, secrets, _) = Reader(graph);

        await reader.ReadMailboxesAsync(TenantA, default);
        await reader.ReadMailboxesAsync(TenantA, default);
        Assert.Equal(1, graph.TokenRequests);

        await reader.ReadMailboxesAsync(TenantB, default);
        Assert.Equal(2, graph.TokenRequests);

        secrets.Set(ProtectedSecretKeys.M365ClientSecret, "rotated-secret");
        await reader.ReadMailboxesAsync(TenantA, default);
        Assert.Equal(3, graph.TokenRequests);
        Assert.Equal("client_secret=rotated-secret", graph.SecretsSeen[^1]);
    }
}
