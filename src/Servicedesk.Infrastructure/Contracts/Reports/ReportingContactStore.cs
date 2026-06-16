using Dapper;
using Npgsql;

namespace Servicedesk.Infrastructure.Contracts.Reports;

/// One contact linked to a company, with the per-link "reporting contact" flag.
/// Drives the Send-report recipient picker: reporting contacts are pre-selected,
/// the rest are listed so the agent can add them and/or promote them.
public sealed class ReportingContactRow
{
    public Guid ContactId { get; set; }
    public string FirstName { get; set; } = string.Empty;
    public string LastName { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string Role { get; set; } = string.Empty;
    public bool IsReportingContact { get; set; }
    public bool IsActive { get; set; }
}

/// A resolved report recipient (name + address). Used both for the default
/// reporting-contact recipients and for the frozen send-log snapshot.
public sealed record ReportRecipient(string Address, string Name);

public interface IReportingContactStore
{
    /// Every contact linked to the company, reporting contacts first.
    Task<IReadOnlyList<ReportingContactRow>> ListForCompanyAsync(Guid companyId, CancellationToken ct);

    /// Flip the per-link reporting flag. Returns false when there is no link
    /// between this contact and company (nothing to flag).
    Task<bool> SetReportingAsync(Guid companyId, Guid contactId, bool isReporting, CancellationToken ct);

    /// Active reporting contacts that have an email — the default recipients.
    Task<IReadOnlyList<ReportRecipient>> GetReportingRecipientsAsync(Guid companyId, CancellationToken ct);
}

public sealed class ReportingContactStore : IReportingContactStore
{
    private readonly NpgsqlDataSource _dataSource;

    public ReportingContactStore(NpgsqlDataSource dataSource)
    {
        _dataSource = dataSource;
    }

    public async Task<IReadOnlyList<ReportingContactRow>> ListForCompanyAsync(Guid companyId, CancellationToken ct)
    {
        const string sql = """
            SELECT c.id                       AS ContactId,
                   c.first_name               AS FirstName,
                   c.last_name                AS LastName,
                   c.email::text              AS Email,
                   cc.role                    AS Role,
                   cc.is_reporting_contact    AS IsReportingContact,
                   c.is_active                AS IsActive
              FROM contact_companies cc
              JOIN contacts c ON c.id = cc.contact_id
             WHERE cc.company_id = @companyId
             ORDER BY cc.is_reporting_contact DESC,
                      lower(c.first_name), lower(c.last_name)
            """;
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<ReportingContactRow>(new CommandDefinition(sql, new { companyId }, cancellationToken: ct));
        return rows.ToList();
    }

    public async Task<bool> SetReportingAsync(Guid companyId, Guid contactId, bool isReporting, CancellationToken ct)
    {
        const string sql = """
            UPDATE contact_companies
               SET is_reporting_contact = @isReporting,
                   updated_utc = now()
             WHERE company_id = @companyId AND contact_id = @contactId
            """;
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var rows = await conn.ExecuteAsync(new CommandDefinition(sql, new { companyId, contactId, isReporting }, cancellationToken: ct));
        return rows > 0;
    }

    public async Task<IReadOnlyList<ReportRecipient>> GetReportingRecipientsAsync(Guid companyId, CancellationToken ct)
    {
        const string sql = """
            SELECT c.email::text AS Address,
                   trim(both ' ' from c.first_name || ' ' || c.last_name) AS Name
              FROM contact_companies cc
              JOIN contacts c ON c.id = cc.contact_id
             WHERE cc.company_id = @companyId
               AND cc.is_reporting_contact = TRUE
               AND c.is_active = TRUE
               AND length(trim(c.email::text)) > 0
             ORDER BY lower(c.first_name), lower(c.last_name)
            """;
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<ReportRecipient>(new CommandDefinition(sql, new { companyId }, cancellationToken: ct));
        return rows.ToList();
    }
}
