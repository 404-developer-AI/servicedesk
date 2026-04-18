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

/// v0.0.9 step 2 — verifies that <see cref="ContactLookupService"/> auto-links
/// a contact via a <c>contact_companies</c> primary-role row on mail arrival,
/// without overwriting an existing, different primary link.
public sealed class ContactLookupServiceTests
{
    [Fact]
    public async Task New_contact_gets_primary_link_when_domain_matches_company()
    {
        var acme = NewCompany("Acme BV", "ACME001");
        var repo = new FakeCompanies();
        repo.DomainToCompany["acme.com"] = acme;
        var audit = new FakeAuditLogger();
        var svc = Build(repo, audit, autoLinkEnabled: true);

        var created = await svc.EnsureByEmailAsync("jan@acme.com", "Jan Janssen", default);

        // CreateContactAsync must have received the primary company-id.
        Assert.Equal(acme.Id, repo.CreatedWithPrimary[created.Id]);
        Assert.Contains(audit.Events, e => e.EventType == "contact.company.auto_linked");
    }

    [Fact]
    public async Task Existing_contact_without_primary_is_backfilled()
    {
        var acme = NewCompany("Acme BV", "ACME001");
        var repo = new FakeCompanies();
        repo.DomainToCompany["acme.com"] = acme;
        var existing = NewContact("jan@acme.com");
        repo.ContactsByEmail[existing.Email] = existing;
        var audit = new FakeAuditLogger();
        var svc = Build(repo, audit, autoLinkEnabled: true);

        await svc.EnsureByEmailAsync("jan@acme.com", "Jan Janssen", default);

        Assert.True(repo.Links.ContainsKey((existing.Id, acme.Id)));
        Assert.Equal("primary", repo.Links[(existing.Id, acme.Id)]);
        Assert.Contains(audit.Events, e => e.EventType == "contact.company.auto_linked");
    }

    [Fact]
    public async Task Existing_contact_with_different_primary_is_not_overwritten()
    {
        var acme = NewCompany("Acme BV", "ACME001");
        var otherCompanyId = Guid.NewGuid();
        var repo = new FakeCompanies();
        repo.DomainToCompany["acme.com"] = acme;
        var existing = NewContact("jan@acme.com");
        repo.ContactsByEmail[existing.Email] = existing;
        repo.Links[(existing.Id, otherCompanyId)] = "primary";
        repo.PrimaryCompanyByContact[existing.Id] = NewCompany("Other BV", "OTHER1") with { Id = otherCompanyId };
        var audit = new FakeAuditLogger();
        var svc = Build(repo, audit, autoLinkEnabled: true);

        await svc.EnsureByEmailAsync("jan@acme.com", "Jan Janssen", default);

        // The old primary link is untouched and no auto-link event fired.
        Assert.Equal("primary", repo.Links[(existing.Id, otherCompanyId)]);
        Assert.False(repo.Links.ContainsKey((existing.Id, acme.Id)));
        Assert.DoesNotContain(audit.Events, e => e.EventType == "contact.company.auto_linked");
    }

    [Fact]
    public async Task Setting_disabled_skips_auto_link()
    {
        var acme = NewCompany("Acme BV", "ACME001");
        var repo = new FakeCompanies();
        repo.DomainToCompany["acme.com"] = acme;
        var audit = new FakeAuditLogger();
        var svc = Build(repo, audit, autoLinkEnabled: false);

        var created = await svc.EnsureByEmailAsync("jan@acme.com", "Jan Janssen", default);

        Assert.False(repo.CreatedWithPrimary.ContainsKey(created.Id));
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
        var acme = NewCompany("Acme BV", "ACME001");
        var repo = new FakeCompanies();
        repo.DomainToCompany["gmail.com"] = acme; // intentionally polluted
        var audit = new FakeAuditLogger();
        var svc = Build(repo, audit, autoLinkEnabled: true, domainBlacklist: new[] { "gmail.com" });

        var created = await svc.EnsureByEmailAsync("jan.klant@gmail.com", "Jan Klant", default);

        Assert.False(repo.CreatedWithPrimary.ContainsKey(created.Id));
        Assert.Empty(repo.FindByDomainCalls);
        Assert.DoesNotContain(audit.Events, e => e.EventType == "contact.company.auto_linked");
    }

    [Fact]
    public async Task Upsert_primary_demotes_existing_primary_to_secondary()
    {
        // Job-change: a contact's primary moves from Old to New. The repo
        // contract says the previous primary is demoted to 'secondary' in the
        // same transaction so the partial unique index is never violated.
        var repo = new FakeCompanies();
        var contactId = Guid.NewGuid();
        var oldCompany = Guid.NewGuid();
        var newCompany = Guid.NewGuid();
        repo.Links[(contactId, oldCompany)] = "primary";

        await repo.UpsertContactLinkAsync(contactId, newCompany, "primary", default);

        Assert.Equal("secondary", repo.Links[(contactId, oldCompany)]);
        Assert.Equal("primary", repo.Links[(contactId, newCompany)]);
    }

    [Fact]
    public async Task Upsert_rejects_invalid_role_argument()
    {
        var repo = new FakeCompanies();
        await Assert.ThrowsAsync<ArgumentException>(() =>
            repo.UpsertContactLinkAsync(Guid.NewGuid(), Guid.NewGuid(), "vip", default));
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

    private static Contact NewContact(string email) => new(
        Id: Guid.NewGuid(), CompanyRole: "Member",
        FirstName: "Jan", LastName: "Janssen",
        Email: email, Phone: "", JobTitle: "",
        IsActive: true, CreatedUtc: DateTime.UtcNow, UpdatedUtc: DateTime.UtcNow);

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

    /// In-memory fake that mirrors just enough of <see cref="ICompanyRepository"/>
    /// for the mail auto-link paths plus the primary-move semantics asserted
    /// above. Everything the service doesn't touch throws, so drift loudly
    /// shows up as a test failure rather than a silent stub.
    private sealed class FakeCompanies : ICompanyRepository
    {
        public Dictionary<string, Company> DomainToCompany { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, Contact> ContactsByEmail { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<(Guid ContactId, Guid CompanyId), string> Links { get; } = new();
        public Dictionary<Guid, Company> PrimaryCompanyByContact { get; } = new();
        public Dictionary<Guid, Guid> CreatedWithPrimary { get; } = new();
        public List<string> FindByDomainCalls { get; } = new();

        public Task<Company?> FindCompanyByDomainAsync(string domain, CancellationToken ct)
        {
            FindByDomainCalls.Add(domain);
            return Task.FromResult(DomainToCompany.TryGetValue(domain, out var c) ? c : null);
        }

        public Task<Contact?> GetContactByEmailAsync(string email, CancellationToken ct)
            => Task.FromResult(ContactsByEmail.TryGetValue(email, out var c) ? c : null);

        public Task<Contact> CreateContactAsync(Contact c, Guid? primaryCompanyId, CancellationToken ct)
        {
            var withId = c with { Id = c.Id == Guid.Empty ? Guid.NewGuid() : c.Id };
            ContactsByEmail[withId.Email] = withId;
            if (primaryCompanyId.HasValue)
            {
                Links[(withId.Id, primaryCompanyId.Value)] = "primary";
                CreatedWithPrimary[withId.Id] = primaryCompanyId.Value;
            }
            return Task.FromResult(withId);
        }

        public Task<Company?> GetPrimaryCompanyForContactAsync(Guid contactId, CancellationToken ct)
            => Task.FromResult(PrimaryCompanyByContact.TryGetValue(contactId, out var c) ? c : null);

        public Task<ContactCompanyLink> UpsertContactLinkAsync(Guid contactId, Guid companyId, string role, CancellationToken ct)
        {
            if (role != "primary" && role != "secondary" && role != "supplier")
                throw new ArgumentException($"Role '{role}' is not one of primary/secondary/supplier.", nameof(role));

            if (role == "primary")
            {
                // Demote any other primary for this contact.
                foreach (var key in Links.Keys.ToList())
                {
                    if (key.ContactId == contactId && key.CompanyId != companyId && Links[key] == "primary")
                        Links[key] = "secondary";
                }
            }
            Links[(contactId, companyId)] = role;
            var now = DateTime.UtcNow;
            return Task.FromResult(new ContactCompanyLink(Guid.NewGuid(), contactId, companyId, role, now, now));
        }

        // ---- Unused members — throw so accidental reliance fails loudly ----
        public Task<IReadOnlyList<Company>> ListCompaniesAsync(string? search, bool includeInactive, CancellationToken ct) => throw new NotImplementedException();
        public Task<Company?> GetCompanyAsync(Guid id, CancellationToken ct) => throw new NotImplementedException();
        public Task<Company?> GetCompanyByCodeAsync(string code, CancellationToken ct) => throw new NotImplementedException();
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
        public Task<IReadOnlyList<ContactCompanyLink>> ListContactLinksAsync(Guid contactId, CancellationToken ct) => throw new NotImplementedException();
        public Task<IReadOnlyList<ContactCompanyLink>> ListCompanyLinksAsync(Guid companyId, CancellationToken ct) => throw new NotImplementedException();
        public Task<bool> RemoveContactLinkAsync(Guid contactId, Guid companyId, CancellationToken ct) => throw new NotImplementedException();
        public Task<bool> SetPrimaryCompanyAsync(Guid contactId, Guid? companyId, CancellationToken ct) => throw new NotImplementedException();
    }
}
