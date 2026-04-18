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
        alert_on_open AS AlertOnOpen, alert_on_open_mode AS AlertOnOpenMode,
        email AS Email
        """;

    // Qualified with `contacts.` because ListContactsAsync joins onto
    // contact_companies, and both tables carry id/created_utc/updated_utc.
    // Unqualified references collide (Postgres 42702).
    private const string ContactCols = """
        contacts.id AS Id, contacts.company_role AS CompanyRole,
        contacts.first_name AS FirstName, contacts.last_name AS LastName,
        contacts.email AS Email, contacts.phone AS Phone,
        contacts.job_title AS JobTitle, contacts.is_active AS IsActive,
        contacts.created_utc AS CreatedUtc, contacts.updated_utc AS UpdatedUtc,
        (SELECT ccp.company_id FROM contact_companies ccp
          WHERE ccp.contact_id = contacts.id AND ccp.role = 'primary' LIMIT 1) AS PrimaryCompanyId
        """;

    private const string LinkCols = """
        id AS Id, contact_id AS ContactId, company_id AS CompanyId,
        role AS Role, created_utc AS CreatedUtc, updated_utc AS UpdatedUtc
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

    public async Task<Company?> GetPrimaryCompanyForContactAsync(Guid contactId, CancellationToken ct)
    {
        // Join via the contact_companies link table; only primary-role links
        // count as "the" company for the contact. c.id is qualified to avoid
        // the 42702 ambiguous-column error we used to hit pre-v0.0.9.
        const string sql = """
            SELECT c.id AS Id, c.name AS Name, c.description AS Description,
                   c.website AS Website, c.phone AS Phone,
                   c.address_line1 AS AddressLine1, c.address_line2 AS AddressLine2,
                   c.city AS City, c.postal_code AS PostalCode, c.country AS Country,
                   c.is_active AS IsActive,
                   c.created_utc AS CreatedUtc, c.updated_utc AS UpdatedUtc,
                   c.code AS Code, c.short_name AS ShortName, c.vat_number AS VatNumber,
                   c.alert_text AS AlertText, c.alert_on_create AS AlertOnCreate,
                   c.alert_on_open AS AlertOnOpen, c.alert_on_open_mode AS AlertOnOpenMode,
                   c.email AS Email
            FROM companies c
            JOIN contact_companies cc ON cc.company_id = c.id AND cc.role = 'primary'
            WHERE cc.contact_id = @contactId AND c.is_active = TRUE
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
                                   alert_text, alert_on_create, alert_on_open, alert_on_open_mode,
                                   email)
            VALUES (@Name, @Description, @Website, @Phone, @AddressLine1, @AddressLine2,
                    @City, @PostalCode, @Country, @IsActive,
                    @Code, @ShortName, @VatNumber,
                    @AlertText, @AlertOnCreate, @AlertOnOpen, @AlertOnOpenMode,
                    @Email)
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
                                 email = @Email,
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
        const string sql = """
            SELECT c.id AS Id, c.name AS Name, c.description AS Description,
                   c.website AS Website, c.phone AS Phone,
                   c.address_line1 AS AddressLine1, c.address_line2 AS AddressLine2,
                   c.city AS City, c.postal_code AS PostalCode, c.country AS Country,
                   c.is_active AS IsActive,
                   c.created_utc AS CreatedUtc, c.updated_utc AS UpdatedUtc,
                   c.code AS Code, c.short_name AS ShortName, c.vat_number AS VatNumber,
                   c.alert_text AS AlertText, c.alert_on_create AS AlertOnCreate,
                   c.alert_on_open AS AlertOnOpen, c.alert_on_open_mode AS AlertOnOpenMode,
                   c.email AS Email
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
        // When a company filter is given we join through contact_companies so
        // any role (primary/secondary/supplier) surfaces the contact. DISTINCT
        // keeps the result set unique in the odd case a contact were somehow
        // double-linked despite the pair-unique constraint.
        var sql = companyId.HasValue
            ? $"""
                SELECT DISTINCT {ContactCols}
                FROM contacts
                JOIN contact_companies cc ON cc.contact_id = contacts.id
                WHERE cc.company_id = @companyId
                """
            : $"SELECT {ContactCols} FROM contacts WHERE 1=1";
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

    public async Task<Contact> CreateContactAsync(Contact c, Guid? primaryCompanyId, CancellationToken ct)
    {
        // Single connection, explicit transaction: the contact row and its
        // primary-link row must commit together so mail intake never leaves
        // a dangling contact without the company association it thought it
        // got.
        const string insertContact = """
            INSERT INTO contacts (company_role, first_name, last_name, email, phone, job_title, is_active)
            VALUES (@CompanyRole, @FirstName, @LastName, @Email, @Phone, @JobTitle, @IsActive)
            RETURNING id
            """;
        const string insertPrimary = """
            INSERT INTO contact_companies (contact_id, company_id, role)
            VALUES (@contactId, @companyId, 'primary')
            """;

        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var newId = await conn.QuerySingleAsync<Guid>(new CommandDefinition(insertContact, c, tx, cancellationToken: ct));
        if (primaryCompanyId.HasValue)
        {
            await conn.ExecuteAsync(new CommandDefinition(insertPrimary,
                new { contactId = newId, companyId = primaryCompanyId.Value }, tx, cancellationToken: ct));
        }
        await tx.CommitAsync(ct);

        // Re-select so PrimaryCompanyId is populated consistently with all
        // other reads — the correlated subquery in ContactCols does the work.
        var reread = await conn.QueryFirstOrDefaultAsync<Contact>(new CommandDefinition(
            $"SELECT {ContactCols} FROM contacts WHERE id = @id", new { id = newId }, cancellationToken: ct));
        return reread!;
    }

    public async Task<Contact?> UpdateContactAsync(Guid id, Contact p, CancellationToken ct)
    {
        var sql = $"""
            UPDATE contacts SET company_role = @CompanyRole,
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

    public async Task<IReadOnlyList<ContactCompanyLink>> ListContactLinksAsync(Guid contactId, CancellationToken ct)
    {
        var sql = $"""
            SELECT {LinkCols}
            FROM contact_companies
            WHERE contact_id = @contactId
            ORDER BY CASE role WHEN 'primary' THEN 0 WHEN 'secondary' THEN 1 ELSE 2 END, created_utc
            """;
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        return (await conn.QueryAsync<ContactCompanyLink>(new CommandDefinition(sql, new { contactId }, cancellationToken: ct))).ToList();
    }

    public async Task<IReadOnlyList<ContactCompanyOption>> ListContactCompanyOptionsAsync(Guid contactId, CancellationToken ct)
    {
        const string sql = """
            SELECT cc.id AS LinkId, co.id AS CompanyId,
                   co.name AS CompanyName, co.code AS CompanyCode,
                   co.short_name AS CompanyShortName, co.is_active AS CompanyIsActive,
                   cc.role AS Role
            FROM contact_companies cc
            JOIN companies co ON co.id = cc.company_id
            WHERE cc.contact_id = @contactId
            ORDER BY CASE cc.role WHEN 'primary' THEN 0 WHEN 'secondary' THEN 1 ELSE 2 END, co.name
            """;
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        return (await conn.QueryAsync<ContactCompanyOption>(
            new CommandDefinition(sql, new { contactId }, cancellationToken: ct))).ToList();
    }

    public async Task<IReadOnlyList<ContactCompanyLink>> ListCompanyLinksAsync(Guid companyId, CancellationToken ct)
    {
        var sql = $"""
            SELECT {LinkCols}
            FROM contact_companies
            WHERE company_id = @companyId
            ORDER BY CASE role WHEN 'primary' THEN 0 WHEN 'secondary' THEN 1 ELSE 2 END, created_utc
            """;
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        return (await conn.QueryAsync<ContactCompanyLink>(new CommandDefinition(sql, new { companyId }, cancellationToken: ct))).ToList();
    }

    public async Task<ContactCompanyLink> UpsertContactLinkAsync(Guid contactId, Guid companyId, string role, CancellationToken ct)
    {
        if (role != "primary" && role != "secondary" && role != "supplier")
            throw new ArgumentException($"Role '{role}' is not one of primary/secondary/supplier.", nameof(role));

        // Atomic primary-move: demote any existing primary link for this
        // contact to 'secondary' BEFORE upserting, so the partial unique index
        // can never fire. Wrapping in a transaction keeps this step and the
        // upsert either fully visible or fully rolled back.
        const string demoteExistingPrimary = """
            UPDATE contact_companies
               SET role = 'secondary', updated_utc = now()
             WHERE contact_id = @contactId
               AND role = 'primary'
               AND company_id <> @companyId
            """;
        const string upsert = """
            INSERT INTO contact_companies (contact_id, company_id, role)
            VALUES (@contactId, @companyId, @role)
            ON CONFLICT (contact_id, company_id) DO UPDATE
               SET role = EXCLUDED.role, updated_utc = now()
            RETURNING id AS Id, contact_id AS ContactId, company_id AS CompanyId,
                      role AS Role, created_utc AS CreatedUtc, updated_utc AS UpdatedUtc
            """;

        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        if (role == "primary")
        {
            await conn.ExecuteAsync(new CommandDefinition(demoteExistingPrimary,
                new { contactId, companyId }, tx, cancellationToken: ct));
        }
        var link = await conn.QuerySingleAsync<ContactCompanyLink>(new CommandDefinition(upsert,
            new { contactId, companyId, role }, tx, cancellationToken: ct));
        await tx.CommitAsync(ct);
        return link;
    }

    public async Task<bool> RemoveContactLinkAsync(Guid contactId, Guid companyId, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var rows = await conn.ExecuteAsync(new CommandDefinition(
            "DELETE FROM contact_companies WHERE contact_id = @contactId AND company_id = @companyId",
            new { contactId, companyId }, cancellationToken: ct));
        return rows > 0;
    }

    public async Task<bool> SetPrimaryCompanyAsync(Guid contactId, Guid? companyId, CancellationToken ct)
    {
        if (companyId is null)
        {
            await using var conn = await _dataSource.OpenConnectionAsync(ct);
            var rows = await conn.ExecuteAsync(new CommandDefinition(
                "DELETE FROM contact_companies WHERE contact_id = @contactId AND role = 'primary'",
                new { contactId }, cancellationToken: ct));
            return rows > 0;
        }

        await UpsertContactLinkAsync(contactId, companyId.Value, "primary", ct);
        return true;
    }
}
