using Dapper;
using Npgsql;
using Servicedesk.Domain.ComposeTemplates;

namespace Servicedesk.Infrastructure.ComposeTemplates;

/// Resolves the supported <see cref="ComposeTokens"/> against the current
/// ticket (or a free-standing contact id, when no ticket exists yet — used
/// by the New-Ticket drawer). Always returns every supported token; an
/// unresolvable one maps to an empty string. The client uses
/// "non-empty values substitute, empty values leave the raw placeholder"
/// so agents see at a glance which field still needs typing.
public interface IComposeTokenResolver
{
    /// Resolve all tokens for a ticket. Falls back to empty strings when
    /// the ticket has no requester / no company / no linked agent yet.
    Task<IReadOnlyDictionary<string, string>> ResolveForTicketAsync(
        Guid ticketId,
        string? agentEmail,
        CancellationToken ct);

    /// Resolve from a free-standing contact (and optional company) — used by
    /// the New-Ticket drawer where no ticket exists yet. Ticket-level tokens
    /// stay empty; contact + company tokens fill in where possible.
    Task<IReadOnlyDictionary<string, string>> ResolveForContactAsync(
        Guid? contactId,
        Guid? companyId,
        string? agentEmail,
        CancellationToken ct);
}

public sealed class ComposeTokenResolver : IComposeTokenResolver
{
    private readonly NpgsqlDataSource _dataSource;

    public ComposeTokenResolver(NpgsqlDataSource dataSource)
    {
        _dataSource = dataSource;
    }

    public async Task<IReadOnlyDictionary<string, string>> ResolveForTicketAsync(
        Guid ticketId, string? agentEmail, CancellationToken ct)
    {
        const string sql = """
            SELECT t.number                                          AS TicketNumber,
                   t.subject                                         AS TicketSubject,
                   c.first_name                                      AS ContactFirstName,
                   c.last_name                                       AS ContactLastName,
                   c.email::text                                     AS ContactEmail,
                   COALESCE(NULLIF(c.phone_e164, ''),  c.phone)        AS ContactPhone,
                   COALESCE(NULLIF(c.mobile_phone_e164, ''), c.mobile_phone) AS ContactMobile,
                   co.name                                           AS CompanyName,
                   co.code::text                                     AS CompanyCode
            FROM tickets t
            LEFT JOIN contacts c   ON c.id = t.requester_contact_id
            LEFT JOIN companies co ON co.id = t.company_id
            WHERE t.id = @ticketId
            LIMIT 1
            """;

        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var row = await conn.QueryFirstOrDefaultAsync<ContextRow>(
            new CommandDefinition(sql, new { ticketId }, cancellationToken: ct));

        return Build(row, agentEmail);
    }

    public async Task<IReadOnlyDictionary<string, string>> ResolveForContactAsync(
        Guid? contactId, Guid? companyId, string? agentEmail, CancellationToken ct)
    {
        // No-context path: still return the full map so the client can do a
        // clean dict-lookup; ticket/contact/company values will be empty.
        if (contactId is null && companyId is null)
            return EmptyMap(agentEmail);

        // Two-table read instead of one: the caller can pass any
        // combination of contactId / companyId without us inventing a
        // synthetic ticket row. Both queries skip when their key is null.
        await using var conn = await _dataSource.OpenConnectionAsync(ct);

        ContextRow? row = null;

        if (contactId is not null)
        {
            const string contactSql = """
                SELECT 0::bigint                                        AS TicketNumber,
                       NULL::text                                       AS TicketSubject,
                       c.first_name                                     AS ContactFirstName,
                       c.last_name                                      AS ContactLastName,
                       c.email::text                                    AS ContactEmail,
                       COALESCE(NULLIF(c.phone_e164, ''),  c.phone)       AS ContactPhone,
                       COALESCE(NULLIF(c.mobile_phone_e164, ''), c.mobile_phone) AS ContactMobile,
                       NULL::text                                       AS CompanyName,
                       NULL::text                                       AS CompanyCode
                FROM contacts c
                WHERE c.id = @contactId
                LIMIT 1
                """;
            row = await conn.QueryFirstOrDefaultAsync<ContextRow>(
                new CommandDefinition(contactSql, new { contactId }, cancellationToken: ct));
        }

        if (companyId is not null)
        {
            const string companySql = """
                SELECT name AS CompanyName, code::text AS CompanyCode
                FROM companies
                WHERE id = @companyId
                LIMIT 1
                """;
            var co = await conn.QueryFirstOrDefaultAsync<CompanyRow>(
                new CommandDefinition(companySql, new { companyId }, cancellationToken: ct));
            if (co is not null)
            {
                row ??= new ContextRow();
                row.CompanyName = co.CompanyName;
                row.CompanyCode = co.CompanyCode;
            }
        }

        return Build(row, agentEmail);
    }

    private static IReadOnlyDictionary<string, string> EmptyMap(string? agentEmail) =>
        Build(null, agentEmail);

    private static IReadOnlyDictionary<string, string> Build(ContextRow? row, string? agentEmail)
    {
        var dict = new Dictionary<string, string>(StringComparer.Ordinal);

        // Seed every supported token first so the client never has to
        // null-check a key. Empty strings mean "no value" — the client
        // leaves the raw {{token}} in place when the resolved value is empty.
        foreach (var t in ComposeTokens.Supported)
            dict[t.Token] = string.Empty;

        if (row is not null)
        {
            dict[ComposeTokens.ContactFirstName] = row.ContactFirstName ?? string.Empty;
            dict[ComposeTokens.ContactLastName]  = row.ContactLastName  ?? string.Empty;
            var first = (row.ContactFirstName ?? string.Empty).Trim();
            var last  = (row.ContactLastName  ?? string.Empty).Trim();
            dict[ComposeTokens.ContactFullName] = (first + " " + last).Trim();
            dict[ComposeTokens.ContactEmail]    = row.ContactEmail  ?? string.Empty;
            dict[ComposeTokens.ContactPhone]    = row.ContactPhone  ?? string.Empty;
            dict[ComposeTokens.ContactMobile]   = row.ContactMobile ?? string.Empty;
            dict[ComposeTokens.CompanyName]     = row.CompanyName   ?? string.Empty;
            dict[ComposeTokens.CompanyCode]     = row.CompanyCode   ?? string.Empty;
            dict[ComposeTokens.TicketNumber]    = row.TicketNumber > 0 ? row.TicketNumber.ToString() : string.Empty;
            dict[ComposeTokens.TicketSubject]   = row.TicketSubject ?? string.Empty;
        }

        dict[ComposeTokens.AgentEmail] = agentEmail ?? string.Empty;
        return dict;
    }

    // Mutable class with public setters — see project memory on Dapper's
    // positional-record-struct mapping bug, which silently returns null
    // for nullable row-DTOs even when the row exists.
    private sealed class ContextRow
    {
        public long TicketNumber { get; set; }
        public string? TicketSubject { get; set; }
        public string? ContactFirstName { get; set; }
        public string? ContactLastName { get; set; }
        public string? ContactEmail { get; set; }
        public string? ContactPhone { get; set; }
        public string? ContactMobile { get; set; }
        public string? CompanyName { get; set; }
        public string? CompanyCode { get; set; }
    }

    private sealed class CompanyRow
    {
        public string? CompanyName { get; set; }
        public string? CompanyCode { get; set; }
    }
}
