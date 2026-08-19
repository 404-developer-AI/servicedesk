using Dapper;
using Npgsql;

namespace Servicedesk.Infrastructure.Mail.Outbound;

/// Ranked recipient suggestions for the ticket mail composer's To/Cc/Bcc
/// fields. Two blocks, in order:
///   1. company-scoped — active contacts linked to the ticket's company (any
///      role) plus addresses previously used on outbound mail of that
///      company's tickets that are not a company contact, ranked by how often
///      the address appeared on outbound mail (all agents, all time);
///   2. general — active contacts matching the typed search, appended below
///      the company block (only when a search term is given).
/// Frequency counts outbound mail only: what agents actually addressed, not
/// what customers put in their own To/Cc.
public interface IRecipientSuggestionRepository
{
    /// Queue + company of a live ticket, or null when the ticket does not
    /// exist or is deleted. The endpoint uses the queue id for the access
    /// check without paying for the full ticket detail load.
    Task<TicketMailContext?> GetTicketContextAsync(Guid ticketId, CancellationToken ct);

    Task<IReadOnlyList<RecipientSuggestionRow>> ListAsync(
        Guid? companyId, string? search, int limit, CancellationToken ct);
}

public sealed class TicketMailContext
{
    public Guid QueueId { get; set; }
    public Guid? CompanyId { get; set; }
}

public sealed class RecipientSuggestionRow
{
    public string Address { get; set; } = string.Empty;
    public string? Name { get; set; }
    public Guid? ContactId { get; set; }
    public bool IsCompanyContact { get; set; }
    public long UsageCount { get; set; }
}

public sealed class RecipientSuggestionRepository : IRecipientSuggestionRepository
{
    private readonly NpgsqlDataSource _dataSource;

    public RecipientSuggestionRepository(NpgsqlDataSource dataSource)
    {
        _dataSource = dataSource;
    }

    public async Task<TicketMailContext?> GetTicketContextAsync(Guid ticketId, CancellationToken ct)
    {
        const string sql = """
            SELECT queue_id   AS QueueId,
                   company_id AS CompanyId
            FROM tickets
            WHERE id = @ticketId AND is_deleted = FALSE
            """;
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        return await conn.QueryFirstOrDefaultAsync<TicketMailContext>(
            new CommandDefinition(sql, new { ticketId }, cancellationToken: ct));
    }

    public async Task<IReadOnlyList<RecipientSuggestionRow>> ListAsync(
        Guid? companyId, string? search, int limit, CancellationToken ct)
    {
        var hasSearch = !string.IsNullOrWhiteSpace(search);
        // No company on the ticket and nothing typed → nothing to suggest.
        if (companyId is null && !hasSearch) return Array.Empty<RecipientSuggestionRow>();

        var sql = BuildSuggestionsSql(hasSearch);
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        return (await conn.QueryAsync<RecipientSuggestionRow>(new CommandDefinition(
            sql,
            new
            {
                companyId,
                search = $"%{search?.Trim()}%",
                limit = Math.Clamp(limit, 1, 50),
            },
            cancellationToken: ct))).ToList();
    }

    // A null @companyId makes every company-scoped predicate false, so the
    // same statement serves company-less tickets (general search only).
    // `mail_recipients.address` and `contacts.email` are both CITEXT, so the
    // usage join and the exclusion checks are case-insensitive for free.
    // Internal so tests can assert the active-only and outbound-only guards.
    internal static string BuildSuggestionsSql(bool hasSearch)
    {
        // The union sits inside a subquery because in PostgreSQL an ORDER BY
        // over a UNION may only reference output columns, and the name tie-
        // break is an expression (COALESCE over two columns).
        var sql = """
            WITH mail_usage AS (
                SELECT r.address AS address,
                       COUNT(*)  AS usage_count,
                       MAX(NULLIF(TRIM(r.display_name), '')) AS display_name
                FROM mail_recipients r
                JOIN mail_messages m ON m.id = r.mail_id
                JOIN tickets t ON t.id = m.ticket_id
                WHERE m.direction = 'Outbound' AND t.company_id = @companyId
                GROUP BY r.address
            ),
            company_contacts AS (
                SELECT DISTINCT c.id, c.email,
                       NULLIF(TRIM(c.first_name || ' ' || c.last_name), '') AS name
                FROM contacts c
                JOIN contact_companies cc ON cc.contact_id = c.id
                WHERE cc.company_id = @companyId AND c.is_active = TRUE
            ),
            company_block AS (
                SELECT c.email::text AS address, c.name, c.id AS contact_id,
                       TRUE AS is_company_contact,
                       COALESCE(u.usage_count, 0) AS usage_count
                FROM company_contacts c
                LEFT JOIN mail_usage u ON u.address = c.email
                UNION ALL
                SELECT u.address::text, u.display_name, NULL::uuid, FALSE, u.usage_count
                FROM mail_usage u
                WHERE NOT EXISTS (
                    SELECT 1 FROM company_contacts c2 WHERE c2.email = u.address)
            )
            SELECT s.address            AS Address,
                   s.name               AS Name,
                   s.contact_id         AS ContactId,
                   s.is_company_contact AS IsCompanyContact,
                   s.usage_count        AS UsageCount
            FROM (
                SELECT address, name, contact_id, is_company_contact, usage_count,
                       0 AS block
                FROM company_block
            """;
        if (hasSearch)
        {
            sql += """

                WHERE (address ILIKE @search OR name ILIKE @search)
                UNION ALL
                SELECT c.email::text, NULLIF(TRIM(c.first_name || ' ' || c.last_name), ''),
                       c.id, FALSE, 0, 1 AS block
                FROM contacts c
                WHERE c.is_active = TRUE
                  AND (c.email ILIKE @search OR c.first_name ILIKE @search
                       OR c.last_name ILIKE @search
                       OR (coalesce(c.first_name,'') || ' ' || coalesce(c.last_name,'')) ILIKE @search)
                  AND NOT EXISTS (SELECT 1 FROM company_contacts cb WHERE cb.email = c.email)
                  AND NOT EXISTS (SELECT 1 FROM mail_usage u2 WHERE u2.address = c.email)
                """;
        }
        sql += """

            ) s
            ORDER BY s.block, s.usage_count DESC, s.is_company_contact DESC,
                     COALESCE(s.name, s.address)
            LIMIT @limit
            """;
        return sql;
    }
}
