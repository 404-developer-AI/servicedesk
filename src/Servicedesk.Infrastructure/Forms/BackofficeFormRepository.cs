using System.Text.Json.Nodes;
using Dapper;
using Npgsql;

namespace Servicedesk.Infrastructure.Forms;

/// v0.1.54 — the back-office states of a filled form. NULL on the row means
/// "not a back-office form" (its template was not marked when it was filled).
public static class BackofficeFormStatus
{
    public const string Open = "Open";
    public const string InProgress = "InProgress";
    public const string Handled = "Handled";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        Open, InProgress, Handled,
    };
}

/// Log actions; the DB CHECK on <c>ticket_form_backoffice_log.action</c>
/// holds the same list.
public static class BackofficeFormAction
{
    public const string Submitted = "submitted";
    public const string Claimed = "claimed";
    public const string Released = "released";
    public const string Handled = "handled";
    public const string Reopened = "reopened";
    /// The form was edited while In progress (it stays claimed).
    public const string Updated = "updated";
}

public sealed record BackofficeFormListQuery(
    string? Status, Guid? TemplateId, Guid? CompanyId, Guid? SubmittedByUserId,
    string? Search, int Offset, int Limit);

public sealed record BackofficeFormListItem(
    Guid Id, Guid TicketId, long TicketNumber, string TicketSubject,
    Guid? CompanyId, string? CompanyName, Guid? TemplateId, string Name, int Version, string Status,
    Guid? SubmittedByUserId, string? SubmittedByName, DateTime SubmittedUtc,
    Guid? UpdatedByUserId, string? UpdatedByName, DateTime UpdatedUtc,
    Guid? ClaimedByUserId, string? ClaimedByName, DateTime? ClaimedUtc, int? ClaimedVersion,
    Guid? HandledByUserId, string? HandledByName, DateTime? HandledUtc, int? HandledVersion,
    string HandledNote, bool Reopened);

public sealed record BackofficeFormPage(IReadOnlyList<BackofficeFormListItem> Items, int Total);

public sealed record BackofficeFormCounts(int Open, int InProgress);

public sealed record BackofficeFormLogEntry(
    long Id, string Action, Guid? UserId, string? UserName, string Note, int FormVersion, DateTime CreatedUtc);

public sealed record BackofficeFormDetail(
    BackofficeFormListItem Item, FormDefinition Snapshot, JsonObject Answers,
    IReadOnlyList<BackofficeFormLogEntry> Log, JsonObject? HandledAnswers);

/// What a state transition left behind; null from the repository = the
/// guarded UPDATE matched nothing (wrong state / not the claimer / gone).
public sealed record BackofficeTransitionResult(int Version);

public interface IBackofficeFormRepository
{
    Task<BackofficeFormPage> ListAsync(BackofficeFormListQuery query, CancellationToken ct);
    Task<BackofficeFormCounts> CountsAsync(CancellationToken ct);
    /// Only back-office forms (bo_status not null) on live tickets.
    Task<BackofficeFormListItem?> GetItemAsync(Guid formId, CancellationToken ct);
    Task<BackofficeFormDetail?> GetDetailAsync(Guid formId, CancellationToken ct);

    /// Open → InProgress (or, with <paramref name="takeOver"/>, an
    /// InProgress form claimed by someone else → claimed by the caller).
    Task<BackofficeTransitionResult?> ClaimAsync(Guid formId, Guid userId, bool takeOver, CancellationToken ct);
    /// InProgress → Open. Only the claimer, unless <paramref name="force"/> (admin).
    Task<BackofficeTransitionResult?> ReleaseAsync(Guid formId, Guid userId, bool force, CancellationToken ct);
    /// Open or InProgress → Handled. An InProgress form claimed by someone
    /// else is only handled with <paramref name="force"/> (admin). Only when
    /// the form is still at <paramref name="expectedVersion"/> (the version
    /// the handler looked at), so an edit made meanwhile is never marked handled.
    Task<BackofficeTransitionResult?> HandleAsync(Guid formId, Guid userId, string note, bool force, int expectedVersion, CancellationToken ct);
    /// Handled → Open (manual reopen; not the "changed after handling" path).
    Task<BackofficeTransitionResult?> ReopenAsync(Guid formId, Guid userId, string note, CancellationToken ct);
}

/// v0.1.54 — Dapper repository for the Back office forms work list. Access is
/// the per-user feature flag, enforced at the endpoint: these queries are
/// deliberately not queue-filtered (owner decision — back office follows up
/// every marked form). They return the form plus a thin ticket context
/// (number, subject, company); no timeline, mail or other ticket content.
public sealed class BackofficeFormRepository : IBackofficeFormRepository
{
    private readonly NpgsqlDataSource _dataSource;

    public BackofficeFormRepository(NpgsqlDataSource dataSource) => _dataSource = dataSource;

    private const string ItemSelect = """
        SELECT f.id                    AS Id,
               f.ticket_id             AS TicketId,
               t.number                AS TicketNumber,
               t.subject               AS TicketSubject,
               t.company_id            AS CompanyId,
               co.name                 AS CompanyName,
               f.template_id           AS TemplateId,
               f.name                  AS Name,
               f.version               AS Version,
               f.bo_status             AS Status,
               f.created_by_user_id    AS SubmittedByUserId,
               COALESCE(NULLIF(cu.display_name, ''), cu.email::text) AS SubmittedByName,
               COALESCE(f.bo_submitted_utc, f.created_utc) AS SubmittedUtc,
               f.updated_by_user_id    AS UpdatedByUserId,
               COALESCE(NULLIF(uu.display_name, ''), uu.email::text) AS UpdatedByName,
               f.updated_utc           AS UpdatedUtc,
               f.bo_claimed_by_user_id AS ClaimedByUserId,
               COALESCE(NULLIF(clu.display_name, ''), clu.email::text) AS ClaimedByName,
               f.bo_claimed_utc        AS ClaimedUtc,
               f.bo_claimed_version    AS ClaimedVersion,
               f.bo_handled_by_user_id AS HandledByUserId,
               COALESCE(NULLIF(hu.display_name, ''), hu.email::text) AS HandledByName,
               f.bo_handled_utc        AS HandledUtc,
               f.bo_handled_version    AS HandledVersion,
               f.bo_handled_note       AS HandledNote,
               f.bo_reopened           AS Reopened
        """;

    private const string ItemFrom = """
        FROM ticket_forms f
        JOIN tickets t ON t.id = f.ticket_id AND t.is_deleted = FALSE
        LEFT JOIN companies co ON co.id = t.company_id
        LEFT JOIN users cu  ON cu.id  = f.created_by_user_id
        LEFT JOIN users uu  ON uu.id  = f.updated_by_user_id
        LEFT JOIN users clu ON clu.id = f.bo_claimed_by_user_id
        LEFT JOIN users hu  ON hu.id  = f.bo_handled_by_user_id
        WHERE f.bo_status IS NOT NULL AND f.deleted_utc IS NULL
        """;

    public async Task<BackofficeFormPage> ListAsync(BackofficeFormListQuery q, CancellationToken ct)
    {
        var status = q.Status is not null && BackofficeFormStatus.All.Contains(q.Status) ? q.Status : null;
        var term = (q.Search ?? string.Empty).Trim();
        if (term.Length > 200) term = term[..200];
        var like = "%" + term.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%";
        // A ticket number typed as "#123" or "123" matches the ticket too.
        long? number = long.TryParse(term.TrimStart('#'), out var n) ? n : null;

        // Open/In progress: oldest first (a work queue); handled/all: newest first.
        var order = status is BackofficeFormStatus.Open or BackofficeFormStatus.InProgress
            ? "COALESCE(f.bo_submitted_utc, f.created_utc) ASC, f.id ASC"
            : "COALESCE(f.bo_handled_utc, f.bo_submitted_utc, f.created_utc) DESC, f.id DESC";

        var sql = ItemSelect + ", COUNT(*) OVER () AS Total\n" + ItemFrom + """

              AND (@status::text IS NULL OR f.bo_status = @status)
              AND (@templateId::uuid IS NULL OR f.template_id = @templateId)
              AND (@companyId::uuid IS NULL OR t.company_id = @companyId)
              AND (@submittedBy::uuid IS NULL OR f.created_by_user_id = @submittedBy)
              AND (@hasSearch = FALSE
                   OR f.name ILIKE @like OR f.search_text ILIKE @like OR t.subject ILIKE @like
                   OR co.name ILIKE @like
                   OR (@number::bigint IS NOT NULL AND t.number = @number))
            """ + "\n ORDER BY " + order + "\n OFFSET @offset LIMIT @limit";

        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var rows = (await conn.QueryAsync<ItemRow>(new CommandDefinition(sql, new
        {
            status,
            templateId = q.TemplateId,
            companyId = q.CompanyId,
            submittedBy = q.SubmittedByUserId,
            hasSearch = term.Length > 0,
            like,
            number,
            offset = Math.Max(0, q.Offset),
            limit = Math.Clamp(q.Limit, 1, 200),
        }, cancellationToken: ct))).ToList();
        return new BackofficeFormPage(rows.Select(ToItem).ToList(), rows.Count == 0 ? 0 : rows[0].Total);
    }

    public async Task<BackofficeFormCounts> CountsAsync(CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var row = await conn.QuerySingleAsync<(int Open, int InProgress)>(new CommandDefinition("""
            SELECT COUNT(*) FILTER (WHERE f.bo_status = 'Open')::int,
                   COUNT(*) FILTER (WHERE f.bo_status = 'InProgress')::int
            FROM ticket_forms f
            JOIN tickets t ON t.id = f.ticket_id AND t.is_deleted = FALSE
            WHERE f.bo_status IN ('Open', 'InProgress') AND f.deleted_utc IS NULL
            """, cancellationToken: ct));
        return new BackofficeFormCounts(row.Open, row.InProgress);
    }

    public async Task<BackofficeFormListItem?> GetItemAsync(Guid formId, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var row = await conn.QuerySingleOrDefaultAsync<ItemRow>(new CommandDefinition(
            ItemSelect + ", 0 AS Total\n" + ItemFrom + "\n AND f.id = @formId",
            new { formId }, cancellationToken: ct));
        return row is null ? null : ToItem(row);
    }

    public async Task<BackofficeFormDetail?> GetDetailAsync(Guid formId, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var row = await conn.QuerySingleOrDefaultAsync<DetailRow>(new CommandDefinition(
            ItemSelect + """
                   , 0 AS Total
                   , f.template_snapshot::text AS SnapshotJson
                   , f.answers::text           AS AnswersJson
                   , (SELECT v.answers::text FROM ticket_form_versions v
                       WHERE v.form_id = f.id AND v.version = f.bo_handled_version) AS HandledAnswersJson
            """ + "\n" + ItemFrom + "\n AND f.id = @formId",
            new { formId }, cancellationToken: ct));
        if (row is null) return null;

        var log = await conn.QueryAsync<LogRow>(new CommandDefinition("""
            SELECT l.id AS Id, l.action AS Action, l.user_id AS UserId,
                   COALESCE(NULLIF(u.display_name, ''), u.email::text) AS UserName,
                   l.note AS Note, l.form_version AS FormVersion, l.created_utc AS CreatedUtc
            FROM ticket_form_backoffice_log l
            LEFT JOIN users u ON u.id = l.user_id
            WHERE l.form_id = @formId
            ORDER BY l.id DESC
            LIMIT 200
            """, new { formId }, cancellationToken: ct));

        return new BackofficeFormDetail(
            ToItem(row),
            FormDefinition.Parse(row.SnapshotJson),
            ParseObject(row.AnswersJson),
            log.Select(l => new BackofficeFormLogEntry(l.Id, l.Action, l.UserId, l.UserName, l.Note, l.FormVersion, l.CreatedUtc)).ToList(),
            row.HandledAnswersJson is null ? null : ParseObject(row.HandledAnswersJson));
    }

    public Task<BackofficeTransitionResult?> ClaimAsync(Guid formId, Guid userId, bool takeOver, CancellationToken ct)
        => TransitionAsync(formId, userId, BackofficeFormAction.Claimed, string.Empty, """
            UPDATE ticket_forms
               SET bo_status = 'InProgress',
                   bo_claimed_by_user_id = @userId,
                   bo_claimed_utc = now(),
                   bo_claimed_version = version
             WHERE id = @formId AND deleted_utc IS NULL
               AND (bo_status = 'Open'
                    OR (@flag AND bo_status = 'InProgress' AND bo_claimed_by_user_id IS DISTINCT FROM @userId))
            RETURNING version
            """, takeOver, ct);

    public Task<BackofficeTransitionResult?> ReleaseAsync(Guid formId, Guid userId, bool force, CancellationToken ct)
        => TransitionAsync(formId, userId, BackofficeFormAction.Released, string.Empty, """
            UPDATE ticket_forms
               SET bo_status = 'Open',
                   bo_claimed_by_user_id = NULL,
                   bo_claimed_utc = NULL,
                   bo_claimed_version = NULL
             WHERE id = @formId AND deleted_utc IS NULL
               AND bo_status = 'InProgress'
               AND (@flag OR bo_claimed_by_user_id = @userId)
            RETURNING version
            """, force, ct);

    public Task<BackofficeTransitionResult?> HandleAsync(Guid formId, Guid userId, string note, bool force, int expectedVersion, CancellationToken ct)
        => TransitionAsync(formId, userId, BackofficeFormAction.Handled, note, """
            UPDATE ticket_forms
               SET bo_status = 'Handled',
                   bo_handled_by_user_id = @userId,
                   bo_handled_utc = now(),
                   bo_handled_note = @note,
                   bo_handled_version = version,
                   bo_reopened = FALSE,
                   bo_claimed_by_user_id = NULL,
                   bo_claimed_utc = NULL,
                   bo_claimed_version = NULL
             WHERE id = @formId AND deleted_utc IS NULL AND version = @expectedVersion
               AND (bo_status = 'Open'
                    OR (bo_status = 'InProgress' AND (@flag OR bo_claimed_by_user_id = @userId)))
            RETURNING version
            """, force, ct, expectedVersion);

    public Task<BackofficeTransitionResult?> ReopenAsync(Guid formId, Guid userId, string note, CancellationToken ct)
        => TransitionAsync(formId, userId, BackofficeFormAction.Reopened, note, """
            UPDATE ticket_forms
               SET bo_status = 'Open'
             WHERE id = @formId AND deleted_utc IS NULL AND bo_status = 'Handled'
            RETURNING version
            """, false, ct);

    /// One guarded UPDATE + its log row in one transaction: two back-office
    /// users acting at once can never both win (the second UPDATE matches no
    /// row once the first committed).
    private async Task<BackofficeTransitionResult?> TransitionAsync(
        Guid formId, Guid userId, string action, string note, string updateSql, bool flag, CancellationToken ct,
        int expectedVersion = 0)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var version = await conn.ExecuteScalarAsync<int?>(new CommandDefinition(
            updateSql, new { formId, userId, note, flag, expectedVersion }, tx, cancellationToken: ct));
        if (version is null)
        {
            await tx.RollbackAsync(ct);
            return null;
        }
        await conn.ExecuteAsync(new CommandDefinition("""
            INSERT INTO ticket_form_backoffice_log (form_id, action, user_id, note, form_version)
            VALUES (@formId, @action, @userId, @note, @version)
            """, new { formId, action, userId, note, version }, tx, cancellationToken: ct));
        await tx.CommitAsync(ct);
        return new BackofficeTransitionResult(version.Value);
    }

    private static JsonObject ParseObject(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new JsonObject();
        try { return JsonNode.Parse(json) as JsonObject ?? new JsonObject(); }
        catch (System.Text.Json.JsonException) { return new JsonObject(); }
    }

    private static BackofficeFormListItem ToItem(ItemRow r) => new(
        r.Id, r.TicketId, r.TicketNumber, r.TicketSubject, r.CompanyId, r.CompanyName, r.TemplateId,
        r.Name, r.Version, r.Status, r.SubmittedByUserId, r.SubmittedByName, r.SubmittedUtc,
        r.UpdatedByUserId, r.UpdatedByName, r.UpdatedUtc,
        r.ClaimedByUserId, r.ClaimedByName, r.ClaimedUtc, r.ClaimedVersion,
        r.HandledByUserId, r.HandledByName, r.HandledUtc, r.HandledVersion,
        r.HandledNote ?? string.Empty, r.Reopened);

    private class ItemRow
    {
        public Guid Id { get; set; }
        public Guid TicketId { get; set; }
        public long TicketNumber { get; set; }
        public string TicketSubject { get; set; } = string.Empty;
        public Guid? CompanyId { get; set; }
        public string? CompanyName { get; set; }
        public Guid? TemplateId { get; set; }
        public string Name { get; set; } = string.Empty;
        public int Version { get; set; }
        public string Status { get; set; } = string.Empty;
        public Guid? SubmittedByUserId { get; set; }
        public string? SubmittedByName { get; set; }
        public DateTime SubmittedUtc { get; set; }
        public Guid? UpdatedByUserId { get; set; }
        public string? UpdatedByName { get; set; }
        public DateTime UpdatedUtc { get; set; }
        public Guid? ClaimedByUserId { get; set; }
        public string? ClaimedByName { get; set; }
        public DateTime? ClaimedUtc { get; set; }
        public int? ClaimedVersion { get; set; }
        public Guid? HandledByUserId { get; set; }
        public string? HandledByName { get; set; }
        public DateTime? HandledUtc { get; set; }
        public int? HandledVersion { get; set; }
        public string? HandledNote { get; set; }
        public bool Reopened { get; set; }
        public int Total { get; set; }
    }

    private sealed class DetailRow : ItemRow
    {
        public string? SnapshotJson { get; set; }
        public string? AnswersJson { get; set; }
        public string? HandledAnswersJson { get; set; }
    }

    private sealed class LogRow
    {
        public long Id { get; set; }
        public string Action { get; set; } = string.Empty;
        public Guid? UserId { get; set; }
        public string? UserName { get; set; }
        public string Note { get; set; } = string.Empty;
        public int FormVersion { get; set; }
        public DateTime CreatedUtc { get; set; }
    }
}
