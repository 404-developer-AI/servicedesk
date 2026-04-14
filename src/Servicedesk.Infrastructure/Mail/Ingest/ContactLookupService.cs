using Servicedesk.Domain.Companies;
using Servicedesk.Infrastructure.Persistence.Companies;

namespace Servicedesk.Infrastructure.Mail.Ingest;

public sealed class ContactLookupService : IContactLookupService
{
    private readonly ICompanyRepository _companies;

    public ContactLookupService(ICompanyRepository companies)
    {
        _companies = companies;
    }

    public async Task<Contact> EnsureByEmailAsync(string email, string displayName, CancellationToken ct)
    {
        var existing = await _companies.GetContactByEmailAsync(email, ct);
        if (existing is not null) return existing;

        var (first, last) = SplitName(displayName, email);
        var now = DateTime.UtcNow;
        var stub = new Contact(
            Id: Guid.Empty,
            CompanyId: null,
            CompanyRole: "Member",
            FirstName: first,
            LastName: last,
            Email: email,
            Phone: string.Empty,
            JobTitle: string.Empty,
            IsActive: true,
            CreatedUtc: now,
            UpdatedUtc: now);
        return await _companies.CreateContactAsync(stub, ct);
    }

    private static (string First, string Last) SplitName(string displayName, string email)
    {
        var name = (displayName ?? string.Empty).Trim();
        if (name.Length == 0)
        {
            // Fall back to the local-part of the email so the contact has
            // *something* readable in the UI.
            var at = email.IndexOf('@');
            var local = at > 0 ? email[..at] : email;
            return (local, string.Empty);
        }

        var space = name.IndexOf(' ');
        return space < 0
            ? (name, string.Empty)
            : (name[..space].Trim(), name[(space + 1)..].Trim());
    }
}
