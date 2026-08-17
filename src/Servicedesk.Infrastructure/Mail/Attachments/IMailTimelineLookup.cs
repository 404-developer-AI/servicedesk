using Servicedesk.Infrastructure.Mail.Ingest;

namespace Servicedesk.Infrastructure.Mail.Attachments;

/// v0.0.101 — batched reads for <see cref="MailTimelineEnricher"/>.
///
/// The enricher used to resolve mail rows, recipients and attachments per
/// timeline event (2–3 queries per inbound mail, 1–3 per note/comment/sent
/// mail), which made every ticket open cost O(events) round-trips — 60–100
/// extra queries on a busy 40-event ticket. This lookup takes the id sets the
/// enricher collects up front and returns everything in one batch, so the
/// enricher's query count is constant in the timeline length.
public interface IMailTimelineLookup
{
    /// <param name="mailIds">mail_message ids referenced by <c>MailReceived</c> events (from their metadata).</param>
    /// <param name="eventIds">ids of <c>Note</c> / <c>Comment</c> / <c>MailSent</c> events (attachments via <c>attachments.event_id</c>).</param>
    /// <param name="sentMailEventIds">ids of <c>MailSent</c> events (mail row via <c>mail_messages.ticket_event_id</c>).</param>
    Task<MailTimelineBatch> LoadAsync(
        IReadOnlyCollection<Guid> mailIds,
        IReadOnlyCollection<long> eventIds,
        IReadOnlyCollection<long> sentMailEventIds,
        CancellationToken ct);
}

public sealed class MailTimelineBatch
{
    public static readonly MailTimelineBatch Empty = new(
        new Dictionary<Guid, MailMessageRow>(),
        new Dictionary<long, MailMessageRow>(),
        Array.Empty<AttachmentRow>().ToLookup(a => a.OwnerId),
        Array.Empty<AttachmentRow>().ToLookup(a => a.EventId ?? 0),
        Array.Empty<(Guid MailId, MailRecipientRow Row)>().ToLookup(r => r.MailId, r => r.Row));

    public MailTimelineBatch(
        IReadOnlyDictionary<Guid, MailMessageRow> mailsById,
        IReadOnlyDictionary<long, MailMessageRow> mailsBySentEventId,
        ILookup<Guid, AttachmentRow> attachmentsByMailId,
        ILookup<long, AttachmentRow> attachmentsByEventId,
        ILookup<Guid, MailRecipientRow> recipientsByMailId)
    {
        MailsById = mailsById;
        MailsBySentEventId = mailsBySentEventId;
        AttachmentsByMailId = attachmentsByMailId;
        AttachmentsByEventId = attachmentsByEventId;
        RecipientsByMailId = recipientsByMailId;
    }

    public IReadOnlyDictionary<Guid, MailMessageRow> MailsById { get; }
    public IReadOnlyDictionary<long, MailMessageRow> MailsBySentEventId { get; }
    /// Attachments owned by an inbound mail (owner_kind = 'Mail'), in (created_utc, id) order per mail.
    public ILookup<Guid, AttachmentRow> AttachmentsByMailId { get; }
    /// Attachments linked to a timeline event (attachments.event_id), in (created_utc, id) order per event.
    public ILookup<long, AttachmentRow> AttachmentsByEventId { get; }
    /// Recipients for both the inbound mails and the mails behind MailSent events, in row order per mail.
    public ILookup<Guid, MailRecipientRow> RecipientsByMailId { get; }
}
