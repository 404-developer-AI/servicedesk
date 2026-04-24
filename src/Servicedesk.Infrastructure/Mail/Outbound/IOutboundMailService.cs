using Servicedesk.Domain.Tickets;
using Servicedesk.Infrastructure.Mail.Graph;

namespace Servicedesk.Infrastructure.Mail.Outbound;

/// Orchestrates sending an outbound mail on behalf of an agent: resolves the
/// from-mailbox off the queue, builds a plus-addressed Reply-To so inbound
/// replies route back to the same ticket, calls <see cref="IGraphMailClient"/>,
/// persists a <c>MailSent</c> ticket event plus a matching <c>mail_messages</c>
/// row, and triggers SLA recalc. Transport failures bubble up as exceptions;
/// validation / config failures come back as a non-Success result so the
/// endpoint can respond with a clean 4xx.
public interface IOutboundMailService
{
    Task<OutboundMailResult> SendAsync(OutboundMailRequest request, CancellationToken ct);
}

public enum OutboundMailKind
{
    Reply,
    ReplyAll,
    New,
    Forward,
}

public sealed record OutboundMailRequest(
    Guid TicketId,
    Guid AuthorUserId,
    OutboundMailKind Kind,
    IReadOnlyList<GraphRecipient> To,
    IReadOnlyList<GraphRecipient> Cc,
    IReadOnlyList<GraphRecipient> Bcc,
    string Subject,
    string BodyHtml,
    IReadOnlyList<Guid>? AttachmentIds = null,
    /// Agent ids tagged via @@-mention in the editor body (v0.0.12 stap 3).
    /// Filtered server-side against the Agent+Admin set; customer ids and
    /// unknown guids are silently dropped before the event metadata is
    /// written.
    IReadOnlyList<Guid>? MentionedUserIds = null,
    /// Intake-form instance ids embedded via `::`-mention (v0.0.19). Each
    /// id must point to a Draft instance owned by this ticket; the Mail
    /// service mints a token, embeds the link in the body, and atomically
    /// flips the instance to Sent + writes an IntakeFormSent ticket event
    /// once Graph accepts the message.
    IReadOnlyList<Guid>? LinkedFormIds = null);

public enum OutboundMailStatus
{
    Sent,
    TicketNotFound,
    NoMailboxConfigured,
    InvalidRequest,
    AttachmentTooLarge,
}

public sealed record OutboundMailResult(
    OutboundMailStatus Status,
    TicketEvent? Event,
    string? ErrorMessage,
    int MentionedUserCount = 0)
{
    public static OutboundMailResult Ok(TicketEvent evt, int mentionedUserCount = 0) =>
        new(OutboundMailStatus.Sent, evt, null, mentionedUserCount);
    public static OutboundMailResult NotFound() => new(OutboundMailStatus.TicketNotFound, null, null);
    public static OutboundMailResult MissingMailbox() => new(
        OutboundMailStatus.NoMailboxConfigured, null,
        "No outbound mailbox is configured on this ticket's queue. Set one in Settings → Tickets → Queues.");
    public static OutboundMailResult Invalid(string message) => new(
        OutboundMailStatus.InvalidRequest, null, message);
    public static OutboundMailResult TooLarge(string message) => new(
        OutboundMailStatus.AttachmentTooLarge, null, message);
}
