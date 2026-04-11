namespace Servicedesk.Domain.Companies;

/// Role of a contact within their linked company. Drives the portal
/// visibility scope: <see cref="Member"/> sees only their own tickets,
/// <see cref="TicketManager"/> sees everything for the company.
public enum CompanyRole
{
    Member = 0,
    TicketManager = 1,
}
