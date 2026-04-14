using Servicedesk.Domain.Tickets;
using Servicedesk.Infrastructure.Persistence.Tickets;

namespace Servicedesk.Infrastructure.Mail.Attachments;

/// Post-processes a <see cref="TicketDetail"/> before it leaves the API so
/// <c>MailReceived</c> events carry renderable HTML: the body_html blob is
/// streamed from storage, any <c>cid:&lt;id&gt;</c> references are rewritten
/// to authenticated attachment-download URLs, and the result replaces the
/// plaintext snippet. Events are left untouched when the mail has no HTML
/// body or the lookup fails — the timeline falls back to plaintext.
public interface IMailTimelineEnricher
{
    Task<TicketDetail> EnrichAsync(TicketDetail detail, CancellationToken ct);
}
