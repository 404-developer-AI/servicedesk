using Dapper;
using Npgsql;

namespace Servicedesk.Infrastructure.Maintenance;

/// v0.1.67 (phase 3) — maintenance plans and their members. Plans are shared
/// among Campaigns flag holders (like campaign metadata); the tickets and
/// answers of a round live on its campaign, which is queue-scoped. A save
/// writes the plan and syncs its members in one transaction; a member that
/// already has a round ticket is switched off instead of deleted.
public interface IMaintenancePlanRepository
{
    Task<IReadOnlyList<MaintenancePlanRow>> ListAsync(bool includeInactive, CancellationToken ct);
    Task<MaintenancePlanDetail?> GetAsync(Guid id, CancellationToken ct);
    /// <paramref name="userId"/> becomes the creator and, with auto-send on, the sender.
    Task<Guid> CreateAsync(MaintenancePlanInput input, Guid userId, CancellationToken ct);
    Task<bool> UpdateAsync(Guid id, MaintenancePlanInput input, Guid userId, CancellationToken ct);
    /// Deletes a plan that never started a round; false otherwise (switch it off instead).
    Task<bool> DeleteUnstartedAsync(Guid id, CancellationToken ct);
    /// True when the plan already had a round for this intervention date —
    /// one round per plan and date (UNIQUE), so that date cannot be planned again.
    Task<bool> HasRoundOnAsync(Guid planId, DateTime interventionDate, CancellationToken ct);
}

public sealed class MaintenancePlanRepository : IMaintenancePlanRepository
{
    private readonly NpgsqlDataSource _dataSource;

    public MaintenancePlanRepository(NpgsqlDataSource dataSource) => _dataSource = dataSource;

    private const string PlanSql = """
        SELECT p.id AS Id, p.name AS Name, p.notes AS Notes, p.queue_id AS QueueId, q.name AS QueueName,
               p.ticket_template_id AS TicketTemplateId, tt.name AS TicketTemplateName, p.ticket_subject AS TicketSubject,
               p.mail_subject AS MailSubject, p.mail_body_html AS MailBodyHtml, p.schedule_mode AS ScheduleMode,
               p.interval_months AS IntervalMonths, p.next_intervention_date AS NextInterventionDate,
               p.intervention_minute AS InterventionMinute, p.mail_days_before AS MailDaysBefore, p.mail_minute AS MailMinute,
               p.other_time_notice AS OtherTimeNotice, p.reminder_enabled AS ReminderEnabled, p.reminder_days AS ReminderDays, p.reminder_subject AS ReminderSubject,
               p.reminder_body_html AS ReminderBodyHtml, lr.reminder_utc AS LastReminderUtc, NULLIF(lr.reminder_note, '') AS LastReminderNote,
               p.auto_send AS AutoSend, p.send_as_user_id AS SendAsUserId, su.email::text AS SendAsEmail,
               p.pending_status_id AS PendingStatusId, p.pending_days AS PendingDays, p.is_active AS IsActive, p.updated_utc AS UpdatedUtc,
               (SELECT COUNT(*)::int FROM maintenance_plan_members m WHERE m.plan_id = p.id AND m.is_active) AS MemberCount,
               lr.id AS LastRoundId, lr.intervention_date AS LastInterventionDate, lr.state AS LastRoundState,
               NULLIF(lr.last_error, '') AS LastRoundError, lr.campaign_id AS LastCampaignId, ca.name AS LastCampaignName,
               COALESCE(st.tickets, 0) AS LastTickets, COALESCE(st.sent, 0) AS LastSent,
               COALESCE(st.approved, 0) AS LastApproved, COALESCE(st.other, 0) AS LastOtherTime,
               COALESCE(st.declined, 0) AS LastDeclined,
               (SELECT COUNT(*)::int FROM maintenance_round_tickets rs WHERE rs.round_id = lr.id AND rs.skipped) AS LastSkipped
        FROM maintenance_plans p
        LEFT JOIN queues q ON q.id = p.queue_id
        LEFT JOIN ticket_templates tt ON tt.id = p.ticket_template_id
        LEFT JOIN users su ON su.id = p.send_as_user_id
        LEFT JOIN LATERAL (
            SELECT r.* FROM maintenance_rounds r WHERE r.plan_id = p.id ORDER BY r.intervention_date DESC LIMIT 1
        ) lr ON TRUE
        LEFT JOIN campaigns ca ON ca.id = lr.campaign_id
        LEFT JOIN LATERAL (
            SELECT COUNT(*)::int AS tickets,
                   COUNT(*) FILTER (WHERE EXISTS (SELECT 1 FROM campaign_messages m
                                                  WHERE m.campaign_id = ct.campaign_id AND m.ticket_id = ct.ticket_id
                                                    AND m.state = 'Sent'))::int AS sent,
                   COUNT(*) FILTER (WHERE ct.decision = 'Approved')::int  AS approved,
                   COUNT(*) FILTER (WHERE ct.decision = 'OtherTime')::int AS other,
                   COUNT(*) FILTER (WHERE ct.decision = 'Declined')::int  AS declined
            FROM campaign_tickets ct WHERE ct.campaign_id = lr.campaign_id
        ) st ON TRUE
        """;

    public async Task<IReadOnlyList<MaintenancePlanRow>> ListAsync(bool includeInactive, CancellationToken ct)
    {
        var sql = PlanSql + """

            WHERE @includeInactive OR p.is_active
            ORDER BY p.is_active DESC, p.next_intervention_date NULLS LAST, lower(p.name)
            LIMIT 1000
            """;
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        return (await conn.QueryAsync<MaintenancePlanRow>(new CommandDefinition(sql, new { includeInactive }, cancellationToken: ct))).ToList();
    }

    public async Task<MaintenancePlanDetail?> GetAsync(Guid id, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var plan = await conn.QueryFirstOrDefaultAsync<MaintenancePlanRow>(new CommandDefinition(
            PlanSql + "\nWHERE p.id = @id", new { id }, cancellationToken: ct));
        if (plan is null) return null;
        var members = await conn.QueryAsync<MaintenanceMemberRow>(new CommandDefinition("""
            SELECT m.id AS Id, m.plan_id AS PlanId, m.company_id AS CompanyId, co.name AS CompanyName,
                   m.contact_id AS ContactId,
                   NULLIF(trim(COALESCE(c.first_name, '') || ' ' || COALESCE(c.last_name, '')), '') AS ContactName,
                   c.email::text AS ContactEmail, m.label AS Label, m.is_active AS IsActive,
                   m.skip_next_round AS SkipNextRound,
                   (SELECT COUNT(*)::int FROM maintenance_member_notes n WHERE n.member_id = m.id AND NOT n.is_deleted) AS NoteCount
            FROM maintenance_plan_members m
            LEFT JOIN companies co ON co.id = m.company_id
            LEFT JOIN contacts c ON c.id = m.contact_id
            WHERE m.plan_id = @id
            ORDER BY m.is_active DESC, lower(COALESCE(co.name, '')), lower(m.label)
            """, new { id }, cancellationToken: ct));
        return new MaintenancePlanDetail(plan, members.ToList());
    }

    public async Task<Guid> CreateAsync(MaintenancePlanInput i, Guid userId, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var p = Parameters(i, userId);
        var id = await conn.ExecuteScalarAsync<Guid>(new CommandDefinition("""
            INSERT INTO maintenance_plans (name, notes, queue_id, ticket_template_id, ticket_subject, mail_subject,
                mail_body_html, schedule_mode, interval_months, next_intervention_date, intervention_minute, anchor_day,
                mail_days_before, mail_minute, auto_send, send_as_user_id, pending_status_id, pending_days, is_active, created_by_user_id,
                reminder_enabled, reminder_days, reminder_subject, reminder_body_html, other_time_notice)
            VALUES (@Name, @Notes, @QueueId, @TicketTemplateId, @TicketSubject, @MailSubject,
                @MailBodyHtml, @ScheduleMode, @IntervalMonths, @NextInterventionDate::date, @InterventionMinute,
                EXTRACT(DAY FROM @NextInterventionDate::date)::int,
                @MailDaysBefore, @MailMinute, @AutoSend, CASE WHEN @AutoSend THEN @userId END, @PendingStatusId, @PendingDays, @IsActive, @userId,
                @ReminderEnabled, @ReminderDays, @ReminderSubject, @ReminderBodyHtml, @OtherTimeNotice)
            RETURNING id
            """, p, tx, cancellationToken: ct));
        await SyncMembersAsync(conn, tx, id, i.Members, ct);
        await tx.CommitAsync(ct);
        return id;
    }

    public async Task<bool> UpdateAsync(Guid id, MaintenancePlanInput i, Guid userId, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var p = Parameters(i, userId);
        p.Add("Id", id);
        // A changed date also resets the day the calendar keeps; whoever
        // saves with auto-send on becomes the sender (rechecked per send).
        var updated = await conn.ExecuteAsync(new CommandDefinition("""
            UPDATE maintenance_plans SET
                name = @Name, notes = @Notes, queue_id = @QueueId, ticket_template_id = @TicketTemplateId,
                ticket_subject = @TicketSubject, mail_subject = @MailSubject, mail_body_html = @MailBodyHtml,
                schedule_mode = @ScheduleMode, interval_months = @IntervalMonths,
                anchor_day = CASE WHEN next_intervention_date IS NOT DISTINCT FROM @NextInterventionDate::date THEN anchor_day
                                  ELSE EXTRACT(DAY FROM @NextInterventionDate::date)::int END,
                next_intervention_date = @NextInterventionDate::date, intervention_minute = @InterventionMinute,
                mail_days_before = @MailDaysBefore, mail_minute = @MailMinute, auto_send = @AutoSend,
                send_as_user_id = CASE WHEN @AutoSend THEN @userId END,
                pending_status_id = @PendingStatusId, pending_days = @PendingDays, is_active = @IsActive,
                reminder_enabled = @ReminderEnabled, reminder_days = @ReminderDays, reminder_subject = @ReminderSubject,
                reminder_body_html = @ReminderBodyHtml, other_time_notice = @OtherTimeNotice, updated_utc = now()
            WHERE id = @Id
            """, p, tx, cancellationToken: ct));
        if (updated == 0) return false;
        await SyncMembersAsync(conn, tx, id, i.Members, ct);
        await tx.CommitAsync(ct);
        return true;
    }

    public async Task<bool> HasRoundOnAsync(Guid planId, DateTime interventionDate, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var p = new DynamicParameters();
        p.Add("planId", planId);
        p.Add("date", interventionDate, System.Data.DbType.Date);
        return await conn.ExecuteScalarAsync<bool>(new CommandDefinition(
            "SELECT EXISTS (SELECT 1 FROM maintenance_rounds WHERE plan_id = @planId AND intervention_date = @date)",
            p, cancellationToken: ct));
    }

    public async Task<bool> DeleteUnstartedAsync(Guid id, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        return await conn.ExecuteAsync(new CommandDefinition("""
            DELETE FROM maintenance_plans p
            WHERE p.id = @id AND NOT EXISTS (SELECT 1 FROM maintenance_rounds r WHERE r.plan_id = p.id)
            """, new { id }, cancellationToken: ct)) > 0;
    }

    private static DynamicParameters Parameters(MaintenancePlanInput i, Guid userId)
    {
        var p = new DynamicParameters();
        p.Add("Name", i.Name);
        p.Add("Notes", i.Notes);
        p.Add("QueueId", i.QueueId);
        p.Add("TicketTemplateId", i.TicketTemplateId);
        p.Add("TicketSubject", i.TicketSubject);
        p.Add("MailSubject", i.MailSubject);
        p.Add("MailBodyHtml", i.MailBodyHtml);
        p.Add("ScheduleMode", i.ScheduleMode);
        p.Add("IntervalMonths", i.ScheduleMode == MaintenanceScheduleModes.Automatic ? i.IntervalMonths : null, System.Data.DbType.Int32);
        p.Add("NextInterventionDate", i.NextInterventionDate, System.Data.DbType.Date);
        p.Add("InterventionMinute", i.InterventionMinute);
        p.Add("MailDaysBefore", i.MailDaysBefore);
        p.Add("MailMinute", i.MailMinute);
        var reminder = i.Reminder ?? new MaintenanceReminderInput(false, 3, string.Empty, string.Empty);
        p.Add("ReminderEnabled", reminder.Enabled && i.AutoSend);
        p.Add("ReminderDays", reminder.Days);
        p.Add("ReminderSubject", reminder.Subject);
        p.Add("ReminderBodyHtml", reminder.BodyHtml);
        p.Add("OtherTimeNotice", i.OtherTimeNotice);
        p.Add("AutoSend", i.AutoSend);
        p.Add("PendingStatusId", i.PendingStatusId);
        p.Add("PendingDays", i.PendingStatusId is null ? null : i.PendingDays, System.Data.DbType.Int32);
        p.Add("IsActive", i.IsActive);
        p.Add("userId", userId);
        return p;
    }

    /// Members in the input are kept (updated) or added; members left out
    /// are deleted, or switched off when a round already gave them a ticket.
    private static async Task SyncMembersAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Guid planId,
        IReadOnlyList<MaintenanceMemberInput> members, CancellationToken ct)
    {
        var keep = new List<Guid>();
        foreach (var m in members)
        {
            if (m.Id is { } existing && await conn.ExecuteAsync(new CommandDefinition("""
                    UPDATE maintenance_plan_members SET company_id = @CompanyId, contact_id = @ContactId,
                                                        label = @Label, is_active = @IsActive, skip_next_round = @SkipNextRound
                    WHERE id = @existing AND plan_id = @planId
                    """, new { m.CompanyId, m.ContactId, m.Label, m.IsActive, m.SkipNextRound, existing, planId }, tx, cancellationToken: ct)) > 0)
            {
                keep.Add(existing);
                continue;
            }
            keep.Add(await conn.ExecuteScalarAsync<Guid>(new CommandDefinition("""
                INSERT INTO maintenance_plan_members (plan_id, company_id, contact_id, label, is_active, skip_next_round)
                VALUES (@planId, @CompanyId, @ContactId, @Label, @IsActive, @SkipNextRound) RETURNING id
                """, new { planId, m.CompanyId, m.ContactId, m.Label, m.IsActive, m.SkipNextRound }, tx, cancellationToken: ct)));
        }

        var ids = keep.ToArray();
        await conn.ExecuteAsync(new CommandDefinition("""
            UPDATE maintenance_plan_members m SET is_active = FALSE
            WHERE m.plan_id = @planId AND NOT (m.id = ANY(@ids))
              AND EXISTS (SELECT 1 FROM maintenance_round_tickets rt WHERE rt.member_id = m.id);
            DELETE FROM maintenance_plan_members m
            WHERE m.plan_id = @planId AND NOT (m.id = ANY(@ids))
              AND NOT EXISTS (SELECT 1 FROM maintenance_round_tickets rt WHERE rt.member_id = m.id);
            """, new { planId, ids }, tx, cancellationToken: ct));
    }
}
