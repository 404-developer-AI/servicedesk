namespace Servicedesk.Infrastructure.Integrations.Adsolut;

/// One contact row written to the Adsolut Accounting API: either a
/// brand-new contact under a customer (POST /customers/{customer}/contacts)
/// or an update against an existing one
/// (PUT /customers/{customer}/contacts/{contact}). Mirrors the inbound
/// <see cref="AdsolutContact"/> shape but only carries the four fields the
/// v0.0.29 push-tak manages — every other field on Adsolut's
/// <c>UpdateCustomerContactRequest</c> stays exclusive to Adsolut and is
/// preserved verbatim from the read-back GET via the read-modify-write path.
///
/// <c>Email</c> is the SD-side match-key, never overwritten after the
/// initial pull-match. We still send it on PUT (as it appears in the GET
/// payload) — the overlay path only writes the four mirror fields and
/// leaves email as-is, so a typo on either side does not silently rewrite
/// the upstream value.
public sealed record AdsolutContactWritePayload(
    string FirstName,
    string LastName,
    string Phone,
    string MobilePhone,
    string Email);

/// Outcome of a single contact-write call. POST returns
/// <c>AddEntityWithValidationResponse</c> (id + validationResults, no
/// lastModified); PUT returns <c>ValidationResponse</c> (validationResults
/// only, no id, no lastModified). Both paths therefore fall back to a
/// read-back GET to obtain the freshly stamped <c>lastModified</c> the
/// pusher needs to anchor the link's <c>adsolut_last_modified</c>.
public sealed record AdsolutContactWriteResult(
    Guid Id,
    DateTimeOffset? LastModified);

/// Write-side counterpart to <see cref="IAdsolutContactsClient"/>. Exposes
/// only the calls the v0.0.29 push-tak needs: create a new customer-contact
/// under an Adsolut customer, and update an existing one. Suppliers are
/// intentionally out of scope for v0.0.29 — same gating discipline as the
/// v0.0.27/28 customer push-tak.
public interface IAdsolutContactsWriteClient
{
    Task<AdsolutContactWriteResult> CreateCustomerContactAsync(
        Guid administrationId,
        Guid customerId,
        AdsolutContactWritePayload payload,
        CancellationToken ct = default);

    Task<AdsolutContactWriteResult> UpdateCustomerContactAsync(
        Guid administrationId,
        Guid customerId,
        Guid contactId,
        AdsolutContactWritePayload payload,
        CancellationToken ct = default);
}
