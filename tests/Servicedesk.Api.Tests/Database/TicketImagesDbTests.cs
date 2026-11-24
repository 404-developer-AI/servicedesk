using Dapper;
using Servicedesk.Infrastructure.Mail.Attachments;
using Xunit;

namespace Servicedesk.Api.Tests.Database;

/// <see cref="AttachmentRepository.ListTicketImagesAsync"/> (the AI image
/// picker): ready images of one ticket over its three ownership paths, and
/// nothing of another ticket, no non-images and no unfinished uploads.
[Collection(PostgresCollection.Name)]
public sealed class TicketImagesDbTests
{
    private readonly PostgresDatabase _db;

    public TicketImagesDbTests(PostgresDatabase db) => _db = db;

    [PostgresFact]
    public async Task Lists_the_ready_images_of_the_ticket_over_all_three_paths()
    {
        var seed = new DbSeed(_db.DataSource);
        var (queue, status, priority) = await seed.TaxonomyAsync();
        var contact = await seed.ContactAsync();
        var ticket = await seed.TicketAsync("Screen broken", contact, queue, status, priority);
        var other = await seed.TicketAsync("Other ticket", contact, queue, status, priority);
        var note = await seed.EventAsync(ticket, "Note", isInternal: true);
        var mail = await MailAsync(ticket);
        var otherMail = await MailAsync(other);

        var staged = await AttachmentAsync("Ticket", ticket, "image/png", "staged.png");
        var onNote = await AttachmentAsync("Ticket", ticket, "image/jpeg", "note.jpg", eventId: note);
        var inMail = await AttachmentAsync("Mail", mail, "image/png", "inline.png", inline: true);
        await AttachmentAsync("Ticket", ticket, "application/pdf", "invoice.pdf");
        await AttachmentAsync("Ticket", ticket, "image/png", "pending.png", state: "Pending");
        await AttachmentAsync("Ticket", other, "image/png", "other.png");
        await AttachmentAsync("Mail", otherMail, "image/png", "other-mail.png");

        var images = await new AttachmentRepository(_db.DataSource).ListTicketImagesAsync(ticket, default);

        Assert.Equal(new[] { staged, onNote, inMail }, images.Select(i => i.Id));
        Assert.True(images.Single(i => i.Id == inMail).IsInline);
    }

    private async Task<Guid> MailAsync(Guid ticketId)
    {
        await using var c = await _db.DataSource.OpenConnectionAsync();
        return await c.ExecuteScalarAsync<Guid>("""
            INSERT INTO mail_messages (message_id, from_address, mailbox_address, received_utc, ticket_id)
            VALUES (@messageId, 'requester@example.test', 'support@example.test', now(), @ticketId)
            RETURNING id
            """, new { messageId = $"<{Guid.NewGuid()}@example.test>", ticketId });
    }

    private async Task<Guid> AttachmentAsync(
        string ownerKind, Guid ownerId, string mime, string name,
        long? eventId = null, bool inline = false, string state = "Ready")
    {
        await using var c = await _db.DataSource.OpenConnectionAsync();
        var id = await c.ExecuteScalarAsync<Guid>("""
            INSERT INTO attachments (content_hash, size_bytes, mime_type, original_filename, owner_kind, owner_id,
                                     is_inline, event_id, processing_state, created_utc)
            VALUES (@hash, 10, @mime, @name, @ownerKind, @ownerId, @inline, @eventId, @state,
                    now() + make_interval(secs => (SELECT count(*) FROM attachments)))
            RETURNING id
            """, new { hash = Guid.NewGuid().ToString("N"), mime, name, ownerKind, ownerId, inline, eventId, state });
        return id;
    }
}
