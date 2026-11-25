using Dapper;
using Npgsql;
using Servicedesk.Infrastructure.Access;

namespace Servicedesk.Infrastructure.Campaigns;

public sealed record CampaignAddResult(int Added, int AlreadyInCampaign, int NotAllowed);

/// v0.1.67 — the recipient list of a campaign. Every write is limited to
/// tickets in the caller's queues (<see cref="QueueAccessScope"/>), and every
/// guarded change is a single UPDATE/DELETE (never read-then-write).
public interface ICampaignRecipientRepository
{
    /// Adds live tickets in the caller's queues; project tickets themselves
    /// are never recipients. Duplicates are counted, not re-added.
    /// <paramref name="userId"/> null = added by the system (maintenance planner).
    Task<CampaignAddResult> AddAsync(Guid campaignId, IReadOnlyCollection<Guid> ticketIds, string source,
        Guid? userId, QueueAccessScope scope, int maxRecipients, CancellationToken ct);

    /// Removes recipients that were never queued for a send. Returns how many.
    Task<int> RemoveUnsentAsync(Guid campaignId, IReadOnlyCollection<Guid> ticketIds, QueueAccessScope scope, CancellationToken ct);

    /// Sets (or clears) the per-ticket proposed date. Returns how many rows changed.
    Task<int> SetProposedAsync(Guid campaignId, IReadOnlyCollection<Guid> ticketIds, DateTime? proposedUtc,
        QueueAccessScope scope, CancellationToken ct);

    /// Records (or clears) the agent's decision on the customer's answer.
    Task<bool> SetDecisionAsync(Guid campaignId, Guid ticketId, string? decision, Guid userId,
        QueueAccessScope scope, CancellationToken ct);

    Task<int> CountAsync(Guid campaignId, CancellationToken ct);

    /// The open (not resolved / closed) tickets linked to a project ticket.
    /// Access is applied by <see cref="AddAsync"/>.
    Task<IReadOnlyList<Guid>> OpenProjectTicketIdsAsync(Guid projectTicketId, CancellationToken ct);
}

public sealed class CampaignRecipientRepository : ICampaignRecipientRepository
{
    private readonly NpgsqlDataSource _dataSource;

    public CampaignRecipientRepository(NpgsqlDataSource dataSource) => _dataSource = dataSource;

    public async Task<CampaignAddResult> AddAsync(Guid campaignId, IReadOnlyCollection<Guid> ticketIds, string source,
        Guid? userId, QueueAccessScope scope, int maxRecipients, CancellationToken ct)
    {
        var ids = ticketIds.Distinct().ToArray();
        if (ids.Length == 0) return new CampaignAddResult(0, 0, 0);

        // Allowed = live, not a project, not merged, in the caller's queues.
        // The cap counts existing rows, inside the same statement, so two
        // concurrent adds cannot push a campaign past it by much (the
        // campaign row lock serialises them).
        var sql = $"""
            WITH lock AS (SELECT id FROM campaigns WHERE id = @campaignId FOR UPDATE),
            allowed AS (
                SELECT t.id FROM tickets t
                WHERE t.id = ANY(@ids)
                  AND t.is_deleted = FALSE
                  AND t.is_project = FALSE
                  AND t.merged_into_ticket_id IS NULL
                  AND {scope.SqlPredicate("t.queue_id")}
            ),
            room AS (
                SELECT GREATEST(0, @maxRecipients - (SELECT COUNT(*) FROM campaign_tickets WHERE campaign_id = @campaignId)) AS n
            ),
            fresh AS (
                SELECT a.id FROM allowed a
                WHERE NOT EXISTS (SELECT 1 FROM campaign_tickets ct WHERE ct.campaign_id = @campaignId AND ct.ticket_id = a.id)
                ORDER BY a.id
                LIMIT (SELECT n FROM room)
            ),
            ins AS (
                INSERT INTO campaign_tickets (campaign_id, ticket_id, source, added_by_user_id)
                SELECT @campaignId, f.id, @source, @userId::uuid FROM fresh f, lock
                ON CONFLICT DO NOTHING
                RETURNING ticket_id
            )
            SELECT (SELECT COUNT(*) FROM ins)::int     AS Added,
                   (SELECT COUNT(*) FROM allowed)::int AS Allowed
            """;
        var p = scope.Parameters(new { campaignId, ids, source, userId, maxRecipients });
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var row = await conn.QuerySingleAsync<AddRow>(new CommandDefinition(sql, p, cancellationToken: ct));
        await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE campaigns SET updated_utc = now() WHERE id = @campaignId", new { campaignId }, cancellationToken: ct));
        var already = row.Allowed - row.Added;
        return new CampaignAddResult(row.Added, Math.Max(0, already), ids.Length - row.Allowed);
    }

    public async Task<int> RemoveUnsentAsync(Guid campaignId, IReadOnlyCollection<Guid> ticketIds, QueueAccessScope scope, CancellationToken ct)
    {
        var sql = $"""
            DELETE FROM campaign_tickets ct
            USING tickets t
            WHERE ct.campaign_id = @campaignId
              AND ct.ticket_id = ANY(@ids)
              AND t.id = ct.ticket_id
              AND {scope.SqlPredicate("t.queue_id")}
              AND NOT EXISTS (SELECT 1 FROM campaign_messages m
                              WHERE m.campaign_id = ct.campaign_id AND m.ticket_id = ct.ticket_id)
            """;
        var p = scope.Parameters(new { campaignId, ids = ticketIds.Distinct().ToArray() });
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        return await conn.ExecuteAsync(new CommandDefinition(sql, p, cancellationToken: ct));
    }

    public async Task<int> SetProposedAsync(Guid campaignId, IReadOnlyCollection<Guid> ticketIds, DateTime? proposedUtc,
        QueueAccessScope scope, CancellationToken ct)
    {
        var sql = $"""
            UPDATE campaign_tickets ct SET proposed_utc = @proposedUtc
            FROM tickets t
            WHERE ct.campaign_id = @campaignId
              AND ct.ticket_id = ANY(@ids)
              AND t.id = ct.ticket_id
              AND {scope.SqlPredicate("t.queue_id")}
            """;
        var p = scope.Parameters(new { campaignId, ids = ticketIds.Distinct().ToArray(), proposedUtc });
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        return await conn.ExecuteAsync(new CommandDefinition(sql, p, cancellationToken: ct));
    }

    public async Task<bool> SetDecisionAsync(Guid campaignId, Guid ticketId, string? decision, Guid userId,
        QueueAccessScope scope, CancellationToken ct)
    {
        var sql = $"""
            UPDATE campaign_tickets ct SET
                decision            = @decision,
                decision_by_user_id = CASE WHEN @decision::text IS NULL THEN NULL ELSE @userId END,
                decision_utc        = CASE WHEN @decision::text IS NULL THEN NULL ELSE now() END,
                decision_source     = CASE WHEN @decision::text IS NULL THEN NULL ELSE 'Agent' END,
                decision_note       = NULL,
                decision_preferred_date   = NULL,
                decision_preferred_minute = NULL
            FROM tickets t
            WHERE ct.campaign_id = @campaignId
              AND ct.ticket_id = @ticketId
              AND t.id = ct.ticket_id
              AND {scope.SqlPredicate("t.queue_id")}
            """;
        var p = scope.Parameters(new { campaignId, ticketId, decision, userId });
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        return await conn.ExecuteAsync(new CommandDefinition(sql, p, cancellationToken: ct)) > 0;
    }

    public async Task<int> CountAsync(Guid campaignId, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        return await conn.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT COUNT(*)::int FROM campaign_tickets WHERE campaign_id = @campaignId",
            new { campaignId }, cancellationToken: ct));
    }

    public async Task<IReadOnlyList<Guid>> OpenProjectTicketIdsAsync(Guid projectTicketId, CancellationToken ct)
    {
        const string sql = """
            SELECT t.id FROM tickets t
            JOIN statuses s ON s.id = t.status_id
            WHERE t.project_ticket_id = @projectTicketId
              AND t.is_deleted = FALSE
              AND s.state_category NOT IN ('Resolved', 'Closed')
            ORDER BY t.project_sort_order NULLS LAST, t.number
            """;
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var ids = await conn.QueryAsync<Guid>(new CommandDefinition(sql, new { projectTicketId }, cancellationToken: ct));
        return ids.ToList();
    }

    private sealed class AddRow
    {
        public int Added { get; set; }
        public int Allowed { get; set; }
    }
}
