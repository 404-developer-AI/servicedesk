using Dapper;
using Npgsql;

namespace Servicedesk.Infrastructure.Maintenance;

/// A round whose reminder is due, claimed for one send.
public sealed class DueMaintenanceReminder
{
    public Guid RoundId { get; set; }
    public Guid PlanId { get; set; }
    public string PlanName { get; set; } = string.Empty;
    public Guid CampaignId { get; set; }
    public DateTime InterventionDate { get; set; }
    public int InterventionMinute { get; set; }
    public string ReminderSubject { get; set; } = string.Empty;
    public string ReminderBodyHtml { get; set; } = string.Empty;
    public Guid? SendAsUserId { get; set; }
    public Guid? PendingStatusId { get; set; }
    public int? PendingDays { get; set; }
}

/// v0.1.67 (phase 3) — the maintenance reminder bookkeeping. Owner decision:
/// ONE reminder per round, auto-send plans only. A round is claimed by
/// stamping <c>reminder_utc</c> before anything is sent, so a crash or a
/// second worker can never send it twice (at most once, never forced).
public interface IMaintenanceReminderRepository
{
    /// Claims rounds whose first (Initial) campaign round started at least
    /// the plan's reminder days before <paramref name="nowUtc"/>.
    Task<IReadOnlyList<DueMaintenanceReminder>> ClaimDueAsync(DateTime nowUtc, int limit, CancellationToken ct);

    /// What happened ("Reminder sent to 2 customers", "everyone answered", an error).
    Task SetNoteAsync(Guid roundId, string note, CancellationToken ct);
}

public sealed class MaintenanceReminderRepository : IMaintenanceReminderRepository
{
    private readonly NpgsqlDataSource _dataSource;

    public MaintenanceReminderRepository(NpgsqlDataSource dataSource) => _dataSource = dataSource;

    public async Task<IReadOnlyList<DueMaintenanceReminder>> ClaimDueAsync(DateTime nowUtc, int limit, CancellationToken ct)
    {
        const string sql = """
            WITH due AS (
                SELECT r.id FROM maintenance_rounds r
                JOIN maintenance_plans p ON p.id = r.plan_id
                JOIN LATERAL (
                    SELECT cr.started_utc FROM campaign_rounds cr
                    WHERE cr.campaign_id = r.campaign_id AND cr.kind = 'Initial'
                    ORDER BY cr.round_no LIMIT 1
                ) first ON TRUE
                WHERE r.state = 'Created' AND r.reminder_utc IS NULL AND r.campaign_id IS NOT NULL
                  AND p.reminder_enabled AND p.auto_send
                  AND first.started_utc + make_interval(days => p.reminder_days) <= @nowUtc
                ORDER BY first.started_utc
                LIMIT @limit
                FOR UPDATE OF r SKIP LOCKED
            )
            UPDATE maintenance_rounds r SET reminder_utc = @nowUtc
            FROM due, maintenance_plans p
            WHERE r.id = due.id AND p.id = r.plan_id
            RETURNING r.id AS RoundId, r.plan_id AS PlanId, p.name AS PlanName, r.campaign_id AS CampaignId,
                      r.intervention_date AS InterventionDate, r.intervention_minute AS InterventionMinute,
                      p.reminder_subject AS ReminderSubject, p.reminder_body_html AS ReminderBodyHtml,
                      p.send_as_user_id AS SendAsUserId, p.pending_status_id AS PendingStatusId, p.pending_days AS PendingDays
            """;
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        return (await conn.QueryAsync<DueMaintenanceReminder>(new CommandDefinition(sql,
            new { nowUtc = DateTime.SpecifyKind(nowUtc, DateTimeKind.Utc), limit }, cancellationToken: ct))).ToList();
    }

    public async Task SetNoteAsync(Guid roundId, string note, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE maintenance_rounds SET reminder_note = @note WHERE id = @roundId",
            new { roundId, note = note.Length <= 1000 ? note : note[..1000] }, cancellationToken: ct));
    }
}
