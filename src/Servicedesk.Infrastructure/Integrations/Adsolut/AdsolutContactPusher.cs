using Dapper;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Servicedesk.Infrastructure.Integrations.Adsolut;

/// v0.0.29 — pushes SD edits on the four mirrored contact fields
/// (<c>first_name</c>, <c>last_name</c>, <c>phone</c>, <c>mobile_phone</c>)
/// back to Adsolut, one PUT per linked customer-contact UUID. Mirrors the
/// v0.0.27 <see cref="AdsolutCompanyPusher"/>:
/// <list type="bullet">
/// <item>Pure <see cref="Decide"/> static — toggle-respect, drift-detection
/// and hash-no-op are unit-testable without Postgres.</item>
/// <item>SQL orchestration in <see cref="LoadCandidatesAsync"/> +
/// <see cref="PushOneAsync"/>.</item>
/// <item>Per-tick LIMIT to chunk a "first push after opt-in" backlog
/// across multiple ticks instead of flooding Adsolut in one shot.</item>
/// <item>Echo-pull no-op via <see cref="AdsolutContactHash"/> — the same
/// hash the v0.0.28 pull-upserter writes after an inbound update.</item>
/// </list>
///
/// Hard rule: SD → Adsolut never pushes a contact without a linked
/// <c>company_id</c> (Adsolut has no concept of a free-floating contact).
/// The candidate query enforces this by requiring
/// <c>companies.adsolut_id IS NOT NULL</c>, which makes the link's
/// CompanyAdsolutId always non-null at the call-site.
public sealed class AdsolutContactPusher : IAdsolutContactPusher
{
    /// Pure decision: given the candidate (already loaded by the caller)
    /// + the toggles + the canonical hash of the candidate's current
    /// state, decide what the storage layer should do.
    public static AdsolutContactPushDecision Decide(
        AdsolutContactPushCandidate candidate,
        AdsolutContactsPushOptions options,
        byte[] candidateHash)
    {
        // SD's contacts schema requires email (CITEXT NOT NULL). If a row
        // somehow reached this gate without one (manual SQL surgery, future
        // schema change), bail honestly instead of generating a 400 from
        // Adsolut.
        if (string.IsNullOrWhiteSpace(candidate.Email))
        {
            return new AdsolutContactPushDecision(AdsolutContactPushOutcome.SkippedNoEmail);
        }

        if (candidate.AdsolutContactId is { } adsolutId)
        {
            // Linked link: only push when there is measurable drift, the
            // update-toggle is on, and the canonical hash differs from
            // what was last successfully synced (echo-pull no-op).
            if (candidate.AdsolutLastModified is { } pulled &&
                candidate.ContactUpdatedUtc <= pulled)
            {
                return new AdsolutContactPushDecision(AdsolutContactPushOutcome.SkippedNoLocalChange);
            }
            if (!options.PushUpdateEnabled)
            {
                return new AdsolutContactPushDecision(AdsolutContactPushOutcome.SkippedUpdateToggleOff);
            }
            if (candidate.AdsolutSyncedHash is { } stored && ByteEquals(stored, candidateHash))
            {
                return new AdsolutContactPushDecision(AdsolutContactPushOutcome.SkippedNoChange);
            }
            return new AdsolutContactPushDecision(AdsolutContactPushOutcome.Updated);
        }

        // Unlinked link: brand-new POST when the create-toggle is on.
        if (!options.PushCreateEnabled)
        {
            return new AdsolutContactPushDecision(AdsolutContactPushOutcome.SkippedCreateToggleOff);
        }
        return new AdsolutContactPushDecision(AdsolutContactPushOutcome.Created);
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
    private readonly IAdsolutContactsWriteClient _writeClient;
    private readonly ILogger<AdsolutContactPusher> _logger;

    public AdsolutContactPusher(
        NpgsqlDataSource dataSource,
        IAdsolutContactsWriteClient writeClient,
        ILogger<AdsolutContactPusher> logger)
    {
        _dataSource = dataSource;
        _writeClient = writeClient;
        _logger = logger;
    }

    public async Task<IReadOnlyList<AdsolutContactPushCandidate>> LoadCandidatesAsync(
        AdsolutContactsPushOptions options,
        int limit,
        CancellationToken ct = default)
    {
        if (!options.PushUpdateEnabled && !options.PushCreateEnabled)
        {
            return Array.Empty<AdsolutContactPushCandidate>();
        }

        // Two qualifying clauses, OR-combined; the toggles below pick one
        // or both. We require the parent company to be Adsolut-linked so
        // the hard-rule "never push a contact without a company" is
        // enforced at the SQL boundary — even if a future code-path
        // accidentally calls with both toggles on, an unlinked-company
        // row never enters the candidate set.
        //
        // Active filter: contacts.is_active=TRUE AND
        // companies.is_active=TRUE — soft-deleted entities are out of
        // scope for outbound sync. Per-link adsolut_active is intentionally
        // NOT filtered: a link that was inactivated upstream still needs
        // its hash kept fresh on the SD-side; the pull-side handles the
        // active-flip itself, and pushing a SD-edit on a deactivated link
        // would give the admin a misleading "edit landed" feel.
        var clauses = new List<string>();
        if (options.PushUpdateEnabled)
        {
            clauses.Add("(cc.adsolut_contact_id IS NOT NULL AND cc.adsolut_last_modified IS NOT NULL AND c.updated_utc > cc.adsolut_last_modified)");
        }
        if (options.PushCreateEnabled)
        {
            clauses.Add("(cc.adsolut_contact_id IS NULL)");
        }
        var where = string.Join(" OR ", clauses);

        var sql = $"""
            SELECT
                cc.id                    AS LinkId,
                cc.contact_id            AS ContactId,
                cc.company_id            AS CompanyId,
                co.adsolut_id            AS CompanyAdsolutId,
                cc.adsolut_contact_id    AS AdsolutContactId,
                c.first_name             AS FirstName,
                c.last_name              AS LastName,
                c.email                  AS Email,
                c.phone                  AS Phone,
                c.mobile_phone           AS MobilePhone,
                c.updated_utc            AS ContactUpdatedUtc,
                cc.adsolut_last_modified AS AdsolutLastModified,
                cc.adsolut_synced_hash   AS AdsolutSyncedHash
            FROM contact_companies cc
            JOIN contacts  c  ON c.id  = cc.contact_id
            JOIN companies co ON co.id = cc.company_id
            WHERE c.is_active = TRUE
              AND co.is_active = TRUE
              AND co.adsolut_id IS NOT NULL
              AND ({where})
            ORDER BY c.updated_utc ASC
            LIMIT @Limit
            """;

        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<AdsolutContactPushCandidate>(new CommandDefinition(
            sql,
            new { Limit = Math.Clamp(limit, 1, 1000) },
            cancellationToken: ct));
        return rows.AsList();
    }

    public async Task<AdsolutContactPushCandidate?> LoadCandidateByLinkIdAsync(
        Guid linkId,
        CancellationToken ct = default)
    {
        // Single-row variant of LoadCandidatesAsync used by the coverage
        // page's per-row Force-sync action. Same hard rule on
        // companies.adsolut_id IS NOT NULL — a link whose company hasn't
        // been linked to Adsolut yet is not a candidate, even by manual
        // trigger; the admin must link the company first.
        const string sql = """
            SELECT
                cc.id                    AS LinkId,
                cc.contact_id            AS ContactId,
                cc.company_id            AS CompanyId,
                co.adsolut_id            AS CompanyAdsolutId,
                cc.adsolut_contact_id    AS AdsolutContactId,
                c.first_name             AS FirstName,
                c.last_name              AS LastName,
                c.email                  AS Email,
                c.phone                  AS Phone,
                c.mobile_phone           AS MobilePhone,
                c.updated_utc            AS ContactUpdatedUtc,
                cc.adsolut_last_modified AS AdsolutLastModified,
                cc.adsolut_synced_hash   AS AdsolutSyncedHash
            FROM contact_companies cc
            JOIN contacts  c  ON c.id  = cc.contact_id
            JOIN companies co ON co.id = cc.company_id
            WHERE cc.id = @Id
              AND c.is_active = TRUE
              AND co.is_active = TRUE
              AND co.adsolut_id IS NOT NULL
            """;
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        return await conn.QuerySingleOrDefaultAsync<AdsolutContactPushCandidate>(
            new CommandDefinition(sql, new { Id = linkId }, cancellationToken: ct));
    }

    public async Task<AdsolutContactPushOutcome> PushOneAsync(
        Guid administrationId,
        AdsolutContactPushCandidate candidate,
        AdsolutContactsPushOptions options,
        CancellationToken ct = default)
    {
        var hash = AdsolutContactHash.Compute(new AdsolutContactHashInput(
            FirstName: candidate.FirstName,
            LastName: candidate.LastName,
            Phone: candidate.Phone,
            MobilePhone: candidate.MobilePhone));

        var decision = Decide(candidate, options, hash);

        if (decision.Outcome == AdsolutContactPushOutcome.SkippedNoChange)
        {
            // Hash-equal but the underlying contact's updated_utc moved
            // (typically an edit on a non-mirrored field — job_title or a
            // company-link change bumps contacts.updated_utc but does not
            // shift our four-field hash). Without this anchor the SQL drift
            // query in v0.0.30 coverage keeps reporting the link forever,
            // and the regular push-tak re-loads it every tick, starving
            // real drift behind it under the per-tick cap of 200. Anchor
            // adsolut_last_modified to c.updated_utc so both surfaces go
            // silent on this row; no upstream call needed because the
            // mirrored state is already proven equal by the hash.
            await using var anchorConn = await _dataSource.OpenConnectionAsync(ct);
            await anchorConn.ExecuteAsync(new CommandDefinition(
                """
                UPDATE contact_companies cc
                   SET adsolut_last_modified = c.updated_utc
                  FROM contacts c
                 WHERE cc.id = @LinkId
                   AND c.id = cc.contact_id
                   AND c.updated_utc > cc.adsolut_last_modified
                """,
                new { LinkId = candidate.LinkId },
                cancellationToken: ct));
            return decision.Outcome;
        }

        if (decision.Outcome == AdsolutContactPushOutcome.SkippedNoLocalChange ||
            decision.Outcome == AdsolutContactPushOutcome.SkippedUpdateToggleOff ||
            decision.Outcome == AdsolutContactPushOutcome.SkippedCreateToggleOff ||
            decision.Outcome == AdsolutContactPushOutcome.SkippedNoEmail)
        {
            return decision.Outcome;
        }

        var payload = new AdsolutContactWritePayload(
            FirstName: candidate.FirstName ?? string.Empty,
            LastName: candidate.LastName ?? string.Empty,
            Phone: candidate.Phone ?? string.Empty,
            MobilePhone: candidate.MobilePhone ?? string.Empty,
            Email: candidate.Email ?? string.Empty);

        await using var conn = await _dataSource.OpenConnectionAsync(ct);

        if (decision.Outcome == AdsolutContactPushOutcome.Updated)
        {
            var result = await _writeClient.UpdateCustomerContactAsync(
                administrationId,
                candidate.CompanyAdsolutId,
                candidate.AdsolutContactId!.Value,
                payload,
                ct);

            // Anchor the link's adsolut_last_modified on the upstream
            // stamp (or now() when WK gave us nothing and the read-back
            // also failed) so the next push-tak scan does not see the
            // link as drift. Stamp the new hash so the next echo-pull
            // closes via SkippedNoChange. Bump the link's updated_utc
            // for honest recency in the SPA.
            var lastModified = result.LastModified?.UtcDateTime;
            await conn.ExecuteAsync(new CommandDefinition(
                """
                UPDATE contact_companies SET
                    adsolut_last_modified = COALESCE(@AdsolutLastModified, adsolut_last_modified),
                    adsolut_synced_hash   = @Hash,
                    updated_utc           = COALESCE(@AdsolutLastModified, now())
                WHERE id = @Id
                """,
                new
                {
                    Id = candidate.LinkId,
                    AdsolutLastModified = lastModified,
                    Hash = hash,
                },
                cancellationToken: ct));
            return AdsolutContactPushOutcome.Updated;
        }

        // Created branch: POST returns the new id (and the read-back GET
        // backfills the lastModified). Persist UUID + hash + lastModified
        // on the link in one UPDATE so the row is fully linked atomically.
        var createResult = await _writeClient.CreateCustomerContactAsync(
            administrationId, candidate.CompanyAdsolutId, payload, ct);
        var createdLastModified = createResult.LastModified?.UtcDateTime;
        await conn.ExecuteAsync(new CommandDefinition(
            """
            UPDATE contact_companies SET
                adsolut_contact_id    = @AdsolutId,
                adsolut_last_modified = @AdsolutLastModified,
                adsolut_active        = TRUE,
                adsolut_synced_hash   = @Hash,
                updated_utc           = COALESCE(@AdsolutLastModified, now())
            WHERE id = @Id
            """,
            new
            {
                Id = candidate.LinkId,
                AdsolutId = createResult.Id,
                AdsolutLastModified = createdLastModified,
                Hash = hash,
            },
            cancellationToken: ct));
        return AdsolutContactPushOutcome.Created;
    }
}
