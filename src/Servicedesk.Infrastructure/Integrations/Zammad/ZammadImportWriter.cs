using System.Text.Json;
using System.Text.RegularExpressions;
using Dapper;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Servicedesk.Infrastructure.Integrations.Zammad;

/// Dapper-backed importer. Writes the tickets / ticket_bodies / ticket_events
/// triad in one transaction so a partial-create never leaves the timeline
/// dangling. Idempotent via the partial unique index <c>ix_tickets_zammad_id</c>:
/// a re-run of the same import returns <see cref="ZammadImportRecordResult.AlreadyImported"/>
/// instead of duplicating the row.
public sealed class ZammadImportWriter : IZammadImportWriter
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly ILogger<ZammadImportWriter> _logger;

    public ZammadImportWriter(
        NpgsqlDataSource dataSource,
        ILogger<ZammadImportWriter> logger)
    {
        _dataSource = dataSource;
        _logger = logger;
    }

    public async Task<ZammadImportWriteResult> WriteAsync(
        ZammadImportWriteInput input,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(input);

        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        // Resolve the requester's primary company. Mirrors the historical
        // backfill in DatabaseBootstrapper: a contact with a primary link
        // pins the ticket to that company; no-primary means
        // awaiting_company_assignment=true so the agent picks one later.
        var primaryCompanyId = await conn.ExecuteScalarAsync<Guid?>(new CommandDefinition(
            "SELECT company_id FROM contact_companies WHERE contact_id = @cid AND role = 'primary' LIMIT 1",
            new { cid = input.ContactId }, tx, cancellationToken: ct));

        // Pre-flight idempotency probe: an earlier import-run may already
        // have linked this Zammad id. The partial UNIQUE index will catch
        // the conflict on INSERT but reading first lets us skip the
        // body/events writes entirely and gives us the original ticket id
        // to surface to the admin.
        var existingTicketId = await conn.ExecuteScalarAsync<Guid?>(new CommandDefinition(
            "SELECT id FROM tickets WHERE zammad_ticket_id = @zid LIMIT 1",
            new { zid = input.ZammadTicketId }, tx, cancellationToken: ct));
        if (existingTicketId is not null)
        {
            await tx.CommitAsync(ct);
            return new ZammadImportWriteResult(
                Result: ZammadImportRecordResult.AlreadyImported,
                LocalTicketId: existingTicketId,
                FailureReason: null);
        }

        var firstArticle = input.Articles.Count > 0 ? input.Articles[0] : null;
        var (firstBodyText, firstBodyHtml) = SplitBody(firstArticle);

        // Group blob-staged attachment plans by their Zammad article-id so
        // the per-article event insert can flush exactly its own rows.
        // Plans whose article never lands as an event (shouldn't happen
        // in normal flow but tolerate gracefully) are silently dropped.
        var attachmentsByArticle = new Dictionary<long, List<ZammadImportAttachmentPlan>>();
        foreach (var p in input.Attachments)
        {
            if (!attachmentsByArticle.TryGetValue(p.ZammadArticleId, out var bucket))
            {
                bucket = new List<ZammadImportAttachmentPlan>();
                attachmentsByArticle[p.ZammadArticleId] = bucket;
            }
            bucket.Add(p);
        }

        // pending_till_utc carries the Zammad pending_time so the local
        // scheduler picks the ticket back up at the same wake-up moment.
        // Normalise to UTC before binding so Npgsql doesn't reject a
        // Kind=Unspecified DateTime on the TIMESTAMPTZ column.
        DateTime? pendingTillUtc = input.PendingTillUtc is null
            ? null
            : DateTime.SpecifyKind(input.PendingTillUtc.Value, DateTimeKind.Utc);

        const string insertTicket = """
            INSERT INTO tickets (subject, requester_contact_id, queue_id,
                                 status_id, priority_id, source,
                                 company_id, awaiting_company_assignment, company_resolved_via,
                                 zammad_ticket_id, zammad_ticket_number,
                                 pending_till_utc,
                                 ticket_type_id)
            VALUES (@Subject, @ContactId, @QueueId,
                    @StatusId, @PriorityId, 'Zammad',
                    @CompanyId, @AwaitingCompany, @CompanyResolvedVia,
                    @ZammadTicketId, @ZammadTicketNumber,
                    @PendingTillUtc,
                    (SELECT id FROM ticket_types WHERE code = 'support' LIMIT 1))
            ON CONFLICT (zammad_ticket_id) WHERE zammad_ticket_id IS NOT NULL DO NOTHING
            RETURNING id
            """;

        var newTicketId = await conn.ExecuteScalarAsync<Guid?>(new CommandDefinition(insertTicket, new
        {
            Subject = string.IsNullOrWhiteSpace(input.ZammadTicketTitle) ? "(no subject)" : input.ZammadTicketTitle,
            ContactId = input.ContactId,
            QueueId = input.QueueId,
            StatusId = input.StatusId,
            PriorityId = input.PriorityId,
            CompanyId = primaryCompanyId,
            AwaitingCompany = primaryCompanyId is null,
            CompanyResolvedVia = primaryCompanyId is null ? null : "primary",
            ZammadTicketId = input.ZammadTicketId,
            ZammadTicketNumber = input.ZammadTicketNumber,
            PendingTillUtc = pendingTillUtc,
        }, tx, cancellationToken: ct));

        // Edge case: another worker raced us in between our pre-flight
        // probe and the INSERT. The partial unique index turned the row
        // into a no-op; surface the (now-committed) existing ticket id.
        if (newTicketId is null)
        {
            var racedId = await conn.ExecuteScalarAsync<Guid?>(new CommandDefinition(
                "SELECT id FROM tickets WHERE zammad_ticket_id = @zid LIMIT 1",
                new { zid = input.ZammadTicketId }, tx, cancellationToken: ct));
            await tx.CommitAsync(ct);
            return new ZammadImportWriteResult(
                Result: ZammadImportRecordResult.AlreadyImported,
                LocalTicketId: racedId,
                FailureReason: null);
        }

        const string insertBody = """
            INSERT INTO ticket_bodies (ticket_id, body_text, body_html)
            VALUES (@TicketId, @BodyText, @BodyHtml)
            """;
        await conn.ExecuteAsync(new CommandDefinition(insertBody, new
        {
            TicketId = newTicketId.Value,
            BodyText = firstBodyText,
            BodyHtml = firstBodyHtml,
        }, tx, cancellationToken: ct));

        // 'Created' event mirrors what TicketRepository.CreateAsync writes
        // for non-migrated tickets. body is NULL because the actual text
        // lives in ticket_bodies above; metadata carries the Zammad
        // provenance so the side-panel badge (fase 5) can render without
        // a second lookup. RETURNING captures the event id so the mail-
        // anchor INSERT below can FK to it.
        const string insertCreated = """
            INSERT INTO ticket_events (ticket_id, event_type, author_contact_id,
                                       body_text, body_html, metadata, created_utc)
            VALUES (@TicketId, 'Created', @AuthorContactId,
                    NULL, NULL, @MetadataJson::jsonb, COALESCE(@CreatedUtc, now()))
            RETURNING id
            """;
        var createdEventId = await conn.ExecuteScalarAsync<long>(new CommandDefinition(insertCreated, new
        {
            TicketId = newTicketId.Value,
            AuthorContactId = (Guid?)input.ContactId,
            MetadataJson = JsonSerializer.Serialize(new
            {
                source = "Zammad",
                zammadTicketId = input.ZammadTicketId,
                zammadTicketNumber = input.ZammadTicketNumber,
                zammadFirstArticleId = firstArticle?.Id,
            }),
            CreatedUtc = firstArticle?.CreatedAt?.UtcDateTime,
        }, tx, cancellationToken: ct));

        // First article is bound to the 'Created' event. If it was an
        // email, write the mail_messages thread anchor too so a future
        // customer reply matches via In-Reply-To/References. The anchor's
        // own UUID is captured so the attachment-row INSERTs below can
        // FK to it (owner_kind='Mail', owner_id=mailId).
        Guid? firstMailId = null;
        if (firstArticle is not null)
        {
            firstMailId = await TryInsertMailAnchorAsync(
                conn, tx, newTicketId.Value, createdEventId, firstArticle, ct);
            if (firstMailId is not null)
            {
                // Stamp mail_message_id on the Created event so the
                // timeline-enricher's recipient + cid-rewrite paths apply
                // even when the originating article landed under the
                // 'Created' event-type. ticket_bodies still carries the
                // body text/html for the description block (no rewrite
                // there) but the timeline row will render attachments.
                await conn.ExecuteAsync(new CommandDefinition("""
                    UPDATE ticket_events
                       SET metadata = jsonb_set(metadata, '{mail_message_id}', to_jsonb(@MailId::text), true)
                     WHERE id = @EventId
                    """, new { MailId = firstMailId.Value, EventId = createdEventId }, tx, cancellationToken: ct));
            }
        }
        if (firstArticle is not null && attachmentsByArticle.TryGetValue(firstArticle.Id, out var firstPlans))
        {
            await InsertAttachmentsForEventAsync(
                conn, tx, newTicketId.Value, createdEventId, firstMailId, firstPlans, ct);
        }

        // Subsequent articles → timeline events. Sender + type drive the
        // event_type; internal flag carries through to is_internal so an
        // imported internal note stays hidden from the customer portal.
        // Phase 5: per-event attachment rows are inserted right after each
        // event so the timeline-enricher can pick them up by event_id (or
        // by mail_id for the email cases) without a post-pass.
        const string insertEvent = """
            INSERT INTO ticket_events (ticket_id, event_type, author_user_id, author_contact_id,
                                       body_text, body_html, metadata, is_internal, created_utc)
            VALUES (@TicketId, @EventType, NULL, @AuthorContactId,
                    @BodyText, @BodyHtml, @MetadataJson::jsonb, @IsInternal,
                    COALESCE(@CreatedUtc, now()))
            RETURNING id
            """;
        for (var i = 1; i < input.Articles.Count; i++)
        {
            var a = input.Articles[i];
            var (et, isInternal, authorContact) = MapArticle(a, input.ContactId);
            var (bodyText, bodyHtml) = SplitBody(a);
            var eventId = await conn.ExecuteScalarAsync<long>(new CommandDefinition(insertEvent, new
            {
                TicketId = newTicketId.Value,
                EventType = et,
                AuthorContactId = authorContact,
                BodyText = bodyText,
                BodyHtml = bodyHtml,
                IsInternal = isInternal,
                MetadataJson = JsonSerializer.Serialize(new
                {
                    source = "Zammad",
                    zammadArticleId = a.Id,
                    zammadSender = a.Sender,
                    zammadType = a.Type,
                    zammadFrom = a.From,
                    zammadTo = a.To,
                    zammadSubject = a.Subject,
                    zammadCreatedBy = a.CreatedByEmail,
                }),
                CreatedUtc = a.CreatedAt?.UtcDateTime,
            }, tx, cancellationToken: ct));

            var mailId = await TryInsertMailAnchorAsync(conn, tx, newTicketId.Value, eventId, a, ct);
            if (mailId is not null)
            {
                // Re-stamp metadata so MailTimelineEnricher.TryGetMailMessageId
                // can locate the row without joining mail_messages.ticket_event_id.
                // jsonb_set with create_missing=true tolerates missing keys.
                await conn.ExecuteAsync(new CommandDefinition("""
                    UPDATE ticket_events
                       SET metadata = jsonb_set(metadata, '{mail_message_id}', to_jsonb(@MailId::text), true)
                     WHERE id = @EventId
                    """, new { MailId = mailId.Value, EventId = eventId }, tx, cancellationToken: ct));
            }

            if (attachmentsByArticle.TryGetValue(a.Id, out var plans))
            {
                await InsertAttachmentsForEventAsync(
                    conn, tx, newTicketId.Value, eventId, mailId, plans, ct);
            }
        }

        await tx.CommitAsync(ct);

        _logger.LogInformation(
            "Zammad import wrote ticket {LocalId} from upstream {ZammadId} (articles {ArticleCount})",
            newTicketId.Value, input.ZammadTicketId, input.Articles.Count);

        return new ZammadImportWriteResult(
            Result: ZammadImportRecordResult.Imported,
            LocalTicketId: newTicketId,
            FailureReason: null);
    }

    /// Splits a Zammad article body into the local (body_text, body_html)
    /// pair. content_type=text/html keeps the HTML and synthesises a
    /// best-effort plain-text mirror so the to_tsvector body_search index
    /// stays meaningful (otherwise the GIN index would tokenise raw HTML
    /// tags). content_type=text/plain (or missing) treats the body as
    /// plain and leaves body_html NULL.
    internal static (string Text, string? Html) SplitBody(ZammadArticle? article)
    {
        if (article is null || string.IsNullOrEmpty(article.Body))
        {
            return (string.Empty, null);
        }
        var ct = article.ContentType?.Trim().ToLowerInvariant() ?? string.Empty;
        if (ct.StartsWith("text/html"))
        {
            var plain = StripHtml(article.Body);
            return (plain, article.Body);
        }
        return (article.Body, null);
    }

    private static string StripHtml(string html)
    {
        // Best-effort plain-text projection. Imported bodies feed the
        // ticket_bodies.body_search GIN index, so a tag-laden body would
        // tokenise <div> / <p> as searchable terms. Stripping tags +
        // collapsing whitespace gets us a sensible search index without
        // pulling in a full HTML parser. The HTML itself is preserved on
        // body_html for the render path.
        var noTags = Regex.Replace(html, "<[^>]+>", " ");
        var decoded = System.Net.WebUtility.HtmlDecode(noTags);
        return Regex.Replace(decoded, @"\s+", " ").Trim();
    }

    /// Maps one Zammad article to the local event tuple. Sender +
    /// article type drive the CHECK-constrained event_type so the
    /// existing list/detail query paths render imported timelines
    /// without special-casing.
    ///
    /// Note-mapping rule: locally <c>event_type=Note</c> is hardcoded
    /// to render with the "Internal note" label, while a public reply
    /// is <c>event_type=Comment</c> with <c>is_internal=false</c>. We
    /// therefore split Zammad notes on their <c>internal</c> flag —
    /// internal notes land as <c>Note</c>, public notes land as
    /// <c>Comment</c> (the local public-reply event).
    internal static (string EventType, bool IsInternal, Guid? AuthorContactId) MapArticle(
        ZammadArticle article, Guid contactId)
    {
        var sender = article.Sender ?? string.Empty;
        var type = (article.Type ?? string.Empty).ToLowerInvariant();
        var isInternal = article.Internal;

        // Customer / Agent / System. Customer articles get the contact
        // attribution so portal visibility and notification rules treat
        // them like local customer comments. Agent and System articles
        // stay anonymous (NULL author_*) since we have no local-user
        // mapping in fase 4; the metadata.zammadCreatedBy field carries
        // the upstream identity.
        var authorContactId = string.Equals(sender, "Customer", StringComparison.OrdinalIgnoreCase)
            ? (Guid?)contactId
            : null;

        var eventType = (sender, type, isInternal) switch
        {
            ("Customer", "email", _) => "MailReceived",
            ("Agent",    "email", _) => "MailSent",
            (_, "note", true)        => "Note",
            _                         => "Comment",
        };
        return (eventType, isInternal, authorContactId);
    }

    /// Placeholder mailbox column for imported rows. mail_messages.mailbox_address
    /// is NOT NULL CITEXT and normally carries the Graph mailbox the inbound
    /// poller is watching. Zammad-imported anchors never participate in the
    /// Graph finalize sweep (mailbox_moved_utc is set to now() below), so the
    /// value is sentinel-only — agents inspecting an imported row see the
    /// "zammad-import" tag instead of a real mailbox.
    private const string ImportedMailboxSentinel = "zammad-import@local";

    /// Inserts a minimal mail_messages row for an imported Zammad email
    /// article so a future customer reply (which carries In-Reply-To set to
    /// this Zammad-side Message-ID) matches the imported ticket. Returns
    /// the inserted (or pre-existing) mail-message UUID so the caller can
    /// stamp it onto the event's metadata + attach attachments under
    /// <c>owner_kind='Mail'</c>. We skip non-email articles, articles
    /// without a Message-ID, and articles without a From address (the
    /// column is NOT NULL CITEXT) — null return tells the caller to use
    /// the ticket-owned fallback for attachments.
    ///
    /// Idempotent via <c>DO UPDATE SET message_id = EXCLUDED.message_id</c>
    /// (no-op assignment) so a re-import or a cross-ticket duplicate
    /// Message-ID still returns the existing row's id via RETURNING. See
    /// [[upsert-race-pattern]] memory for why this beats DO NOTHING +
    /// fallback SELECT under cross-worker race conditions.
    /// mailbox_moved_utc=now() keeps the inbound finalizer-sweep from
    /// picking the row up.
    private static async Task<Guid?> TryInsertMailAnchorAsync(
        Npgsql.NpgsqlConnection conn,
        System.Data.Common.DbTransaction tx,
        Guid ticketId,
        long eventId,
        ZammadArticle article,
        CancellationToken ct)
    {
        var type = (article.Type ?? string.Empty).ToLowerInvariant();
        if (type != "email") return null;

        var messageId = (article.MessageId ?? string.Empty).Trim().Trim('<', '>');
        if (string.IsNullOrEmpty(messageId)) return null;

        var fromAddress = ExtractEmailAddress(article.From);
        if (string.IsNullOrEmpty(fromAddress)) return null;

        var direction = string.Equals(article.Sender, "Agent", StringComparison.OrdinalIgnoreCase)
            ? "Outbound"
            : "Inbound";

        const string sql = """
            INSERT INTO mail_messages (
                message_id, from_address, from_name, subject,
                mailbox_address, received_utc, sent_utc, direction,
                ticket_id, ticket_event_id, mailbox_moved_utc)
            VALUES (
                @MessageId, @FromAddress, '', @Subject,
                @Mailbox, @ReceivedUtc, @SentUtc, @Direction,
                @TicketId, @EventId, now())
            ON CONFLICT (message_id) DO UPDATE SET message_id = EXCLUDED.message_id
            RETURNING id
            """;

        var ts = article.CreatedAt?.UtcDateTime ?? DateTime.UtcNow;
        return await conn.ExecuteScalarAsync<Guid?>(new CommandDefinition(sql, new
        {
            MessageId = messageId,
            FromAddress = fromAddress,
            Subject = article.Subject ?? string.Empty,
            Mailbox = ImportedMailboxSentinel,
            ReceivedUtc = direction == "Inbound" ? ts : (DateTime?)null,
            SentUtc = direction == "Outbound" ? ts : (DateTime?)null,
            Direction = direction,
            TicketId = ticketId,
            EventId = eventId,
        }, tx, cancellationToken: ct));
    }

    /// Inserts <c>attachments</c> rows for one event from a list of
    /// blob-staged plans. Routing follows the same convention the local
    /// mail / note paths use:
    /// <list type="bullet">
    /// <item>When <paramref name="mailMessageId"/> is non-null (email
    /// article): <c>owner_kind='Mail', owner_id=mailMessageId</c>. The
    /// timeline-enricher resolves these via <see cref="IAttachmentRepository.ListByMailAsync"/>
    /// and rewrites <c>cid:</c> references in the bodyHtml. event_id is
    /// also stamped so <see cref="IAttachmentRepository.ListByEventAsync"/>
    /// returns the same rows uniformly.</item>
    /// <item>Otherwise (note / phone / web article): <c>owner_kind='Ticket',
    /// owner_id=ticketId, event_id=eventId</c>. is_inline is always false
    /// — non-email Zammad articles don't carry cid references.</item>
    /// </list>
    /// processing_state is set straight to 'Ready' because the bytes are
    /// already in the blob store; no AttachmentWorker fetch step needed.
    /// Imports that skipped a too-large attachment never enter this method
    /// — the worker drops the plan and adds the upstream id to the
    /// per-record reasons list instead.
    private static async Task InsertAttachmentsForEventAsync(
        Npgsql.NpgsqlConnection conn,
        System.Data.Common.DbTransaction tx,
        Guid ticketId,
        long eventId,
        Guid? mailMessageId,
        IReadOnlyList<ZammadImportAttachmentPlan> plans,
        CancellationToken ct)
    {
        if (plans.Count == 0) return;

        var ownerKind = mailMessageId is not null ? "Mail" : "Ticket";
        var ownerId = mailMessageId ?? ticketId;

        const string sql = """
            INSERT INTO attachments
                (content_hash, size_bytes, mime_type, original_filename,
                 owner_kind, owner_id, is_inline, content_id, event_id,
                 processing_state)
            VALUES
                (@ContentHash, @SizeBytes, @MimeType, @Filename,
                 @OwnerKind, @OwnerId,
                 @IsInline, @ContentId, @EventId,
                 'Ready')
            """;

        foreach (var p in plans)
        {
            // Email articles use the plan's is_inline + content_id straight
            // through; non-email articles flatten to is_inline=false and
            // content_id=null because the local schema doesn't carry cid
            // for them and the local cid-rewriter only consults Mail-owned
            // attachments anyway.
            var inline = mailMessageId is not null && p.IsInline;
            var contentId = mailMessageId is not null ? p.ContentId : null;

            await conn.ExecuteAsync(new CommandDefinition(sql, new
            {
                p.ContentHash,
                p.SizeBytes,
                MimeType = string.IsNullOrWhiteSpace(p.MimeType) ? "application/octet-stream" : p.MimeType,
                Filename = string.IsNullOrEmpty(p.Filename) ? "attachment" : p.Filename,
                OwnerKind = ownerKind,
                OwnerId = ownerId,
                IsInline = inline,
                ContentId = contentId,
                EventId = (long?)eventId,
            }, tx, cancellationToken: ct));
        }
    }

    /// Pulls the bare email address out of an RFC-5322 From header value.
    /// Accepts "Name <addr@host>", "<addr@host>", or "addr@host". Returns
    /// null when nothing email-shaped is present so the caller skips the
    /// anchor insert instead of writing garbage into the CITEXT column.
    internal static string? ExtractEmailAddress(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var trimmed = raw.Trim();
        var open = trimmed.IndexOf('<');
        var close = trimmed.IndexOf('>');
        if (open >= 0 && close > open)
        {
            var inner = trimmed.Substring(open + 1, close - open - 1).Trim();
            if (inner.Contains('@')) return inner;
        }
        if (trimmed.Contains('@')) return trimmed;
        return null;
    }
}
