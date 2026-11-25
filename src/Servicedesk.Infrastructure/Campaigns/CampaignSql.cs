namespace Servicedesk.Infrastructure.Campaigns;

/// v0.1.67 — the shared SQL of "how is this recipient doing": the first and
/// last successful send (+ the pending-till of the latest sent round) and the
/// customer's first answer after the first send. One definition for the
/// campaign dashboard and the maintenance planner board, so "reply received"
/// can never mean two things. Arguments are column references written by the
/// callers (never user input).
internal static class CampaignSql
{
    /// LATERAL exposing <c>{alias}.first_sent</c>, <c>{alias}.last_sent</c>, <c>{alias}.pending_till</c>.
    public static string SentLateral(string campaignIdExpr, string ticketIdExpr, string alias) => $"""
        LEFT JOIN LATERAL (
            SELECT MIN(m.sent_utc) AS first_sent, MAX(m.sent_utc) AS last_sent,
                   (ARRAY_AGG(r.pending_till_utc ORDER BY m.sent_utc DESC))[1] AS pending_till
            FROM campaign_messages m
            JOIN campaign_rounds r ON r.id = m.round_id
            WHERE m.campaign_id = {campaignIdExpr} AND m.ticket_id = {ticketIdExpr} AND m.state = 'Sent'
        ) {alias} ON TRUE
        """;

    /// LATERAL exposing <c>{alias}.created_utc</c>, <c>{alias}.event_type</c>: the
    /// first customer mail, portal message or answer-link answer after
    /// <paramref name="firstSentExpr"/>; auto-replies never count.
    public static string ReplyLateral(string ticketIdExpr, string firstSentExpr, string alias) => $"""
        LEFT JOIN LATERAL (
            SELECT e.created_utc, e.event_type
            FROM ticket_events e
            LEFT JOIN mail_messages mm ON mm.ticket_event_id = e.id
            WHERE e.ticket_id = {ticketIdExpr}
              AND {firstSentExpr} IS NOT NULL
              AND e.created_utc > {firstSentExpr}
              AND e.event_type IN ('MailReceived', 'PortalMessage', 'CampaignAnswer')
              AND COALESCE(mm.is_auto_submitted, FALSE) = FALSE
            ORDER BY e.created_utc
            LIMIT 1
        ) {alias} ON TRUE
        """;
}
