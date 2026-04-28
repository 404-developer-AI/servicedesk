namespace Servicedesk.Domain.Companies;

public sealed record Company(
    Guid Id,
    string Name,
    string Description,
    string Website,
    string Phone,
    string AddressLine1,
    string AddressLine2,
    string City,
    string PostalCode,
    string Country,
    bool IsActive,
    DateTime CreatedUtc,
    DateTime UpdatedUtc,
    string Code,
    string ShortName,
    string VatNumber,
    string AlertText,
    bool AlertOnCreate,
    bool AlertOnOpen,
    string AlertOnOpenMode,
    string Email,
    Guid? AdsolutId = null,
    DateTime? AdsolutLastModified = null);

public sealed record CompanyDomain(
    Guid Id,
    Guid CompanyId,
    string Domain,
    DateTime CreatedUtc);
