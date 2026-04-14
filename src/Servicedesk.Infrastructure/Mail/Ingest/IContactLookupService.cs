using Servicedesk.Domain.Companies;

namespace Servicedesk.Infrastructure.Mail.Ingest;

/// Resolves (or auto-creates) a contact for an inbound mail From address.
/// Mail ingest cannot reject a message just because the sender isn't in the
/// CRM yet — a customer emailing for the first time must still land as a
/// ticket. The auto-created contact has company_id=null until an admin
/// attaches it via the UI (or a future company-domain matcher does).
public interface IContactLookupService
{
    Task<Contact> EnsureByEmailAsync(string email, string displayName, CancellationToken ct);
}
