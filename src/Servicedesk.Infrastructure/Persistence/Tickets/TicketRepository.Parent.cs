using Servicedesk.Infrastructure.Access;
using System.Text;
using System.Text.Json;
using Dapper;
using Npgsql;
using Servicedesk.Domain.Tickets;
using Servicedesk.Infrastructure.Mail.Ingest;
using Servicedesk.Infrastructure.Tickets;

namespace Servicedesk.Infrastructure.Persistence.Tickets;

public sealed partial class TicketRepository
{
    public async Task<LinkParentResult> LinkParentAsync(
        Guid ticketId, Guid parentTicketId, Guid actorUserId, CancellationToken ct)
    {
        if (ticketId == parentTicketId)
            return new LinkParentResult(false, LinkParentFailureReason.SameTicket);

        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        // Lock both rows so a concurrent re-parent on either side can't
        // race the cycle check. SELECT FOR UPDATE blocks until the other
        // tx commits, then we re-read the up-to-date parent chain.
        const string readSql = """
            SELECT id AS Id, merged_into_ticket_id AS MergedIntoTicketId,
                   parent_ticket_id AS ParentTicketId
            FROM tickets
            WHERE id = @id AND is_deleted = FALSE
            FOR UPDATE
            """;
        var source = await conn.QueryFirstOrDefaultAsync<LinkParentRow>(
            new CommandDefinition(readSql, new { id = ticketId }, tx, cancellationToken: ct));
        if (source is null) { await tx.RollbackAsync(ct); return new LinkParentResult(false, LinkParentFailureReason.SourceNotFound); }
        if (source.MergedIntoTicketId is not null)
        {
            await tx.RollbackAsync(ct);
            return new LinkParentResult(false, LinkParentFailureReason.SourceIsMerged);
        }

        var parent = await conn.QueryFirstOrDefaultAsync<LinkParentRow>(
            new CommandDefinition(readSql, new { id = parentTicketId }, tx, cancellationToken: ct));
        if (parent is null) { await tx.RollbackAsync(ct); return new LinkParentResult(false, LinkParentFailureReason.ParentNotFound); }
        if (parent.MergedIntoTicketId is not null)
        {
            await tx.RollbackAsync(ct);
            return new LinkParentResult(false, LinkParentFailureReason.ParentIsMerged);
        }

        // Cycle check: walk the candidate parent's chain upward. If we
        // hit `ticketId` along the way, accepting the link would create a
        // cycle. Bounded at 50 hops; the index on parent_ticket_id makes
        // each hop a single indexed PK lookup. Typical depth is 1.
        var cursor = parent.ParentTicketId;
        const int maxDepth = 50;
        for (var i = 0; i < maxDepth && cursor is Guid pid; i++)
        {
            if (pid == ticketId)
            {
                await tx.RollbackAsync(ct);
                return new LinkParentResult(false, LinkParentFailureReason.WouldCycle);
            }
            cursor = await conn.ExecuteScalarAsync<Guid?>(new CommandDefinition(
                "SELECT parent_ticket_id FROM tickets WHERE id = @id",
                new { id = pid }, tx, cancellationToken: ct));
        }

        const string updateSql = """
            UPDATE tickets
               SET parent_ticket_id         = @parentTicketId,
                   parent_linked_utc        = now(),
                   parent_linked_by_user_id = @actorUserId,
                   updated_utc              = now()
             WHERE id = @ticketId AND is_deleted = FALSE
            """;
        var rows = await conn.ExecuteAsync(new CommandDefinition(updateSql,
            new { ticketId, parentTicketId, actorUserId }, tx, cancellationToken: ct));
        if (rows == 0)
        {
            await tx.RollbackAsync(ct);
            return new LinkParentResult(false, LinkParentFailureReason.SourceNotFound);
        }

        // Timeline event on the child so the audit trail is complete.
        // The parent ticket's "Sub tickets" list is derived from the FK,
        // not from events, so no event is written on that side.
        var metadata = System.Text.Json.JsonSerializer.Serialize(new
        {
            parentTicketId,
            parentNumber = await conn.ExecuteScalarAsync<long?>(new CommandDefinition(
                "SELECT number FROM tickets WHERE id = @id", new { id = parentTicketId }, tx, cancellationToken: ct)),
            previousParentTicketId = source.ParentTicketId,
        });
        await conn.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO ticket_events (ticket_id, event_type, author_user_id, metadata, is_internal)
            VALUES (@ticketId, 'ParentLinked', @actorUserId, @metadata::jsonb, FALSE)
            """,
            new { ticketId, actorUserId, metadata }, tx, cancellationToken: ct));

        await tx.CommitAsync(ct);
        return new LinkParentResult(true, null);
    }

    public async Task<bool> UnlinkParentAsync(Guid ticketId, Guid actorUserId, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        // Snapshot the previous parent so the event metadata carries it.
        // If the ticket has no parent we treat it as "nothing to do" — the
        // endpoint maps that to 404 so an idempotent client doesn't get a
        // false success.
        const string readSql = """
            SELECT parent_ticket_id AS ParentTicketId
            FROM tickets
            WHERE id = @ticketId AND is_deleted = FALSE
            FOR UPDATE
            """;
        var prevParent = await conn.ExecuteScalarAsync<Guid?>(
            new CommandDefinition(readSql, new { ticketId }, tx, cancellationToken: ct));
        if (prevParent is null) { await tx.RollbackAsync(ct); return false; }

        const string updateSql = """
            UPDATE tickets
               SET parent_ticket_id         = NULL,
                   parent_linked_utc        = NULL,
                   parent_linked_by_user_id = NULL,
                   updated_utc              = now()
             WHERE id = @ticketId AND is_deleted = FALSE
            """;
        var rows = await conn.ExecuteAsync(new CommandDefinition(updateSql,
            new { ticketId }, tx, cancellationToken: ct));
        if (rows == 0) { await tx.RollbackAsync(ct); return false; }

        var metadata = System.Text.Json.JsonSerializer.Serialize(new { previousParentTicketId = prevParent });
        await conn.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO ticket_events (ticket_id, event_type, author_user_id, metadata, is_internal)
            VALUES (@ticketId, 'ParentUnlinked', @actorUserId, @metadata::jsonb, FALSE)
            """,
            new { ticketId, actorUserId, metadata }, tx, cancellationToken: ct));

        await tx.CommitAsync(ct);
        return true;
    }

    private sealed class LinkParentRow
    {
        public Guid Id { get; set; }
        public Guid? MergedIntoTicketId { get; set; }
        public Guid? ParentTicketId { get; set; }
    }
}
