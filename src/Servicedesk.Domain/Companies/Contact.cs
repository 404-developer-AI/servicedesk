namespace Servicedesk.Domain.Companies;

/// The persistent contact row. <see cref="PrimaryCompanyId"/> is a read-only
/// denormalization populated by repository SELECTs via a correlated sub-query
/// against <c>contact_companies</c>; INSERT/UPDATE paths ignore the field.
/// This keeps the join-table as the single source of truth while letting
/// read-heavy consumers (ticket side-panel, contact picker, etc.) see the
/// primary company id without an extra round-trip.
public sealed record Contact(
    Guid Id,
    string CompanyRole,
    string FirstName,
    string LastName,
    string Email,
    string Phone,
    string MobilePhone,
    string JobTitle,
    bool IsActive,
    DateTime CreatedUtc,
    DateTime UpdatedUtc,
    Guid? PrimaryCompanyId = null);

public sealed record ContactCompanyLink(
    Guid Id,
    Guid ContactId,
    Guid CompanyId,
    string Role,
    DateTime CreatedUtc,
    DateTime UpdatedUtc,
    /// v0.1.0 — customer-portal role for this link (Member | TicketManager);
    /// null reads as Member on primary/secondary links, no access on supplier.
    string? PortalRole = null);

public sealed record ContactCompanyOption(
    Guid LinkId,
    Guid CompanyId,
    string CompanyName,
    string CompanyCode,
    string CompanyShortName,
    bool CompanyIsActive,
    string Role,
    string? PortalRole = null);

/// Row shape used by the dedicated `/contacts` overview page. Extends the
/// base contact with denormalized primary-company metadata, a count of
/// extra links (secondary + supplier), and the timestamp of the contact's
/// most recent ticket activity so the overview can rank contacts without
/// a per-row round-trip.
public sealed record ContactListItem(
    Guid Id,
    string CompanyRole,
    string FirstName,
    string LastName,
    string Email,
    string Phone,
    string MobilePhone,
    string JobTitle,
    bool IsActive,
    DateTime CreatedUtc,
    DateTime UpdatedUtc,
    Guid? PrimaryCompanyId,
    string? PrimaryCompanyName,
    string? PrimaryCompanyCode,
    string? PrimaryCompanyShortName,
    bool PrimaryCompanyIsActive,
    int ExtraLinkCount,
    DateTime? LastTicketUpdatedUtc);

public sealed record ContactOverviewPage(
    IReadOnlyList<ContactListItem> Items,
    int Total,
    int Page,
    int PageSize);

/// Row shape returned by the call-popup's phone-keyed lookup. Adds the
/// "best" company link (primary → secondary → supplier priority) to the
/// base contact so the popup can render the linked company name without
/// a second round-trip. All three link fields are null when the contact
/// has no links at all. Kept separate from <see cref="Contact"/> so the
/// many other Contact consumers (picker, edit form, list) keep their
/// flat shape.
public sealed record ContactPhoneLookupRow(
    Guid Id,
    string CompanyRole,
    string FirstName,
    string LastName,
    string Email,
    string Phone,
    string MobilePhone,
    string JobTitle,
    bool IsActive,
    DateTime CreatedUtc,
    DateTime UpdatedUtc,
    Guid? PrimaryCompanyId,
    Guid? LinkedCompanyId,
    string? LinkedCompanyName,
    string? LinkedCompanyRole);
