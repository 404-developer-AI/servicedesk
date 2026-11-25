using Dapper;
using Npgsql;
using Servicedesk.Infrastructure.Access;

namespace Servicedesk.Infrastructure.Campaigns;

/// v0.1.67 — the recipient list of one campaign with its derived state.
/// One query, bounded by Campaigns.MaxRecipients: the latest outbox row and
/// the first customer answer after the first successful send come from
/// LATERAL lookups on indexed columns (ix_campaign_messages_ticket,
/// ix_ticket_events_ticket_created). Rows outside the caller's queues are
/// left out and counted, like the project panel.
public interface ICampaignDashboardReader
{
    Task<CampaignDashboard> ReadAsync(Guid campaignId, QueueAccessScope scope, DateTime nowUtc, CancellationToken ct);
}

public sealed class CampaignDashboardReader : ICampaignDashboardReader
{
    private readonly NpgsqlDataSource _dataSource;

    public CampaignDashboardReader(NpgsqlDataSource dataSource) => _dataSource = dataSource;

    public async Task<CampaignDashboard> ReadAsync(Guid campaignId, QueueAccessScope scope, DateTime nowUtc, CancellationToken ct)
    {
        var sql = $"""
            SELECT ct.ticket_id AS TicketId, t.number AS Number, t.subject AS Subject, t.queue_id AS QueueId,
                   t.is_project AS IsProject, (t.merged_into_ticket_id IS NOT NULL) AS IsMerged,
                   s.name AS StatusName, s.state_category AS StateCategory, t.pending_till_utc AS PendingTillUtc,
                   c.email::text AS RequesterEmail,
                   NULLIF(trim(COALESCE(c.first_name, '') || ' ' || COALESCE(c.last_name, '')), '') AS RequesterName,
                   co.name AS CompanyName,
                   ct.proposed_utc AS ProposedUtc, ct.source AS Source, ct.added_utc AS AddedUtc,
                   ct.decision AS Decision, ct.decision_utc AS DecisionUtc, du.email AS DecisionByEmail,
                   ct.decision_source AS DecisionSource, ct.decision_note AS DecisionNote,
                   ct.decision_preferred_date AS DecisionPreferredDate, ct.decision_preferred_minute AS DecisionPreferredMinute,
                   ct.approval_expires_utc AS AnswerLinkExpiresUtc,
                   lm.state AS MessageState, lm.skip_reason AS SkipReason, NULLIF(lm.last_error, '') AS LastError,
                   lm.pending_result AS PendingResult, lm.not_before_utc AS NotBeforeUtc,
                   lr.round_no AS RoundNo,
                   fs.first_sent AS FirstSentUtc, fs.last_sent AS LastSentUtc,
                   fs.pending_till AS RoundPendingTillUtc,
                   rep.created_utc AS ReplyUtc, rep.event_type AS ReplyType
            FROM campaign_tickets ct
            JOIN tickets t ON t.id = ct.ticket_id AND t.is_deleted = FALSE
            JOIN statuses s ON s.id = t.status_id
            LEFT JOIN contacts c ON c.id = t.requester_contact_id
            LEFT JOIN companies co ON co.id = t.company_id
            LEFT JOIN users du ON du.id = ct.decision_by_user_id
            LEFT JOIN LATERAL (
                SELECT m.state, m.skip_reason, m.last_error, m.round_id, m.pending_result, m.not_before_utc
                FROM campaign_messages m
                WHERE m.campaign_id = ct.campaign_id AND m.ticket_id = ct.ticket_id
                ORDER BY m.created_utc DESC
                LIMIT 1
            ) lm ON TRUE
            LEFT JOIN campaign_rounds lr ON lr.id = lm.round_id
            {CampaignSql.SentLateral("ct.campaign_id", "ct.ticket_id", "fs")}
            {CampaignSql.ReplyLateral("t.id", "fs.first_sent", "rep")}
            WHERE ct.campaign_id = @campaignId
              AND {scope.SqlPredicate("t.queue_id")}
            ORDER BY t.number;

            SELECT COUNT(*)::int
            FROM campaign_tickets ct
            JOIN tickets t ON t.id = ct.ticket_id AND t.is_deleted = FALSE
            WHERE ct.campaign_id = @campaignId
              AND NOT ({scope.SqlPredicate("t.queue_id")});
            """;

        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var grid = await conn.QueryMultipleAsync(new CommandDefinition(
            sql, scope.Parameters(new { campaignId }), cancellationToken: ct));
        var rows = (await grid.ReadAsync<CampaignRecipientRow>()).ToList();
        var hidden = await grid.ReadSingleAsync<int>();

        var views = rows.Select(r => new CampaignRecipientView(r, CampaignRecipientState.Derive(r, nowUtc))).ToList();
        return new CampaignDashboard(views, hidden);
    }
}
