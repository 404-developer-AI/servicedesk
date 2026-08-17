using Dapper;
using Npgsql;
using Servicedesk.Infrastructure.Mail.Ingest;

namespace Servicedesk.Infrastructure.Mail.Attachments;

/// v0.0.101 — one batched round-trip for everything the timeline enricher
/// needs. Five statements on one connection, all `= ANY(@ids)` against
/// existing indexes (mail_messages pkey, ix_mail_messages_ticket_event,
/// ix_attachments_owner, ix_attachments_event, ix_mail_recipients_mail).
public sealed class MailTimelineLookup : IMailTimelineLookup
{
    private readonly NpgsqlDataSource _dataSource;

    public MailTimelineLookup(NpgsqlDataSource dataSource)
    {
        _dataSource = dataSource;
    }

    public async Task<MailTimelineBatch> LoadAsync(
        IReadOnlyCollection<Guid> mailIds,
        IReadOnlyCollection<long> eventIds,
        IReadOnlyCollection<long> sentMailEventIds,
        CancellationToken ct)
    {
        if (mailIds.Count == 0 && eventIds.Count == 0 && sentMailEventIds.Count == 0)
            return MailTimelineBatch.Empty;

        var mailIdArr = mailIds.Distinct().ToArray();
        var eventIdArr = eventIds.Distinct().ToArray();
        var sentEventIdArr = sentMailEventIds.Distinct().ToArray();

        // Recipients cover the inbound mails AND the mails behind MailSent
        // events; the latter ids are only known server-side. Two UNION ALL
        // arms (each an index scan) instead of `… OR mail_id IN (subselect)`,
        // which the planner cannot BitmapOr and turns into a full scan of
        // mail_recipients. r.id is selected so the C# side can dedupe/order.
        var sql =
            MailMessageRepository.SelectColumns + " FROM mail_messages WHERE id = ANY(@mailIds);\n" +
            MailMessageRepository.SelectColumns + " FROM mail_messages WHERE ticket_event_id = ANY(@sentEventIds);\n" +
            AttachmentRepository.SelectColumns + " FROM attachments WHERE owner_kind = 'Mail' AND owner_id = ANY(@mailIds) ORDER BY created_utc, id;\n" +
            AttachmentRepository.SelectColumns + " FROM attachments WHERE event_id = ANY(@eventIds) ORDER BY created_utc, id;\n" +
            """
            SELECT r.id AS Id, r.mail_id AS MailId, r.kind AS Kind, r.address::text AS Address, r.display_name AS DisplayName
              FROM mail_recipients r
             WHERE r.mail_id = ANY(@mailIds)
            UNION ALL
            SELECT r.id AS Id, r.mail_id AS MailId, r.kind AS Kind, r.address::text AS Address, r.display_name AS DisplayName
              FROM mail_recipients r
              JOIN mail_messages m ON m.id = r.mail_id
             WHERE m.ticket_event_id = ANY(@sentEventIds)
            """;

        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var grid = await conn.QueryMultipleAsync(new CommandDefinition(
            sql,
            new { mailIds = mailIdArr, sentEventIds = sentEventIdArr, eventIds = eventIdArr },
            cancellationToken: ct));

        var mails = (await grid.ReadAsync<MailMessageRow>()).ToList();
        var sentMails = (await grid.ReadAsync<MailMessageRow>()).ToList();
        var mailAttachments = (await grid.ReadAsync<AttachmentRow>()).ToList();
        var eventAttachments = (await grid.ReadAsync<AttachmentRow>()).ToList();
        var recipients = (await grid.ReadAsync<RecipientRow>())
            .GroupBy(r => r.Id).Select(g => g.First())
            .OrderBy(r => r.Id)
            .ToList();

        var mailsById = new Dictionary<Guid, MailMessageRow>(mails.Count);
        foreach (var m in mails) mailsById[m.Id] = m;

        var mailsBySentEvent = new Dictionary<long, MailMessageRow>(sentMails.Count);
        foreach (var m in sentMails)
            if (m.TicketEventId is { } eid && !mailsBySentEvent.ContainsKey(eid)) mailsBySentEvent[eid] = m;

        return new MailTimelineBatch(
            mailsById,
            mailsBySentEvent,
            mailAttachments.ToLookup(a => a.OwnerId),
            eventAttachments.Where(a => a.EventId.HasValue).ToLookup(a => a.EventId!.Value),
            recipients.ToLookup(r => r.MailId, r => new MailRecipientRow(r.Kind, r.Address, r.DisplayName)));
    }

    private sealed class RecipientRow
    {
        public long Id { get; set; }
        public Guid MailId { get; set; }
        public string Kind { get; set; } = string.Empty;
        public string Address { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;
    }
}
