using Servicedesk.Domain.Companies;
using Servicedesk.Infrastructure.Persistence.Taxonomy;

namespace Servicedesk.Infrastructure.Persistence.Companies;

public interface ICompanyRepository
{
    Task<IReadOnlyList<Company>> ListCompaniesAsync(string? search, CancellationToken ct);
    Task<Company?> GetCompanyAsync(Guid id, CancellationToken ct);
    Task<Company> CreateCompanyAsync(Company c, CancellationToken ct);
    Task<Company?> UpdateCompanyAsync(Guid id, Company patch, CancellationToken ct);
    Task<DeleteResult> DeleteCompanyAsync(Guid id, CancellationToken ct);

    Task<IReadOnlyList<CompanyDomain>> ListDomainsAsync(Guid companyId, CancellationToken ct);
    Task<CompanyDomain?> AddDomainAsync(Guid companyId, string domain, CancellationToken ct);
    Task<bool> RemoveDomainAsync(Guid domainId, CancellationToken ct);
    Task<Company?> FindCompanyByDomainAsync(string domain, CancellationToken ct);

    Task<IReadOnlyList<Contact>> ListContactsAsync(Guid? companyId, string? search, CancellationToken ct);
    Task<Contact?> GetContactAsync(Guid id, CancellationToken ct);
    Task<Contact?> GetContactByEmailAsync(string email, CancellationToken ct);
    Task<Contact> CreateContactAsync(Contact c, CancellationToken ct);
    Task<Contact?> UpdateContactAsync(Guid id, Contact patch, CancellationToken ct);
    Task<DeleteResult> DeleteContactAsync(Guid id, CancellationToken ct);
}
