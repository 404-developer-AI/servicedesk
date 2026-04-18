using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Servicedesk.Api.Auth;
using Servicedesk.Domain.Companies;
using Servicedesk.Infrastructure.Audit;
using Servicedesk.Infrastructure.Mail.Ingest;
using Servicedesk.Infrastructure.Persistence.Companies;
using Servicedesk.Infrastructure.Persistence.Taxonomy;
using Servicedesk.Infrastructure.Settings;

namespace Servicedesk.Api.Companies;

/// v0.0.9: customer company management with code/short_name/VAT/alerts.
/// Access is split: admins manage the full list and lifecycle; agents can
/// read/update individual companies they reach via tickets or global search,
/// but not the overview or delete. Customers have no access.
public static class CompanyEndpoints
{
    public static IEndpointRouteBuilder MapCompanyEndpoints(this IEndpointRouteBuilder app)
    {
        // ---- Admin-only: full list + lifecycle + domains ----
        var adminGroup = app.MapGroup("/api/companies")
            .WithTags("Companies")
            .RequireAuthorization(AuthorizationPolicies.RequireAdmin);

        adminGroup.MapGet("/", async (
            string? search, bool? includeInactive,
            ICompanyRepository repo, CancellationToken ct) =>
            Results.Ok(await repo.ListCompaniesAsync(search, includeInactive ?? false, ct)))
            .WithName("ListCompanies").WithOpenApi();

        adminGroup.MapPost("/", async (
            [FromBody] CompanyRequest req, HttpContext http,
            ICompanyRepository repo, IAuditLogger audit, CancellationToken ct) =>
        {
            if (ValidateCompany(req) is { } err) return err;
            var code = req.Code!.Trim();
            if (await repo.GetCompanyByCodeAsync(code, ct) is not null)
                return Results.Conflict(new { error = "A company with this code already exists." });

            var now = DateTime.UtcNow;
            var alertMode = NormalizeAlertMode(req.AlertOnOpenMode, req.AlertOnOpen ?? false);
            var created = await repo.CreateCompanyAsync(new Company(
                Id: Guid.Empty,
                Name: req.Name!.Trim(),
                Description: req.Description ?? "",
                Website: req.Website ?? "",
                Phone: req.Phone ?? "",
                AddressLine1: req.AddressLine1 ?? "",
                AddressLine2: req.AddressLine2 ?? "",
                City: req.City ?? "",
                PostalCode: req.PostalCode ?? "",
                Country: req.Country ?? "",
                IsActive: req.IsActive ?? true,
                CreatedUtc: now,
                UpdatedUtc: now,
                Code: code,
                ShortName: (req.ShortName ?? "").Trim(),
                VatNumber: NormalizeVat(req.VatNumber),
                AlertText: req.AlertText ?? "",
                AlertOnCreate: req.AlertOnCreate ?? false,
                AlertOnOpen: req.AlertOnOpen ?? false,
                AlertOnOpenMode: alertMode,
                Email: NormalizeEmail(req.Email)), ct);
            await AuditWrite(audit, http, "company.created", created.Id.ToString(), new { created.Id, created.Code, created.Name });
            return Results.Created($"/api/companies/{created.Id}", created);
        }).WithName("CreateCompany").WithOpenApi();

        adminGroup.MapDelete("/{id:guid}", async (
            Guid id, HttpContext http, ICompanyRepository repo, IAuditLogger audit, CancellationToken ct) =>
        {
            var result = await repo.SoftDeleteCompanyAsync(id, ct);
            return result switch
            {
                DeleteResult.NotFound => Results.NotFound(),
                _ => await DeletedOk(http, audit, "company.deleted", id),
            };
        }).WithName("DeleteCompany").WithOpenApi();

        // ---- Domains: admin-only (shaping portal visibility + mail auto-link) ----
        //
        // The blacklist check is enforced on add (freemail providers may never
        // seed an auto-link rule — see Mail.AutoLinkDomainBlacklist), but not
        // on delete: existing installs that stored a now-blacklisted value
        // before this gate shipped must still be able to remove it.
        adminGroup.MapPost("/{id:guid}/domains", async (
            Guid id, [FromBody] DomainRequest req, HttpContext http,
            ICompanyRepository repo, IAuditLogger audit,
            ISettingsService settings, ILoggerFactory loggers,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(req.Domain))
                return Results.BadRequest(new { error = "Domain is required." });
            var domain = req.Domain.Trim().ToLowerInvariant();
            var blacklist = await MailDomainBlacklist.LoadAsync(settings, loggers.CreateLogger(typeof(MailDomainBlacklist)), ct);
            if (blacklist.Contains(domain))
                return Results.BadRequest(new
                {
                    error = $"'{domain}' is a public mail domain and cannot be linked to a company. Adjust Mail.AutoLinkDomainBlacklist in Settings if this is intentional.",
                });
            var added = await repo.AddDomainAsync(id, domain, ct);
            if (added is null) return Results.Conflict(new { error = "Domain already linked." });
            await AuditWrite(audit, http, "company.domain.added", id.ToString(), new { domain });
            return Results.Created($"/api/companies/{id}/domains/{added.Id}", added);
        }).WithName("AddCompanyDomain").WithOpenApi();

        adminGroup.MapDelete("/{id:guid}/domains/{domainId:guid}", async (
            Guid id, Guid domainId, HttpContext http,
            ICompanyRepository repo, IAuditLogger audit, CancellationToken ct) =>
        {
            var removed = await repo.RemoveDomainAsync(domainId, ct);
            if (!removed) return Results.NotFound();
            await AuditWrite(audit, http, "company.domain.removed", id.ToString(), new { domainId });
            return Results.NoContent();
        }).WithName("RemoveCompanyDomain").WithOpenApi();

        // ---- Agent+Admin: per-company read/update, contacts management ----
        var agentGroup = app.MapGroup("/api/companies")
            .WithTags("Companies")
            .RequireAuthorization(AuthorizationPolicies.RequireAgent);

        agentGroup.MapGet("/{id:guid}", async (Guid id, ICompanyRepository repo, CancellationToken ct) =>
        {
            var company = await repo.GetCompanyAsync(id, ct);
            if (company is null) return Results.NotFound();
            var domains = await repo.ListDomainsAsync(id, ct);
            return Results.Ok(new { company, domains });
        }).WithName("GetCompany").WithOpenApi();

        agentGroup.MapPut("/{id:guid}", async (
            Guid id, [FromBody] CompanyRequest req, HttpContext http,
            ICompanyRepository repo, IAuditLogger audit, CancellationToken ct) =>
        {
            if (ValidateCompany(req) is { } err) return err;
            var existing = await repo.GetCompanyAsync(id, ct);
            if (existing is null) return Results.NotFound();
            var code = req.Code!.Trim();
            if (!string.Equals(code, existing.Code, StringComparison.OrdinalIgnoreCase))
            {
                var clash = await repo.GetCompanyByCodeAsync(code, ct);
                if (clash is not null && clash.Id != id)
                    return Results.Conflict(new { error = "A company with this code already exists." });
            }
            var alertOnOpen = req.AlertOnOpen ?? existing.AlertOnOpen;
            var patch = existing with
            {
                Name = req.Name!.Trim(),
                Description = req.Description ?? existing.Description,
                Website = req.Website ?? existing.Website,
                Phone = req.Phone ?? existing.Phone,
                AddressLine1 = req.AddressLine1 ?? existing.AddressLine1,
                AddressLine2 = req.AddressLine2 ?? existing.AddressLine2,
                City = req.City ?? existing.City,
                PostalCode = req.PostalCode ?? existing.PostalCode,
                Country = req.Country ?? existing.Country,
                IsActive = req.IsActive ?? existing.IsActive,
                Code = code,
                ShortName = (req.ShortName ?? existing.ShortName).Trim(),
                VatNumber = req.VatNumber is null ? existing.VatNumber : NormalizeVat(req.VatNumber),
                AlertText = req.AlertText ?? existing.AlertText,
                AlertOnCreate = req.AlertOnCreate ?? existing.AlertOnCreate,
                AlertOnOpen = alertOnOpen,
                AlertOnOpenMode = NormalizeAlertMode(req.AlertOnOpenMode ?? existing.AlertOnOpenMode, alertOnOpen),
                Email = req.Email is null ? existing.Email : NormalizeEmail(req.Email),
            };
            var updated = await repo.UpdateCompanyAsync(id, patch, ct);
            if (updated is null) return Results.NotFound();
            await AuditWrite(audit, http, "company.updated", id.ToString(), new { updated.Id, updated.Code, updated.Name });
            return Results.Ok(updated);
        }).WithName("UpdateCompany").WithOpenApi();

        agentGroup.MapGet("/{id:guid}/contacts", async (
            Guid id, string? search, ICompanyRepository repo, CancellationToken ct) =>
            Results.Ok(await repo.ListContactsAsync(id, search, ct)))
            .WithName("ListCompanyContacts").WithOpenApi();

        // Link a contact to a company with an explicit role. The body is
        // optional; when omitted we default to 'primary' so existing flows
        // (the pre-v0.0.9 "add contact to company" button) keep working
        // unchanged. Upsert semantics: posting again with a different role
        // for the same pair updates the role in place.
        agentGroup.MapPost("/{id:guid}/contacts/{contactId:guid}", async (
            Guid id, Guid contactId, [FromBody] ContactLinkRequest? req, HttpContext http,
            ICompanyRepository repo, IAuditLogger audit, CancellationToken ct) =>
        {
            var company = await repo.GetCompanyAsync(id, ct);
            if (company is null) return Results.NotFound(new { error = "Company not found." });
            var contact = await repo.GetContactAsync(contactId, ct);
            if (contact is null) return Results.NotFound(new { error = "Contact not found." });

            var role = (req?.Role ?? "primary").Trim().ToLowerInvariant();
            if (role != "primary" && role != "secondary" && role != "supplier")
                return Results.BadRequest(new { error = "role must be 'primary', 'secondary' or 'supplier'." });

            var link = await repo.UpsertContactLinkAsync(contactId, id, role, ct);
            await AuditWrite(audit, http, "company.contact.linked", id.ToString(),
                new { contactId, role, linkId = link.Id });
            return Results.Ok(link);
        }).WithName("LinkCompanyContact").WithOpenApi();

        agentGroup.MapDelete("/{id:guid}/contacts/{contactId:guid}", async (
            Guid id, Guid contactId, HttpContext http,
            ICompanyRepository repo, IAuditLogger audit, CancellationToken ct) =>
        {
            var removed = await repo.RemoveContactLinkAsync(contactId, id, ct);
            if (!removed) return Results.NotFound();
            await AuditWrite(audit, http, "company.contact.unlinked", id.ToString(), new { contactId });
            return Results.NoContent();
        }).WithName("UnlinkCompanyContact").WithOpenApi();

        // New in v0.0.9 step 2: inspect all role-tagged links for one contact.
        // The Contacts tab and the ticket side-panel both use this to render
        // role badges without hitting /api/companies for each hit.
        agentGroup.MapGet("/{id:guid}/links", async (
            Guid id, ICompanyRepository repo, CancellationToken ct) =>
            Results.Ok(await repo.ListCompanyLinksAsync(id, ct)))
            .WithName("ListCompanyLinks").WithOpenApi();

        // ---- Contacts (unchanged, top-level, agent+admin) ----
        var contactGroup = app.MapGroup("/api/contacts")
            .WithTags("Contacts")
            .RequireAuthorization(AuthorizationPolicies.RequireAgent);

        contactGroup.MapGet("/", async (Guid? companyId, string? search,
            ICompanyRepository repo, CancellationToken ct) =>
            Results.Ok(await repo.ListContactsAsync(companyId, search, ct)))
            .WithName("ListContacts").WithOpenApi();

        contactGroup.MapGet("/{id:guid}", async (Guid id, ICompanyRepository repo, CancellationToken ct) =>
        {
            var c = await repo.GetContactAsync(id, ct);
            return c is null ? Results.NotFound() : Results.Ok(c);
        }).WithName("GetContact").WithOpenApi();

        contactGroup.MapPost("/", async (
            [FromBody] ContactRequest req, HttpContext http,
            ICompanyRepository repo, IAuditLogger audit, CancellationToken ct) =>
        {
            if (ValidateContact(req) is { } err) return err;
            var now = DateTime.UtcNow;
            // CompanyId on the request is shorthand for "also insert a primary
            // link" — the repo does this atomically in one transaction.
            var created = await repo.CreateContactAsync(new Contact(
                Guid.Empty, req.CompanyRole ?? "Member",
                req.FirstName ?? "", req.LastName ?? "", req.Email!.Trim().ToLowerInvariant(),
                req.Phone ?? "", req.JobTitle ?? "", IsActive: true, now, now), req.CompanyId, ct);
            await AuditWrite(audit, http, "contact.created", created.Id.ToString(), new { created.Email, primaryCompanyId = req.CompanyId });
            return Results.Created($"/api/contacts/{created.Id}", created);
        }).WithName("CreateContact").WithOpenApi();

        contactGroup.MapPut("/{id:guid}", async (
            Guid id, [FromBody] ContactRequest req, HttpContext http,
            ICompanyRepository repo, IAuditLogger audit, CancellationToken ct) =>
        {
            if (ValidateContact(req) is { } err) return err;
            var existing = await repo.GetContactAsync(id, ct);
            if (existing is null) return Results.NotFound();
            var patch = existing with
            {
                CompanyRole = req.CompanyRole ?? existing.CompanyRole,
                FirstName = req.FirstName ?? existing.FirstName,
                LastName = req.LastName ?? existing.LastName,
                Email = req.Email!.Trim().ToLowerInvariant(),
                Phone = req.Phone ?? existing.Phone,
                JobTitle = req.JobTitle ?? existing.JobTitle,
                IsActive = req.IsActive ?? existing.IsActive,
            };
            var updated = await repo.UpdateContactAsync(id, patch, ct);
            if (updated is null) return Results.NotFound();
            // CompanyId in the PUT body is a primary-link shortcut. Only act
            // when explicitly supplied so callers that PATCH other fields
            // don't accidentally nuke the contact's primary link.
            if (req.CompanyId is not null)
                await repo.SetPrimaryCompanyAsync(id, req.CompanyId, ct);
            await AuditWrite(audit, http, "contact.updated", id.ToString(), new { updated.Email });
            return Results.Ok(updated);
        }).WithName("UpdateContact").WithOpenApi();

        contactGroup.MapDelete("/{id:guid}", async (
            Guid id, HttpContext http, ICompanyRepository repo, IAuditLogger audit, CancellationToken ct) =>
        {
            var result = await repo.DeleteContactAsync(id, ct);
            return result switch
            {
                DeleteResult.NotFound => Results.NotFound(),
                DeleteResult.InUse => Results.Conflict(new { error = "Contact still has open tickets." }),
                _ => await DeletedOk(http, audit, "contact.deleted", id),
            };
        }).WithName("DeleteContact").WithOpenApi();

        return app;
    }

    public sealed record CompanyRequest(
        [property: Required] string? Name,
        [property: Required] string? Code,
        string? ShortName,
        string? VatNumber,
        string? Email,
        string? Description,
        string? Website,
        string? Phone,
        string? AddressLine1,
        string? AddressLine2,
        string? City,
        string? PostalCode,
        string? Country,
        bool? IsActive,
        string? AlertText,
        bool? AlertOnCreate,
        bool? AlertOnOpen,
        string? AlertOnOpenMode);

    public sealed record DomainRequest([property: Required] string Domain);

    public sealed record ContactLinkRequest(string? Role);

    public sealed record ContactRequest(
        [property: Required] string? Email,
        Guid? CompanyId,
        string? CompanyRole,
        string? FirstName,
        string? LastName,
        string? Phone,
        string? JobTitle,
        bool? IsActive);

    private static IResult? ValidateCompany(CompanyRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Name))
            return Results.BadRequest(new { error = "Name is required." });
        if (req.Name.Length > 200)
            return Results.BadRequest(new { error = "Name may be at most 200 characters." });
        if (string.IsNullOrWhiteSpace(req.Code))
            return Results.BadRequest(new { error = "Customer code is required." });
        if (req.Code.Trim().Length > 64)
            return Results.BadRequest(new { error = "Customer code may be at most 64 characters." });
        if (req.AlertOnOpenMode is { } mode && mode != "session" && mode != "every")
            return Results.BadRequest(new { error = "alertOnOpenMode must be 'session' or 'every'." });
        if (!string.IsNullOrWhiteSpace(req.Email))
        {
            var trimmed = req.Email.Trim();
            if (trimmed.Length > 200)
                return Results.BadRequest(new { error = "Email may be at most 200 characters." });
            // Single '@', non-empty local/domain, a dot in the domain. Matches
            // the level of server-side checking we do for contact emails without
            // dragging in a full RFC 5322 parser.
            var at = trimmed.IndexOf('@');
            if (at <= 0 || at == trimmed.Length - 1
                || trimmed.IndexOf('@', at + 1) >= 0
                || !trimmed[(at + 1)..].Contains('.'))
                return Results.BadRequest(new { error = "Email must look like name@domain.tld." });
        }
        return null;
    }

    private static IResult? ValidateContact(ContactRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Email) || !req.Email.Contains('@'))
            return Results.BadRequest(new { error = "A valid email is required." });
        if (req.CompanyRole is not null && req.CompanyRole != "Member" && req.CompanyRole != "TicketManager")
            return Results.BadRequest(new { error = "companyRole must be 'Member' or 'TicketManager'." });
        return null;
    }

    private static string NormalizeAlertMode(string? mode, bool alertOnOpen)
    {
        if (!alertOnOpen) return "session";
        return mode == "every" ? "every" : "session";
    }

    private static string NormalizeVat(string? value)
        => string.IsNullOrWhiteSpace(value) ? "" : value.Trim().ToUpperInvariant().Replace(" ", "");

    private static string NormalizeEmail(string? value)
        => string.IsNullOrWhiteSpace(value) ? "" : value.Trim().ToLowerInvariant();

    private static async Task AuditWrite(IAuditLogger audit, HttpContext http, string eventType, string target, object payload)
    {
        var (actor, role) = ActorContext.Resolve(http);
        await audit.LogAsync(new AuditEvent(
            EventType: eventType, Actor: actor, ActorRole: role, Target: target,
            ClientIp: http.Connection.RemoteIpAddress?.ToString(),
            UserAgent: http.Request.Headers.UserAgent.ToString(),
            Payload: payload));
    }

    private static async Task<IResult> DeletedOk(HttpContext http, IAuditLogger audit, string eventType, Guid id)
    {
        await AuditWrite(audit, http, eventType, id.ToString(), new { id });
        return Results.NoContent();
    }
}
