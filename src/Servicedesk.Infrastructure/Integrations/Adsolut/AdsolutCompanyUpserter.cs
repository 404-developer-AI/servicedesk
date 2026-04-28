using Dapper;
using Npgsql;

namespace Servicedesk.Infrastructure.Integrations.Adsolut;

/// One row from the local <c>companies</c> table — the minimum set of
/// columns the upsert decision-logic needs from a candidate match. Lifted
/// out of <see cref="AdsolutCompanyUpserter"/> so the pure decision
/// function can be unit-tested without a Postgres connection.
public sealed record AdsolutCompanyMatchRow(Guid Id, DateTime UpdatedUtc, Guid? AdsolutId);

/// Output of <see cref="AdsolutCompanyUpserter.Decide"/>. Drives whether
/// the storage layer issues an UPDATE, an INSERT, or a no-op skip. The
/// <c>Match</c> is non-null exactly when the outcome is Updated /
/// SkippedLocalNewer / SkippedUpdateToggleOff (i.e. a row exists locally).
public sealed record AdsolutUpsertDecision(
    AdsolutUpsertOutcome Outcome,
    AdsolutCompanyMatchRow? Match);

public sealed class AdsolutCompanyUpserter : IAdsolutCompanyUpserter
{
    /// Pure decision: given the local row that matched (or null if none)
    /// + the Adsolut-side customer + the per-tick toggles, decide what
    /// the storage layer should do. Lifted out of the SQL path so the
    /// match-precedence + conflict + toggle interaction is unit-testable.
    public static AdsolutUpsertDecision Decide(
        AdsolutCompanyMatchRow? match,
        AdsolutCustomer customer,
        AdsolutSyncOptions options)
    {
        if (match is not null)
        {
            // Conflict tie-breaker: local won? skip.
            if (customer.LastModified is { } adsolutMod &&
                match.UpdatedUtc > adsolutMod.UtcDateTime)
            {
                return new AdsolutUpsertDecision(AdsolutUpsertOutcome.SkippedLocalNewer, match);
            }
            if (!options.PullUpdateEnabled)
            {
                return new AdsolutUpsertDecision(AdsolutUpsertOutcome.SkippedUpdateToggleOff, match);
            }
            return new AdsolutUpsertDecision(AdsolutUpsertOutcome.Updated, match);
        }

        return new AdsolutUpsertDecision(
            options.PullCreateEnabled
                ? AdsolutUpsertOutcome.Created
                : AdsolutUpsertOutcome.SkippedCreateToggleOff,
            null);
    }


    private readonly NpgsqlDataSource _dataSource;

    public AdsolutCompanyUpserter(NpgsqlDataSource dataSource)
    {
        _dataSource = dataSource;
    }

    public async Task<AdsolutUpsertOutcome> UpsertAsync(
        AdsolutCustomer customer,
        AdsolutSyncOptions options,
        CancellationToken ct = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);

        // Step 1 — match by adsolut_id (already linked).
        var matched = await conn.QueryFirstOrDefaultAsync<AdsolutCompanyMatchRow>(new CommandDefinition(
            """
            SELECT id AS Id, updated_utc AS UpdatedUtc, adsolut_id AS AdsolutId
            FROM companies
            WHERE adsolut_id = @AdsolutId
            LIMIT 1
            """,
            new { AdsolutId = customer.Id },
            cancellationToken: ct));

        // Step 2 — match by code (first-link case). Only run when code is
        // populated AND we didn't already match by adsolut_id; the unique
        // index on (code) makes this lookup cheap and unambiguous.
        if (matched is null && !string.IsNullOrWhiteSpace(customer.Code))
        {
            matched = await conn.QueryFirstOrDefaultAsync<AdsolutCompanyMatchRow>(new CommandDefinition(
                """
                SELECT id AS Id, updated_utc AS UpdatedUtc, adsolut_id AS AdsolutId
                FROM companies
                WHERE code = @Code AND adsolut_id IS NULL
                LIMIT 1
                """,
                new { Code = customer.Code },
                cancellationToken: ct));
        }

        var decision = Decide(matched, customer, options);

        var combinedAddress = string.IsNullOrEmpty(customer.AddressLine2)
            ? customer.AddressLine1
            : customer.AddressLine1;
        var line2 = customer.AddressLine2;
        var combinedVat = CombineVat(customer.CountryPrefixVatNumber, customer.VatNumber);
        var lastModified = customer.LastModified?.UtcDateTime;

        if (decision.Outcome == AdsolutUpsertOutcome.SkippedLocalNewer ||
            decision.Outcome == AdsolutUpsertOutcome.SkippedUpdateToggleOff ||
            decision.Outcome == AdsolutUpsertOutcome.SkippedCreateToggleOff)
        {
            return decision.Outcome;
        }

        if (decision.Outcome == AdsolutUpsertOutcome.Updated)
        {
            await conn.ExecuteAsync(new CommandDefinition(
                """
                UPDATE companies SET
                    name                  = @Name,
                    email                 = @Email,
                    phone                 = @Phone,
                    address_line1         = @AddressLine1,
                    address_line2         = @AddressLine2,
                    postal_code           = @PostalCode,
                    city                  = @City,
                    country               = @Country,
                    vat_number            = @VatNumber,
                    code                  = COALESCE(@Code, code),
                    adsolut_id            = @AdsolutId,
                    adsolut_last_modified = @AdsolutLastModified,
                    updated_utc           = now()
                WHERE id = @Id
                """,
                new
                {
                    Id = decision.Match!.Id,
                    Name = NotNull(customer.Name),
                    Email = NotNull(customer.Email),
                    Phone = NotNull(customer.Phone),
                    AddressLine1 = NotNull(combinedAddress),
                    AddressLine2 = NotNull(line2),
                    PostalCode = NotNull(customer.PostalCode),
                    City = NotNull(customer.City),
                    Country = NotNull(customer.Country),
                    VatNumber = NotNull(combinedVat),
                    Code = NormalizeCode(customer.Code),
                    AdsolutId = customer.Id,
                    AdsolutLastModified = lastModified,
                },
                cancellationToken: ct));
            await TryLinkDomainAsync(conn, decision.Match!.Id, customer.Email, options, ct);
            return AdsolutUpsertOutcome.Updated;
        }

        // Decide returned Created — falling through here after the Updated
        // branch above means there was no match and the create-toggle is on.
        // Adsolut customers without a code violate our UNIQUE(code) index;
        // synthesize a stable surrogate from the adsolut_id so re-syncing
        // the same row produces the same code and we don't drift.
        var insertCode = NormalizeCode(customer.Code) ?? $"adsolut-{customer.Id.ToString("N")[..8]}";

        var newId = await conn.QuerySingleAsync<Guid>(new CommandDefinition(
            """
            INSERT INTO companies (
                name, description, website, phone,
                address_line1, address_line2, city, postal_code, country,
                is_active, code, short_name, vat_number,
                alert_text, alert_on_create, alert_on_open, alert_on_open_mode,
                email, adsolut_id, adsolut_last_modified
            )
            VALUES (
                @Name, '', '', @Phone,
                @AddressLine1, @AddressLine2, @City, @PostalCode, @Country,
                TRUE, @Code, '', @VatNumber,
                '', FALSE, FALSE, 'session',
                @Email, @AdsolutId, @AdsolutLastModified
            )
            RETURNING id
            """,
            new
            {
                Name = NotNull(customer.Name),
                Phone = NotNull(customer.Phone),
                AddressLine1 = NotNull(combinedAddress),
                AddressLine2 = NotNull(line2),
                PostalCode = NotNull(customer.PostalCode),
                City = NotNull(customer.City),
                Country = NotNull(customer.Country),
                Code = insertCode,
                VatNumber = NotNull(combinedVat),
                Email = NotNull(customer.Email),
                AdsolutId = customer.Id,
                AdsolutLastModified = lastModified,
            },
            cancellationToken: ct));
        await TryLinkDomainAsync(conn, newId, customer.Email, options, ct);
        return AdsolutUpsertOutcome.Created;
    }

    /// Inserts the email's domain into <c>company_domains</c> when the
    /// link-domains toggle is on AND the domain isn't on the freemail
    /// blacklist. Idempotent — the unique index on <c>company_domains.domain</c>
    /// makes a re-sync a no-op, and a domain already claimed by another
    /// company is silently skipped (first-claim-wins convention shared with
    /// the admin "Add domain" endpoint).
    private static async Task TryLinkDomainAsync(
        Npgsql.NpgsqlConnection conn,
        Guid companyId,
        string? email,
        AdsolutSyncOptions options,
        CancellationToken ct)
    {
        if (!options.LinkCompanyDomainsFromEmail) return;
        var domain = ExtractLinkableDomain(email, options.FreemailBlacklist);
        if (domain is null) return;

        await conn.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO company_domains (company_id, domain)
            VALUES (@CompanyId, @Domain)
            ON CONFLICT (domain) DO NOTHING
            """,
            new { CompanyId = companyId, Domain = domain },
            cancellationToken: ct));
    }

    /// Pure helper: parse the host part of an email and screen it against
    /// the freemail blacklist. Returns the lowercase domain when it is safe
    /// to auto-link; null when the input is empty, malformed, or blacklisted.
    /// Lifted out of <see cref="TryLinkDomainAsync"/> so the screening rules
    /// are unit-testable without a Postgres connection.
    public static string? ExtractLinkableDomain(string? email, IReadOnlySet<string>? blacklist)
    {
        if (string.IsNullOrWhiteSpace(email)) return null;
        var atIdx = email.LastIndexOf('@');
        if (atIdx <= 0 || atIdx >= email.Length - 1) return null;
        var domain = email[(atIdx + 1)..].Trim().ToLowerInvariant();
        if (domain.Length == 0 || domain.Contains(' ')) return null;
        // Reject obviously malformed hosts ("foo", ".com", "foo.") — must
        // contain at least one dot with non-empty labels on both sides.
        var dot = domain.IndexOf('.');
        if (dot <= 0 || dot == domain.Length - 1) return null;
        if (blacklist?.Contains(domain) == true) return null;
        return domain;
    }

    private static string NotNull(string? s) => s ?? string.Empty;

    private static string? NormalizeCode(string? code)
    {
        if (string.IsNullOrWhiteSpace(code)) return null;
        return code.Trim();
    }

    /// Adsolut splits VAT into a country prefix + the digit body. The local
    /// <c>companies.vat_number</c> column carries a single concatenated
    /// string ("BE0123456789"), matching the inbound mail-ingest convention.
    private static string CombineVat(string? prefix, string? digits)
    {
        var p = (prefix ?? string.Empty).Trim();
        var d = (digits ?? string.Empty).Trim();
        if (p.Length == 0 && d.Length == 0) return string.Empty;
        return p + d;
    }

}
