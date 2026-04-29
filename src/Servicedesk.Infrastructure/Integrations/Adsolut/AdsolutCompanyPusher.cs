using Dapper;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Servicedesk.Infrastructure.Integrations.Adsolut;

public sealed class AdsolutCompanyPusher : IAdsolutCompanyPusher
{
    /// Pure decision: given the candidate (already loaded by the caller)
    /// + the toggles + the canonical hash of the candidate's current
    /// state, decide what the storage layer should do.
    public static AdsolutPushDecision Decide(
        AdsolutCompanyPushCandidate candidate,
        AdsolutPushOptions options,
        byte[] candidateHash)
    {
        // Linked row: only push when there's measurable drift, the
        // update-toggle is on, and the canonical hash differs from what
        // we last successfully synced (echo-pull no-op).
        if (candidate.AdsolutId is { } adsolutId)
        {
            if (candidate.AdsolutLastModified is { } pulled &&
                candidate.UpdatedUtc <= pulled)
            {
                return new AdsolutPushDecision(AdsolutPushOutcome.SkippedNoLocalChange);
            }
            if (!options.PushUpdateEnabled)
            {
                return new AdsolutPushDecision(AdsolutPushOutcome.SkippedUpdateToggleOff);
            }
            if (candidate.AdsolutSyncedHash is { } stored && ByteEquals(stored, candidateHash))
            {
                return new AdsolutPushDecision(AdsolutPushOutcome.SkippedNoChange);
            }
            // v0.0.27 — Adsolut's UpdateCustomerRequest treats an absent
            // `number` field as "clear klantnummer", which is forbidden
            // after creation. If our local row was pulled before the
            // adsolut_number column was added (upgrade-from-v0.0.26 case),
            // we don't have the value to send back yet. Skip until the
            // next pull tick populates it.
            if (string.IsNullOrEmpty(candidate.AdsolutNumber))
            {
                return new AdsolutPushDecision(AdsolutPushOutcome.SkippedMissingAdsolutNumber);
            }
            return new AdsolutPushDecision(AdsolutPushOutcome.Updated);
        }

        // Unlinked row: brand-new POST when the create-toggle is on.
        if (!options.PushCreateEnabled)
        {
            return new AdsolutPushDecision(AdsolutPushOutcome.SkippedCreateToggleOff);
        }
        return new AdsolutPushDecision(AdsolutPushOutcome.Created);
    }

    /// Hand-rolled byte equality so the pusher does not depend on a
    /// crypto-library helper for a 32-byte compare. Constant-time is not
    /// required here — both sides come from our own SHA-256 over local
    /// inputs, not from untrusted user input.
    private static bool ByteEquals(byte[] a, byte[] b)
    {
        if (a.Length != b.Length) return false;
        for (var i = 0; i < a.Length; i++)
        {
            if (a[i] != b[i]) return false;
        }
        return true;
    }

    /// Splits a single SD-style address line ("Hoofdstraat 12B") into the
    /// streetName + streetNumber pair Adsolut expects. Heuristic: if the
    /// last whitespace-delimited token starts with a digit, that token is
    /// the number; everything before is the street. Falls back to "all
    /// text in streetName, empty streetNumber" when no trailing digit token
    /// is present so a free-form address still round-trips. Public for
    /// unit-testability.
    public static (string StreetName, string StreetNumber) SplitAddressLine1(string? line1)
    {
        if (string.IsNullOrWhiteSpace(line1)) return (string.Empty, string.Empty);
        var trimmed = line1.Trim();
        var lastSpace = trimmed.LastIndexOfAny(new[] { ' ', '\t' });
        if (lastSpace <= 0 || lastSpace >= trimmed.Length - 1)
        {
            return (trimmed, string.Empty);
        }
        var tail = trimmed[(lastSpace + 1)..];
        if (!char.IsDigit(tail[0]))
        {
            return (trimmed, string.Empty);
        }
        var head = trimmed[..lastSpace].TrimEnd();
        return (head, tail);
    }

    /// "Bus 12" → "12"; "Box A" → "A"; bare "12B" → "12B". The pull-side
    /// always writes "Bus &lt;n&gt;" so this strip is the inverse for those;
    /// admin-typed values that don't follow the convention are pushed through
    /// as-is so we do not silently swallow non-standard box markers.
    public static string ExtractBoxNumber(string? line2)
    {
        if (string.IsNullOrWhiteSpace(line2)) return string.Empty;
        var trimmed = line2.Trim();
        if (trimmed.StartsWith("Bus ", StringComparison.OrdinalIgnoreCase))
            return trimmed[4..].Trim();
        if (trimmed.StartsWith("Box ", StringComparison.OrdinalIgnoreCase))
            return trimmed[4..].Trim();
        return trimmed;
    }

    /// "BE0123456789" → ("BE", "0123456789"); pure-digit input goes through
    /// as country-prefix-empty; pure-letter input goes through as digits-empty.
    /// We only treat the FIRST two characters as a prefix when both are
    /// letters — otherwise the whole string is the digit body, matching how
    /// admins enter VAT in countries that don't use 2-letter prefixes.
    public static (string Prefix, string Digits) SplitVat(string? combined)
    {
        if (string.IsNullOrWhiteSpace(combined)) return (string.Empty, string.Empty);
        var trimmed = combined.Trim();
        if (trimmed.Length < 2) return (string.Empty, trimmed);
        if (char.IsLetter(trimmed[0]) && char.IsLetter(trimmed[1]))
        {
            return (trimmed[..2].ToUpperInvariant(), trimmed[2..]);
        }
        return (string.Empty, trimmed);
    }

    private readonly NpgsqlDataSource _dataSource;
    private readonly IAdsolutCustomersWriteClient _writeClient;
    private readonly ILogger<AdsolutCompanyPusher> _logger;

    public AdsolutCompanyPusher(
        NpgsqlDataSource dataSource,
        IAdsolutCustomersWriteClient writeClient,
        ILogger<AdsolutCompanyPusher> logger)
    {
        _dataSource = dataSource;
        _writeClient = writeClient;
        _logger = logger;
    }

    public async Task<IReadOnlyList<AdsolutCompanyPushCandidate>> LoadCandidatesAsync(
        AdsolutPushOptions options,
        int limit,
        CancellationToken ct = default)
    {
        // Build the WHERE clause from the toggles so a tick with both
        // toggles off short-circuits to zero rows without a SQL round-trip
        // returning the entire address book just to skip every row.
        if (!options.PushUpdateEnabled && !options.PushCreateEnabled)
        {
            return Array.Empty<AdsolutCompanyPushCandidate>();
        }

        // Two qualifying clauses, OR-combined; the toggles below pick one
        // or both. Active rows only — soft-deleted companies are out of
        // scope for outbound sync. The LIMIT is a safety cap so a runaway
        // backlog (first push after admin opts in for the first time) is
        // chunked across ticks instead of flooding Adsolut in one shot.
        var clauses = new List<string>();
        if (options.PushUpdateEnabled)
        {
            clauses.Add("(adsolut_id IS NOT NULL AND adsolut_last_modified IS NOT NULL AND updated_utc > adsolut_last_modified)");
        }
        if (options.PushCreateEnabled)
        {
            clauses.Add("(adsolut_id IS NULL)");
        }
        var where = string.Join(" OR ", clauses);

        var sql = $"""
            SELECT
                id                    AS Id,
                name                  AS Name,
                code                  AS Code,
                email                 AS Email,
                phone                 AS Phone,
                address_line1         AS AddressLine1,
                address_line2         AS AddressLine2,
                postal_code           AS PostalCode,
                city                  AS City,
                country               AS Country,
                vat_number            AS VatNumber,
                adsolut_id            AS AdsolutId,
                adsolut_number        AS AdsolutNumber,
                adsolut_alpha_code    AS AdsolutAlphaCode,
                adsolut_last_modified AS AdsolutLastModified,
                adsolut_synced_hash   AS AdsolutSyncedHash,
                updated_utc           AS UpdatedUtc
            FROM companies
            WHERE is_active = TRUE
              AND ({where})
            ORDER BY updated_utc ASC
            LIMIT @Limit
            """;

        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<AdsolutCompanyPushCandidate>(new CommandDefinition(
            sql,
            new { Limit = Math.Clamp(limit, 1, 1000) },
            cancellationToken: ct));
        return rows.AsList();
    }

    public async Task<AdsolutPushOutcome> PushOneAsync(
        Guid administrationId,
        AdsolutCompanyPushCandidate candidate,
        AdsolutPushOptions options,
        CancellationToken ct = default)
    {
        var (prefix, digits) = SplitVat(candidate.VatNumber);
        var (street, number) = SplitAddressLine1(candidate.AddressLine1);
        var box = ExtractBoxNumber(candidate.AddressLine2);

        var hash = AdsolutCompanyHash.Compute(new AdsolutCompanyHashInput(
            Name: candidate.Name,
            Code: candidate.Code,
            VatCombined: candidate.VatNumber,
            AddressLine1: candidate.AddressLine1,
            AddressLine2: candidate.AddressLine2,
            PostalCode: candidate.PostalCode,
            City: candidate.City,
            Country: candidate.Country,
            Phone: candidate.Phone,
            Email: candidate.Email));

        var decision = Decide(candidate, options, hash);

        if (decision.Outcome == AdsolutPushOutcome.SkippedNoChange ||
            decision.Outcome == AdsolutPushOutcome.SkippedNoLocalChange ||
            decision.Outcome == AdsolutPushOutcome.SkippedUpdateToggleOff ||
            decision.Outcome == AdsolutPushOutcome.SkippedCreateToggleOff)
        {
            return decision.Outcome;
        }

        var payload = new AdsolutCustomerWritePayload(
            Name: candidate.Name,
            AlphaCode: candidate.AdsolutAlphaCode,
            Number: candidate.AdsolutNumber,
            Email: candidate.Email,
            Phone: candidate.Phone,
            StreetName: street,
            StreetNumber: number,
            BoxNumber: box,
            PostalCode: candidate.PostalCode,
            City: candidate.City,
            VatNumber: digits,
            CountryPrefixVatNumber: prefix);

        await using var conn = await _dataSource.OpenConnectionAsync(ct);

        if (decision.Outcome == AdsolutPushOutcome.Updated)
        {
            var result = await _writeClient.UpdateCustomerAsync(
                administrationId, candidate.AdsolutId!.Value, payload, ct);

            // Anchor updated_utc on the upstream lastModified (or now() if
            // Adsolut returned no timestamp and the read-back also failed)
            // so the next push-tak scan does not see this row as drift.
            // Refresh adsolut_number / adsolut_alpha_code from the response
            // when present, so a row that arrived via push (no full pull
            // tick yet) keeps the klantnummer in sync. COALESCE leaves the
            // existing value untouched when WK only echoed `id + lastModified`.
            var lastModified = result.LastModified?.UtcDateTime;
            await conn.ExecuteAsync(new CommandDefinition(
                """
                UPDATE companies SET
                    adsolut_last_modified = COALESCE(@AdsolutLastModified, adsolut_last_modified),
                    adsolut_number        = COALESCE(@AdsolutNumber, adsolut_number),
                    adsolut_alpha_code    = COALESCE(@AdsolutAlphaCode, adsolut_alpha_code),
                    adsolut_synced_hash   = @Hash,
                    updated_utc           = COALESCE(@AdsolutLastModified, now())
                WHERE id = @Id
                """,
                new
                {
                    Id = candidate.Id,
                    AdsolutLastModified = lastModified,
                    AdsolutNumber = result.Number,
                    AdsolutAlphaCode = result.AlphaCode,
                    Hash = hash,
                },
                cancellationToken: ct));
            return AdsolutPushOutcome.Updated;
        }

        // Created branch: POST returns the new id, alphaCode and number
        // (Adsolut auto-assigns klantnummer on create); persist all of
        // them + hash + lastModified in one UPDATE so the row is fully
        // linked atomically. Without alphaCode + number the next push-tak
        // would gate on SkippedMissingAdsolutNumber even though the row
        // was just successfully pushed.
        var createResult = await _writeClient.CreateCustomerAsync(administrationId, payload, ct);
        var createdLastModified = createResult.LastModified?.UtcDateTime;
        await conn.ExecuteAsync(new CommandDefinition(
            """
            UPDATE companies SET
                adsolut_id            = @AdsolutId,
                adsolut_number        = @AdsolutNumber,
                adsolut_alpha_code    = @AdsolutAlphaCode,
                adsolut_last_modified = @AdsolutLastModified,
                adsolut_synced_hash   = @Hash,
                updated_utc           = COALESCE(@AdsolutLastModified, now())
            WHERE id = @Id
            """,
            new
            {
                Id = candidate.Id,
                AdsolutId = createResult.Id,
                AdsolutNumber = createResult.Number,
                AdsolutAlphaCode = createResult.AlphaCode,
                AdsolutLastModified = createdLastModified,
                Hash = hash,
            },
            cancellationToken: ct));
        return AdsolutPushOutcome.Created;
    }
}
