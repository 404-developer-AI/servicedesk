using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Servicedesk.Api.Tests.TestInfrastructure;
using Servicedesk.Infrastructure.Integrations.Telavox;
using Servicedesk.Infrastructure.Secrets;
using Servicedesk.Infrastructure.Settings;
using Xunit;

namespace Servicedesk.Api.Tests;

/// Telavox call-event probe (v0.1.36): invisible unless switched on with a
/// token, 401 + audit on a wrong token, and a valid post only lands in the
/// in-memory buffer — phone numbers masked, nothing else happens.
public sealed class TelavoxEventProbeTests
{
    private const string Token = "probe-token-0123456789abcdefghijklmnopqrs";
    private const string Url = "/api/public/telavox/call-events?event=ringing&caller=%2B32470123456&ts=1760320000000";

    [Fact]
    public async Task Switched_off_answers_404_even_with_the_right_token()
    {
        using var factory = new SecurityBaselineFactory();
        factory.Secrets.Set(ProtectedSecretKeys.TelavoxEventProbeToken, Token);

        var response = await PostAsync(factory.CreateClient(), Url, Token);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Empty(Buffer(factory).Snapshot().Events);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("wrong")]
    public async Task Missing_or_wrong_token_gets_401_and_is_audited(string? presented)
    {
        using var factory = Enabled();

        var response = await PostAsync(factory.CreateClient(), Url, presented);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains(factory.Audit.Events, e => e.EventType == TelavoxEventTypes.EventProbeDenied);
        Assert.Empty(Buffer(factory).Snapshot().Events);
    }

    [Fact]
    public async Task Valid_post_without_csrf_is_recorded_with_the_number_masked()
    {
        using var factory = Enabled();

        // Server-to-server: no session, no CSRF pair — only the Bearer token.
        var response = await PostAsync(factory.CreateClient(), Url, Token,
            new FormUrlEncodedContent(new Dictionary<string, string> { ["agent"] = "nobody@example.test" }));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var evt = Assert.Single(Buffer(factory).Snapshot().Events);
        Assert.Equal("POST", evt.Method);
        Assert.Contains(evt.Fields, f => f is { Source: "query", Name: "event", Value: "ringing" });
        Assert.Contains(evt.Fields, f => f.Name == "caller" && f.Value == "+32••••••456");
        Assert.Contains(evt.Fields, f => f.Name == "ts" && f.Value == "1760320000000");
        Assert.Contains(evt.Fields, f => f is { Source: "form", Name: "agent" });
        Assert.Equal("nobody@example.test", evt.AgentEmail);
        Assert.False(evt.AgentMatchesUser);
    }

    [Fact]
    public async Task Admin_routes_need_an_admin()
    {
        using var factory = new SecurityBaselineFactory();

        var response = await factory.CreateClient().GetAsync("/api/admin/integrations/telavox/event-probe/");

        Assert.True(response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden,
            $"→ {(int)response.StatusCode}");
    }

    [Theory]
    [InlineData("+32470123456", "+32••••••456")]
    [InlineData("0470 12 34 56", "•••• •• •4 56")]
    [InlineData("ringing", "ringing")]
    [InlineData("agent@example.test", "agent@example.test")]
    [InlineData("2026-10-13T04:00:00Z", "2026-10-13T04:00:00Z")]
    [InlineData("12345", "12345")]
    public void Phone_numbers_are_masked_to_the_last_three_digits(string raw, string expected) =>
        Assert.Equal(expected, TelavoxEventProbeBuffer.MaskPhone(raw));

    [Fact]
    public void Buffer_keeps_only_the_newest_events()
    {
        var buffer = new TelavoxEventProbeBuffer();
        for (var i = 0; i < TelavoxEventProbeBuffer.Capacity + 5; i++)
            buffer.Add(new TelavoxProbeEvent(DateTime.UtcNow, "POST", null, Array.Empty<TelavoxProbeField>(), null, null));

        var (events, received) = buffer.Snapshot();
        Assert.Equal(TelavoxEventProbeBuffer.Capacity, events.Count);
        Assert.Equal(TelavoxEventProbeBuffer.Capacity + 5, received);
    }

    private static SecurityBaselineFactory Enabled()
    {
        var factory = new SecurityBaselineFactory();
        factory.Settings.Set(SettingKeys.Telavox.EventProbeEnabled, "true");
        factory.Secrets.Set(ProtectedSecretKeys.TelavoxEventProbeToken, Token);
        return factory;
    }

    private static TelavoxEventProbeBuffer Buffer(SecurityBaselineFactory factory) =>
        factory.Services.GetRequiredService<TelavoxEventProbeBuffer>();

    private static Task<HttpResponseMessage> PostAsync(HttpClient client, string url, string? token, HttpContent? body = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = body ?? new StringContent("") };
        if (token is not null) request.Headers.Add("Authorization", $"Bearer {token}");
        return client.SendAsync(request);
    }
}
