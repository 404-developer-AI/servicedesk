using Servicedesk.Domain.Companies;
using Servicedesk.Infrastructure.Persistence.Taxonomy;

namespace Servicedesk.Infrastructure.Persistence.Companies;

public interface ICompanyRepository
{
    Task<IReadOnlyList<Company>> ListCompaniesAsync(string? search, bool includeInactive, CancellationToken ct);
    Task<Company?> GetCompanyAsync(Guid id, CancellationToken ct);
    Task<Company?> GetCompanyByCodeAsync(string code, CancellationToken ct);
    /// Returns the company that owns the contact's primary link, or null when
    /// the contact has no primary link (or it points to an inactive company).
    Task<Company?> GetPrimaryCompanyForContactAsync(Guid contactId, CancellationToken ct);
    Task<Company> CreateCompanyAsync(Company c, CancellationToken ct);
    Task<Company?> UpdateCompanyAsync(Guid id, Company patch, CancellationToken ct);
    Task<DeleteResult> SoftDeleteCompanyAsync(Guid id, CancellationToken ct);

    Task<IReadOnlyList<CompanyDomain>> ListDomainsAsync(Guid companyId, CancellationToken ct);
    Task<CompanyDomain?> AddDomainAsync(Guid companyId, string domain, CancellationToken ct);
    Task<bool> RemoveDomainAsync(Guid domainId, CancellationToken ct);
    Task<Company?> FindCompanyByDomainAsync(string domain, CancellationToken ct);

    /// Lists distinct contacts that have at least one link (any role) to the
    /// given company, or — when companyId is null — every contact.
    Task<IReadOnlyList<Contact>> ListContactsAsync(Guid? companyId, string? search, CancellationToken ct);
    /// Paginated + enriched overview for the dedicated `/contacts` page.
    /// <paramref name="role"/> accepts "primary"/"secondary"/"supplier"/"none"
    /// (the "none" branch filters contacts with zero links); null means any.
    /// <paramref name="sort"/> accepts "name_asc", "email_asc",
    /// "last_activity_desc"; any other value falls back to "name_asc".
    Task<ContactOverviewPage> ListContactsOverviewAsync(
        string? search, Guid? companyId, string? role, bool includeInactive,
        string? sort, int page, int pageSize, CancellationToken ct);
    Task<Contact?> GetContactAsync(Guid id, CancellationToken ct);
    Task<Contact?> GetContactByEmailAsync(string email, CancellationToken ct);
    /// Creates a contact and optionally inserts a role-tagged company link in
    /// one call so mail intake and UI-create flows don't have to orchestrate
    /// two round-trips. <paramref name="role"/> is ignored when
    /// <paramref name="companyId"/> is null; otherwise it must be one of
    /// 'primary' / 'secondary' / 'supplier'. When the role is 'primary' and
    /// the contact already has a primary link elsewhere, the caller is
    /// responsible for the demote — this path is only hit on brand-new
    /// contact ids so there is no pre-existing link to demote.
    Task<Contact> CreateContactAsync(Contact c, Guid? companyId, string role, CancellationToken ct);
    Task<Contact?> UpdateContactAsync(Guid id, Contact patch, CancellationToken ct);
    Task<DeleteResult> DeleteContactAsync(Guid id, CancellationToken ct);

    /// All role-tagged company links for one contact (primary + secondary + supplier).
    Task<IReadOnlyList<ContactCompanyLink>> ListContactLinksAsync(Guid contactId, CancellationToken ct);
    /// Joined projection used by the ticket company-assignment dialog: each of
    /// the contact's links annotated with the target company's name/code so the
    /// picker can render role-badge rows without an N+1 fan-out.
    Task<IReadOnlyList<ContactCompanyOption>> ListContactCompanyOptionsAsync(Guid contactId, CancellationToken ct);
    /// All role-tagged contact links for one company (used by the Contacts tab).
    Task<IReadOnlyList<ContactCompanyLink>> ListCompanyLinksAsync(Guid companyId, CancellationToken ct);
    /// Upsert a single link. When <paramref name="role"/> is 'primary' and the
    /// contact already has a different primary link, the previous primary is
    /// demoted to 'secondary' in the same transaction so the invariant
    /// "max one primary per contact" is never violated mid-flight.
    Task<ContactCompanyLink> UpsertContactLinkAsync(Guid contactId, Guid companyId, string role, CancellationToken ct);
    /// Removes one specific (contact, company) pair regardless of role. Returns
    /// true when a row was deleted.
    Task<bool> RemoveContactLinkAsync(Guid contactId, Guid companyId, CancellationToken ct);
    /// Convenience wrapper: set or clear the contact's primary link. When
    /// companyId is null the existing primary row is deleted; otherwise this
    /// delegates to <see cref="UpsertContactLinkAsync"/> with role='primary'.
    Task<bool> SetPrimaryCompanyAsync(Guid contactId, Guid? companyId, CancellationToken ct);
}
