using System.Text.Json.Nodes;
using Dapper;
using Npgsql;

namespace Servicedesk.Infrastructure.Forms;

public sealed record TicketFormArticleOption(Guid Id, string Code, string Name);

public sealed record TicketFormContactOption(Guid Id, string Name, string Email);

public interface ITicketFormRepository
{
    Task<IReadOnlyList<TicketFormSummary>> ListForTicketAsync(Guid ticketId, CancellationToken ct);
    Task<TicketFormDetail?> GetAsync(Guid formId, CancellationToken ct);
    Task<int> CountForTicketAsync(Guid ticketId, CancellationToken ct);
    Task<Guid> CreateAsync(Guid ticketId, Guid? templateId, string name, FormDefinition snapshot,
        JsonObject answers, string searchText, Guid userId, CancellationToken ct);
    /// Optimistic update: only applies when the stored version equals
    /// <paramref name="expectedVersion"/>. Returns the new version, or null
    /// on a version conflict / deleted form.
    Task<int?> UpdateAsync(Guid formId, int expectedVersion, JsonObject answers, string searchText,
        Guid userId, CancellationToken ct);
    Task SetVersionEventAsync(Guid formId, int version, long eventId, CancellationToken ct);
    Task<bool> SoftDeleteAsync(Guid formId, Guid userId, string reason, CancellationToken ct);
    /// Hard delete — only used to roll back a create whose note could not be written.
    Task HardDeleteAsync(Guid formId, CancellationToken ct);
    Task<IReadOnlyList<TicketFormVersion>> ListVersionsAsync(Guid formId, CancellationToken ct);

    // Reference resolution for answers (all scoped; unknown ids are simply absent).
    Task<IReadOnlyDictionary<Guid, string>> ResolveAgentsAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct);
    Task<IReadOnlyDictionary<Guid, (string Name, string Email)>> ResolveCompanyContactsAsync(
        Guid? companyId, Guid requesterContactId, IReadOnlyCollection<Guid> ids, CancellationToken ct);
    Task<IReadOnlyDictionary<Guid, (string Code, string Name)>> ResolveArticlesAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct);

    /// Article picker. <paramref name="contractOnly"/> = articles on the
    /// company's Adsolut contracts (none when the ticket has no company).
    Task<IReadOnlyList<TicketFormArticleOption>> SearchArticlesAsync(
        Guid? companyId, bool contractOnly, string? search, int limit, CancellationToken ct);

    /// Contact picker: the requester plus active contacts of the company.
    Task<IReadOnlyList<TicketFormContactOption>> SearchCompanyContactsAsync(
        Guid? companyId, Guid requesterContactId, string? search, int limit, CancellationToken ct);
}

/// v0.1.52 — Dapper repository for filled forms on tickets.
public sealed class TicketFormRepository : ITicketFormRepository
{
    private readonly NpgsqlDataSource _dataSource;

    public TicketFormRepository(NpgsqlDataSource dataSource) => _dataSource = dataSource;

    private const string SummarySelect = """
        SELECT f.id                 AS Id,
               f.ticket_id          AS TicketId,
               f.name               AS Name,
               f.version            AS Version,
               f.created_by_user_id AS CreatedByUserId,
               COALESCE(NULLIF(cu.display_name, ''), cu.email) AS CreatedByName,
               f.created_utc        AS CreatedUtc,
               f.updated_by_user_id AS UpdatedByUserId,
               COALESCE(NULLIF(uu.display_name, ''), uu.email) AS UpdatedByName,
               f.updated_utc        AS UpdatedUtc
        """;

    private const string SummaryFrom = """
        FROM ticket_forms f
        LEFT JOIN users cu ON cu.id = f.created_by_user_id
        LEFT JOIN users uu ON uu.id = f.updated_by_user_id
        """;

    public async Task<IReadOnlyList<TicketFormSummary>> ListForTicketAsync(Guid ticketId, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<SummaryRow>(new CommandDefinition(
            SummarySelect + "\n" + SummaryFrom + "\n" + """
             WHERE f.ticket_id = @ticketId AND f.deleted_utc IS NULL
             ORDER BY f.created_utc, f.id
            """, new { ticketId }, cancellationToken: ct));
        return rows.Select(ToSummary).ToList();
    }

    public async Task<TicketFormDetail?> GetAsync(Guid formId, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var row = await conn.QuerySingleOrDefaultAsync<DetailRow>(new CommandDefinition(
            SummarySelect + "\n" + """
                   , f.template_id             AS TemplateId
                   , f.template_snapshot::text AS SnapshotJson
                   , f.answers::text           AS AnswersJson
            """ + "\n" + SummaryFrom + "\n WHERE f.id = @formId AND f.deleted_utc IS NULL",
            new { formId }, cancellationToken: ct));
        if (row is null) return null;
        return new TicketFormDetail(ToSummary(row), row.TemplateId,
            FormDefinition.Parse(row.SnapshotJson), ParseObject(row.AnswersJson));
    }

    public async Task<int> CountForTicketAsync(Guid ticketId, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        return await conn.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT COUNT(*)::int FROM ticket_forms WHERE ticket_id = @ticketId AND deleted_utc IS NULL",
            new { ticketId }, cancellationToken: ct));
    }

    public async Task<Guid> CreateAsync(Guid ticketId, Guid? templateId, string name, FormDefinition snapshot,
        JsonObject answers, string searchText, Guid userId, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var answersJson = answers.ToJsonString();
        var id = await conn.ExecuteScalarAsync<Guid>(new CommandDefinition("""
            INSERT INTO ticket_forms
                (ticket_id, template_id, name, template_snapshot, answers, version, search_text,
                 created_by_user_id, updated_by_user_id)
            VALUES (@ticketId, @templateId, @name, @snapshot::jsonb, @answers::jsonb, 1, @searchText, @userId, @userId)
            RETURNING id
            """, new
        {
            ticketId, templateId, name,
            snapshot = snapshot.ToJson(),
            answers = answersJson,
            searchText, userId,
        }, tx, cancellationToken: ct));
        await conn.ExecuteAsync(new CommandDefinition("""
            INSERT INTO ticket_form_versions (form_id, version, answers, saved_by_user_id)
            VALUES (@id, 1, @answers::jsonb, @userId)
            """, new { id, answers = answersJson, userId }, tx, cancellationToken: ct));
        await tx.CommitAsync(ct);
        return id;
    }

    public async Task<int?> UpdateAsync(Guid formId, int expectedVersion, JsonObject answers, string searchText,
        Guid userId, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var answersJson = answers.ToJsonString();
        var newVersion = await conn.ExecuteScalarAsync<int?>(new CommandDefinition("""
            UPDATE ticket_forms
               SET answers = @answers::jsonb,
                   search_text = @searchText,
                   version = version + 1,
                   updated_by_user_id = @userId,
                   updated_utc = now()
             WHERE id = @formId AND version = @expectedVersion AND deleted_utc IS NULL
            RETURNING version
            """, new { formId, expectedVersion, answers = answersJson, searchText, userId }, tx, cancellationToken: ct));
        if (newVersion is null)
        {
            await tx.RollbackAsync(ct);
            return null;
        }
        await conn.ExecuteAsync(new CommandDefinition("""
            INSERT INTO ticket_form_versions (form_id, version, answers, saved_by_user_id)
            VALUES (@formId, @newVersion, @answers::jsonb, @userId)
            """, new { formId, newVersion, answers = answersJson, userId }, tx, cancellationToken: ct));
        await tx.CommitAsync(ct);
        return newVersion;
    }

    public async Task SetVersionEventAsync(Guid formId, int version, long eventId, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE ticket_form_versions SET event_id = @eventId WHERE form_id = @formId AND version = @version",
            new { formId, version, eventId }, cancellationToken: ct));
    }

    public async Task<bool> SoftDeleteAsync(Guid formId, Guid userId, string reason, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var n = await conn.ExecuteAsync(new CommandDefinition("""
            UPDATE ticket_forms
               SET deleted_utc = now(), deleted_by_user_id = @userId, deleted_reason = @reason
             WHERE id = @formId AND deleted_utc IS NULL
            """, new { formId, userId, reason }, cancellationToken: ct));
        return n > 0;
    }

    public async Task HardDeleteAsync(Guid formId, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(
            "DELETE FROM ticket_forms WHERE id = @formId", new { formId }, cancellationToken: ct));
    }

    public async Task<IReadOnlyList<TicketFormVersion>> ListVersionsAsync(Guid formId, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<VersionRow>(new CommandDefinition("""
            SELECT v.version          AS Version,
                   v.answers::text    AS AnswersJson,
                   v.event_id         AS EventId,
                   v.saved_by_user_id AS SavedByUserId,
                   COALESCE(NULLIF(u.display_name, ''), u.email) AS SavedByName,
                   v.saved_utc        AS SavedUtc
            FROM ticket_form_versions v
            LEFT JOIN users u ON u.id = v.saved_by_user_id
            WHERE v.form_id = @formId
            ORDER BY v.version DESC
            """, new { formId }, cancellationToken: ct));
        return rows.Select(r => new TicketFormVersion(
            r.Version, ParseObject(r.AnswersJson), r.EventId, r.SavedByUserId, r.SavedByName, r.SavedUtc)).ToList();
    }

    public async Task<IReadOnlyDictionary<Guid, string>> ResolveAgentsAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct)
    {
        if (ids.Count == 0) return new Dictionary<Guid, string>();
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<(Guid Id, string Name)>(new CommandDefinition("""
            SELECT id, COALESCE(NULLIF(display_name, ''), email::text)
            FROM users
            WHERE id = ANY(@ids) AND role_name IN ('Agent', 'Admin')
            """, new { ids = ids.ToArray() }, cancellationToken: ct));
        return rows.ToDictionary(r => r.Id, r => r.Name);
    }

    public async Task<IReadOnlyDictionary<Guid, (string Name, string Email)>> ResolveCompanyContactsAsync(
        Guid? companyId, Guid requesterContactId, IReadOnlyCollection<Guid> ids, CancellationToken ct)
    {
        if (ids.Count == 0) return new Dictionary<Guid, (string, string)>();
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        // A contact field can only point at the ticket's requester or a
        // contact linked to the ticket's company — never an arbitrary contact.
        var rows = await conn.QueryAsync<(Guid Id, string Name, string Email)>(new CommandDefinition("""
            SELECT c.id, trim(c.first_name || ' ' || c.last_name), c.email::text
            FROM contacts c
            WHERE c.id = ANY(@ids)
              AND (c.id = @requesterContactId
                   OR (@companyId::uuid IS NOT NULL AND EXISTS (
                        SELECT 1 FROM contact_companies cc
                        WHERE cc.contact_id = c.id AND cc.company_id = @companyId)))
            """, new { ids = ids.ToArray(), companyId, requesterContactId }, cancellationToken: ct));
        return rows.ToDictionary(r => r.Id, r => (r.Name, r.Email));
    }

    public async Task<IReadOnlyDictionary<Guid, (string Code, string Name)>> ResolveArticlesAsync(
        IReadOnlyCollection<Guid> ids, CancellationToken ct)
    {
        if (ids.Count == 0) return new Dictionary<Guid, (string, string)>();
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<(Guid Id, string Code, string Name)>(new CommandDefinition("""
            SELECT id, COALESCE(code, ''), COALESCE(name, '')
            FROM adsolut_articles
            WHERE id = ANY(@ids)
            """, new { ids = ids.ToArray() }, cancellationToken: ct));
        return rows.ToDictionary(r => r.Id, r => (r.Code, r.Name));
    }

    public async Task<IReadOnlyList<TicketFormArticleOption>> SearchArticlesAsync(
        Guid? companyId, bool contractOnly, string? search, int limit, CancellationToken ct)
    {
        if (contractOnly && companyId is null) return Array.Empty<TicketFormArticleOption>();
        var term = (search ?? string.Empty).Trim();
        var like = "%" + term.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%";

        // Contract scope bridges contract → ERP customer code → company
        // (companies.adsolut_number), the same join AdsolutContractRepository
        // uses; the ERP GUID does not match companies.adsolut_id.
        var sql = """
            SELECT a.id AS Id, COALESCE(a.code, '') AS Code, COALESCE(a.name, '') AS Name
            FROM adsolut_articles a
            WHERE a.active = TRUE
              AND (@hasSearch = FALSE OR a.code ILIKE @like OR a.name ILIKE @like)
              AND (@contractOnly = FALSE OR EXISTS (
                    SELECT 1
                    FROM adsolut_contract_lines l
                    JOIN adsolut_contracts c ON c.id = l.contract_id
                    JOIN adsolut_erp_customers ec ON ec.id = c.customer_adsolut_id AND ec.code IS NOT NULL
                    JOIN companies co ON co.adsolut_number = ec.code
                    WHERE l.article_id = a.id AND co.id = @companyId))
            ORDER BY a.code ASC NULLS LAST, a.id
            LIMIT @limit
            """;
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<TicketFormArticleOption>(new CommandDefinition(sql, new
        {
            hasSearch = term.Length > 0, like, contractOnly, companyId,
            limit = Math.Clamp(limit, 1, 100),
        }, cancellationToken: ct));
        return rows.ToList();
    }

    public async Task<IReadOnlyList<TicketFormContactOption>> SearchCompanyContactsAsync(
        Guid? companyId, Guid requesterContactId, string? search, int limit, CancellationToken ct)
    {
        var term = (search ?? string.Empty).Trim();
        var like = "%" + term.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%";
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<TicketFormContactOption>(new CommandDefinition("""
            SELECT c.id AS Id, trim(c.first_name || ' ' || c.last_name) AS Name, c.email::text AS Email
            FROM contacts c
            WHERE (c.id = @requesterContactId
                   OR (@companyId::uuid IS NOT NULL AND c.is_active AND EXISTS (
                        SELECT 1 FROM contact_companies cc
                        WHERE cc.contact_id = c.id AND cc.company_id = @companyId)))
              AND (@hasSearch = FALSE
                   OR c.first_name ILIKE @like OR c.last_name ILIKE @like OR c.email::text ILIKE @like
                   OR (c.first_name || ' ' || c.last_name) ILIKE @like)
            ORDER BY (c.id = @requesterContactId) DESC, lower(c.first_name), lower(c.last_name), c.id
            LIMIT @limit
            """, new
        {
            companyId, requesterContactId, hasSearch = term.Length > 0, like,
            limit = Math.Clamp(limit, 1, 100),
        }, cancellationToken: ct));
        return rows.ToList();
    }

    private static JsonObject ParseObject(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new JsonObject();
        try { return JsonNode.Parse(json) as JsonObject ?? new JsonObject(); }
        catch (System.Text.Json.JsonException) { return new JsonObject(); }
    }

    private static TicketFormSummary ToSummary(SummaryRow r) => new(
        r.Id, r.TicketId, r.Name, r.Version, r.CreatedByUserId, r.CreatedByName, r.CreatedUtc,
        r.UpdatedByUserId, r.UpdatedByName, r.UpdatedUtc);

    private class SummaryRow
    {
        public Guid Id { get; set; }
        public Guid TicketId { get; set; }
        public string Name { get; set; } = string.Empty;
        public int Version { get; set; }
        public Guid? CreatedByUserId { get; set; }
        public string? CreatedByName { get; set; }
        public DateTime CreatedUtc { get; set; }
        public Guid? UpdatedByUserId { get; set; }
        public string? UpdatedByName { get; set; }
        public DateTime UpdatedUtc { get; set; }
    }

    private sealed class DetailRow : SummaryRow
    {
        public Guid? TemplateId { get; set; }
        public string? SnapshotJson { get; set; }
        public string? AnswersJson { get; set; }
    }

    private sealed class VersionRow
    {
        public int Version { get; set; }
        public string? AnswersJson { get; set; }
        public long? EventId { get; set; }
        public Guid? SavedByUserId { get; set; }
        public string? SavedByName { get; set; }
        public DateTime SavedUtc { get; set; }
    }
}
