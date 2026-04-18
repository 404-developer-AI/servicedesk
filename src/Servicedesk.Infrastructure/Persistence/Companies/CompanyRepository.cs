using Dapper;
using Npgsql;
using Servicedesk.Domain.Companies;
using Servicedesk.Infrastructure.Persistence.Taxonomy;

namespace Servicedesk.Infrastructure.Persistence.Companies;

public sealed class CompanyRepository : ICompanyRepository
{
    private const string CompanyCols = """
        id AS Id, name AS Name, description AS Description, website AS Website, phone AS Phone,
        address_line1 AS AddressLine1, address_line2 AS AddressLine2, city AS City,
        postal_code AS PostalCode, country AS Country, is_active AS IsActive,
        created_utc AS CreatedUtc, updated_utc AS UpdatedUtc,
        code AS Code, short_name AS ShortName, vat_number AS VatNumber,
        alert_text AS AlertText, alert_on_create AS AlertOnCreate,
        alert_on_open AS AlertOnOpen, alert_on_open_mode AS AlertOnOpenMode
        """;

    private const string ContactCols = """
        id AS Id, company_id AS CompanyId, company_role AS CompanyRole,
        first_name AS FirstName, last_name AS LastName, email AS Email, phone AS Phone,
        job_title AS JobTitle, is_active AS IsActive,
        created_utc AS CreatedUtc, updated_utc AS UpdatedUtc
        """;

    private readonly NpgsqlDataSource _dataSource;

    public CompanyRepository(NpgsqlDataSource dataSource)
    {
        _dataSource = dataSource;
    }

    public async Task<IReadOnlyList<Company>> ListCompaniesAsync(string? search, bool includeInactive, CancellationToken ct)
    {
        var sql = $"SELECT {CompanyCols} FROM companies WHERE 1=1";
        if (!includeInactive) sql += " AND is_active = TRUE";
        if (!string.IsNullOrWhiteSpace(search))
        {
            sql += """
                 AND (
                    name ILIKE @search
                    OR short_name ILIKE @search
                    OR code::text ILIKE @search
                    OR vat_number ILIKE @search
                 )
                """;
        }
        sql += " ORDER BY name LIMIT 500";
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        return (await conn.QueryAsync<Company>(new CommandDefinition(
            sql, new { search = $"%{search}%" }, cancellationToken: ct))).ToList();
    }

    public async Task<Company?> GetCompanyAsync(Guid id, CancellationToken ct)
    {
        var sql = $"SELECT {CompanyCols} FROM companies WHERE id = @id";
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        return await conn.QueryFirstOrDefaultAsync<Company>(new CommandDefinition(sql, new { id }, cancellationToken: ct));
    }

    public async Task<Company?> GetCompanyByCodeAsync(string code, CancellationToken ct)
    {
        var sql = $"SELECT {CompanyCols} FROM companies WHERE code = @code";
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        return await conn.QueryFirstOrDefaultAsync<Company>(new CommandDefinition(sql, new { code }, cancellationToken: ct));
    }

    public async Task<Company?> GetCompanyForContactAsync(Guid contactId, CancellationToken ct)
    {
        // Column list is prefixed with c. so it doesn't collide with contacts.id
        // when joined; unqualified "id" would throw 42702 column-is-ambiguous.
        const string sql = """
            SELECT c.id AS Id, c.name AS Name, c.description AS Description,
                   c.website AS Website, c.phone AS Phone,
                   c.address_line1 AS AddressLine1, c.address_line2 AS AddressLine2,
                   c.city AS City, c.postal_code AS PostalCode, c.country AS Country,
                   c.is_active AS IsActive,
                   c.created_utc AS CreatedUtc, c.updated_utc AS UpdatedUtc,
                   c.code AS Code, c.short_name AS ShortName, c.vat_number AS VatNumber,
                   c.alert_text AS AlertText, c.alert_on_create AS AlertOnCreate,
                   c.alert_on_open AS AlertOnOpen, c.alert_on_open_mode AS AlertOnOpenMode
            FROM companies c
            JOIN contacts ct ON ct.company_id = c.id
            WHERE ct.id = @contactId AND c.is_active = TRUE
            LIMIT 1
            """;
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        return await conn.QueryFirstOrDefaultAsync<Company>(new CommandDefinition(sql, new { contactId }, cancellationToken: ct));
    }

    public async Task<Company> CreateCompanyAsync(Company c, CancellationToken ct)
    {
        var sql = $"""
            INSERT INTO companies (name, description, website, phone, address_line1, address_line2,
                                   city, postal_code, country, is_active,
                                   code, short_name, vat_number,
                                   alert_text, alert_on_create, alert_on_open, alert_on_open_mode)
            VALUES (@Name, @Description, @Website, @Phone, @AddressLine1, @AddressLine2,
                    @City, @PostalCode, @Country, @IsActive,
                    @Code, @ShortName, @VatNumber,
                    @AlertText, @AlertOnCreate, @AlertOnOpen, @AlertOnOpenMode)
            RETURNING {CompanyCols}
            """;
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        return await conn.QuerySingleAsync<Company>(new CommandDefinition(sql, c, cancellationToken: ct));
    }

    public async Task<Company?> UpdateCompanyAsync(Guid id, Company p, CancellationToken ct)
    {
        var sql = $"""
            UPDATE companies SET name = @Name, description = @Description, website = @Website, phone = @Phone,
                                 address_line1 = @AddressLine1, address_line2 = @AddressLine2, city = @City,
                                 postal_code = @PostalCode, country = @Country, is_active = @IsActive,
                                 code = @Code, short_name = @ShortName, vat_number = @VatNumber,
                                 alert_text = @AlertText, alert_on_create = @AlertOnCreate,
                                 alert_on_open = @AlertOnOpen, alert_on_open_mode = @AlertOnOpenMode,
                                 updated_utc = now()
            WHERE id = @Id
            RETURNING {CompanyCols}
            """;
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        return await conn.QueryFirstOrDefaultAsync<Company>(new CommandDefinition(sql, p with { Id = id }, cancellationToken: ct));
    }

    public async Task<DeleteResult> SoftDeleteCompanyAsync(Guid id, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var exists = await conn.QueryFirstOrDefaultAsync<int?>(new CommandDefinition(
            "SELECT 1 FROM companies WHERE id = @id", new { id }, cancellationToken: ct));
        if (exists is null) return DeleteResult.NotFound;

        await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE companies SET is_active = FALSE, updated_utc = now() WHERE id = @id",
            new { id }, cancellationToken: ct));
        return DeleteResult.Deleted;
    }

    public async Task<IReadOnlyList<CompanyDomain>> ListDomainsAsync(Guid companyId, CancellationToken ct)
    {
        const string sql = """
            SELECT id AS Id, company_id AS CompanyId, domain AS Domain, created_utc AS CreatedUtc
            FROM company_domains WHERE company_id = @companyId ORDER BY domain
            """;
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        return (await conn.QueryAsync<CompanyDomain>(new CommandDefinition(sql, new { companyId }, cancellationToken: ct))).ToList();
    }

    public async Task<CompanyDomain?> AddDomainAsync(Guid companyId, string domain, CancellationToken ct)
    {
        const string sql = """
            INSERT INTO company_domains (company_id, domain)
            VALUES (@companyId, @domain)
            ON CONFLICT (domain) DO NOTHING
            RETURNING id AS Id, company_id AS CompanyId, domain AS Domain, created_utc AS CreatedUtc
            """;
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        return await conn.QueryFirstOrDefaultAsync<CompanyDomain>(new CommandDefinition(sql, new { companyId, domain }, cancellationToken: ct));
    }

    public async Task<bool> RemoveDomainAsync(Guid domainId, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var rows = await conn.ExecuteAsync(new CommandDefinition(
            "DELETE FROM company_domains WHERE id = @domainId", new { domainId }, cancellationToken: ct));
        return rows > 0;
    }

    public async Task<Company?> FindCompanyByDomainAsync(string domain, CancellationToken ct)
    {
        // Qualified columns — see GetCompanyForContactAsync for the "id is
        // ambiguous" bug this avoids.
        const string sql = """
            SELECT c.id AS Id, c.name AS Name, c.description AS Description,
                   c.website AS Website, c.phone AS Phone,
                   c.address_line1 AS AddressLine1, c.address_line2 AS AddressLine2,
                   c.city AS City, c.postal_code AS PostalCode, c.country AS Country,
                   c.is_active AS IsActive,
                   c.created_utc AS CreatedUtc, c.updated_utc AS UpdatedUtc,
                   c.code AS Code, c.short_name AS ShortName, c.vat_number AS VatNumber,
                   c.alert_text AS AlertText, c.alert_on_create AS AlertOnCreate,
                   c.alert_on_open AS AlertOnOpen, c.alert_on_open_mode AS AlertOnOpenMode
            FROM companies c
            JOIN company_domains d ON d.company_id = c.id
            WHERE d.domain = @domain AND c.is_active = TRUE
            LIMIT 1
            """;
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        return await conn.QueryFirstOrDefaultAsync<Company>(new CommandDefinition(sql, new { domain }, cancellationToken: ct));
    }

    public async Task<IReadOnlyList<Contact>> ListContactsAsync(Guid? companyId, string? search, CancellationToken ct)
    {
        var sql = $"SELECT {ContactCols} FROM contacts WHERE 1=1";
        if (companyId.HasValue) sql += " AND company_id = @companyId";
        if (!string.IsNullOrWhiteSpace(search))
        {
            sql += " AND (email ILIKE @search OR first_name ILIKE @search OR last_name ILIKE @search)";
        }
        sql += " ORDER BY last_name, first_name LIMIT 500";
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        return (await conn.QueryAsync<Contact>(new CommandDefinition(sql,
            new { companyId, search = $"%{search}%" }, cancellationToken: ct))).ToList();
    }

    public async Task<Contact?> GetContactAsync(Guid id, CancellationToken ct)
    {
        var sql = $"SELECT {ContactCols} FROM contacts WHERE id = @id";
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        return await conn.QueryFirstOrDefaultAsync<Contact>(new CommandDefinition(sql, new { id }, cancellationToken: ct));
    }

    public async Task<Contact?> GetContactByEmailAsync(string email, CancellationToken ct)
    {
        var sql = $"SELECT {ContactCols} FROM contacts WHERE email = @email";
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        return await conn.QueryFirstOrDefaultAsync<Contact>(new CommandDefinition(sql, new { email }, cancellationToken: ct));
    }

    public async Task<Contact> CreateContactAsync(Contact c, CancellationToken ct)
    {
        var sql = $"""
            INSERT INTO contacts (company_id, company_role, first_name, last_name, email, phone, job_title, is_active)
            VALUES (@CompanyId, @CompanyRole, @FirstName, @LastName, @Email, @Phone, @JobTitle, @IsActive)
            RETURNING {ContactCols}
            """;
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        return await conn.QuerySingleAsync<Contact>(new CommandDefinition(sql, c, cancellationToken: ct));
    }

    public async Task<Contact?> UpdateContactAsync(Guid id, Contact p, CancellationToken ct)
    {
        var sql = $"""
            UPDATE contacts SET company_id = @CompanyId, company_role = @CompanyRole,
                                first_name = @FirstName, last_name = @LastName, email = @Email,
                                phone = @Phone, job_title = @JobTitle, is_active = @IsActive,
                                updated_utc = now()
            WHERE id = @Id
            RETURNING {ContactCols}
            """;
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        return await conn.QueryFirstOrDefaultAsync<Contact>(new CommandDefinition(sql, p with { Id = id }, cancellationToken: ct));
    }

    public async Task<DeleteResult> DeleteContactAsync(Guid id, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var exists = await conn.QueryFirstOrDefaultAsync<int?>(new CommandDefinition(
            "SELECT 1 FROM contacts WHERE id = @id", new { id }, cancellationToken: ct));
        if (exists is null) return DeleteResult.NotFound;

        var ticketCount = await conn.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT count(*)::int FROM tickets WHERE requester_contact_id = @id AND is_deleted = FALSE",
            new { id }, cancellationToken: ct));
        if (ticketCount > 0) return DeleteResult.InUse;

        await conn.ExecuteAsync(new CommandDefinition("DELETE FROM contacts WHERE id = @id", new { id }, cancellationToken: ct));
        return DeleteResult.Deleted;
    }

    public async Task<bool> SetContactCompanyAsync(Guid contactId, Guid? companyId, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var rows = await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE contacts SET company_id = @companyId, updated_utc = now() WHERE id = @contactId",
            new { companyId, contactId }, cancellationToken: ct));
        return rows > 0;
    }
}
