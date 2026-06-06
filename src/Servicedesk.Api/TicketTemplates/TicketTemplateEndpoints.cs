using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Mvc;
using Servicedesk.Api.Auth;
using Servicedesk.Domain.TicketTemplates;
using Servicedesk.Infrastructure.Audit;
using Servicedesk.Infrastructure.KnowledgeBase;
using Servicedesk.Infrastructure.TicketTemplates;

namespace Servicedesk.Api.TicketTemplates;

/// HTTP surface for ticket templates — pre-canned ticket field sets an agent
/// applies while creating a ticket.
///
/// <list type="bullet">
/// <item><b>Admin</b> — CRUD over <c>ticket_templates</c> under
/// <c>/api/settings/ticket-templates</c>.</item>
/// <item><b>Agent</b> — a single read endpoint at
/// <c>/api/ticket-templates/usable</c> returning the active templates the
/// New-Ticket drawer lists in its picker. Token resolution for the
/// subject/body/initial-note placeholders reuses the existing
/// <c>/api/compose-templates/resolve</c> endpoint.</item>
/// </list>
public static class TicketTemplateEndpoints
{
    private const int MaxNameLength = 200;
    private const int MaxDescriptionLength = 2000;
    private const int MaxSubjectLength = 1000;
    private const int MaxHtmlLength = 200_000;

    public static IEndpointRouteBuilder MapTicketTemplateEndpoints(this IEndpointRouteBuilder app)
    {
        MapAdminEndpoints(app);
        MapAgentEndpoints(app);
        return app;
    }

    private static void MapAdminEndpoints(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/settings/ticket-templates")
            .WithTags("TicketTemplates")
            .RequireAuthorization(AuthorizationPolicies.RequireAdmin);

        group.MapGet("/", async (
            bool? includeInactive, ITicketTemplateRepository repo, CancellationToken ct) =>
        {
            var templates = await repo.ListAsync(includeInactive ?? true, ct);
            return Results.Ok(templates.Select(MapDto));
        }).WithName("ListTicketTemplates").WithOpenApi();

        group.MapGet("/{id:guid}", async (
            Guid id, ITicketTemplateRepository repo, CancellationToken ct) =>
        {
            var template = await repo.GetAsync(id, ct);
            return template is null ? Results.NotFound() : Results.Ok(MapDto(template));
        }).WithName("GetTicketTemplate").WithOpenApi();

        group.MapPost("/", async (
            [FromBody] UpsertRequest req, HttpContext http,
            ITicketTemplateRepository repo, IKbHtmlSanitizer sanitizer,
            IAuditLogger audit, CancellationToken ct) =>
        {
            var err = Validate(req);
            if (err is not null) return Results.BadRequest(new { error = err });

            var userId = Guid.Parse(http.User.FindFirst(ClaimTypes.NameIdentifier)!.Value);
            var template = ToDomain(Guid.Empty, req, sanitizer, userId);

            Guid id;
            try
            {
                id = await repo.CreateAsync(template, ct);
            }
            catch (Npgsql.PostgresException pg) when (pg.SqlState == "23505" && pg.ConstraintName == "ux_ticket_templates_active_name")
            {
                return Results.Conflict(new
                {
                    error = "An active template with that name already exists. Pick a different name or reactivate the existing one.",
                });
            }

            var (actor, role) = ActorContext.Resolve(http);
            await audit.LogAsync(new AuditEvent(
                EventType: "ticket_template.create",
                Actor: actor,
                ActorRole: role,
                Target: id.ToString(),
                ClientIp: http.Connection.RemoteIpAddress?.ToString(),
                UserAgent: http.Request.Headers.UserAgent.ToString(),
                Payload: new { name = req.Name }));

            var created = await repo.GetAsync(id, ct);
            return Results.Created($"/api/settings/ticket-templates/{id}", MapDto(created!));
        }).WithName("CreateTicketTemplate").WithOpenApi();

        group.MapPut("/{id:guid}", async (
            Guid id, [FromBody] UpsertRequest req, HttpContext http,
            ITicketTemplateRepository repo, IKbHtmlSanitizer sanitizer,
            IAuditLogger audit, CancellationToken ct) =>
        {
            var err = Validate(req);
            if (err is not null) return Results.BadRequest(new { error = err });

            var existing = await repo.GetAsync(id, ct);
            if (existing is null) return Results.NotFound();

            var template = ToDomain(id, req, sanitizer, existing.CreatedBy);

            try
            {
                await repo.UpdateAsync(template, ct);
            }
            catch (Npgsql.PostgresException pg) when (pg.SqlState == "23505" && pg.ConstraintName == "ux_ticket_templates_active_name")
            {
                return Results.Conflict(new
                {
                    error = "Another active template already uses that name. Pick a different name before saving.",
                });
            }

            var (actor, role) = ActorContext.Resolve(http);
            await audit.LogAsync(new AuditEvent(
                EventType: "ticket_template.update",
                Actor: actor,
                ActorRole: role,
                Target: id.ToString(),
                ClientIp: http.Connection.RemoteIpAddress?.ToString(),
                UserAgent: http.Request.Headers.UserAgent.ToString(),
                Payload: new { isActive = req.IsActive }));

            var updated = await repo.GetAsync(id, ct);
            return Results.Ok(MapDto(updated!));
        }).WithName("UpdateTicketTemplate").WithOpenApi();

        group.MapDelete("/{id:guid}", async (
            Guid id, HttpContext http, ITicketTemplateRepository repo,
            IAuditLogger audit, CancellationToken ct) =>
        {
            var deleted = await repo.DeleteAsync(id, ct);
            if (!deleted) return Results.NotFound();

            var (actor, role) = ActorContext.Resolve(http);
            await audit.LogAsync(new AuditEvent(
                EventType: "ticket_template.delete",
                Actor: actor,
                ActorRole: role,
                Target: id.ToString(),
                ClientIp: http.Connection.RemoteIpAddress?.ToString(),
                UserAgent: http.Request.Headers.UserAgent.ToString()));

            return Results.NoContent();
        }).WithName("DeleteTicketTemplate").WithOpenApi();
    }

    private static void MapAgentEndpoints(IEndpointRouteBuilder app)
    {
        // The New-Ticket drawer pulls the full active list once when it opens.
        // Small table, no per-keystroke calls — a plain "active templates"
        // read is enough. Agents may apply any active template (admin scopes
        // visibility purely through the active flag).
        app.MapGet("/api/ticket-templates/usable", async (
            ITicketTemplateRepository repo, CancellationToken ct) =>
        {
            var templates = await repo.ListActiveAsync(ct);
            return Results.Ok(templates.Select(MapDto));
        })
        .WithTags("TicketTemplatesAgent")
        .WithName("ListUsableTicketTemplates")
        .RequireAuthorization(AuthorizationPolicies.RequireAgent)
        .WithOpenApi();
    }

    public sealed record UpsertRequest(
        [Required] string? Name,
        string? Description,
        bool? IsActive,
        string? Subject,
        string? BodyHtml,
        string? InitialNoteHtml,
        bool? InitialNoteInternal,
        Guid? QueueId,
        Guid? PriorityId,
        Guid? StatusId,
        Guid? CategoryId,
        Guid? TicketTypeId,
        Guid? AssigneeUserId);

    private static string? Validate(UpsertRequest req)
    {
        if (req is null) return "Body is required.";
        if (string.IsNullOrWhiteSpace(req.Name) || req.Name.Trim().Length > MaxNameLength)
            return $"Name is required and must be ≤{MaxNameLength} characters.";
        if (req.Description is not null && req.Description.Length > MaxDescriptionLength)
            return $"Description must be ≤{MaxDescriptionLength} characters.";
        if (req.Subject is not null && req.Subject.Length > MaxSubjectLength)
            return $"Subject must be ≤{MaxSubjectLength} characters.";
        if (req.BodyHtml is not null && req.BodyHtml.Length > MaxHtmlLength)
            return $"Body must be ≤{MaxHtmlLength} characters.";
        if (req.InitialNoteHtml is not null && req.InitialNoteHtml.Length > MaxHtmlLength)
            return $"Initial note must be ≤{MaxHtmlLength} characters.";
        return null;
    }

    private static TicketTemplate ToDomain(
        Guid id, UpsertRequest req, IKbHtmlSanitizer sanitizer, Guid? createdBy) => new(
        Id: id,
        Name: req.Name!.Trim(),
        Description: string.IsNullOrWhiteSpace(req.Description) ? null : req.Description.Trim(),
        IsActive: req.IsActive ?? true,
        // Subject is plain text rendered into the form's <input> and the ticket
        // subject; trim only. Token substitution happens client-side on apply.
        Subject: (req.Subject ?? string.Empty).Trim(),
        BodyHtml: sanitizer.Sanitize(req.BodyHtml ?? string.Empty),
        InitialNoteHtml: sanitizer.Sanitize(req.InitialNoteHtml ?? string.Empty),
        InitialNoteInternal: req.InitialNoteInternal ?? true,
        QueueId: req.QueueId,
        PriorityId: req.PriorityId,
        StatusId: req.StatusId,
        CategoryId: req.CategoryId,
        TicketTypeId: req.TicketTypeId,
        AssigneeUserId: req.AssigneeUserId,
        CreatedUtc: default,
        UpdatedUtc: default,
        CreatedBy: createdBy);

    private static object MapDto(TicketTemplate t) => new
    {
        id = t.Id,
        name = HtmlEncoder.Default.Encode(t.Name),
        description = t.Description is null ? null : HtmlEncoder.Default.Encode(t.Description),
        isActive = t.IsActive,
        // Subject is plain text returned raw: it lands in the New-Ticket
        // form's <input value> and the created ticket's subject, both of
        // which React renders as escaped text. Encoding here would surface
        // a literal "&amp;" in the input for a subject containing "&".
        subject = t.Subject,
        // body_html / initial_note_html are admin-authored sanitised HTML —
        // returned raw so the editor round-trips the markup.
        bodyHtml = t.BodyHtml,
        initialNoteHtml = t.InitialNoteHtml,
        initialNoteInternal = t.InitialNoteInternal,
        queueId = t.QueueId,
        priorityId = t.PriorityId,
        statusId = t.StatusId,
        categoryId = t.CategoryId,
        ticketTypeId = t.TicketTypeId,
        assigneeUserId = t.AssigneeUserId,
        createdUtc = t.CreatedUtc,
        updatedUtc = t.UpdatedUtc,
    };
}
