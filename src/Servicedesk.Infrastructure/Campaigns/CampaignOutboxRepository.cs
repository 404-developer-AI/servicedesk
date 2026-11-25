using Dapper;
using Npgsql;

namespace Servicedesk.Infrastructure.Campaigns;

public sealed record NewCampaignRound(
    Guid CampaignId,
    string Kind,
    string Subject,
    string BodyHtml,
    Guid SentByUserId,
    string SentByRole,
    Guid? PendingStatusId,
    DateTime? PendingTillUtc,
    /// Phase 2 — "send on …"; null = now. Becomes not_before_utc on every row.
    DateTime? ScheduledUtc = null);

/// One rendered mail (Queued) or a ticket left out with a reason (Skipped).
public sealed record NewCampaignMessage(
    Guid TicketId,
    string State,
    string? SkipReason,
    string ToAddress,
    string RenderedSubject,
    string RenderedBodyHtml);

/// A claimed outbox row with everything the send step needs.
public sealed class ClaimedCampaignMessage
{
    public Guid Id { get; set; }
    public Guid RoundId { get; set; }
    public Guid CampaignId { get; set; }
    public string CampaignName { get; set; } = string.Empty;
    public Guid TicketId { get; set; }
    public string ToAddress { get; set; } = string.Empty;
    public string RenderedSubject { get; set; } = string.Empty;
    public string RenderedBodyHtml { get; set; } = string.Empty;
    public int Attempts { get; set; }
    public Guid? SentByUserId { get; set; }
    public string SentByRole { get; set; } = string.Empty;
    public Guid? PendingStatusId { get; set; }
    public DateTime? PendingTillUtc { get; set; }
}

/// What the send step re-checks right before a mail goes out: the ticket as
/// it is now, and the sender as they are now.
public sealed class CampaignSendContext
{
    public bool TicketExists { get; set; }
    public bool IsProject { get; set; }
    public bool IsMerged { get; set; }
    public string StateCategory { get; set; } = string.Empty;
    public Guid QueueId { get; set; }
    public string? RequesterEmail { get; set; }
    public string? RequesterName { get; set; }
    public bool SenderActive { get; set; }
    public bool SenderFlag { get; set; }
    public string? SenderRole { get; set; }
    public string? SenderEmail { get; set; }
}

/// A round whose last outbox row just settled.
public sealed class CompletedCampaignRound
{
    public Guid RoundId { get; set; }
    public Guid CampaignId { get; set; }
    public string CampaignName { get; set; } = string.Empty;
    public Guid? ProjectTicketId { get; set; }
    public int RoundNo { get; set; }
    public string Kind { get; set; } = string.Empty;
    public Guid? SentByUserId { get; set; }
    public int Total { get; set; }
    public int Sent { get; set; }
    public int Skipped { get; set; }
    public int Failed { get; set; }
    public int Cancelled { get; set; }
}

/// v0.1.67 — the campaign outbox. Rows are claimed with
/// <c>FOR UPDATE SKIP LOCKED</c> (two workers never take the same row), the
/// unique (round_id, ticket_id) constraint makes a double enqueue impossible,
/// and every state change is one guarded UPDATE.
public interface ICampaignOutboxRepository
{
    Task<(Guid RoundId, int RoundNo)> EnqueueRoundAsync(NewCampaignRound round, IReadOnlyList<NewCampaignMessage> messages, CancellationToken ct);
    Task<IReadOnlyList<ClaimedCampaignMessage>> ClaimDueAsync(int limit, CancellationToken ct);
    Task<CampaignSendContext> GetSendContextAsync(Guid ticketId, Guid? senderUserId, CancellationToken ct);
    Task MarkSentAsync(Guid messageId, long eventId, CancellationToken ct);
    Task MarkPendingResultAsync(Guid messageId, string result, CancellationToken ct);
    Task MarkRetryAsync(Guid messageId, string error, DateTime nextAttemptUtc, CancellationToken ct);
    Task MarkFailedAsync(Guid messageId, string error, CancellationToken ct);
    Task MarkSkippedAsync(Guid messageId, string reason, CancellationToken ct);
    /// Rows stuck in Sending (worker stopped mid-send): Sent when the ticket
    /// already carries the MailSent event stamped with the row id, otherwise
    /// Failed — never sent again automatically, an agent retries knowingly.
    Task<int> ReconcileStaleAsync(TimeSpan olderThan, CancellationToken ct);
    /// Stamps completed_utc on rounds with nothing left to send; returns them.
    Task<IReadOnlyList<CompletedCampaignRound>> CompleteSettledRoundsAsync(CancellationToken ct);
    /// Both limited to tickets in the caller's queues.
    Task<int> CancelQueuedAsync(Guid campaignId, Access.QueueAccessScope scope, CancellationToken ct);
    Task<int> RetryFailedAsync(Guid campaignId, Access.QueueAccessScope scope, CancellationToken ct);
    /// Queued + Sending rows of a campaign (blocks overlapping rounds per ticket).
    Task<IReadOnlySet<Guid>> TicketsInFlightAsync(Guid campaignId, CancellationToken ct);
}

public sealed class CampaignOutboxRepository : ICampaignOutboxRepository
{
    private readonly NpgsqlDataSource _dataSource;

    public CampaignOutboxRepository(NpgsqlDataSource dataSource) => _dataSource = dataSource;

    public async Task<(Guid RoundId, int RoundNo)> EnqueueRoundAsync(
        NewCampaignRound round, IReadOnlyList<NewCampaignMessage> messages, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        // The campaign row lock serialises round numbering.
        await conn.ExecuteAsync(new CommandDefinition(
            "SELECT 1 FROM campaigns WHERE id = @CampaignId FOR UPDATE", round, tx, cancellationToken: ct));
        var created = await conn.QuerySingleAsync<RoundIds>(new CommandDefinition("""
            INSERT INTO campaign_rounds (campaign_id, round_no, kind, subject, body_html,
                                         sent_by_user_id, sent_by_role, pending_status_id, pending_till_utc,
                                         scheduled_utc)
            SELECT @CampaignId, COALESCE(MAX(round_no), 0) + 1, @Kind, @Subject, @BodyHtml,
                   @SentByUserId, @SentByRole, @PendingStatusId, @PendingTillUtc, @ScheduledUtc
            FROM campaign_rounds WHERE campaign_id = @CampaignId
            RETURNING id AS Id, round_no AS RoundNo
            """, round, tx, cancellationToken: ct));

        foreach (var m in messages)
        {
            await conn.ExecuteAsync(new CommandDefinition("""
                INSERT INTO campaign_messages (round_id, campaign_id, ticket_id, state, skip_reason,
                                               to_address, rendered_subject, rendered_body_html, not_before_utc)
                VALUES (@roundId, @campaignId, @TicketId, @State, @SkipReason,
                        @ToAddress, @RenderedSubject, @RenderedBodyHtml, @notBefore)
                """, new
            {
                roundId = created.Id,
                campaignId = round.CampaignId,
                notBefore = round.ScheduledUtc,
                m.TicketId,
                m.State,
                m.SkipReason,
                m.ToAddress,
                m.RenderedSubject,
                m.RenderedBodyHtml,
            }, tx, cancellationToken: ct));
        }

        await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE campaigns SET state = 'Active', updated_utc = now() WHERE id = @CampaignId AND state = 'Draft'",
            round, tx, cancellationToken: ct));
        await tx.CommitAsync(ct);
        return (created.Id, created.RoundNo);
    }

    public async Task<IReadOnlyList<ClaimedCampaignMessage>> ClaimDueAsync(int limit, CancellationToken ct)
    {
        const string sql = """
            WITH due AS (
                SELECT m.id FROM campaign_messages m
                WHERE m.state = 'Queued'
                  AND COALESCE(m.next_attempt_utc, '-infinity') <= now()
                  AND COALESCE(m.not_before_utc, '-infinity') <= now()
                ORDER BY m.next_attempt_utc NULLS FIRST, m.created_utc
                LIMIT @limit
                FOR UPDATE SKIP LOCKED
            )
            UPDATE campaign_messages m
            SET state = 'Sending', claimed_utc = now(), attempts = m.attempts + 1
            FROM due, campaign_rounds r, campaigns c
            WHERE m.id = due.id AND r.id = m.round_id AND c.id = m.campaign_id
            RETURNING m.id AS Id, m.round_id AS RoundId, m.campaign_id AS CampaignId, c.name AS CampaignName,
                      m.ticket_id AS TicketId, m.to_address AS ToAddress,
                      m.rendered_subject AS RenderedSubject, m.rendered_body_html AS RenderedBodyHtml,
                      m.attempts AS Attempts, r.sent_by_user_id AS SentByUserId, r.sent_by_role AS SentByRole,
                      r.pending_status_id AS PendingStatusId, r.pending_till_utc AS PendingTillUtc
            """;
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<ClaimedCampaignMessage>(new CommandDefinition(sql, new { limit }, cancellationToken: ct));
        return rows.ToList();
    }

    public async Task<CampaignSendContext> GetSendContextAsync(Guid ticketId, Guid? senderUserId, CancellationToken ct)
    {
        const string sql = """
            SELECT (t.id IS NOT NULL)                         AS TicketExists,
                   COALESCE(t.is_project, FALSE)              AS IsProject,
                   (t.merged_into_ticket_id IS NOT NULL)      AS IsMerged,
                   COALESCE(s.state_category, '')             AS StateCategory,
                   COALESCE(t.queue_id, '00000000-0000-0000-0000-000000000000'::uuid) AS QueueId,
                   c.email::text                              AS RequesterEmail,
                   NULLIF(trim(COALESCE(c.first_name, '') || ' ' || COALESCE(c.last_name, '')), '') AS RequesterName,
                   COALESCE(u.is_active, FALSE)               AS SenderActive,
                   COALESCE(u.campaigns_enabled, FALSE)       AS SenderFlag,
                   u.role_name                                AS SenderRole,
                   u.email::text                              AS SenderEmail
            FROM (SELECT 1) one
            LEFT JOIN tickets t   ON t.id = @ticketId AND t.is_deleted = FALSE
            LEFT JOIN statuses s  ON s.id = t.status_id
            LEFT JOIN contacts c  ON c.id = t.requester_contact_id
            LEFT JOIN users u     ON u.id = @senderUserId
            """;
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        return await conn.QuerySingleAsync<CampaignSendContext>(new CommandDefinition(
            sql, new { ticketId, senderUserId }, cancellationToken: ct));
    }

    public Task MarkSentAsync(Guid messageId, long eventId, CancellationToken ct) => ExecAsync("""
        UPDATE campaign_messages SET state = 'Sent', sent_utc = now(), mail_event_id = @eventId, last_error = ''
        WHERE id = @messageId AND state = 'Sending'
        """, new { messageId, eventId }, ct);

    public Task MarkPendingResultAsync(Guid messageId, string result, CancellationToken ct) => ExecAsync(
        "UPDATE campaign_messages SET pending_result = @result WHERE id = @messageId",
        new { messageId, result }, ct);

    public Task MarkRetryAsync(Guid messageId, string error, DateTime nextAttemptUtc, CancellationToken ct) => ExecAsync("""
        UPDATE campaign_messages SET state = 'Queued', next_attempt_utc = @nextAttemptUtc, last_error = @error
        WHERE id = @messageId AND state = 'Sending'
        """, new { messageId, error = Trim(error), nextAttemptUtc }, ct);

    public Task MarkFailedAsync(Guid messageId, string error, CancellationToken ct) => ExecAsync("""
        UPDATE campaign_messages SET state = 'Failed', last_error = @error
        WHERE id = @messageId AND state = 'Sending'
        """, new { messageId, error = Trim(error) }, ct);

    public Task MarkSkippedAsync(Guid messageId, string reason, CancellationToken ct) => ExecAsync("""
        UPDATE campaign_messages SET state = 'Skipped', skip_reason = @reason
        WHERE id = @messageId AND state = 'Sending'
        """, new { messageId, reason }, ct);

    public async Task<int> ReconcileStaleAsync(TimeSpan olderThan, CancellationToken ct)
    {
        const string sql = """
            WITH stale AS (
                SELECT m.id, m.ticket_id FROM campaign_messages m
                WHERE m.state = 'Sending' AND m.claimed_utc < now() - @olderThan
                FOR UPDATE SKIP LOCKED
            ),
            found AS (
                SELECT s.id, ev.id AS event_id FROM stale s
                LEFT JOIN LATERAL (
                    SELECT e.id FROM ticket_events e
                    WHERE e.ticket_id = s.ticket_id AND e.event_type = 'MailSent'
                      AND e.metadata ->> 'campaignMessageId' = s.id::text
                    LIMIT 1
                ) ev ON TRUE
            )
            UPDATE campaign_messages m SET
                state         = CASE WHEN f.event_id IS NULL THEN 'Failed' ELSE 'Sent' END,
                sent_utc      = CASE WHEN f.event_id IS NULL THEN m.sent_utc ELSE COALESCE(m.sent_utc, m.claimed_utc) END,
                mail_event_id = f.event_id,
                last_error    = CASE WHEN f.event_id IS NULL
                                     THEN 'Sending was interrupted. Check the ticket timeline before retrying — the mail may have gone out.'
                                     ELSE '' END
            FROM found f
            WHERE m.id = f.id
            """;
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        return await conn.ExecuteAsync(new CommandDefinition(sql, new { olderThan }, cancellationToken: ct));
    }

    public async Task<IReadOnlyList<CompletedCampaignRound>> CompleteSettledRoundsAsync(CancellationToken ct)
    {
        // Guarded: only the statement that flips completed_utc returns the
        // round, so its follow-up (project event, audit) runs exactly once.
        const string sql = """
            WITH settled AS (
                UPDATE campaign_rounds r SET completed_utc = now()
                WHERE r.completed_utc IS NULL
                  AND NOT EXISTS (SELECT 1 FROM campaign_messages m
                                  WHERE m.round_id = r.id AND m.state IN ('Queued','Sending'))
                RETURNING r.id, r.campaign_id, r.round_no, r.kind, r.sent_by_user_id
            )
            SELECT s.id AS RoundId, s.campaign_id AS CampaignId, c.name AS CampaignName,
                   c.project_ticket_id AS ProjectTicketId, s.round_no AS RoundNo, s.kind AS Kind,
                   s.sent_by_user_id AS SentByUserId,
                   COUNT(m.id)::int AS Total,
                   COUNT(*) FILTER (WHERE m.state = 'Sent')::int      AS Sent,
                   COUNT(*) FILTER (WHERE m.state = 'Skipped')::int   AS Skipped,
                   COUNT(*) FILTER (WHERE m.state = 'Failed')::int    AS Failed,
                   COUNT(*) FILTER (WHERE m.state = 'Cancelled')::int AS Cancelled
            FROM settled s
            JOIN campaigns c ON c.id = s.campaign_id
            LEFT JOIN campaign_messages m ON m.round_id = s.id
            GROUP BY s.id, s.campaign_id, c.name, c.project_ticket_id, s.round_no, s.kind, s.sent_by_user_id
            """;
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<CompletedCampaignRound>(new CommandDefinition(sql, cancellationToken: ct));
        return rows.ToList();
    }

    public async Task<int> CancelQueuedAsync(Guid campaignId, Access.QueueAccessScope scope, CancellationToken ct)
    {
        var sql = $"""
            UPDATE campaign_messages m SET state = 'Cancelled'
            FROM tickets t
            WHERE m.campaign_id = @campaignId AND m.state = 'Queued'
              AND t.id = m.ticket_id AND {scope.SqlPredicate("t.queue_id")}
            """;
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        return await conn.ExecuteAsync(new CommandDefinition(sql, scope.Parameters(new { campaignId }), cancellationToken: ct));
    }

    public async Task<int> RetryFailedAsync(Guid campaignId, Access.QueueAccessScope scope, CancellationToken ct)
    {
        // Reopens the rounds the retried rows belong to, so their completion
        // (project event) reflects the new outcome.
        var sql = $"""
            WITH retried AS (
                UPDATE campaign_messages m SET state = 'Queued', attempts = 0, next_attempt_utc = NULL
                FROM tickets t
                WHERE m.campaign_id = @campaignId AND m.state = 'Failed'
                  AND t.id = m.ticket_id AND {scope.SqlPredicate("t.queue_id")}
                RETURNING m.round_id
            ),
            reopened AS (
                UPDATE campaign_rounds r SET completed_utc = NULL
                WHERE r.id IN (SELECT round_id FROM retried)
                RETURNING r.id
            )
            SELECT (SELECT COUNT(*) FROM retried)::int
            """;
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        return await conn.ExecuteScalarAsync<int>(new CommandDefinition(sql, scope.Parameters(new { campaignId }), cancellationToken: ct));
    }

    public async Task<IReadOnlySet<Guid>> TicketsInFlightAsync(Guid campaignId, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var ids = await conn.QueryAsync<Guid>(new CommandDefinition(
            "SELECT ticket_id FROM campaign_messages WHERE campaign_id = @campaignId AND state IN ('Queued','Sending')",
            new { campaignId }, cancellationToken: ct));
        return ids.ToHashSet();
    }

    private async Task ExecAsync(string sql, object args, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(sql, args, cancellationToken: ct));
    }

    private static string Trim(string error) => error.Length <= 1000 ? error : error[..1000];

    private sealed class RoundIds
    {
        public Guid Id { get; set; }
        public int RoundNo { get; set; }
    }
}
