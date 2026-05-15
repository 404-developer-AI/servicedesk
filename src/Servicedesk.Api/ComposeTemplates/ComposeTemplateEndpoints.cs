using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Mvc;
using Servicedesk.Api.Auth;
using Servicedesk.Domain.ComposeTemplates;
using Servicedesk.Infrastructure.Audit;
using Servicedesk.Infrastructure.ComposeTemplates;
using Servicedesk.Infrastructure.KnowledgeBase;
using Servicedesk.Infrastructure.Persistence.Tickets;
using Servicedesk.Infrastructure.Access;

namespace Servicedesk.Api.ComposeTemplates;

/// HTTP surface for the Templates feature — pre-canned HTML snippets the
/// agent pulls into an editor (internal note, public reply, outgoing mail,
/// mail reply) via the `::` mention picker.
///
/// <list type="bullet">
/// <item><b>Admin</b> — CRUD over <c>compose_templates</c> under
/// <c>/api/settings/compose-templates</c>. Same auth posture as
/// intake-form admin endpoints.</item>
/// <item><b>Agent</b> — a single read endpoint at
/// <c>/api/compose-templates/usable</c> that returns templates visible to
/// the caller for a given queue. The :: picker hits this on every keystroke
/// (cheap — small table, GIN index on queue_ids).</item>
/// </list>
public static class ComposeTemplateEndpoints
{
    private const int MaxNameLength = 200;
    private const int MaxDescriptionLength = 2000;
    /// Loose cap. The HTML is admin-authored and sanitised, but a runaway
    /// paste shouldn't be able to drop a megabyte into the editor.
    private const int MaxBodyHtmlLength = 200_000;
    private const int MaxQueueAssignments = 256;

    public static IEndpointRouteBuilder MapComposeTemplateEndpoints(this IEndpointRouteBuilder app)
    {
        MapAdminEndpoints(app);
        MapAgentEndpoints(app);
        return app;
    }

    private static void MapAdminEndpoints(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/settings/compose-templates")
            .WithTags("ComposeTemplates")
            .RequireAuthorization(AuthorizationPolicies.RequireAdmin);

        group.MapGet("/", async (
            bool? includeInactive,
            IComposeTemplateRepository repo,
            CancellationToken ct) =>
        {
            var templates = await repo.ListAsync(includeInactive ?? true, ct);
            return Results.Ok(templates.Select(MapDto));
        }).WithName("ListComposeTemplates").WithOpenApi();

        group.MapGet("/{id:guid}", async (
            Guid id, IComposeTemplateRepository repo, CancellationToken ct) =>
        {
            var template = await repo.GetAsync(id, ct);
            return template is null ? Results.NotFound() : Results.Ok(MapDto(template));
        }).WithName("GetComposeTemplate").WithOpenApi();

        group.MapPost("/", async (
            [FromBody] UpsertRequest req, HttpContext http,
            IComposeTemplateRepository repo, IKbHtmlSanitizer sanitizer,
            IAuditLogger audit, CancellationToken ct) =>
        {
            var err = Validate(req);
            if (err is not null) return Results.BadRequest(new { error = err });

            var userId = Guid.Parse(http.User.FindFirst(ClaimTypes.NameIdentifier)!.Value);
            var sanitized = sanitizer.Sanitize(req.BodyHtml);

            Guid id;
            try
            {
                id = await repo.CreateAsync(
                    req.Name!.Trim(),
                    string.IsNullOrWhiteSpace(req.Description) ? null : req.Description.Trim(),
                    sanitized,
                    req.QueueIds ?? Array.Empty<Guid>(),
                    req.LinkedSurveyId,
                    userId,
                    ct);
            }
            catch (Npgsql.PostgresException pg) when (pg.SqlState == "23505" && pg.ConstraintName == "ux_compose_templates_active_name")
            {
                return Results.Conflict(new
                {
                    error = "An active template with that name already exists. Pick a different name or reactivate the existing one.",
                });
            }

            var (actor, role) = ActorContext.Resolve(http);
            await audit.LogAsync(new AuditEvent(
                EventType: "compose_template.create",
                Actor: actor,
                ActorRole: role,
                Target: id.ToString(),
                ClientIp: http.Connection.RemoteIpAddress?.ToString(),
                UserAgent: http.Request.Headers.UserAgent.ToString(),
                Payload: new { queueScope = req.QueueIds?.Count ?? 0 }));

            var created = await repo.GetAsync(id, ct);
            return Results.Created($"/api/settings/compose-templates/{id}", MapDto(created!));
        }).WithName("CreateComposeTemplate").WithOpenApi();

        group.MapPut("/{id:guid}", async (
            Guid id, [FromBody] UpsertRequest req, HttpContext http,
            IComposeTemplateRepository repo, IKbHtmlSanitizer sanitizer,
            IAuditLogger audit, CancellationToken ct) =>
        {
            var err = Validate(req);
            if (err is not null) return Results.BadRequest(new { error = err });

            var existing = await repo.GetAsync(id, ct);
            if (existing is null) return Results.NotFound();

            var sanitized = sanitizer.Sanitize(req.BodyHtml);

            try
            {
                await repo.UpdateAsync(
                    id,
                    req.Name!.Trim(),
                    string.IsNullOrWhiteSpace(req.Description) ? null : req.Description.Trim(),
                    sanitized,
                    req.IsActive ?? existing.IsActive,
                    req.QueueIds ?? Array.Empty<Guid>(),
                    req.LinkedSurveyId,
                    ct);
            }
            catch (Npgsql.PostgresException pg) when (pg.SqlState == "23505" && pg.ConstraintName == "ux_compose_templates_active_name")
            {
                return Results.Conflict(new
                {
                    error = "Another active template already uses that name. Pick a different name before saving.",
                });
            }

            var (actor, role) = ActorContext.Resolve(http);
            await audit.LogAsync(new AuditEvent(
                EventType: "compose_template.update",
                Actor: actor,
                ActorRole: role,
                Target: id.ToString(),
                ClientIp: http.Connection.RemoteIpAddress?.ToString(),
                UserAgent: http.Request.Headers.UserAgent.ToString(),
                Payload: new { isActive = req.IsActive, queueScope = req.QueueIds?.Count ?? 0 }));

            var updated = await repo.GetAsync(id, ct);
            return Results.Ok(MapDto(updated!));
        }).WithName("UpdateComposeTemplate").WithOpenApi();

        group.MapDelete("/{id:guid}", async (
            Guid id, HttpContext http, IComposeTemplateRepository repo,
            IAuditLogger audit, CancellationToken ct) =>
        {
            // Hard delete is safe: compose_templates have no FK references.
            // Past usages are baked into the ticket body / mail HTML — the
            // template row is just a starting point, not a live link.
            var deleted = await repo.DeleteAsync(id, ct);
            if (!deleted) return Results.NotFound();

            var (actor, role) = ActorContext.Resolve(http);
            await audit.LogAsync(new AuditEvent(
                EventType: "compose_template.delete",
                Actor: actor,
                ActorRole: role,
                Target: id.ToString(),
                ClientIp: http.Connection.RemoteIpAddress?.ToString(),
                UserAgent: http.Request.Headers.UserAgent.ToString()));

            return Results.NoContent();
        }).WithName("DeleteComposeTemplate").WithOpenApi();
    }

    private static void MapAgentEndpoints(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/compose-templates")
            .WithTags("ComposeTemplatesAgent")
            .RequireAuthorization(AuthorizationPolicies.RequireAgent);

        // The :: picker hits this on every keystroke. Queue scope filters
        // server-side so a queue-restricted template can't even be enumerated
        // by an agent who happens to be looking at a ticket in a different
        // queue. queueId is optional — the New-Ticket drawer has no queue
        // chosen yet, in which case only unrestricted templates surface.
        group.MapGet("/usable", async (
            Guid? queueId, IComposeTemplateRepository repo, CancellationToken ct) =>
        {
            var templates = await repo.ListForQueueAsync(queueId, ct);
            return Results.Ok(templates.Select(MapUsableDto));
        }).WithName("ListUsableComposeTemplates").WithOpenApi();

        // Token resolution for the :: insert flow. Either ticketId or
        // contactId (or both) must be supplied — empty calls just return
        // every supported token mapped to "". The client substitutes
        // {{token}} → value before insertContent and leaves empty values
        // as the raw placeholder so the agent notices missing data.
        // Queue access is enforced when ticketId is supplied: an agent
        // can only resolve tokens for tickets in queues they may see.
        group.MapGet("/resolve", async (
            Guid? ticketId, Guid? contactId, Guid? companyId,
            HttpContext http, IComposeTokenResolver resolver,
            ITicketRepository tickets, IQueueAccessService queueAccess,
            CancellationToken ct) =>
        {
            var agentEmail = http.User.FindFirst(ClaimTypes.Email)?.Value;

            IReadOnlyDictionary<string, string> tokens;
            if (ticketId is Guid ticketGuid)
            {
                var userId = Guid.Parse(http.User.FindFirst(ClaimTypes.NameIdentifier)!.Value);
                var role = http.User.FindFirst(ClaimTypes.Role)!.Value;
                var ticket = await tickets.GetByIdAsync(ticketGuid, ct);
                if (ticket is null) return Results.NotFound();
                if (!await queueAccess.HasQueueAccessAsync(userId, role, ticket.Ticket.QueueId, ct))
                    return Results.NotFound();

                tokens = await resolver.ResolveForTicketAsync(ticketGuid, agentEmail, ct);
            }
            else
            {
                tokens = await resolver.ResolveForContactAsync(contactId, companyId, agentEmail, ct);
            }

            return Results.Ok(new { tokens });
        }).WithName("ResolveComposeTokens").WithOpenApi();

        // Static picker metadata for the template-editor dropdown so the
        // admin sees the same labels the server knows about. Admin-only
        // because only admins author templates — no need to expose it
        // wider, and it keeps the user-facing list smaller for agents.
        app.MapGet("/api/settings/compose-templates/tokens",
            () => Results.Ok(new { tokens = ComposeTokens.Supported.Select(t => new { token = t.Token, label = t.Label }) }))
            .WithTags("ComposeTemplates")
            .WithName("ListComposeTokens")
            .RequireAuthorization(AuthorizationPolicies.RequireAdmin)
            .WithOpenApi();
    }

    public sealed record UpsertRequest(
        [Required] string? Name,
        string? Description,
        string? BodyHtml,
        bool? IsActive,
        IReadOnlyList<Guid>? QueueIds,
        Guid? LinkedSurveyId);

    private static string? Validate(UpsertRequest req)
    {
        if (req is null) return "Body is required.";
        if (string.IsNullOrWhiteSpace(req.Name) || req.Name.Trim().Length > MaxNameLength)
            return $"Name is required and must be ≤{MaxNameLength} characters.";
        if (req.Description is not null && req.Description.Length > MaxDescriptionLength)
            return $"Description must be ≤{MaxDescriptionLength} characters.";
        if (req.BodyHtml is not null && req.BodyHtml.Length > MaxBodyHtmlLength)
            return $"Body must be ≤{MaxBodyHtmlLength} characters.";
        if (req.QueueIds is not null && req.QueueIds.Count > MaxQueueAssignments)
            return $"A template may target at most {MaxQueueAssignments} queues.";
        if (req.QueueIds is not null && req.QueueIds.Distinct().Count() != req.QueueIds.Count)
            return "queueIds must not contain duplicates.";
        return null;
    }

    private static object MapDto(ComposeTemplate t) => new
    {
        id = t.Id,
        name = HtmlEncoder.Default.Encode(t.Name),
        description = t.Description is null ? null : HtmlEncoder.Default.Encode(t.Description),
        // bodyHtml is admin-authored sanitised HTML — encoding it here would
        // re-escape the markup and break round-tripping in the editor.
        bodyHtml = t.BodyHtml,
        isActive = t.IsActive,
        queueIds = t.QueueIds,
        linkedSurveyId = t.LinkedSurveyId,
        createdUtc = t.CreatedUtc,
        updatedUtc = t.UpdatedUtc,
    };

    /// Slim DTO for the :: picker — drops timestamps + creator id so the
    /// payload is roughly half the size on every keystroke.
    private static object MapUsableDto(ComposeTemplate t) => new
    {
        id = t.Id,
        name = HtmlEncoder.Default.Encode(t.Name),
        description = t.Description is null ? null : HtmlEncoder.Default.Encode(t.Description),
        bodyHtml = t.BodyHtml,
        queueIds = t.QueueIds,
        linkedSurveyId = t.LinkedSurveyId,
    };
}
