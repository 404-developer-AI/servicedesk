using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Servicedesk.Api.Tests.TestInfrastructure;
using Servicedesk.Domain.Companies;
using Servicedesk.Infrastructure.Mail.Ingest;
using Servicedesk.Infrastructure.Persistence.Companies;
using Servicedesk.Infrastructure.Persistence.Taxonomy;
using Servicedesk.Infrastructure.Settings;
using Xunit;

namespace Servicedesk.Api.Tests;

/// v0.0.9 — verifies that <see cref="ContactLookupService"/> auto-links a
/// contact to a company using the company_domains table when a new mail
/// arrives, without overwriting an existing, different company link.
public sealed class ContactLookupServiceTests
{
    [Fact]
    public async Task New_contact_gets_auto_linked_when_domain_matches_company()
    {
        var acme = NewCompany(name: "Acme BV", code: "ACME001");
        var repo = new FakeCompanies();
        repo.DomainToCompany["acme.com"] = acme;
        var audit = new FakeAuditLogger();
        var svc = Build(repo, audit, autoLinkEnabled: true);

        var created = await svc.EnsureByEmailAsync("jan@acme.com", "Jan Janssen", default);

        Assert.Equal(acme.Id, created.CompanyId);
        Assert.Contains(audit.Events, e => e.EventType == "contact.company.auto_linked");
    }

    [Fact]
    public async Task Existing_contact_without_company_is_backfilled()
    {
        var acme = NewCompany(name: "Acme BV", code: "ACME001");
        var repo = new FakeCompanies();
        repo.DomainToCompany["acme.com"] = acme;
        var existing = new Contact(
            Id: Guid.NewGuid(), CompanyId: null, CompanyRole: "Member",
            FirstName: "Jan", LastName: "Janssen",
            Email: "jan@acme.com", Phone: "", JobTitle: "",
            IsActive: true, CreatedUtc: DateTime.UtcNow, UpdatedUtc: DateTime.UtcNow);
        repo.ContactsByEmail[existing.Email] = existing;
        var audit = new FakeAuditLogger();
        var svc = Build(repo, audit, autoLinkEnabled: true);

        var returned = await svc.EnsureByEmailAsync("jan@acme.com", "Jan Janssen", default);

        Assert.Equal(acme.Id, returned.CompanyId);
        Assert.True(repo.SetCompanyCalls.TryGetValue(existing.Id, out var cid) && cid == acme.Id);
        Assert.Contains(audit.Events, e => e.EventType == "contact.company.auto_linked");
    }

    [Fact]
    public async Task Existing_contact_with_different_company_is_not_overwritten()
    {
        var acme = NewCompany(name: "Acme BV", code: "ACME001");
        var otherCompanyId = Guid.NewGuid();
        var repo = new FakeCompanies();
        repo.DomainToCompany["acme.com"] = acme;
        var existing = new Contact(
            Id: Guid.NewGuid(), CompanyId: otherCompanyId, CompanyRole: "Member",
            FirstName: "Jan", LastName: "Janssen",
            Email: "jan@acme.com", Phone: "", JobTitle: "",
            IsActive: true, CreatedUtc: DateTime.UtcNow, UpdatedUtc: DateTime.UtcNow);
        repo.ContactsByEmail[existing.Email] = existing;
        var audit = new FakeAuditLogger();
        var svc = Build(repo, audit, autoLinkEnabled: true);

        var returned = await svc.EnsureByEmailAsync("jan@acme.com", "Jan Janssen", default);

        Assert.Equal(otherCompanyId, returned.CompanyId);
        Assert.False(repo.SetCompanyCalls.ContainsKey(existing.Id));
        Assert.DoesNotContain(audit.Events, e => e.EventType == "contact.company.auto_linked");
    }

    [Fact]
    public async Task Setting_disabled_skips_auto_link()
    {
        var acme = NewCompany(name: "Acme BV", code: "ACME001");
        var repo = new FakeCompanies();
        repo.DomainToCompany["acme.com"] = acme;
        var audit = new FakeAuditLogger();
        var svc = Build(repo, audit, autoLinkEnabled: false);

        var created = await svc.EnsureByEmailAsync("jan@acme.com", "Jan Janssen", default);

        Assert.Null(created.CompanyId);
        Assert.Empty(audit.Events);
    }

    /// Freemail/public domains must never drive an auto-link: otherwise a single
    /// admin misconfiguring one company's domains with `gmail.com` would bind
    /// every gmail sender to that company. The blacklist short-circuits before
    /// the repo is ever consulted — we assert that by wiring gmail.com to a
    /// company yet still expecting zero link.
    [Fact]
    public async Task Blacklisted_sender_domain_skips_auto_link()
    {
        var acme = NewCompany(name: "Acme BV", code: "ACME001");
        var repo = new FakeCompanies();
        repo.DomainToCompany["gmail.com"] = acme; // intentionally polluted
        var audit = new FakeAuditLogger();
        var svc = Build(repo, audit, autoLinkEnabled: true, domainBlacklist: new[] { "gmail.com" });

        var created = await svc.EnsureByEmailAsync("jan.klant@gmail.com", "Jan Klant", default);

        Assert.Null(created.CompanyId);
        Assert.Empty(repo.FindByDomainCalls);
        Assert.DoesNotContain(audit.Events, e => e.EventType == "contact.company.auto_linked");
    }

    private static Company NewCompany(string name, string code) => new(
        Id: Guid.NewGuid(),
        Name: name,
        Description: "",
        Website: "",
        Phone: "",
        AddressLine1: "",
        AddressLine2: "",
        City: "",
        PostalCode: "",
        Country: "",
        IsActive: true,
        CreatedUtc: DateTime.UtcNow,
        UpdatedUtc: DateTime.UtcNow,
        Code: code,
        ShortName: "",
        VatNumber: "",
        AlertText: "",
        AlertOnCreate: false,
        AlertOnOpen: false,
        AlertOnOpenMode: "session",
        Email: "");

    private static ContactLookupService Build(
        FakeCompanies repo, FakeAuditLogger audit, bool autoLinkEnabled, string[]? domainBlacklist = null)
        => new(repo, new StubSettings(autoLinkEnabled, domainBlacklist), audit, NullLogger<ContactLookupService>.Instance);

    private sealed class StubSettings : ISettingsService
    {
        private readonly bool _autoLinkEnabled;
        private readonly string _blacklistJson;

        public StubSettings(bool autoLinkEnabled, string[]? domainBlacklist)
        {
            _autoLinkEnabled = autoLinkEnabled;
            _blacklistJson = domainBlacklist is null || domainBlacklist.Length == 0
                ? ""
                : JsonSerializer.Serialize(domainBlacklist);
        }

        public Task<T> GetAsync<T>(string key, CancellationToken ct = default)
        {
            if (key == SettingKeys.Mail.AutoLinkCompanyByDomain && typeof(T) == typeof(bool))
                return Task.FromResult((T)(object)_autoLinkEnabled);
            if (key == SettingKeys.Mail.AutoLinkDomainBlacklist && typeof(T) == typeof(string))
                return Task.FromResult((T)(object)_blacklistJson);
            return Task.FromResult(default(T)!);
        }
        public Task SetAsync<T>(string key, T value, string actor, string actorRole, CancellationToken ct = default) => Task.CompletedTask;
        public Task<IReadOnlyList<SettingEntry>> ListAsync(string? category = null, CancellationToken ct = default) => throw new NotImplementedException();
        public Task EnsureDefaultsAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class FakeCompanies : ICompanyRepository
    {
        public Dictionary<string, Company> DomainToCompany { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, Contact> ContactsByEmail { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<Guid, Guid?> SetCompanyCalls { get; } = new();
        public List<string> FindByDomainCalls { get; } = new();

        public Task<Company?> FindCompanyByDomainAsync(string domain, CancellationToken ct)
        {
            FindByDomainCalls.Add(domain);
            return Task.FromResult(DomainToCompany.TryGetValue(domain, out var c) ? c : null);
        }

        public Task<Contact?> GetContactByEmailAsync(string email, CancellationToken ct)
            => Task.FromResult(ContactsByEmail.TryGetValue(email, out var c) ? c : null);

        public Task<Contact> CreateContactAsync(Contact c, CancellationToken ct)
        {
            var withId = c with { Id = c.Id == Guid.Empty ? Guid.NewGuid() : c.Id };
            ContactsByEmail[withId.Email] = withId;
            return Task.FromResult(withId);
        }

        public Task<bool> SetContactCompanyAsync(Guid contactId, Guid? companyId, CancellationToken ct)
        {
            SetCompanyCalls[contactId] = companyId;
            var row = ContactsByEmail.Values.FirstOrDefault(c => c.Id == contactId);
            if (row is not null)
                ContactsByEmail[row.Email] = row with { CompanyId = companyId };
            return Task.FromResult(true);
        }

        // ---- Unused members — throw so accidental reliance fails loudly ----
        public Task<IReadOnlyList<Company>> ListCompaniesAsync(string? search, bool includeInactive, CancellationToken ct) => throw new NotImplementedException();
        public Task<Company?> GetCompanyAsync(Guid id, CancellationToken ct) => throw new NotImplementedException();
        public Task<Company?> GetCompanyByCodeAsync(string code, CancellationToken ct) => throw new NotImplementedException();
        public Task<Company?> GetCompanyForContactAsync(Guid contactId, CancellationToken ct) => throw new NotImplementedException();
        public Task<Company> CreateCompanyAsync(Company c, CancellationToken ct) => throw new NotImplementedException();
        public Task<Company?> UpdateCompanyAsync(Guid id, Company patch, CancellationToken ct) => throw new NotImplementedException();
        public Task<DeleteResult> SoftDeleteCompanyAsync(Guid id, CancellationToken ct) => throw new NotImplementedException();
        public Task<IReadOnlyList<CompanyDomain>> ListDomainsAsync(Guid companyId, CancellationToken ct) => throw new NotImplementedException();
        public Task<CompanyDomain?> AddDomainAsync(Guid companyId, string domain, CancellationToken ct) => throw new NotImplementedException();
        public Task<bool> RemoveDomainAsync(Guid domainId, CancellationToken ct) => throw new NotImplementedException();
        public Task<IReadOnlyList<Contact>> ListContactsAsync(Guid? companyId, string? search, CancellationToken ct) => throw new NotImplementedException();
        public Task<Contact?> GetContactAsync(Guid id, CancellationToken ct) => throw new NotImplementedException();
        public Task<Contact?> UpdateContactAsync(Guid id, Contact patch, CancellationToken ct) => throw new NotImplementedException();
        public Task<DeleteResult> DeleteContactAsync(Guid id, CancellationToken ct) => throw new NotImplementedException();
    }
}
