namespace Servicedesk.Domain.Companies;

public sealed record Contact(
    Guid Id,
    Guid? CompanyId,
    string CompanyRole,
    string FirstName,
    string LastName,
    string Email,
    string Phone,
    string JobTitle,
    bool IsActive,
    DateTime CreatedUtc,
    DateTime UpdatedUtc);
