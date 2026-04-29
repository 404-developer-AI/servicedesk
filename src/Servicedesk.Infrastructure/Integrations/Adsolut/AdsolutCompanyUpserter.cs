using Dapper;
using Npgsql;

namespace Servicedesk.Infrastructure.Integrations.Adsolut;

/// One row from the local <c>companies</c> table — the minimum set of
/// columns the upsert decision-logic needs from a candidate match. Lifted
/// out of <see cref="AdsolutCompanyUpserter"/> so the pure decision
/// function can be unit-tested without a Postgres connection. v0.0.27
/// adds <see cref="AdsolutSyncedHash"/> so the no-op guard can compare
/// the inbound row's canonical hash against the last-known-synced hash
/// without re-reading the row.
public sealed record AdsolutCompanyMatchRow(
    Guid Id,
    DateTime UpdatedUtc,
    Guid? AdsolutId,
    byte[]? AdsolutSyncedHash = null);

/// Output of <see cref="AdsolutCompanyUpserter.Decide"/>. Drives whether
/// the storage layer issues an UPDATE, an INSERT, or a no-op skip. The
/// <c>Match</c> is non-null exactly when the outcome is Updated /
/// SkippedLocalNewer / SkippedUpdateToggleOff / SkippedNoChange (i.e. a
/// row exists locally).
public sealed record AdsolutUpsertDecision(
    AdsolutUpsertOutcome Outcome,
    AdsolutCompanyMatchRow? Match);

public sealed class AdsolutCompanyUpserter : IAdsolutCompanyUpserter
{
    /// Pure decision: given the local row that matched (or null if none)
    /// + the Adsolut-side customer + the per-tick toggles + the inbound
    /// row's canonical hash, decide what the storage layer should do.
    /// Lifted out of the SQL path so the match-precedence + conflict +
    /// toggle interaction + hash-no-op are unit-testable.
    public static AdsolutUpsertDecision Decide(
        AdsolutCompanyMatchRow? match,
        AdsolutCustomer customer,
        AdsolutSyncOptions options,
        byte[] inboundHash)
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
            // v0.0.27 inbound no-op guard: the canonical hash of the
            // mirrored field-set equals the hash we stored on the last
            // successful sync — any UPDATE here would be byte-for-byte
            // identical, so skip it. Closes the echo-pull loop on the
            // inbound side.
            if (match.AdsolutSyncedHash is { } stored && ByteEquals(stored, inboundHash))
            {
                return new AdsolutUpsertDecision(AdsolutUpsertOutcome.SkippedNoChange, match);
            }
            return new AdsolutUpsertDecision(AdsolutUpsertOutcome.Updated, match);
        }

        return new AdsolutUpsertDecision(
            options.PullCreateEnabled
                ? AdsolutUpsertOutcome.Created
                : AdsolutUpsertOutcome.SkippedCreateToggleOff,
            null);
    }

    private static bool ByteEquals(byte[] a, byte[] b)
    {
        if (a.Length != b.Length) return false;
        for (var i = 0; i < a.Length; i++)
        {
            if (a[i] != b[i]) return false;
        }
        return true;
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
            SELECT id AS Id, updated_utc AS UpdatedUtc, adsolut_id AS AdsolutId, adsolut_synced_hash AS AdsolutSyncedHash
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
                SELECT id AS Id, updated_utc AS UpdatedUtc, adsolut_id AS AdsolutId, adsolut_synced_hash AS AdsolutSyncedHash
                FROM companies
                WHERE code = @Code AND adsolut_id IS NULL
                LIMIT 1
                """,
                new { Code = customer.Code },
                cancellationToken: ct));
        }

        var combinedAddress = string.IsNullOrEmpty(customer.AddressLine2)
            ? customer.AddressLine1
            : customer.AddressLine1;
        var line2 = customer.AddressLine2;
        var combinedVat = CombineVat(customer.CountryPrefixVatNumber, customer.VatNumber);
        var lastModified = customer.LastModified?.UtcDateTime;

        // Compute the canonical hash of the inbound row once — Decide
        // uses it for the no-op guard, the storage path persists it.
        var inboundHash = AdsolutCompanyHash.Compute(new AdsolutCompanyHashInput(
            Name: customer.Name,
            Code: customer.Code,
            VatCombined: combinedVat,
            AddressLine1: combinedAddress,
            AddressLine2: line2,
            PostalCode: customer.PostalCode,
            City: customer.City,
            Country: customer.Country,
            Phone: customer.Phone,
            Email: customer.Email));

        var decision = Decide(matched, customer, options, inboundHash);

        if (decision.Outcome == AdsolutUpsertOutcome.SkippedLocalNewer ||
            decision.Outcome == AdsolutUpsertOutcome.SkippedUpdateToggleOff ||
            decision.Outcome == AdsolutUpsertOutcome.SkippedCreateToggleOff ||
            decision.Outcome == AdsolutUpsertOutcome.SkippedNoChange)
        {
            return decision.Outcome;
        }

        if (decision.Outcome == AdsolutUpsertOutcome.Updated)
        {
            // updated_utc is anchored on Adsolut's lastModified (not now())
            // so the next push-tak scan does not re-trigger on an inbound
            // update we just absorbed. Fallback to now() in the rare case
            // Adsolut returned a row without a lastModified value.
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
                    adsolut_number        = @AdsolutNumber,
                    adsolut_alpha_code    = @AdsolutAlphaCode,
                    adsolut_last_modified = @AdsolutLastModified,
                    adsolut_synced_hash   = @AdsolutSyncedHash,
                    updated_utc           = COALESCE(@AdsolutLastModified, now())
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
                    AdsolutNumber = customer.Number,
                    AdsolutAlphaCode = customer.AlphaCode,
                    AdsolutLastModified = lastModified,
                    AdsolutSyncedHash = inboundHash,
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
                email, adsolut_id, adsolut_number, adsolut_alpha_code,
                adsolut_last_modified, adsolut_synced_hash, updated_utc
            )
            VALUES (
                @Name, '', '', @Phone,
                @AddressLine1, @AddressLine2, @City, @PostalCode, @Country,
                TRUE, @Code, '', @VatNumber,
                '', FALSE, FALSE, 'session',
                @Email, @AdsolutId, @AdsolutNumber, @AdsolutAlphaCode,
                @AdsolutLastModified, @AdsolutSyncedHash,
                COALESCE(@AdsolutLastModified, now())
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
                AdsolutNumber = customer.Number,
                AdsolutAlphaCode = customer.AlphaCode,
                AdsolutLastModified = lastModified,
                AdsolutSyncedHash = inboundHash,
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
