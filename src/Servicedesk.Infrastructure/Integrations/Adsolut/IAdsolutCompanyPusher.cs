namespace Servicedesk.Infrastructure.Integrations.Adsolut;

/// One company row read from the local database for the push-tak. Fields
/// match the canonical hash-input shape (see <see cref="AdsolutCompanyHash"/>)
/// plus the bookkeeping the pusher needs: row id, link id, last-pulled
/// timestamp, last-synced hash. Lifted out of <see cref="AdsolutCompanyPusher"/>
/// so the pure decision function can be unit-tested without a Postgres
/// connection.
public sealed class AdsolutCompanyPushCandidate
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Code { get; set; }
    public string Email { get; set; } = string.Empty;
    public string Phone { get; set; } = string.Empty;
    public string AddressLine1 { get; set; } = string.Empty;
    public string AddressLine2 { get; set; } = string.Empty;
    public string PostalCode { get; set; } = string.Empty;
    public string City { get; set; } = string.Empty;
    public string Country { get; set; } = string.Empty;
    public string VatNumber { get; set; } = string.Empty;
    public Guid? AdsolutId { get; set; }
    public string? AdsolutNumber { get; set; }
    public string? AdsolutAlphaCode { get; set; }
    public DateTime? AdsolutLastModified { get; set; }
    public byte[]? AdsolutSyncedHash { get; set; }
    public DateTime UpdatedUtc { get; set; }
}

/// Toggles + capacity for one push-tick. Snapshot at the start of a tick
/// so a settings-edit mid-tick can't change behaviour for the rows already
/// partially processed. Mirrors <see cref="AdsolutSyncOptions"/> but for
/// the outbound direction.
public sealed record AdsolutPushOptions(
    bool PushUpdateEnabled,
    bool PushCreateEnabled);

/// Outcome of a single push-attempt against one local company row.
public enum AdsolutPushOutcome
{
    /// Linked row was PUT to /customers/{id}.
    Updated,
    /// Unlinked row was POSTed to /customers and the response id was
    /// persisted on the local row.
    Created,
    /// Linked row hashed identically to the last-synced hash — no PUT
    /// fired (closes the echo-pull loop on the push side).
    SkippedNoChange,
    /// Update-toggle is OFF and the row needs an update.
    SkippedUpdateToggleOff,
    /// Create-toggle is OFF and the row would be a new POST.
    SkippedCreateToggleOff,
    /// Linked row's local <c>updated_utc</c> is not strictly newer than
    /// the last pulled <c>adsolut_last_modified</c>, so there is nothing
    /// to push (the local row is already in sync per timestamp).
    SkippedNoLocalChange,
    /// v0.0.27 — linked row was pulled before the <c>adsolut_number</c>
    /// column existed and has not been re-pulled since. Pushing without
    /// the klantnummer would trigger an `UpdateCustomerNumberNotValid`
    /// rejection; we skip until the next pull-tick populates the field.
    SkippedMissingAdsolutNumber,
}

/// Pure decision used by the push-tak: should we PUT, POST, or skip this
/// row? Lifted out of the SQL path so the toggle-respect + drift-detection
/// + hash-no-op interplay is unit-testable.
public sealed record AdsolutPushDecision(AdsolutPushOutcome Outcome);

/// Outbound counterpart to <see cref="IAdsolutCompanyUpserter"/>. One method
/// processes a single candidate: build the canonical hash, run the decision,
/// call the write-client, persist the result on the local row.
public interface IAdsolutCompanyPusher
{
    Task<AdsolutPushOutcome> PushOneAsync(
        Guid administrationId,
        AdsolutCompanyPushCandidate candidate,
        AdsolutPushOptions options,
        CancellationToken ct = default);

    /// Loads the candidate set from <c>companies</c>. Linked rows where
    /// <c>updated_utc</c> &gt; <c>adsolut_last_modified</c> qualify for
    /// update; unlinked rows qualify for create. The caller filters on
    /// the toggles before calling <see cref="PushOneAsync"/>.
    Task<IReadOnlyList<AdsolutCompanyPushCandidate>> LoadCandidatesAsync(
        AdsolutPushOptions options,
        int limit,
        CancellationToken ct = default);
}
