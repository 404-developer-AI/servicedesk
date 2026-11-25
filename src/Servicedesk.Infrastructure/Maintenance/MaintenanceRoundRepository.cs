using Dapper;
using Npgsql;

namespace Servicedesk.Infrastructure.Maintenance;

/// A round the worker still has to finish, with the plan settings it needs.
public sealed class OpenMaintenanceRound
{
    public Guid Id { get; set; }
    public Guid PlanId { get; set; }
    public DateTime InterventionDate { get; set; }
    public int InterventionMinute { get; set; }
    public int Attempts { get; set; }
    public Guid? CampaignId { get; set; }
    public string PlanName { get; set; } = string.Empty;
    public Guid QueueId { get; set; }
    public Guid? TicketTemplateId { get; set; }
    public string TicketSubject { get; set; } = string.Empty;
    public string MailSubject { get; set; } = string.Empty;
    public string MailBodyHtml { get; set; } = string.Empty;
    public bool AutoSend { get; set; }
    public Guid? SendAsUserId { get; set; }
    public Guid? PendingStatusId { get; set; }
    public int? PendingDays { get; set; }
    public string OtherTimeNotice { get; set; } = string.Empty;
}

/// A member of the round's plan and its ticket for this round (if made yet).
public sealed class RoundMember
{
    public Guid MemberId { get; set; }
    public Guid? CompanyId { get; set; }
    public Guid ContactId { get; set; }
    public string Label { get; set; } = string.Empty;
    public Guid? TicketId { get; set; }
}

/// v0.1.67 (phase 3) — round bookkeeping. Starting a round and moving the
/// plan on happen in one transaction under the plan row lock
/// (<c>FOR UPDATE SKIP LOCKED</c>), and UNIQUE (plan_id, intervention_date)
/// makes a double start impossible. Tickets are a second step per member:
/// each is recorded in maintenance_round_tickets right after creation, so a
/// retry never creates a second ticket for the same member.
public interface IMaintenanceRoundRepository
{
    /// Starts the rounds whose mail moment (intervention − days before, at the
    /// plan's mail time) is on or before <paramref name="nowLocal"/> (server
    /// wall time). Automatic plans move to their next intervention, manual
    /// plans are left without one.
    Task<int> StartDueRoundsAsync(DateTime nowLocal, int limit, CancellationToken ct);

    /// Starts the plan's next round now, whatever its mail day ("Start now").
    Task<Guid?> StartNowAsync(Guid planId, CancellationToken ct);

    Task<IReadOnlyList<OpenMaintenanceRound>> ClaimOpenAsync(int limit, CancellationToken ct);
    Task<IReadOnlyList<RoundMember>> MembersAsync(Guid roundId, Guid planId, CancellationToken ct);
    Task RecordTicketAsync(Guid roundId, Guid memberId, Guid ticketId, CancellationToken ct);
    Task SetCampaignAsync(Guid roundId, Guid campaignId, CancellationToken ct);
    Task MarkCreatedAsync(Guid roundId, CancellationToken ct);
    Task MarkFailedAsync(Guid roundId, string error, DateTime nextAttemptUtc, CancellationToken ct);
}

public sealed class MaintenanceRoundRepository : IMaintenanceRoundRepository
{
    private readonly NpgsqlDataSource _dataSource;

    public MaintenanceRoundRepository(NpgsqlDataSource dataSource) => _dataSource = dataSource;

    private const string DueColumns = """
        p.id AS Id, p.next_intervention_date AS NextDate, p.intervention_minute AS Minute,
        p.schedule_mode AS Mode, p.interval_months AS Months, p.anchor_day AS AnchorDay
        """;

    public async Task<int> StartDueRoundsAsync(DateTime nowLocal, int limit, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var due = (await conn.QueryAsync<DuePlan>(new CommandDefinition($"""
            SELECT {DueColumns}
            FROM maintenance_plans p
            WHERE p.is_active AND p.next_intervention_date IS NOT NULL
              AND (p.next_intervention_date - p.mail_days_before) + make_interval(mins => p.mail_minute) <= @now::timestamp
            ORDER BY p.next_intervention_date
            LIMIT @limit
            FOR UPDATE SKIP LOCKED
            """, new { now = DateTime.SpecifyKind(nowLocal, DateTimeKind.Unspecified), limit }, tx, cancellationToken: ct))).ToList();

        foreach (var plan in due)
            await StartAsync(conn, tx, plan, ct);
        await tx.CommitAsync(ct);
        return due.Count;
    }

    public async Task<Guid?> StartNowAsync(Guid planId, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var plan = await conn.QueryFirstOrDefaultAsync<DuePlan>(new CommandDefinition($"""
            SELECT {DueColumns}
            FROM maintenance_plans p
            WHERE p.id = @planId AND p.is_active AND p.next_intervention_date IS NOT NULL
            FOR UPDATE
            """, new { planId }, tx, cancellationToken: ct));
        if (plan is null) return null;
        var roundId = await StartAsync(conn, tx, plan, ct);
        await tx.CommitAsync(ct);
        return roundId;
    }

    private static async Task<Guid?> StartAsync(NpgsqlConnection conn, NpgsqlTransaction tx, DuePlan plan, CancellationToken ct)
    {
        var roundId = await conn.ExecuteScalarAsync<Guid?>(new CommandDefinition("""
            INSERT INTO maintenance_rounds (plan_id, intervention_date, intervention_minute)
            VALUES (@Id, @NextDate::date, @Minute)
            ON CONFLICT (plan_id, intervention_date) DO NOTHING
            RETURNING id
            """, plan, tx, cancellationToken: ct));
        if (roundId is { } started)
        {
            // "Skip next round" is consumed by the round that just started, in
            // the same transaction: recorded as a skipped row, flag cleared.
            await conn.ExecuteAsync(new CommandDefinition("""
                INSERT INTO maintenance_round_tickets (round_id, member_id, skipped)
                SELECT @started, m.id, TRUE FROM maintenance_plan_members m
                WHERE m.plan_id = @planId AND m.is_active AND m.skip_next_round
                ON CONFLICT (round_id, member_id) DO NOTHING;
                UPDATE maintenance_plan_members SET skip_next_round = FALSE
                WHERE plan_id = @planId AND skip_next_round;
                """, new { started, planId = plan.Id }, tx, cancellationToken: ct));
        }
        DateTime? next = plan.Mode == MaintenanceScheduleModes.Automatic && plan.Months is { } months
            ? MaintenanceCalendar.NextDue(plan.NextDate, months, plan.AnchorDay ?? plan.NextDate.Day)
            : null;
        await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE maintenance_plans SET next_intervention_date = @next::date, updated_utc = now() WHERE id = @id",
            new { next, id = plan.Id }, tx, cancellationToken: ct));
        return roundId;
    }

    public async Task<IReadOnlyList<OpenMaintenanceRound>> ClaimOpenAsync(int limit, CancellationToken ct)
    {
        // Claimed by bumping attempts and pushing next_attempt_utc out, so a
        // crashed run retries later instead of in a tight loop.
        const string sql = """
            WITH open AS (
                SELECT r.id FROM maintenance_rounds r
                WHERE r.state IN ('Pending','Failed')
                  AND COALESCE(r.next_attempt_utc, '-infinity') <= now()
                  AND r.attempts < 10
                ORDER BY r.next_attempt_utc NULLS FIRST, r.created_utc
                LIMIT @limit
                FOR UPDATE SKIP LOCKED
            )
            UPDATE maintenance_rounds r
            SET attempts = r.attempts + 1, next_attempt_utc = now() + interval '15 minutes'
            FROM open, maintenance_plans p
            WHERE r.id = open.id AND p.id = r.plan_id
            RETURNING r.id AS Id, r.plan_id AS PlanId, r.intervention_date AS InterventionDate,
                      r.intervention_minute AS InterventionMinute, r.attempts AS Attempts, r.campaign_id AS CampaignId,
                      p.name AS PlanName, p.queue_id AS QueueId, p.ticket_template_id AS TicketTemplateId,
                      p.ticket_subject AS TicketSubject, p.mail_subject AS MailSubject, p.mail_body_html AS MailBodyHtml,
                      p.auto_send AS AutoSend, p.send_as_user_id AS SendAsUserId, p.pending_status_id AS PendingStatusId,
                      p.pending_days AS PendingDays, p.other_time_notice AS OtherTimeNotice
            """;
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        return (await conn.QueryAsync<OpenMaintenanceRound>(new CommandDefinition(sql, new { limit }, cancellationToken: ct))).ToList();
    }

    public async Task<IReadOnlyList<RoundMember>> MembersAsync(Guid roundId, Guid planId, CancellationToken ct)
    {
        // Active members, plus any member that already got a ticket this round;
        // never a member skipped for this round.
        const string sql = """
            SELECT m.id AS MemberId, m.company_id AS CompanyId, m.contact_id AS ContactId, m.label AS Label,
                   rt.ticket_id AS TicketId
            FROM maintenance_plan_members m
            LEFT JOIN maintenance_round_tickets rt ON rt.round_id = @roundId AND rt.member_id = m.id
            WHERE m.plan_id = @planId AND (m.is_active OR rt.ticket_id IS NOT NULL) AND rt.skipped IS NOT TRUE
            ORDER BY m.created_utc
            """;
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        return (await conn.QueryAsync<RoundMember>(new CommandDefinition(sql, new { roundId, planId }, cancellationToken: ct))).ToList();
    }

    public Task RecordTicketAsync(Guid roundId, Guid memberId, Guid ticketId, CancellationToken ct) => ExecAsync("""
        INSERT INTO maintenance_round_tickets (round_id, member_id, ticket_id) VALUES (@roundId, @memberId, @ticketId)
        ON CONFLICT (round_id, member_id) DO UPDATE SET ticket_id = EXCLUDED.ticket_id
        WHERE NOT maintenance_round_tickets.skipped
        """, new { roundId, memberId, ticketId }, ct);

    public Task SetCampaignAsync(Guid roundId, Guid campaignId, CancellationToken ct) => ExecAsync(
        "UPDATE maintenance_rounds SET campaign_id = @campaignId WHERE id = @roundId", new { roundId, campaignId }, ct);

    public Task MarkCreatedAsync(Guid roundId, CancellationToken ct) => ExecAsync("""
        UPDATE maintenance_rounds SET state = 'Created', last_error = '', next_attempt_utc = NULL WHERE id = @roundId
        """, new { roundId }, ct);

    public Task MarkFailedAsync(Guid roundId, string error, DateTime nextAttemptUtc, CancellationToken ct) => ExecAsync("""
        UPDATE maintenance_rounds SET state = 'Failed', last_error = @error, next_attempt_utc = @nextAttemptUtc
        WHERE id = @roundId AND state <> 'Created'
        """, new { roundId, error = error.Length <= 1000 ? error : error[..1000], nextAttemptUtc }, ct);

    private async Task ExecAsync(string sql, object args, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(sql, args, cancellationToken: ct));
    }

    private sealed class DuePlan
    {
        public Guid Id { get; set; }
        public DateTime NextDate { get; set; }
        public int Minute { get; set; }
        public string Mode { get; set; } = MaintenanceScheduleModes.Manual;
        public int? Months { get; set; }
        public int? AnchorDay { get; set; }
    }
}
