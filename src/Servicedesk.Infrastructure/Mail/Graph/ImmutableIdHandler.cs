namespace Servicedesk.Infrastructure.Mail.Graph;

/// Opts every Microsoft Graph mail request into <em>immutable</em> item IDs by
/// stamping the documented <c>Prefer: IdType="ImmutableId"</c> header on the
/// outgoing request.
///
/// Why a pipeline handler instead of per-call wiring: a Graph item id normally
/// changes when a message moves folder, which is the root cause of the inbound
/// mark-as-read 404 ("object was not found in the store") — the id we stored at
/// ingest goes stale the moment the message is moved (by our finalizer, an
/// Exchange rule, the user, or other mail software). Immutable IDs make the id
/// survive moves. But Graph forbids <em>mixing</em> id formats across calls, so
/// the preference must be applied to <em>every</em> mail call — delta poll,
/// single-message fetch, mark-as-read, move, attachment fetch, raw-MIME fetch
/// and send. A single innermost handler guarantees that without relying on each
/// call site remembering to add the header.
internal sealed class ImmutableIdHandler : DelegatingHandler
{
    private const string PreferHeader = "Prefer";
    private const string ImmutableIdValue = "IdType=\"ImmutableId\"";

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        // The retry handler re-sends the same HttpRequestMessage, so guard
        // against stamping the header twice on a retried request.
        if (!request.Headers.Contains(PreferHeader))
        {
            request.Headers.TryAddWithoutValidation(PreferHeader, ImmutableIdValue);
        }

        return base.SendAsync(request, cancellationToken);
    }
}
