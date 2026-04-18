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

    /// Runs the 6-step company-resolution decision tree for a brand-new ticket
    /// whose requester is <paramref name="contactId"/>. The caller skips this
    /// entirely when the mail threads into an existing ticket — that ticket
    /// keeps its own company_id.
    Task<CompanyResolution> ResolveCompanyForNewTicketAsync(Guid contactId, CancellationToken ct);
}

/// Outcome of the decision tree. <see cref="CompanyId"/> is NULL when we
/// cannot pick a single company; in that case <see cref="Awaiting"/> is true
/// so the UI prompts an agent to assign manually (ToDo #4). When no links
/// exist at all we leave everything NULL and don't flag awaiting — this is
/// the "personal sender" state, not an ambiguity we can resolve.
public sealed record CompanyResolution(Guid? CompanyId, string? ResolvedVia, bool Awaiting)
{
    public static CompanyResolution None { get; } = new(null, null, false);
}
