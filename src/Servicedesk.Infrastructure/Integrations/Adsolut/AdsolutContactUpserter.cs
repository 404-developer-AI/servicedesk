using Dapper;
using Npgsql;
using Servicedesk.Infrastructure.Phones;

namespace Servicedesk.Infrastructure.Integrations.Adsolut;

/// One row from <c>contact_companies</c>, the minimum set of columns the
/// upsert decision-logic needs from a candidate match. Shape mirrors the
/// SQL projection in <see cref="AdsolutContactUpserter"/>.
public sealed class AdsolutContactLinkRow
{
    public Guid Id { get; set; }
    public Guid ContactId { get; set; }
    public Guid CompanyId { get; set; }
    public Guid? AdsolutContactId { get; set; }
    public DateTime? AdsolutLastModified { get; set; }
    public bool? AdsolutActive { get; set; }
    public byte[]? AdsolutSyncedHash { get; set; }
    public string Role { get; set; } = string.Empty;
}

/// Companion row for the contact lookup-by-email path. Sealed class +
/// settable properties because Dapper can't materialise a positional
/// record with snake_case→PascalCase aliases reliably here (matches the
/// `feedback_dapper_record_struct_null_bug` memory).
public sealed class AdsolutContactByEmailRow
{
    public Guid Id { get; set; }
    public DateTime UpdatedUtc { get; set; }
}

public sealed class AdsolutContactUpserter : IAdsolutContactUpserter
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly IContactPhoneNormalizer _phoneNormalizer;

    public AdsolutContactUpserter(NpgsqlDataSource dataSource, IContactPhoneNormalizer phoneNormalizer)
    {
        _dataSource = dataSource;
        _phoneNormalizer = phoneNormalizer;
    }

    public async Task<AdsolutContactUpsertOutcome> UpsertAsync(
        Guid companyId,
        AdsolutContact contact,
        AdsolutContactsSyncOptions options,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(contact.Email))
        {
            return AdsolutContactUpsertOutcome.SkippedNoEmail;
        }

        var inboundHash = AdsolutContactHash.Compute(new AdsolutContactHashInput(
            FirstName: contact.FirstName,
            LastName: contact.LastName,
            Phone: contact.Phone,
            MobilePhone: contact.MobilePhone));
        var inboundLastMod = contact.LastModified?.UtcDateTime;

        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        // Step 1 — link already linked by adsolut_contact_id (fast path).
        var existingLink = await conn.QueryFirstOrDefaultAsync<AdsolutContactLinkRow>(new CommandDefinition(
            """
            SELECT id                    AS Id,
                   contact_id            AS ContactId,
                   company_id            AS CompanyId,
                   adsolut_contact_id    AS AdsolutContactId,
                   adsolut_last_modified AS AdsolutLastModified,
                   adsolut_active        AS AdsolutActive,
                   adsolut_synced_hash   AS AdsolutSyncedHash,
                   role                  AS Role
            FROM contact_companies
            WHERE adsolut_contact_id = @AdsolutId
            LIMIT 1
            """,
            new { AdsolutId = contact.Id },
            transaction: tx,
            cancellationToken: ct));

        if (existingLink is not null && existingLink.CompanyId != companyId)
        {
            // Same Adsolut UUID claimed by a different SD company — should
            // not happen for legitimate Adsolut data (each row is bound to
            // exactly one customer upstream). Skip; the partial-unique
            // index would also reject any attempt to re-anchor the UUID
            // anyway. Reconcile-loop will eventually heal the state.
            await tx.RollbackAsync(ct);
            return AdsolutContactUpsertOutcome.SkippedLinkCompanyMismatch;
        }

        // Step 2 — find or determine the contact row.
        Guid? contactId = existingLink?.ContactId;
        DateTime? contactUpdatedUtc = null;

        if (contactId is null)
        {
            // No UUID match — try to find the person by email. email is CITEXT,
            // but the bare `text` parameter would resolve to a case-sensitive
            // comparison; cast to citext so the match folds case at the DB level.
            var byEmail = await conn.QueryFirstOrDefaultAsync<AdsolutContactByEmailRow>(new CommandDefinition(
                """
                SELECT id           AS Id,
                       updated_utc  AS UpdatedUtc
                FROM contacts
                WHERE email = @Email::citext
                LIMIT 1
                """,
                new { Email = contact.Email },
                transaction: tx,
                cancellationToken: ct));
            if (byEmail is not null)
            {
                contactId = byEmail.Id;
                contactUpdatedUtc = byEmail.UpdatedUtc;
            }
        }
        else
        {
            contactUpdatedUtc = await conn.QueryFirstOrDefaultAsync<DateTime?>(new CommandDefinition(
                "SELECT updated_utc FROM contacts WHERE id = @Id LIMIT 1",
                new { Id = contactId.Value },
                transaction: tx,
                cancellationToken: ct));
        }

        // Step 3 — upgrade case: contact exists, but link to (contact, company)
        // doesn't yet have a UUID. We'll claim it on this pass.
        if (existingLink is null && contactId is not null)
        {
            existingLink = await conn.QueryFirstOrDefaultAsync<AdsolutContactLinkRow>(new CommandDefinition(
                """
                SELECT id                    AS Id,
                       contact_id            AS ContactId,
                       company_id            AS CompanyId,
                       adsolut_contact_id    AS AdsolutContactId,
                       adsolut_last_modified AS AdsolutLastModified,
                       adsolut_active        AS AdsolutActive,
                       adsolut_synced_hash   AS AdsolutSyncedHash,
                       role                  AS Role
                FROM contact_companies
                WHERE contact_id = @ContactId AND company_id = @CompanyId
                LIMIT 1
                """,
                new { ContactId = contactId.Value, CompanyId = companyId },
                transaction: tx,
                cancellationToken: ct));
        }

        var hasContact = contactId.HasValue;
        var hasLink = existingLink is not null;
        var isCreate = !hasLink;

        // Toggle-gating: a missing link is an "add" (gated by Create); a
        // present link is an "edit" (gated by Update). Contact-row
        // creation rides on Create too — if we'd insert a fresh person
        // and the toggle is off, bail before any writes.
        if (isCreate && !options.PullCreateEnabled)
        {
            await tx.RollbackAsync(ct);
            return AdsolutContactUpsertOutcome.SkippedCreateToggleOff;
        }
        if (!isCreate && !options.PullUpdateEnabled)
        {
            await tx.RollbackAsync(ct);
            return AdsolutContactUpsertOutcome.SkippedUpdateToggleOff;
        }

        // No-op guard (echo-pull defence). Only fires on a fully-Adsolut-
        // linked existing link (UUID already present + hash already stored).
        // Active in the comparison because it lives outside the hash —
        // a true↔false flip without value change must still flow through.
        if (existingLink is { AdsolutContactId: not null, AdsolutSyncedHash: { } stored } el &&
            ByteEquals(stored, inboundHash) &&
            (el.AdsolutActive ?? false) == contact.Active)
        {
            await tx.RollbackAsync(ct);
            return AdsolutContactUpsertOutcome.SkippedNoChange;
        }

        // SD-wins tie-breaker for contact-level fields. Per-link state
        // (UUID, active, lastModified, hash) is always synced regardless
        // — that's Adsolut's mirror, not ours.
        var sdContactWins = hasContact
            && contactUpdatedUtc.HasValue
            && inboundLastMod.HasValue
            && contactUpdatedUtc.Value > inboundLastMod.Value;

        Guid finalContactId;
        if (!hasContact)
        {
            // Fresh contact + fresh link. Default company_role 'Member' —
            // the Customer/TicketManager distinction is portal-only and a
            // sync-imported contact starts as a regular member.
            //
            // ON CONFLICT (email) DO UPDATE SET updated_utc = contacts.updated_utc
            // is a deliberate no-op-write that exists ONLY so RETURNING
            // always fires — both on the happy "row inserted" path and on
            // the race-loser "row already exists" path. We tried the
            // DO-NOTHING + fallback-SELECT pattern first; it crashed in
            // production with "Sequence contains no elements" because the
            // SELECT after a soft-conflict couldn't always see the
            // conflicting row in the same transaction. Forcing a no-op
            // UPDATE in the conflict branch makes RETURNING the single
            // source of truth — no fallback path required.
            var (insertPhoneE164, insertMobileE164) = await _phoneNormalizer.NormalizePairAsync(
                contact.Phone, contact.MobilePhone, ct);
            finalContactId = await conn.QuerySingleAsync<Guid>(new CommandDefinition(
                """
                INSERT INTO contacts (
                    company_role, first_name, last_name, email,
                    phone, mobile_phone, phone_e164, mobile_phone_e164,
                    is_active, updated_utc
                )
                VALUES (
                    'Member', @FirstName, @LastName, @Email,
                    @Phone, @MobilePhone, @PhoneE164, @MobilePhoneE164,
                    @IsActive, COALESCE(@LastModified, now())
                )
                ON CONFLICT (email) DO UPDATE SET
                    updated_utc = contacts.updated_utc
                RETURNING id
                """,
                new
                {
                    FirstName = contact.FirstName ?? string.Empty,
                    LastName = contact.LastName ?? string.Empty,
                    Email = contact.Email,
                    Phone = contact.Phone ?? string.Empty,
                    MobilePhone = contact.MobilePhone ?? string.Empty,
                    PhoneE164 = insertPhoneE164,
                    MobilePhoneE164 = insertMobileE164,
                    IsActive = contact.Active,
                    LastModified = inboundLastMod,
                },
                transaction: tx,
                cancellationToken: ct));
        }
        else
        {
            finalContactId = contactId!.Value;

            // Update contact-level fields only when this inbound row is
            // the freshest known Adsolut state AND SD didn't beat it.
            if (!sdContactWins)
            {
                var maxOther = await conn.QueryFirstOrDefaultAsync<DateTime?>(new CommandDefinition(
                    """
                    SELECT MAX(adsolut_last_modified)
                    FROM contact_companies
                    WHERE contact_id = @ContactId
                      AND adsolut_contact_id IS NOT NULL
                      AND adsolut_contact_id <> @CurrentUuid
                    """,
                    new { ContactId = finalContactId, CurrentUuid = contact.Id },
                    transaction: tx,
                    cancellationToken: ct));

                var inboundIsFreshest =
                    (inboundLastMod ?? DateTime.MinValue) >= (maxOther ?? DateTime.MinValue);

                if (inboundIsFreshest)
                {
                    var (updPhoneE164, updMobileE164) = await _phoneNormalizer.NormalizePairAsync(
                        contact.Phone, contact.MobilePhone, ct);
                    await conn.ExecuteAsync(new CommandDefinition(
                        """
                        UPDATE contacts SET
                            first_name        = @FirstName,
                            last_name         = @LastName,
                            phone             = @Phone,
                            mobile_phone      = @MobilePhone,
                            phone_e164        = @PhoneE164,
                            mobile_phone_e164 = @MobilePhoneE164,
                            updated_utc       = COALESCE(@LastModified, now())
                        WHERE id = @Id
                        """,
                        new
                        {
                            Id = finalContactId,
                            FirstName = contact.FirstName ?? string.Empty,
                            LastName = contact.LastName ?? string.Empty,
                            Phone = contact.Phone ?? string.Empty,
                            MobilePhone = contact.MobilePhone ?? string.Empty,
                            PhoneE164 = updPhoneE164,
                            MobilePhoneE164 = updMobileE164,
                            LastModified = inboundLastMod,
                        },
                        transaction: tx,
                        cancellationToken: ct));
                }
            }
        }

        // Upsert the link itself.
        if (hasLink)
        {
            await conn.ExecuteAsync(new CommandDefinition(
                """
                UPDATE contact_companies SET
                    adsolut_contact_id    = @AdsolutId,
                    adsolut_last_modified = @LastModified,
                    adsolut_active        = @Active,
                    adsolut_synced_hash   = @Hash,
                    updated_utc           = now()
                WHERE id = @Id
                """,
                new
                {
                    Id = existingLink!.Id,
                    AdsolutId = contact.Id,
                    LastModified = inboundLastMod,
                    Active = contact.Active,
                    Hash = inboundHash,
                },
                transaction: tx,
                cancellationToken: ct));
        }
        else
        {
            // First link for this contact → 'primary'; subsequent → 'secondary'.
            // Respects the partial-unique on (contact_id WHERE role='primary').
            var hasAnyLink = await conn.QueryFirstOrDefaultAsync<int?>(new CommandDefinition(
                "SELECT 1 FROM contact_companies WHERE contact_id = @ContactId LIMIT 1",
                new { ContactId = finalContactId },
                transaction: tx,
                cancellationToken: ct));
            var role = hasAnyLink.HasValue ? "secondary" : "primary";

            // ON CONFLICT (contact_id, company_id) absorbs a concurrent
            // worker that just inserted the same link — DO UPDATE writes
            // the inbound Adsolut-state onto the existing row so both
            // sides of the race converge instead of one crashing on the
            // pair-unique. role is intentionally left out of DO UPDATE
            // so an admin's manual promotion to 'primary' isn't reverted
            // by a sync tick that thought it was inserting a new link.
            await conn.ExecuteAsync(new CommandDefinition(
                """
                INSERT INTO contact_companies (
                    contact_id, company_id, role,
                    adsolut_contact_id, adsolut_last_modified, adsolut_active, adsolut_synced_hash
                )
                VALUES (
                    @ContactId, @CompanyId, @Role,
                    @AdsolutId, @LastModified, @Active, @Hash
                )
                ON CONFLICT (contact_id, company_id) DO UPDATE SET
                    adsolut_contact_id    = EXCLUDED.adsolut_contact_id,
                    adsolut_last_modified = EXCLUDED.adsolut_last_modified,
                    adsolut_active        = EXCLUDED.adsolut_active,
                    adsolut_synced_hash   = EXCLUDED.adsolut_synced_hash,
                    updated_utc           = now()
                """,
                new
                {
                    ContactId = finalContactId,
                    CompanyId = companyId,
                    Role = role,
                    AdsolutId = contact.Id,
                    LastModified = inboundLastMod,
                    Active = contact.Active,
                    Hash = inboundHash,
                },
                transaction: tx,
                cancellationToken: ct));
        }

        // Recompute contacts.is_active from the aggregate of links. TRUE
        // when no link has ever been Adsolut-aware (pure SD contact) OR
        // ≥1 Adsolut-aware link is still active. We discriminate "ever
        // was Adsolut" via `adsolut_active IS NOT NULL` rather than
        // `adsolut_contact_id IS NOT NULL` because the hard-delete path
        // nulls the UUID but keeps the false-flag — a link that lost its
        // UUID still proves the contact was Adsolut-aware once, so
        // pure-SD-fallback shouldn't kick in for it.
        await conn.ExecuteAsync(new CommandDefinition(
            """
            UPDATE contacts SET is_active = (
                NOT EXISTS (
                    SELECT 1 FROM contact_companies
                    WHERE contact_id = @Id AND adsolut_active IS NOT NULL
                )
                OR EXISTS (
                    SELECT 1 FROM contact_companies
                    WHERE contact_id = @Id AND adsolut_active = TRUE
                )
            )
            WHERE id = @Id
            """,
            new { Id = finalContactId },
            transaction: tx,
            cancellationToken: ct));

        await tx.CommitAsync(ct);

        if (sdContactWins && !isCreate)
        {
            // Per-link state was synced; contact-level fields preserved.
            // Surface as SkippedLocalNewer so the operational log shows
            // honest LWW behaviour even though we did write to the link.
            return AdsolutContactUpsertOutcome.SkippedLocalNewer;
        }
        return isCreate
            ? AdsolutContactUpsertOutcome.Created
            : AdsolutContactUpsertOutcome.Updated;
    }

    public async Task<int> ReconcileMissingLinksAsync(
        Guid companyId,
        IReadOnlyCollection<Guid> presentAdsolutContactIds,
        CancellationToken ct = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        // Snapshot all Adsolut-linked links for this company before mutating.
        // ANY treats an empty array as "no exclusions" — a fresh resync
        // returning zero contacts would correctly flag every link as missing.
        var present = presentAdsolutContactIds.ToArray();
        var affected = await conn.QueryAsync<Guid>(new CommandDefinition(
            """
            SELECT contact_id
            FROM contact_companies
            WHERE company_id = @CompanyId
              AND adsolut_contact_id IS NOT NULL
              AND NOT (adsolut_contact_id = ANY(@Present))
            """,
            new { CompanyId = companyId, Present = present },
            transaction: tx,
            cancellationToken: ct));
        var affectedList = affected.AsList();

        // Per the locked spec: hard-delete catch-up clears the UUID and
        // flags adsolut_active=false. The link row itself is not deleted
        // (ticket history references contacts; the contact row stays).
        await conn.ExecuteAsync(new CommandDefinition(
            """
            UPDATE contact_companies SET
                adsolut_active        = FALSE,
                adsolut_contact_id    = NULL,
                adsolut_last_modified = COALESCE(adsolut_last_modified, now()),
                updated_utc           = now()
            WHERE company_id = @CompanyId
              AND adsolut_contact_id IS NOT NULL
              AND NOT (adsolut_contact_id = ANY(@Present))
            """,
            new { CompanyId = companyId, Present = present },
            transaction: tx,
            cancellationToken: ct));

        // Recompute is_active for every affected contact. Discrimination
        // matches the upserter (`adsolut_active IS NOT NULL` for "ever
        // was Adsolut") so a hard-delete of the only Adsolut link
        // correctly flips the contact inactive instead of falling back
        // to the pure-SD-default-TRUE branch.
        if (affectedList.Count > 0)
        {
            await conn.ExecuteAsync(new CommandDefinition(
                """
                UPDATE contacts SET is_active = (
                    NOT EXISTS (
                        SELECT 1 FROM contact_companies
                        WHERE contact_id = contacts.id AND adsolut_active IS NOT NULL
                    )
                    OR EXISTS (
                        SELECT 1 FROM contact_companies
                        WHERE contact_id = contacts.id AND adsolut_active = TRUE
                    )
                )
                WHERE id = ANY(@Ids)
                """,
                new { Ids = affectedList.Distinct().ToArray() },
                transaction: tx,
                cancellationToken: ct));
        }

        await tx.CommitAsync(ct);
        return affectedList.Count;
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
}
