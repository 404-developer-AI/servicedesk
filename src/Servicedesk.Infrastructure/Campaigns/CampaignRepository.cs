using Dapper;
using Npgsql;

namespace Servicedesk.Infrastructure.Campaigns;

public sealed record NewCampaign(
    string Name,
    string Subject,
    string BodyHtml,
    Guid? SourceTemplateId,
    Guid? ProjectTicketId,
    DateTime? DefaultProposedUtc,
    Guid CreatedByUserId,
    Guid? DuplicatedFromId = null,
    string OtherTimeNotice = "");

/// Partial update; null keeps the stored value. <see cref="ClearProject"/>
/// and <see cref="ClearDefaultProposed"/> clear the nullable columns.
public sealed record CampaignUpdate(
    string? Name = null,
    string? Subject = null,
    string? BodyHtml = null,
    Guid? SourceTemplateId = null,
    Guid? ProjectTicketId = null,
    bool ClearProject = false,
    DateTime? DefaultProposedUtc = null,
    bool ClearDefaultProposed = false,
    string? State = null,
    string? OtherTimeNotice = null);

/// v0.1.67 — the campaign rows themselves (message, state, project link) and
/// the round history. Campaign metadata is visible to every holder of the
/// Campaigns flag; the recipients hanging off it are queue-scoped
/// (<see cref="CampaignDashboardReader"/>).
public interface ICampaignRepository
{
    Task<IReadOnlyList<CampaignListItem>> ListAsync(bool includeArchived, Guid? projectTicketId, CancellationToken ct);
    Task<CampaignRow?> GetAsync(Guid id, CancellationToken ct);
    Task<Guid> CreateAsync(NewCampaign campaign, CancellationToken ct);
    Task<bool> UpdateAsync(Guid id, CampaignUpdate update, CancellationToken ct);
    /// Deletes a campaign that never queued a mail. False when it has.
    Task<bool> DeleteUnsentAsync(Guid id, CancellationToken ct);
    Task<IReadOnlyList<CampaignRoundRow>> ListRoundsAsync(Guid campaignId, CancellationToken ct);

    /// Phase 3 — the campaign of one maintenance round, created on first use
    /// with the plan's mail (one per round, unique index; race-safe).
    Task<Guid> GetOrCreateForRoundAsync(Guid roundId, string name, string subject, string bodyHtml, string otherTimeNotice,
        CancellationToken ct);
}

public sealed class CampaignRepository : ICampaignRepository
{
    private readonly NpgsqlDataSource _dataSource;

    public CampaignRepository(NpgsqlDataSource dataSource) => _dataSource = dataSource;

    public async Task<IReadOnlyList<CampaignListItem>> ListAsync(bool includeArchived, Guid? projectTicketId, CancellationToken ct)
    {
        const string sql = """
            SELECT c.id AS Id, c.name AS Name, c.state AS State,
                   c.project_ticket_id AS ProjectTicketId, p.number AS ProjectTicketNumber,
                   u.email AS CreatedByEmail, c.created_utc AS CreatedUtc, c.updated_utc AS UpdatedUtc,
                   (SELECT COUNT(*)::int FROM campaign_tickets ct WHERE ct.campaign_id = c.id) AS TicketCount,
                   COALESCE(m.sent, 0) AS SentCount, COALESCE(m.queued, 0) AS QueuedCount,
                   COALESCE(m.failed, 0) AS FailedCount, m.last_sent AS LastSentUtc
            FROM campaigns c
            LEFT JOIN tickets p ON p.id = c.project_ticket_id
            LEFT JOIN users u ON u.id = c.created_by_user_id
            LEFT JOIN LATERAL (
                SELECT COUNT(*) FILTER (WHERE cm.state = 'Sent')::int                     AS sent,
                       COUNT(*) FILTER (WHERE cm.state IN ('Queued','Sending'))::int      AS queued,
                       COUNT(*) FILTER (WHERE cm.state = 'Failed')::int                   AS failed,
                       MAX(cm.sent_utc)                                                   AS last_sent
                FROM campaign_messages cm WHERE cm.campaign_id = c.id
            ) m ON TRUE
            WHERE (@includeArchived OR c.state <> 'Archived')
              AND (@projectTicketId::uuid IS NULL OR c.project_ticket_id = @projectTicketId)
            ORDER BY c.updated_utc DESC
            LIMIT 500
            """;
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<CampaignListItem>(new CommandDefinition(
            sql, new { includeArchived, projectTicketId }, cancellationToken: ct));
        return rows.ToList();
    }

    public async Task<CampaignRow?> GetAsync(Guid id, CancellationToken ct)
    {
        const string sql = """
            SELECT c.id AS Id, c.name AS Name, c.subject AS Subject, c.body_html AS BodyHtml, c.other_time_notice AS OtherTimeNotice,
                   c.source_template_id AS SourceTemplateId, c.project_ticket_id AS ProjectTicketId,
                   p.number AS ProjectTicketNumber, p.subject AS ProjectTicketSubject,
                   c.default_proposed_utc AS DefaultProposedUtc, c.state AS State,
                   c.duplicated_from_id AS DuplicatedFromId, c.created_by_user_id AS CreatedByUserId,
                   mr.plan_id AS MaintenancePlanId, mp.name AS MaintenancePlanName,
                   c.maintenance_round_id AS MaintenanceRoundId,
                   u.email AS CreatedByEmail, c.created_utc AS CreatedUtc, c.updated_utc AS UpdatedUtc
            FROM campaigns c
            LEFT JOIN tickets p ON p.id = c.project_ticket_id
            LEFT JOIN users u ON u.id = c.created_by_user_id
            LEFT JOIN maintenance_rounds mr ON mr.id = c.maintenance_round_id
            LEFT JOIN maintenance_plans mp ON mp.id = mr.plan_id
            WHERE c.id = @id
            """;
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        return await conn.QueryFirstOrDefaultAsync<CampaignRow>(new CommandDefinition(sql, new { id }, cancellationToken: ct));
    }

    public async Task<Guid> CreateAsync(NewCampaign c, CancellationToken ct)
    {
        const string sql = """
            INSERT INTO campaigns (name, subject, body_html, source_template_id, project_ticket_id,
                                   default_proposed_utc, created_by_user_id, duplicated_from_id, other_time_notice)
            VALUES (@Name, @Subject, @BodyHtml, @SourceTemplateId, @ProjectTicketId,
                    @DefaultProposedUtc, @CreatedByUserId, @DuplicatedFromId, @OtherTimeNotice)
            RETURNING id
            """;
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        return await conn.ExecuteScalarAsync<Guid>(new CommandDefinition(sql, c, cancellationToken: ct));
    }

    public async Task<bool> UpdateAsync(Guid id, CampaignUpdate u, CancellationToken ct)
    {
        const string sql = """
            UPDATE campaigns SET
                name                 = COALESCE(@Name, name),
                subject              = COALESCE(@Subject, subject),
                body_html            = COALESCE(@BodyHtml, body_html),
                source_template_id   = COALESCE(@SourceTemplateId, source_template_id),
                project_ticket_id    = CASE WHEN @ClearProject THEN NULL
                                            ELSE COALESCE(@ProjectTicketId, project_ticket_id) END,
                default_proposed_utc = CASE WHEN @ClearDefaultProposed THEN NULL
                                            ELSE COALESCE(@DefaultProposedUtc, default_proposed_utc) END,
                state                = COALESCE(@State, state),
                other_time_notice    = COALESCE(@OtherTimeNotice, other_time_notice),
                updated_utc          = now()
            WHERE id = @Id
            """;
        var p = new DynamicParameters(u);
        p.Add("Id", id);
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        return await conn.ExecuteAsync(new CommandDefinition(sql, p, cancellationToken: ct)) > 0;
    }

    public async Task<bool> DeleteUnsentAsync(Guid id, CancellationToken ct)
    {
        // One guarded statement: never delete a campaign whose mails are on
        // ticket timelines (the history must stay explainable).
        const string sql = """
            DELETE FROM campaigns c
            WHERE c.id = @id
              AND NOT EXISTS (SELECT 1 FROM campaign_rounds r WHERE r.campaign_id = c.id)
            """;
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        return await conn.ExecuteAsync(new CommandDefinition(sql, new { id }, cancellationToken: ct)) > 0;
    }

    public async Task<Guid> GetOrCreateForRoundAsync(Guid roundId, string name, string subject, string bodyHtml,
        string otherTimeNotice, CancellationToken ct)
    {
        const string sql = """
            INSERT INTO campaigns (name, subject, body_html, maintenance_round_id, other_time_notice)
            VALUES (@name, @subject, @bodyHtml, @roundId, @otherTimeNotice)
            ON CONFLICT (maintenance_round_id) WHERE maintenance_round_id IS NOT NULL DO NOTHING
            RETURNING id
            """;
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var id = await conn.ExecuteScalarAsync<Guid?>(new CommandDefinition(
            sql, new { roundId, name, subject, bodyHtml, otherTimeNotice }, cancellationToken: ct));
        // Already there (a retry, or a concurrent start): read it.
        return id ?? await conn.ExecuteScalarAsync<Guid>(new CommandDefinition(
            "SELECT id FROM campaigns WHERE maintenance_round_id = @roundId", new { roundId }, cancellationToken: ct));
    }

    public async Task<IReadOnlyList<CampaignRoundRow>> ListRoundsAsync(Guid campaignId, CancellationToken ct)
    {
        const string sql = """
            SELECT r.id AS Id, r.campaign_id AS CampaignId, r.round_no AS RoundNo, r.kind AS Kind,
                   r.subject AS Subject, r.sent_by_user_id AS SentByUserId, u.email AS SentByEmail,
                   r.sent_by_role AS SentByRole, r.pending_status_id AS PendingStatusId,
                   r.pending_till_utc AS PendingTillUtc, r.scheduled_utc AS ScheduledUtc,
                   r.started_utc AS StartedUtc, r.completed_utc AS CompletedUtc,
                   COUNT(m.id)::int                                          AS Total,
                   COUNT(*) FILTER (WHERE m.state = 'Sent')::int             AS Sent,
                   COUNT(*) FILTER (WHERE m.state IN ('Queued','Sending'))::int AS Queued,
                   COUNT(*) FILTER (WHERE m.state = 'Failed')::int           AS Failed,
                   COUNT(*) FILTER (WHERE m.state = 'Skipped')::int          AS Skipped,
                   COUNT(*) FILTER (WHERE m.state = 'Cancelled')::int        AS Cancelled
            FROM campaign_rounds r
            LEFT JOIN users u ON u.id = r.sent_by_user_id
            LEFT JOIN campaign_messages m ON m.round_id = r.id
            WHERE r.campaign_id = @campaignId
            GROUP BY r.id, u.email
            ORDER BY r.round_no DESC
            """;
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<CampaignRoundRow>(new CommandDefinition(sql, new { campaignId }, cancellationToken: ct));
        return rows.ToList();
    }
}
