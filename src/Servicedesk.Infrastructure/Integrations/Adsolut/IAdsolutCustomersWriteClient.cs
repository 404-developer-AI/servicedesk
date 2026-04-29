namespace Servicedesk.Infrastructure.Integrations.Adsolut;

/// One row written to the Adsolut Accounting API: either a brand-new
/// customer (POST /customers) or an update against an existing one
/// (PUT /customers/{id}). Mirrors the inbound <see cref="AdsolutCustomer"/>
/// shape but adds split fields the WK API expects on writes (street vs.
/// number vs. box, country-prefix VAT vs. digit-body) and uses the
/// write-side identifiers (<c>AlphaCode</c>, <c>Number</c>) instead of the
/// read-only <c>code</c> field.
///
/// Identifier round-trip: <c>AlphaCode</c> and <c>Number</c> must be sent
/// back unchanged on PUT — Adsolut treats both as immutable post-creation
/// and rejects an absent <c>number</c> with <c>UpdateCustomerNumberNotValid</c>.
/// Both fields are populated by the v0.0.27 pull-side from the response;
/// rows pulled before the columns existed are skipped on push until the
/// next pull tick fills them in.
///
/// Country: the v0.0.26 pull stored only the country display string, and
/// Adsolut's write-shape expects <c>countryId</c> (UUID) which we don't
/// resolve yet. v0.0.27 push omits country entirely; admins edit country
/// in Adsolut directly until the v0.0.28 supplier branch adds country-
/// reference handling.
public sealed record AdsolutCustomerWritePayload(
    string Name,
    string? AlphaCode,
    string? Number,
    string Email,
    string Phone,
    string StreetName,
    string StreetNumber,
    string BoxNumber,
    string PostalCode,
    string City,
    string VatNumber,
    string CountryPrefixVatNumber);

/// Outcome of a single write call. Both POST and PUT return the persisted
/// row; this struct only carries the fields the push-tak needs back —
/// the canonical id (for new rows), the upstream lastModified so the
/// echo-pull stays a no-op, and the assigned identifiers (<c>AlphaCode</c>,
/// <c>Number</c>) so a created row can immediately round-trip on the next
/// PUT without needing a separate pull-tick to populate them.
public sealed record AdsolutCustomerWriteResult(
    Guid Id,
    DateTimeOffset? LastModified,
    string? AlphaCode,
    string? Number);

/// Write-side counterpart to <see cref="IAdsolutCustomersClient"/>. Exposes
/// only the calls the v0.0.27 push-tak needs: create a new customer in the
/// active dossier, and update an existing one. Suppliers are intentionally
/// out of scope for v0.0.27 — see the supplier toggles in
/// <see cref="Servicedesk.Infrastructure.Settings.SettingKeys.Adsolut"/>.
public interface IAdsolutCustomersWriteClient
{
    Task<AdsolutCustomerWriteResult> CreateCustomerAsync(
        Guid administrationId,
        AdsolutCustomerWritePayload payload,
        CancellationToken ct = default);

    Task<AdsolutCustomerWriteResult> UpdateCustomerAsync(
        Guid administrationId,
        Guid customerId,
        AdsolutCustomerWritePayload payload,
        CancellationToken ct = default);
}
