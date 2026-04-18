using Microsoft.Extensions.Logging;
using Servicedesk.Domain.Companies;
using Servicedesk.Infrastructure.Audit;
using Servicedesk.Infrastructure.Persistence.Companies;
using Servicedesk.Infrastructure.Settings;

namespace Servicedesk.Infrastructure.Mail.Ingest;

public sealed class ContactLookupService : IContactLookupService
{
    private readonly ICompanyRepository _companies;
    private readonly ISettingsService _settings;
    private readonly IAuditLogger _audit;
    private readonly ILogger<ContactLookupService> _logger;

    public ContactLookupService(
        ICompanyRepository companies,
        ISettingsService settings,
        IAuditLogger audit,
        ILogger<ContactLookupService> logger)
    {
        _companies = companies;
        _settings = settings;
        _audit = audit;
        _logger = logger;
    }

    public async Task<Contact> EnsureByEmailAsync(string email, string displayName, CancellationToken ct)
    {
        var existing = await _companies.GetContactByEmailAsync(email, ct);
        if (existing is not null)
        {
            // Existing contacts that already have a primary link keep it — we
            // never overwrite a manual link from the mail path. Only backfill
            // when there's no primary yet, so returning customers don't drift
            // across companies.
            var primary = await _companies.GetPrimaryCompanyForContactAsync(existing.Id, ct);
            if (primary is null)
            {
                var matched = await TryAutoLinkAsync(email, ct);
                if (matched is not null)
                {
                    await _companies.UpsertContactLinkAsync(existing.Id, matched.Id, "primary", ct);
                    await AuditAutoLinkAsync("contact.company.auto_linked", existing.Id, matched.Id, email, ct);
                }
            }
            return existing;
        }

        var autoLinked = await TryAutoLinkAsync(email, ct);
        var (first, last) = SplitName(displayName, email);
        var now = DateTime.UtcNow;
        var stub = new Contact(
            Id: Guid.Empty,
            CompanyRole: "Member",
            FirstName: first,
            LastName: last,
            Email: email,
            Phone: string.Empty,
            JobTitle: string.Empty,
            IsActive: true,
            CreatedUtc: now,
            UpdatedUtc: now);
        var created = await _companies.CreateContactAsync(stub, autoLinked?.Id, ct);
        if (autoLinked is not null)
            await AuditAutoLinkAsync("contact.company.auto_linked", created.Id, autoLinked.Id, email, ct);
        return created;
    }

    private async Task<Company?> TryAutoLinkAsync(string email, CancellationToken ct)
    {
        bool enabled;
        try
        {
            enabled = await _settings.GetAsync<bool>(SettingKeys.Mail.AutoLinkCompanyByDomain, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read AutoLinkCompanyByDomain setting; defaulting to enabled.");
            enabled = true;
        }
        if (!enabled) return null;

        var domain = ExtractDomain(email);
        if (domain is null) return null;

        // Freemail/public domains must never drive an auto-link — otherwise a
        // @gmail.com reply would bind half the internet to whichever company
        // happened to list gmail.com as a domain. Manual linking (agent picks
        // company on the contact) stays unaffected; that path doesn't touch
        // this service.
        var blacklist = await MailDomainBlacklist.LoadAsync(_settings, _logger, ct);
        if (blacklist.Contains(domain)) return null;

        return await _companies.FindCompanyByDomainAsync(domain, ct);
    }

    private async Task AuditAutoLinkAsync(string eventType, Guid contactId, Guid companyId, string email, CancellationToken ct)
    {
        await _audit.LogAsync(new AuditEvent(
            EventType: eventType,
            Actor: "mail-intake",
            ActorRole: "System",
            Target: contactId.ToString(),
            ClientIp: null,
            UserAgent: null,
            Payload: new { contactId, companyId, email }));
    }

    private static string? ExtractDomain(string email)
    {
        if (string.IsNullOrWhiteSpace(email)) return null;
        var at = email.IndexOf('@');
        if (at < 0 || at == email.Length - 1) return null;
        return email[(at + 1)..].Trim().ToLowerInvariant();
    }

    private static (string First, string Last) SplitName(string displayName, string email)
    {
        var name = (displayName ?? string.Empty).Trim();
        if (name.Length == 0)
        {
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
