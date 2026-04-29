namespace Servicedesk.Infrastructure.Integrations.Adsolut;

/// One Adsolut customer (debiteur) row. The shape mirrors the API but only
/// the fields the v0.0.26 sync worker writes back to <c>companies</c> are
/// populated; everything else stays available on the parsed JSON for a
/// future enrichment pass without forcing a schema change here. Empty
/// strings rather than nulls keep the upsert SQL straightforward (the SD
/// <c>companies</c> table already uses NOT NULL DEFAULT '' for these
/// address-style fields).
public sealed record AdsolutCustomer(
    Guid Id,
    string Name,
    string? Code,
    string? AlphaCode,
    string? Number,
    string Email,
    string Phone,
    string MobilePhone,
    string AddressLine1,
    string AddressLine2,
    string PostalCode,
    string City,
    string Country,
    string VatNumber,
    string CountryPrefixVatNumber,
    DateTimeOffset? LastModified);

/// One page of paged-result data + the pagination metadata. The sync
/// worker walks pages until <c>currentPage == totalPages</c>; the
/// shape is shared with future endpoints that follow the same paging
/// convention (Suppliers, Contacts, …).
public sealed record AdsolutPagedResult<T>(
    int CurrentPage,
    int TotalItems,
    int TotalPages,
    IReadOnlyList<T> Items);

/// Read-only client for Adsolut Accounting Customers (and Suppliers — the
/// suppliers list-method shares the same shape, so we register one client
/// and let the worker pick the path). Delta-sync uses
/// <paramref name="modifiedSince"/> + OrderBy=lastModified per Adsolut docs.
public interface IAdsolutCustomersClient
{
    Task<AdsolutPagedResult<AdsolutCustomer>> ListCustomersAsync(
        Guid administrationId,
        DateTimeOffset? modifiedSince,
        int page,
        int limit,
        CancellationToken ct = default);

    Task<AdsolutPagedResult<AdsolutCustomer>> ListSuppliersAsync(
        Guid administrationId,
        DateTimeOffset? modifiedSince,
        int page,
        int limit,
        CancellationToken ct = default);
}
