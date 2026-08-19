using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Servicedesk.Api.Tests.TestInfrastructure;
using Servicedesk.Infrastructure.Portal;
using Servicedesk.Infrastructure.Secrets;
using Servicedesk.Infrastructure.Settings;
using Xunit;

namespace Servicedesk.Api.Tests;

/// v0.1.0 — Cloudflare Turnstile siteverify wrapper. Every non-happy path
/// must FAIL CLOSED (registration refused): missing secret, HTTP error,
/// timeout, malformed body, success=false, action or hostname mismatch.
/// Cloudflare's documented dummy keys are used where a real value shape
/// matters (1x0000000000000000000000000000000AA = always passes,
/// 2x0000000000000000000000000000000AA = always fails); the HTTP leg is a
/// fake handler so no network is touched.
public sealed class TurnstileVerifierTests
{
    private const string AlwaysPassSecret = "1x0000000000000000000000000000000AA";
    private const string AlwaysFailSecret = "2x0000000000000000000000000000000AA";

    [Fact]
    public async Task Missing_secret_fails_closed_without_network()
    {
        var handler = new FakeHandler(_ => Task.FromException<HttpResponseMessage>(new InvalidOperationException("network must not be touched")));
        var sut = Build(handler, secret: null);

        var verdict = await sut.VerifyAsync("token", "1.2.3.4", "portal-register", "sd.example.com", CancellationToken.None);

        Assert.False(verdict.Success);
        Assert.Equal("secret_missing", verdict.Reason);
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task Missing_token_fails_closed_without_network()
    {
        var handler = new FakeHandler(_ => Task.FromException<HttpResponseMessage>(new InvalidOperationException("network must not be touched")));
        var sut = Build(handler, secret: AlwaysPassSecret);

        var verdict = await sut.VerifyAsync("", "1.2.3.4", "portal-register", "sd.example.com", CancellationToken.None);

        Assert.False(verdict.Success);
        Assert.Equal("missing_token", verdict.Reason);
    }

    [Fact]
    public async Task Success_with_matching_action_and_hostname_passes()
    {
        var handler = new FakeHandler(_ => Json("""{"success":true,"action":"portal-register","hostname":"sd.example.com"}"""));
        var sut = Build(handler, secret: AlwaysPassSecret);

        var verdict = await sut.VerifyAsync("token", "1.2.3.4", "portal-register", "SD.example.com", CancellationToken.None);

        Assert.True(verdict.Success);
        Assert.Equal(1, handler.Calls);
        Assert.Contains("secret=" + AlwaysPassSecret, handler.LastBody);
        Assert.Contains("response=token", handler.LastBody);
        Assert.Contains("remoteip=1.2.3.4", handler.LastBody);
    }

    [Fact]
    public async Task Cloudflare_rejection_fails()
    {
        var handler = new FakeHandler(_ => Json("""{"success":false,"error-codes":["invalid-input-response"]}"""));
        var sut = Build(handler, secret: AlwaysFailSecret);

        var verdict = await sut.VerifyAsync("token", null, "portal-register", "sd.example.com", CancellationToken.None);

        Assert.False(verdict.Success);
        Assert.StartsWith("rejected:", verdict.Reason);
    }

    [Fact]
    public async Task Action_mismatch_fails()
    {
        var handler = new FakeHandler(_ => Json("""{"success":true,"action":"login","hostname":"sd.example.com"}"""));
        var sut = Build(handler, secret: AlwaysPassSecret);

        var verdict = await sut.VerifyAsync("token", null, "portal-register", "sd.example.com", CancellationToken.None);

        Assert.False(verdict.Success);
        Assert.Equal("action_mismatch", verdict.Reason);
    }

    [Fact]
    public async Task Hostname_mismatch_fails()
    {
        var handler = new FakeHandler(_ => Json("""{"success":true,"action":"portal-register","hostname":"evil.example.net"}"""));
        var sut = Build(handler, secret: AlwaysPassSecret);

        var verdict = await sut.VerifyAsync("token", null, "portal-register", "sd.example.com", CancellationToken.None);

        Assert.False(verdict.Success);
        Assert.Equal("hostname_mismatch", verdict.Reason);
    }

    [Fact]
    public async Task Http_error_fails()
    {
        var handler = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.BadGateway));
        var sut = Build(handler, secret: AlwaysPassSecret);

        var verdict = await sut.VerifyAsync("token", null, "portal-register", null, CancellationToken.None);

        Assert.False(verdict.Success);
        Assert.Equal("http_502", verdict.Reason);
    }

    [Fact]
    public async Task Malformed_body_fails()
    {
        var handler = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("<html>not json</html>", Encoding.UTF8, "text/html"),
        });
        var sut = Build(handler, secret: AlwaysPassSecret);

        var verdict = await sut.VerifyAsync("token", null, "portal-register", null, CancellationToken.None);

        Assert.False(verdict.Success);
    }

    [Fact]
    public async Task Timeout_fails_closed()
    {
        var handler = new FakeHandler(async ct =>
        {
            await Task.Delay(TimeSpan.FromSeconds(5), ct);
            return Json("""{"success":true}""");
        });
        var settings = new InMemorySettingsService();
        settings.Set(SettingKeys.Portal.TurnstileTimeoutSeconds, "1");
        var sut = Build(handler, secret: AlwaysPassSecret, settings);

        var verdict = await sut.VerifyAsync("token", null, "portal-register", null, CancellationToken.None);

        Assert.False(verdict.Success);
        Assert.Equal("timeout", verdict.Reason);
    }

    private static TurnstileVerifier Build(FakeHandler handler, string? secret, InMemorySettingsService? settings = null)
    {
        var secrets = new InMemorySecretStore();
        if (secret is not null) secrets.SetAsync(ProtectedSecretKeys.PortalTurnstileSecret, secret).GetAwaiter().GetResult();
        return new TurnstileVerifier(
            new FakeHttpClientFactory(handler),
            secrets,
            settings ?? new InMemorySettingsService(),
            NullLogger<TurnstileVerifier>.Instance);
    }

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json"),
    };

    private sealed class FakeHttpClientFactory : IHttpClientFactory
    {
        private readonly HttpMessageHandler _handler;
        public FakeHttpClientFactory(HttpMessageHandler handler) => _handler = handler;
        public HttpClient CreateClient(string name) => new(_handler, disposeHandler: false);
    }

    private sealed class FakeHandler : HttpMessageHandler
    {
        private readonly Func<CancellationToken, Task<HttpResponseMessage>> _respond;
        public int Calls { get; private set; }
        public string LastBody { get; private set; } = string.Empty;

        public FakeHandler(Func<CancellationToken, HttpResponseMessage> respond)
            : this(ct => Task.FromResult(respond(ct))) { }

        public FakeHandler(Func<CancellationToken, Task<HttpResponseMessage>> respond) => _respond = respond;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            Assert.Equal(TurnstileVerifier.SiteVerifyUrl, request.RequestUri!.ToString());
            LastBody = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            return await _respond(cancellationToken);
        }
    }
}
