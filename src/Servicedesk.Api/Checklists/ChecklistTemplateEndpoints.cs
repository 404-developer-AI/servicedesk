using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Servicedesk.Api.Auth;
using Servicedesk.Infrastructure.Audit;
using Servicedesk.Infrastructure.Checklists;

namespace Servicedesk.Api.Checklists;

/// v0.0.103 — admin CRUD for checklist templates (Settings → Tickets →
/// Checklists). Templates are content agents attach to tickets; queue scope
/// decides where a template may be attached, so the whole surface is
/// admin-only. Attached checklists are snapshots: editing or deleting a
/// template never touches them.
public static class ChecklistTemplateEndpoints
{
    public static IEndpointRouteBuilder MapChecklistTemplateEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/settings/checklist-templates")
            .WithTags("Checklists")
            .RequireAuthorization(AuthorizationPolicies.RequireAdmin);

        group.MapGet("/", async (IChecklistTemplateRepository repo, CancellationToken ct) =>
        {
            var list = await repo.ListAsync(ct);
            return Results.Ok(new { items = list.Select(MapSummary) });
        }).WithName("ListChecklistTemplates").WithOpenApi();

        group.MapGet("/{id:guid}", async (Guid id, IChecklistTemplateRepository repo, CancellationToken ct) =>
        {
            var t = await repo.GetAsync(id, ct);
            return t is null ? Results.NotFound() : Results.Ok(MapDetail(t));
        }).WithName("GetChecklistTemplate").WithOpenApi();

        group.MapPost("/", async (
            [FromBody] ChecklistTemplateRequest req, HttpContext http,
            IChecklistTemplateRepository repo, IChecklistSettingsReader settings,
            IAuditLogger audit, CancellationToken ct) =>
        {
            var (input, err) = await NormalizeAsync(req, settings, ct);
            if (err is not null || input is null) return Results.BadRequest(new { error = err ?? "Invalid template." });

            var userId = Guid.Parse(http.User.FindFirst(ClaimTypes.NameIdentifier)!.Value);
            var id = await repo.CreateAsync(input, userId, ct);
            await Audit(audit, http, "checklist_template.created", id,
                new { input.Name, itemCount = input.Definition.ItemCount, input.BlockClose, queueCount = input.QueueIds.Count });
            var created = await repo.GetAsync(id, ct);
            return Results.Created($"/api/settings/checklist-templates/{id}", MapDetail(created!));
        }).WithName("CreateChecklistTemplate").WithOpenApi();

        group.MapPut("/{id:guid}", async (
            Guid id, [FromBody] ChecklistTemplateRequest req, HttpContext http,
            IChecklistTemplateRepository repo, IChecklistSettingsReader settings,
            IAuditLogger audit, CancellationToken ct) =>
        {
            var (input, err) = await NormalizeAsync(req, settings, ct);
            if (err is not null || input is null) return Results.BadRequest(new { error = err ?? "Invalid template." });

            var ok = await repo.UpdateAsync(id, input, ct);
            if (!ok) return Results.NotFound();
            await Audit(audit, http, "checklist_template.updated", id,
                new { input.Name, itemCount = input.Definition.ItemCount, input.BlockClose, input.IsActive, queueCount = input.QueueIds.Count });
            var updated = await repo.GetAsync(id, ct);
            return Results.Ok(MapDetail(updated!));
        }).WithName("UpdateChecklistTemplate").WithOpenApi();

        group.MapPost("/{id:guid}/duplicate", async (
            Guid id, HttpContext http, IChecklistTemplateRepository repo, IAuditLogger audit, CancellationToken ct) =>
        {
            var src = await repo.GetAsync(id, ct);
            if (src is null) return Results.NotFound();
            var name = src.Name.Length + 7 > ChecklistLimits.NameMax ? src.Name[..(ChecklistLimits.NameMax - 7)] + " (copy)" : src.Name + " (copy)";
            var userId = Guid.Parse(http.User.FindFirst(ClaimTypes.NameIdentifier)!.Value);
            // Copies start inactive so a half-edited duplicate never shows up
            // in the agents' picker by accident.
            var copyId = await repo.CreateAsync(
                new ChecklistTemplateInput(name, src.Description, IsActive: false, src.BlockClose, src.QueueIds, src.Definition),
                userId, ct);
            await Audit(audit, http, "checklist_template.duplicated", copyId, new { sourceId = id, name });
            var created = await repo.GetAsync(copyId, ct);
            return Results.Created($"/api/settings/checklist-templates/{copyId}", MapDetail(created!));
        }).WithName("DuplicateChecklistTemplate").WithOpenApi();

        group.MapDelete("/{id:guid}", async (
            Guid id, HttpContext http, IChecklistTemplateRepository repo, IAuditLogger audit, CancellationToken ct) =>
        {
            var existing = await repo.GetAsync(id, ct);
            if (existing is null) return Results.NotFound();
            await repo.DeleteAsync(id, ct);
            await Audit(audit, http, "checklist_template.deleted", id, new { existing.Name });
            return Results.NoContent();
        }).WithName("DeleteChecklistTemplate").WithOpenApi();

        return app;
    }

    private static async Task<(ChecklistTemplateInput? Input, string? Error)> NormalizeAsync(
        ChecklistTemplateRequest req, IChecklistSettingsReader settings, CancellationToken ct)
    {
        var err = ChecklistTemplateValidator.ValidateName(req.Name)
                  ?? ChecklistTemplateValidator.ValidateDescription(req.Description);
        if (err is not null) return (null, err);

        var def = new ChecklistTemplateDefinition
        {
            Sections = (req.Sections ?? new List<ChecklistTemplateSectionRequest>()).Select(s => new ChecklistTemplateSection
            {
                Title = s.Title ?? string.Empty,
                Items = (s.Items ?? new List<ChecklistTemplateItemRequest>()).Select(i => new ChecklistTemplateItem
                {
                    Title = i.Title ?? string.Empty,
                    Description = i.Description ?? string.Empty,
                    TeamLabel = i.TeamLabel ?? string.Empty,
                    TimingLabel = i.TimingLabel ?? string.Empty,
                    LinkUrl = i.LinkUrl ?? string.Empty,
                    LinkLabel = i.LinkLabel ?? string.Empty,
                    IsRequired = i.IsRequired ?? true,
                }).ToList(),
            }).ToList(),
        };
        var runtime = await settings.GetAsync(ct);
        err = ChecklistTemplateValidator.ValidateAndNormalize(def, runtime.MaxItemsPerChecklist);
        if (err is not null) return (null, err);

        var queueIds = (req.QueueIds ?? Array.Empty<Guid>()).Where(q => q != Guid.Empty).Distinct().ToList();
        return (new ChecklistTemplateInput(
            req.Name!.Trim(), (req.Description ?? string.Empty).Trim(),
            req.IsActive ?? true, req.BlockClose ?? true, queueIds, def), null);
    }

    private static object MapSummary(ChecklistTemplateSummary t) => new
    {
        id = t.Id,
        name = t.Name,
        description = t.Description,
        isActive = t.IsActive,
        blockClose = t.BlockClose,
        queueIds = t.QueueIds,
        itemCount = t.ItemCount,
        createdUtc = t.CreatedUtc,
        updatedUtc = t.UpdatedUtc,
    };

    private static object MapDetail(ChecklistTemplateDetail t) => new
    {
        id = t.Id,
        name = t.Name,
        description = t.Description,
        isActive = t.IsActive,
        blockClose = t.BlockClose,
        queueIds = t.QueueIds,
        itemCount = t.ItemCount,
        createdUtc = t.CreatedUtc,
        updatedUtc = t.UpdatedUtc,
        sections = t.Definition.Sections.Select(s => new
        {
            title = s.Title,
            items = s.Items.Select(i => new
            {
                title = i.Title,
                description = i.Description,
                teamLabel = i.TeamLabel,
                timingLabel = i.TimingLabel,
                linkUrl = i.LinkUrl,
                linkLabel = i.LinkLabel,
                isRequired = i.IsRequired,
            }),
        }),
    };

    private static async Task Audit(IAuditLogger audit, HttpContext http, string eventType, Guid target, object? payload)
    {
        var (actor, role) = ActorContext.Resolve(http);
        await audit.LogAsync(new AuditEvent(
            EventType: eventType,
            Actor: actor,
            ActorRole: role,
            Target: target.ToString(),
            ClientIp: http.Connection.RemoteIpAddress?.ToString(),
            UserAgent: http.Request.Headers.UserAgent.ToString(),
            Payload: payload));
    }

    public sealed record ChecklistTemplateRequest(
        string? Name,
        string? Description,
        bool? IsActive,
        bool? BlockClose,
        IReadOnlyList<Guid>? QueueIds,
        List<ChecklistTemplateSectionRequest>? Sections);

    public sealed record ChecklistTemplateSectionRequest(
        string? Title,
        List<ChecklistTemplateItemRequest>? Items);

    public sealed record ChecklistTemplateItemRequest(
        string? Title,
        string? Description,
        string? TeamLabel,
        string? TimingLabel,
        string? LinkUrl,
        string? LinkLabel,
        bool? IsRequired);
}
