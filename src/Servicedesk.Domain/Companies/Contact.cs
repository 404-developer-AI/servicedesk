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
    DateTime UpdatedUtc);

public sealed record ContactCompanyOption(
    Guid LinkId,
    Guid CompanyId,
    string CompanyName,
    string CompanyCode,
    string CompanyShortName,
    bool CompanyIsActive,
    string Role);
