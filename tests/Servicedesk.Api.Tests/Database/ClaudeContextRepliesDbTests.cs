using Dapper;
using Servicedesk.Infrastructure.Integrations.Claude;
using Xunit;

namespace Servicedesk.Api.Tests.Database;

/// Replies on internal notes are part of the AI context (proposal, summary
/// and status update): live replies join the timeline as internal
/// "NoteReply" rows in time order, soft-deleted replies never do.
[Collection(PostgresCollection.Name)]
public sealed class ClaudeContextRepliesDbTests
{
    private readonly PostgresDatabase _db;

    public ClaudeContextRepliesDbTests(PostgresDatabase db) => _db = db;

    private async Task<Guid> TicketWithRepliesAsync()
    {
        var seed = new DbSeed(_db.DataSource);
        var (q, s, p) = await seed.TaxonomyAsync();
        var ticketId = await seed.TicketAsync("Licence request", await seed.ContactAsync(), q, s, p);
        var author = await seed.UserAsync("Agent");
        var noteId = await seed.EventAsync(ticketId, "Note", isInternal: true, "Please check the licences", author);

        await using var c = await _db.DataSource.OpenConnectionAsync();
        const string insert = """
            INSERT INTO ticket_note_replies (root_event_id, author_user_id, body_html, body_text, created_utc, deleted_utc)
            VALUES (@noteId, @author, '', @text, now() + @offset, @deleted)
            """;
        await c.ExecuteAsync(insert, new { noteId, author, text = "Vendor ships the key on Friday", offset = TimeSpan.FromMinutes(1), deleted = (DateTime?)null });
        await c.ExecuteAsync(insert, new { noteId, author, text = "Withdrawn remark", offset = TimeSpan.FromMinutes(2), deleted = (DateTime?)DateTime.UtcNow });
        return ticketId;
    }

    [PostgresFact]
    public async Task Proposal_and_summary_context_carries_live_replies_only()
    {
        var ticketId = await TicketWithRepliesAsync();
        await using var c = await _db.DataSource.OpenConnectionAsync();

        var rows = await ClaudeAssistService.LoadContextEventsAsync(c, ticketId, default);

        Assert.Equal(new[] { "Please check the licences", "Vendor ships the key on Friday" }, rows.Select(r => r.BodyText));
        var reply = rows[^1];
        Assert.Equal(ClaudeStatusContext.NoteReplyEventType, reply.EventType);
        Assert.True(reply.IsInternal);
        Assert.NotNull(reply.AuthorUserId);
        Assert.Null(reply.AuthorContactId);
    }

    [PostgresFact]
    public async Task Status_context_carries_live_replies_with_their_author()
    {
        var ticketId = await TicketWithRepliesAsync();
        await using var c = await _db.DataSource.OpenConnectionAsync();

        var rows = await ClaudeAssistService.LoadStatusEventsAsync(c, ticketId, default);

        Assert.Equal(new[] { "Please check the licences", "Vendor ships the key on Friday" }, rows.Select(r => r.BodyText));
        var reply = rows[^1];
        Assert.Equal(ClaudeStatusContext.NoteReplyEventType, reply.EventType);
        Assert.True(reply.IsInternal);
        Assert.False(string.IsNullOrEmpty(reply.UserName));
        Assert.Contains("NoteReply (internal): Vendor ships the key on Friday",
            ClaudeStatusContext.FormatEvent(reply, TimeZoneInfo.Utc));
    }
}
