using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using Servicedesk.Api.Auth;
using Servicedesk.Domain.Companies;
using Servicedesk.Infrastructure.Audit;
using Servicedesk.Infrastructure.Persistence.Companies;
using Servicedesk.Infrastructure.Persistence.Taxonomy;

namespace Servicedesk.Api.Companies;

/// Admin-only CRUD for customer companies, their email domains, and their
/// contacts. The schema supports the multi-tenant portal (see
/// project_tickets_future_vision memory), but in v0.0.5 these endpoints
/// exist only for seeding and API-level management — no portal UI yet.
public static class CompanyEndpoints
{
    public static IEndpointRouteBuilder MapCompanyEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/companies")
            .WithTags("Companies")
            .RequireAuthorization(AuthorizationPolicies.RequireAdmin);

        group.MapGet("/", async (string? search, ICompanyRepository repo, CancellationToken ct) =>
            Results.Ok(await repo.ListCompaniesAsync(search, ct)))
            .WithName("ListCompanies").WithOpenApi();

        group.MapGet("/{id:guid}", async (Guid id, ICompanyRepository repo, CancellationToken ct) =>
        {
            var company = await repo.GetCompanyAsync(id, ct);
            if (company is null) return Results.NotFound();
            var domains = await repo.ListDomainsAsync(id, ct);
            return Results.Ok(new { company, domains });
        }).WithName("GetCompany").WithOpenApi();

        group.MapPost("/", async (
            [FromBody] CompanyRequest req, HttpContext http,
            ICompanyRepository repo, IAuditLogger audit, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(req.Name))
                return Results.BadRequest(new { error = "Name is required." });
            var now = DateTime.UtcNow;
            var created = await repo.CreateCompanyAsync(new Company(
                Guid.Empty, req.Name.Trim(), req.Description ?? "", req.Website ?? "", req.Phone ?? "",
                req.AddressLine1 ?? "", req.AddressLine2 ?? "", req.City ?? "", req.PostalCode ?? "",
                req.Country ?? "", IsActive: true, now, now), ct);
            await AuditWrite(audit, http, "company.created", created.Id.ToString(), created);
            return Results.Created($"/api/companies/{created.Id}", created);
        }).WithName("CreateCompany").WithOpenApi();

        group.MapPut("/{id:guid}", async (
            Guid id, [FromBody] CompanyRequest req, HttpContext http,
            ICompanyRepository repo, IAuditLogger audit, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(req.Name))
                return Results.BadRequest(new { error = "Name is required." });
            var existing = await repo.GetCompanyAsync(id, ct);
            if (existing is null) return Results.NotFound();
            var patch = existing with
            {
                Name = req.Name.Trim(),
                Description = req.Description ?? existing.Description,
                Website = req.Website ?? existing.Website,
                Phone = req.Phone ?? existing.Phone,
                AddressLine1 = req.AddressLine1 ?? existing.AddressLine1,
                AddressLine2 = req.AddressLine2 ?? existing.AddressLine2,
                City = req.City ?? existing.City,
                PostalCode = req.PostalCode ?? existing.PostalCode,
                Country = req.Country ?? existing.Country,
                IsActive = req.IsActive ?? existing.IsActive,
            };
            var updated = await repo.UpdateCompanyAsync(id, patch, ct);
            if (updated is null) return Results.NotFound();
            await AuditWrite(audit, http, "company.updated", id.ToString(), updated);
            return Results.Ok(updated);
        }).WithName("UpdateCompany").WithOpenApi();

        group.MapDelete("/{id:guid}", async (
            Guid id, HttpContext http, ICompanyRepository repo, IAuditLogger audit, CancellationToken ct) =>
        {
            var result = await repo.DeleteCompanyAsync(id, ct);
            return result switch
            {
                DeleteResult.NotFound => Results.NotFound(),
                DeleteResult.InUse => Results.Conflict(new { error = "Company still has open tickets via its contacts." }),
                DeleteResult.SystemProtected => Results.Conflict(new { error = "Cannot delete a system company." }),
                _ => await DeletedOk(http, audit, "company.deleted", id),
            };
        }).WithName("DeleteCompany").WithOpenApi();

        // ---- Domains ----
        group.MapPost("/{id:guid}/domains", async (
            Guid id, [FromBody] DomainRequest req, HttpContext http,
            ICompanyRepository repo, IAuditLogger audit, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(req.Domain))
                return Results.BadRequest(new { error = "Domain is required." });
            var domain = req.Domain.Trim().ToLowerInvariant();
            var added = await repo.AddDomainAsync(id, domain, ct);
            if (added is null) return Results.Conflict(new { error = "Domain already linked." });
            await AuditWrite(audit, http, "company.domain.added", id.ToString(), new { domain });
            return Results.Created($"/api/companies/{id}/domains/{added.Id}", added);
        }).WithName("AddCompanyDomain").WithOpenApi();

        group.MapDelete("/{id:guid}/domains/{domainId:guid}", async (
            Guid id, Guid domainId, HttpContext http,
            ICompanyRepository repo, IAuditLogger audit, CancellationToken ct) =>
        {
            var removed = await repo.RemoveDomainAsync(domainId, ct);
            if (!removed) return Results.NotFound();
            await AuditWrite(audit, http, "company.domain.removed", id.ToString(), new { domainId });
            return Results.NoContent();
        }).WithName("RemoveCompanyDomain").WithOpenApi();

        // ---- Contacts ----
        var contactGroup = app.MapGroup("/api/contacts")
            .WithTags("Contacts")
            .RequireAuthorization(AuthorizationPolicies.RequireAdmin);

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
            var created = await repo.CreateContactAsync(new Contact(
                Guid.Empty, req.CompanyId, req.CompanyRole ?? "Member",
                req.FirstName ?? "", req.LastName ?? "", req.Email!.Trim().ToLowerInvariant(),
                req.Phone ?? "", req.JobTitle ?? "", IsActive: true, now, now), ct);
            await AuditWrite(audit, http, "contact.created", created.Id.ToString(), new { created.Email });
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
                CompanyId = req.CompanyId,
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
        [property: Required] string Name,
        string? Description,
        string? Website,
        string? Phone,
        string? AddressLine1,
        string? AddressLine2,
        string? City,
        string? PostalCode,
        string? Country,
        bool? IsActive);

    public sealed record DomainRequest([property: Required] string Domain);

    public sealed record ContactRequest(
        [property: Required] string? Email,
        Guid? CompanyId,
        string? CompanyRole,
        string? FirstName,
        string? LastName,
        string? Phone,
        string? JobTitle,
        bool? IsActive);

    private static IResult? ValidateContact(ContactRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Email) || !req.Email.Contains('@'))
            return Results.BadRequest(new { error = "A valid email is required." });
        if (req.CompanyRole is not null && req.CompanyRole != "Member" && req.CompanyRole != "TicketManager")
            return Results.BadRequest(new { error = "companyRole must be 'Member' or 'TicketManager'." });
        return null;
    }

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
