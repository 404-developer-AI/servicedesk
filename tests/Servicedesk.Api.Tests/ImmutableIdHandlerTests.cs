using System.Net;
using Servicedesk.Infrastructure.Mail.Graph;
using Xunit;

namespace Servicedesk.Api.Tests;

/// Proves the Graph mail client's immutable-id opt-in is applied to every
/// outgoing request. This is the all-or-nothing guarantee that closes the
/// mark-as-read 404 race (v0.0.91): a message id stored at ingest must survive
/// folder moves, which only holds if Prefer: IdType="ImmutableId" rides on the
/// request — including mark-as-read, move and the delta poll.
public sealed class ImmutableIdHandlerTests
{
    private const string PreferHeader = "Prefer";
    private const string ImmutableIdValue = "IdType=\"ImmutableId\"";

    [Fact]
    public async Task Stamps_immutable_id_prefer_header_on_request()
    {
        var capture = new CapturingHandler();
        var handler = new ImmutableIdHandler { InnerHandler = capture };
        var invoker = new HttpMessageInvoker(handler);

        using var request = new HttpRequestMessage(HttpMethod.Get, "https://graph.microsoft.com/v1.0/me/messages");
        await invoker.SendAsync(request, CancellationToken.None);

        Assert.NotNull(capture.LastRequest);
        Assert.True(capture.LastRequest!.Headers.TryGetValues(PreferHeader, out var values));
        Assert.Equal(ImmutableIdValue, Assert.Single(values!));
    }

    [Fact]
    public async Task Does_not_duplicate_header_when_already_present()
    {
        // The retry handler re-sends the same request; the header must not pile up.
        var capture = new CapturingHandler();
        var handler = new ImmutableIdHandler { InnerHandler = capture };
        var invoker = new HttpMessageInvoker(handler);

        using var request = new HttpRequestMessage(HttpMethod.Patch, "https://graph.microsoft.com/v1.0/me/messages/abc");
        await invoker.SendAsync(request, CancellationToken.None);
        await invoker.SendAsync(request, CancellationToken.None);

        Assert.True(capture.LastRequest!.Headers.TryGetValues(PreferHeader, out var values));
        Assert.Single(values!);
    }

    private sealed class CapturingHandler : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }
}
