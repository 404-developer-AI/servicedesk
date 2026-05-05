namespace Servicedesk.Infrastructure.Integrations.Adsolut;

/// One Adsolut customer-contact (or supplier-contact — same shape under
/// /suppliers/{id}/contacts). Adsolut models one row per work-relationship,
/// so the same email can appear three times across three customers as
/// three different UUIDs with their own <see cref="LastModified"/> and
/// <see cref="Active"/> values. SD bundles those into one persons-row +
/// three <c>contact_companies</c> links — see <c>AdsolutContactUpserter</c>.
///
/// Only the v0.0.28 mirrored fields are populated; everything else from
/// the API (fax, memo, address, country, role-flags, dateOfBirth, …)
/// stays in Adsolut and is intentionally not surfaced here.
public sealed record AdsolutContact(
    Guid Id,
    string FirstName,
    string LastName,
    string Email,
    string Phone,
    string MobilePhone,
    bool Active,
    DateTimeOffset? LastModified);

/// Read-only client for Adsolut customer-contacts (and supplier-contacts).
/// Per Adsolut docs the contacts sub-resource does NOT support
/// <c>?ModifiedSince=</c> or <c>?Limit=/?Page=</c> — every call returns
/// the complete contact-set for the customer. The sync worker keeps the
/// load down by only calling this for customers whose own
/// <c>customer.lastModified</c> advanced this tick.
public interface IAdsolutContactsClient
{
    Task<IReadOnlyList<AdsolutContact>> ListCustomerContactsAsync(
        Guid administrationId,
        Guid customerId,
        CancellationToken ct = default);

    Task<IReadOnlyList<AdsolutContact>> ListSupplierContactsAsync(
        Guid administrationId,
        Guid supplierId,
        CancellationToken ct = default);
}
