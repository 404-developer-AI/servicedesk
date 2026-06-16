using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Servicedesk.Api.Auth;
using Servicedesk.Domain.Reports;
using Servicedesk.Infrastructure.Audit;
using Servicedesk.Infrastructure.Auth;
using Servicedesk.Infrastructure.Contracts.Reports;
using Servicedesk.Infrastructure.KnowledgeBase;
using Servicedesk.Infrastructure.Settings;

namespace Servicedesk.Api.Contracts;

/// Agent-facing endpoints for the Contracts → Settings "report email templates"
/// module and the per-company "Send report" flow. Anyone with the
/// <c>contracts_enabled</c> flag may author templates and send reports — this is
/// deliberately not admin-only. Gated exactly like the other Contracts modules:
/// the route policy is RequireAgent and every handler re-checks the flag.
public static class ContractReportsEndpoints
{
    private const int MaxName = 200;
    private const int MaxDescription = 2000;
    private const int MaxSubject = 500;
    private const int MaxBodyHtml = 200_000;

    public static IEndpointRouteBuilder MapContractReportsEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/contracts/reports")
            .WithTags("Contracts")
            .RequireAuthorization(AuthorizationPolicies.RequireAgent);

        // Template CRUD.
        group.MapGet("/templates", ListTemplates).WithName("ListReportTemplates").WithOpenApi();
        group.MapGet("/templates/{id:guid}", GetTemplate).WithName("GetReportTemplate").WithOpenApi();
        group.MapPost("/templates", CreateTemplate).WithName("CreateReportTemplate").WithOpenApi();
        group.MapPut("/templates/{id:guid}", UpdateTemplate).WithName("UpdateReportTemplate").WithOpenApi();
        group.MapDelete("/templates/{id:guid}", DeleteTemplate).WithName("DeleteReportTemplate").WithOpenApi();

        // Authoring metadata.
        group.MapGet("/columns", GetColumns).WithName("ListReportColumns").WithOpenApi();
        group.MapGet("/tokens", GetTokens).WithName("ListReportTokens").WithOpenApi();
        group.MapGet("/defaults", GetDefaults).WithName("GetReportDefaults").WithOpenApi();

        // Reporting contacts (per company).
        group.MapGet("/companies/{companyId:guid}/reporting-contacts", ListReportingContacts)
            .WithName("ListReportingContacts").WithOpenApi();
        group.MapPut("/companies/{companyId:guid}/reporting-contacts/{contactId:guid}", SetReportingContact)
            .WithName("SetReportingContact").WithOpenApi();

        // Preview + send.
        group.MapPost("/companies/{companyId:guid}/preview", Preview).WithName("PreviewReport").WithOpenApi();
        group.MapPost("/companies/{companyId:guid}/send", Send).WithName("SendReport").WithOpenApi();

        // "Last sent" stamps for the matching list + detail header.
        group.MapGet("/last-sent", ListLastSent).WithName("ListReportLastSent").WithOpenApi();

        return group;
    }

    private static async Task<(Guid UserId, IResult? Deny)> RequireContractsFlagAsync(
        HttpContext http, IUserService users, CancellationToken ct)
    {
        var userId = ActorContext.GetUserId(http);
        if (userId == Guid.Empty) return (Guid.Empty, Results.Unauthorized());
        if (!await users.GetContractsEnabledAsync(userId, ct)) return (Guid.Empty, Results.Forbid());
        return (userId, null);
    }

    private static ReportActor ResolveActor(HttpContext http)
    {
        var email = http.User.FindFirst(ClaimTypes.Email)?.Value;
        var name = http.User.Identity?.Name ?? email ?? "Agent";
        Guid? userId = Guid.TryParse(http.User.FindFirst(ClaimTypes.NameIdentifier)?.Value, out var g) ? g : null;
        return new ReportActor(userId, name, email);
    }

    // ── Templates ────────────────────────────────────────────────────────

    private static async Task<IResult> ListTemplates(
        bool? includeInactive, HttpContext http, IUserService users,
        IReportTemplateRepository repo, CancellationToken ct)
    {
        var (_, deny) = await RequireContractsFlagAsync(http, users, ct);
        if (deny is not null) return deny;

        var rows = await repo.ListAsync(ReportPurpose.M365, includeInactive ?? true, ct);
        return Results.Ok(rows.Select(MapDto));
    }

    private static async Task<IResult> GetTemplate(
        Guid id, HttpContext http, IUserService users,
        IReportTemplateRepository repo, CancellationToken ct)
    {
        var (_, deny) = await RequireContractsFlagAsync(http, users, ct);
        if (deny is not null) return deny;

        var t = await repo.GetAsync(id, ct);
        return t is null ? Results.NotFound() : Results.Ok(MapDto(t));
    }

    private static async Task<IResult> CreateTemplate(
        [FromBody] TemplateUpsert req, HttpContext http, IUserService users,
        IReportTemplateRepository repo, IKbHtmlSanitizer sanitizer, IAuditLogger audit, CancellationToken ct)
    {
        var (userId, deny) = await RequireContractsFlagAsync(http, users, ct);
        if (deny is not null) return deny;

        var err = Validate(req);
        if (err is not null) return Results.BadRequest(new { error = err });

        var columns = ReportColumns.Normalize(req.Columns);
        var scope = ReportScope.IsValid(req.Scope) ? req.Scope! : ReportScope.All;
        var sanitized = sanitizer.Sanitize(req.BodyHtml ?? string.Empty);

        Guid id;
        try
        {
            id = await repo.CreateAsync(
                ReportPurpose.M365, req.Name!.Trim(),
                string.IsNullOrWhiteSpace(req.Description) ? null : req.Description.Trim(),
                (req.Subject ?? string.Empty).Trim(), sanitized,
                req.QueueId, columns, scope, req.AttachPdf ?? true, userId, ct);
        }
        catch (Npgsql.PostgresException pg) when (pg.SqlState == "23505" && pg.ConstraintName == "ux_report_templates_active_name")
        {
            return Results.Conflict(new { error = "An active template with that name already exists. Pick a different name." });
        }

        var (actor, role) = ActorContext.Resolve(http);
        await audit.LogAsync(new AuditEvent(
            EventType: "contracts.report.template_created",
            Actor: actor, ActorRole: role, Target: id.ToString(),
            ClientIp: http.Connection.RemoteIpAddress?.ToString(),
            UserAgent: http.Request.Headers.UserAgent.ToString()), ct);

        var created = await repo.GetAsync(id, ct);
        return Results.Created($"/api/contracts/reports/templates/{id}", MapDto(created!));
    }

    private static async Task<IResult> UpdateTemplate(
        Guid id, [FromBody] TemplateUpsert req, HttpContext http, IUserService users,
        IReportTemplateRepository repo, IKbHtmlSanitizer sanitizer, IAuditLogger audit, CancellationToken ct)
    {
        var (_, deny) = await RequireContractsFlagAsync(http, users, ct);
        if (deny is not null) return deny;

        var err = Validate(req);
        if (err is not null) return Results.BadRequest(new { error = err });

        var existing = await repo.GetAsync(id, ct);
        if (existing is null) return Results.NotFound();

        var columns = ReportColumns.Normalize(req.Columns);
        var scope = ReportScope.IsValid(req.Scope) ? req.Scope! : existing.Scope;
        var sanitized = sanitizer.Sanitize(req.BodyHtml ?? string.Empty);

        try
        {
            await repo.UpdateAsync(
                id, req.Name!.Trim(),
                string.IsNullOrWhiteSpace(req.Description) ? null : req.Description.Trim(),
                (req.Subject ?? string.Empty).Trim(), sanitized,
                req.QueueId, columns, scope, req.AttachPdf ?? existing.AttachPdf,
                req.IsActive ?? existing.IsActive, ct);
        }
        catch (Npgsql.PostgresException pg) when (pg.SqlState == "23505" && pg.ConstraintName == "ux_report_templates_active_name")
        {
            return Results.Conflict(new { error = "Another active template already uses that name." });
        }

        var (actor, role) = ActorContext.Resolve(http);
        await audit.LogAsync(new AuditEvent(
            EventType: "contracts.report.template_updated",
            Actor: actor, ActorRole: role, Target: id.ToString(),
            ClientIp: http.Connection.RemoteIpAddress?.ToString(),
            UserAgent: http.Request.Headers.UserAgent.ToString()), ct);

        var updated = await repo.GetAsync(id, ct);
        return Results.Ok(MapDto(updated!));
    }

    private static async Task<IResult> DeleteTemplate(
        Guid id, HttpContext http, IUserService users,
        IReportTemplateRepository repo, IAuditLogger audit, CancellationToken ct)
    {
        var (_, deny) = await RequireContractsFlagAsync(http, users, ct);
        if (deny is not null) return deny;

        var deleted = await repo.DeleteAsync(id, ct);
        if (!deleted) return Results.NotFound();

        var (actor, role) = ActorContext.Resolve(http);
        await audit.LogAsync(new AuditEvent(
            EventType: "contracts.report.template_deleted",
            Actor: actor, ActorRole: role, Target: id.ToString(),
            ClientIp: http.Connection.RemoteIpAddress?.ToString(),
            UserAgent: http.Request.Headers.UserAgent.ToString()), ct);

        return Results.NoContent();
    }

    // ── Authoring metadata ────────────────────────────────────────────────

    private static async Task<IResult> GetColumns(HttpContext http, IUserService users, CancellationToken ct)
    {
        var (_, deny) = await RequireContractsFlagAsync(http, users, ct);
        if (deny is not null) return deny;
        return Results.Ok(new { columns = ReportColumns.All.Select(c => new { key = c.Key, label = c.Label }) });
    }

    private static async Task<IResult> GetTokens(HttpContext http, IUserService users, CancellationToken ct)
    {
        var (_, deny) = await RequireContractsFlagAsync(http, users, ct);
        if (deny is not null) return deny;
        return Results.Ok(new { tokens = ReportTokens.Supported.Select(t => new { token = t.Token, label = t.Label }) });
    }

    private static async Task<IResult> GetDefaults(
        HttpContext http, IUserService users, ISettingsService settings, CancellationToken ct)
    {
        var (_, deny) = await RequireContractsFlagAsync(http, users, ct);
        if (deny is not null) return deny;
        var csv = await settings.GetAsync<string>(SettingKeys.Reports.DefaultColumns, ct);
        return Results.Ok(new { columns = ReportColumns.ParseCsv(csv) });
    }

    // ── Reporting contacts ────────────────────────────────────────────────

    private static async Task<IResult> ListReportingContacts(
        Guid companyId, HttpContext http, IUserService users,
        IReportingContactStore store, CancellationToken ct)
    {
        var (_, deny) = await RequireContractsFlagAsync(http, users, ct);
        if (deny is not null) return deny;

        var rows = await store.ListForCompanyAsync(companyId, ct);
        return Results.Ok(new
        {
            items = rows.Select(r => new
            {
                contactId = r.ContactId,
                firstName = r.FirstName,
                lastName = r.LastName,
                email = r.Email,
                role = r.Role,
                isReportingContact = r.IsReportingContact,
                isActive = r.IsActive,
            }),
        });
    }

    private static async Task<IResult> SetReportingContact(
        Guid companyId, Guid contactId, [FromBody] ToggleReportingRequest req, HttpContext http,
        IUserService users, IReportingContactStore store, IAuditLogger audit, CancellationToken ct)
    {
        var (_, deny) = await RequireContractsFlagAsync(http, users, ct);
        if (deny is not null) return deny;

        var ok = await store.SetReportingAsync(companyId, contactId, req.IsReporting, ct);
        if (!ok) return Results.NotFound(new { error = "That contact is not linked to this company." });

        var (actor, role) = ActorContext.Resolve(http);
        await audit.LogAsync(new AuditEvent(
            EventType: "contracts.report.reporting_contact_set",
            Actor: actor, ActorRole: role, Target: $"{companyId}:{contactId}",
            ClientIp: http.Connection.RemoteIpAddress?.ToString(),
            UserAgent: http.Request.Headers.UserAgent.ToString(),
            Payload: new { isReporting = req.IsReporting }), ct);

        return Results.Ok(new { ok = true });
    }

    // ── Preview + send ────────────────────────────────────────────────────

    private static async Task<IResult> Preview(
        Guid companyId, [FromBody] SendBody req, HttpContext http, IUserService users,
        IM365ReportSender sender, CancellationToken ct)
    {
        var (_, deny) = await RequireContractsFlagAsync(http, users, ct);
        if (deny is not null) return deny;

        var input = ToInput(companyId, req);
        var preview = await sender.PreviewAsync(input, ct);
        if (preview is null) return Results.NotFound(new { error = "Template not found." });

        return Results.Ok(MapPreview(preview));
    }

    private static async Task<IResult> Send(
        Guid companyId, [FromBody] SendBody req, HttpContext http, IUserService users,
        IM365ReportSender sender, IAuditLogger audit, CancellationToken ct)
    {
        var (_, deny) = await RequireContractsFlagAsync(http, users, ct);
        if (deny is not null) return deny;

        var input = ToInput(companyId, req);
        var actor = ResolveActor(http);
        var result = await sender.SendAsync(input, actor, ct);

        var (auditActor, role) = ActorContext.Resolve(http);
        await audit.LogAsync(new AuditEvent(
            EventType: result.Ok ? "contracts.report.sent" : "contracts.report.send_failed",
            Actor: auditActor, ActorRole: role, Target: companyId.ToString(),
            ClientIp: http.Connection.RemoteIpAddress?.ToString(),
            UserAgent: http.Request.Headers.UserAgent.ToString(),
            Payload: new { templateId = req.TemplateId, ok = result.Ok }), ct);

        if (!result.Ok)
            return Results.BadRequest(new { error = result.Error ?? "Sending failed." });

        return Results.Ok(new { ok = true, sendId = result.SendId, internetMessageId = result.InternetMessageId });
    }

    private static async Task<IResult> ListLastSent(
        HttpContext http, IUserService users, IM365ReportSendLog log, CancellationToken ct)
    {
        var (_, deny) = await RequireContractsFlagAsync(http, users, ct);
        if (deny is not null) return deny;

        var map = await log.GetLastSentMapAsync(ct);
        return Results.Ok(new
        {
            items = map.Values.Select(v => new
            {
                companyId = v.CompanyId,
                sentUtc = v.SentUtc,
                status = v.Status,
                sentByName = v.SentByName,
                subject = v.Subject,
                mailboxCount = v.MailboxCount,
            }),
        });
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    private static ReportSendInput ToInput(Guid companyId, SendBody req) => new(
        companyId,
        req.TemplateId,
        req.Columns,
        req.Scope,
        req.Recipients?.Select(r => new ReportRecipient(r.Address ?? string.Empty, r.Name ?? string.Empty)).ToList());

    private static string? Validate(TemplateUpsert req)
    {
        if (req is null) return "Body is required.";
        if (string.IsNullOrWhiteSpace(req.Name) || req.Name.Trim().Length > MaxName)
            return $"Name is required and must be ≤{MaxName} characters.";
        if (req.Description is not null && req.Description.Length > MaxDescription)
            return $"Description must be ≤{MaxDescription} characters.";
        if (req.Subject is not null && req.Subject.Length > MaxSubject)
            return $"Subject must be ≤{MaxSubject} characters.";
        if (req.BodyHtml is not null && req.BodyHtml.Length > MaxBodyHtml)
            return $"Body must be ≤{MaxBodyHtml} characters.";
        if (req.Scope is not null && !ReportScope.IsValid(req.Scope))
            return "Scope must be 'all' or 'unprotected'.";
        if (req.Columns is not null && req.Columns.Any(c => !string.IsNullOrWhiteSpace(c) && !ReportColumns.IsValid(c)))
            return "columns contains an unknown column key.";
        return null;
    }

    private static object MapDto(ReportTemplate t) => new
    {
        id = t.Id,
        name = t.Name,
        description = t.Description,
        subject = t.Subject,
        bodyHtml = t.BodyHtml,
        queueId = t.QueueId,
        columns = t.Columns,
        scope = t.Scope,
        attachPdf = t.AttachPdf,
        isActive = t.IsActive,
        createdUtc = t.CreatedUtc,
        updatedUtc = t.UpdatedUtc,
    };

    private static object MapPreview(ReportPreviewResult p) => new
    {
        subject = p.Subject,
        bodyHtml = p.BodyHtml,
        recipients = p.Recipients.Select(r => new { address = r.Address, name = r.Name }),
        fromAddress = p.FromAddress,
        attachPdf = p.AttachPdf,
        scope = p.Scope,
        columns = p.Columns,
        mailboxCount = p.MailboxCount,
        spamProtected = p.SpamProtected,
        spamTotal = p.SpamTotal,
        exchangeProtected = p.ExchangeProtected,
        exchangeTotal = p.ExchangeTotal,
        onedriveProtected = p.OneDriveProtected,
        onedriveTotal = p.OneDriveTotal,
        warnings = p.Warnings,
    };

    public sealed record TemplateUpsert(
        [Required] string? Name,
        string? Description,
        string? Subject,
        string? BodyHtml,
        Guid? QueueId,
        IReadOnlyList<string>? Columns,
        string? Scope,
        bool? AttachPdf,
        bool? IsActive);

    public sealed record ToggleReportingRequest(bool IsReporting);

    public sealed record RecipientInput(string? Address, string? Name);

    public sealed record SendBody(
        Guid TemplateId,
        IReadOnlyList<string>? Columns,
        string? Scope,
        IReadOnlyList<RecipientInput>? Recipients);
}
