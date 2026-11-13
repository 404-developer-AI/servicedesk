using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Servicedesk.Api.Auth;
using Servicedesk.Infrastructure.Audit;
using Servicedesk.Infrastructure.Forms;

namespace Servicedesk.Api.Forms;

/// v0.1.52 — admin CRUD for form templates (Settings → Templates → Forms).
/// Filled forms are snapshots: editing or deleting a template never touches
/// a form already filled in on a ticket.
public static class FormTemplateEndpoints
{
    public static IEndpointRouteBuilder MapFormTemplateEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/settings/form-templates")
            .WithTags("Forms")
            .RequireAuthorization(AuthorizationPolicies.RequireAdmin);

        group.MapGet("/", async (IFormTemplateRepository repo, CancellationToken ct) =>
        {
            var list = await repo.ListAsync(ct);
            return Results.Ok(new { items = list.Select(MapSummary) });
        }).WithName("ListFormTemplates").WithOpenApi();

        group.MapGet("/{id:guid}", async (Guid id, IFormTemplateRepository repo, CancellationToken ct) =>
        {
            var t = await repo.GetAsync(id, ct);
            return t is null ? Results.NotFound() : Results.Ok(MapDetail(t));
        }).WithName("GetFormTemplate").WithOpenApi();

        group.MapPost("/", async (
            [FromBody] FormTemplateRequest req, HttpContext http,
            IFormTemplateRepository repo, IFormSettingsReader settings,
            IAuditLogger audit, CancellationToken ct) =>
        {
            var (input, err) = await NormalizeAsync(req, settings, ct);
            if (err is not null || input is null) return Results.BadRequest(new { error = err ?? "Invalid form." });
            if (input.IsActive && await repo.ActiveNameTakenAsync(input.Name, null, ct))
                return Results.Conflict(new { error = "An active form with this name already exists." });

            var id = await repo.CreateAsync(input, CurrentUserId(http), ct);
            await Audit(audit, http, "form_template.created", id,
                new { input.Name, fieldCount = input.Definition.Fields.Count, queueCount = input.QueueIds.Count });
            var created = await repo.GetAsync(id, ct);
            return Results.Created($"/api/settings/form-templates/{id}", MapDetail(created!));
        }).WithName("CreateFormTemplate").WithOpenApi();

        group.MapPut("/{id:guid}", async (
            Guid id, [FromBody] FormTemplateRequest req, HttpContext http,
            IFormTemplateRepository repo, IFormSettingsReader settings,
            IAuditLogger audit, CancellationToken ct) =>
        {
            var (input, err) = await NormalizeAsync(req, settings, ct);
            if (err is not null || input is null) return Results.BadRequest(new { error = err ?? "Invalid form." });
            if (input.IsActive && await repo.ActiveNameTakenAsync(input.Name, id, ct))
                return Results.Conflict(new { error = "An active form with this name already exists." });

            if (!await repo.UpdateAsync(id, input, ct)) return Results.NotFound();
            await Audit(audit, http, "form_template.updated", id,
                new { input.Name, fieldCount = input.Definition.Fields.Count, input.IsActive, queueCount = input.QueueIds.Count });
            var updated = await repo.GetAsync(id, ct);
            return Results.Ok(MapDetail(updated!));
        }).WithName("UpdateFormTemplate").WithOpenApi();

        group.MapPost("/{id:guid}/duplicate", async (
            Guid id, HttpContext http, IFormTemplateRepository repo, IAuditLogger audit, CancellationToken ct) =>
        {
            var src = await repo.GetAsync(id, ct);
            if (src is null) return Results.NotFound();
            var name = src.Name.Length + 7 > FormLimits.NameMax ? src.Name[..(FormLimits.NameMax - 7)] + " (copy)" : src.Name + " (copy)";
            // New field ids so answers of the copy never collide with the
            // original's in reporting; copies start inactive so a half-edited
            // duplicate never shows up in the agents' picker.
            var def = FormDefinition.Parse(src.Definition.ToJson());
            foreach (var f in def.Fields) f.Id = Guid.NewGuid();
            var copyId = await repo.CreateAsync(
                new FormTemplateInput(name, src.Description, IsActive: false, src.QueueIds, def),
                CurrentUserId(http), ct);
            await Audit(audit, http, "form_template.duplicated", copyId, new { sourceId = id, name });
            var created = await repo.GetAsync(copyId, ct);
            return Results.Created($"/api/settings/form-templates/{copyId}", MapDetail(created!));
        }).WithName("DuplicateFormTemplate").WithOpenApi();

        group.MapDelete("/{id:guid}", async (
            Guid id, HttpContext http, IFormTemplateRepository repo, IAuditLogger audit, CancellationToken ct) =>
        {
            var existing = await repo.GetAsync(id, ct);
            if (existing is null) return Results.NotFound();
            await repo.DeleteAsync(id, ct);
            await Audit(audit, http, "form_template.deleted", id, new { existing.Name });
            return Results.NoContent();
        }).WithName("DeleteFormTemplate").WithOpenApi();

        return app;
    }

    private static Guid CurrentUserId(HttpContext http)
        => Guid.Parse(http.User.FindFirst(ClaimTypes.NameIdentifier)!.Value);

    private static async Task<(FormTemplateInput? Input, string? Error)> NormalizeAsync(
        FormTemplateRequest req, IFormSettingsReader settings, CancellationToken ct)
    {
        if (req is null) return (null, "Invalid form.");
        var err = FormTemplateValidator.ValidateName(req.Name)
                  ?? FormTemplateValidator.ValidateDescription(req.Description);
        if (err is not null) return (null, err);

        var def = new FormDefinition
        {
            Fields = (req.Fields ?? new List<FormField>()).Where(f => f is not null).ToList(),
        };
        var runtime = await settings.GetAsync(ct);
        err = FormTemplateValidator.ValidateAndNormalize(def, runtime.MaxFieldsPerTemplate);
        if (err is not null) return (null, err);

        var queueIds = (req.QueueIds ?? Array.Empty<Guid>()).Where(q => q != Guid.Empty).Distinct().ToList();
        if (queueIds.Count > 500) return (null, "Too many queues.");
        return (new FormTemplateInput(
            req.Name!.Trim(), (req.Description ?? string.Empty).Trim(),
            req.IsActive ?? true, queueIds, def), null);
    }

    private static object MapSummary(FormTemplateSummary t) => new
    {
        id = t.Id,
        name = t.Name,
        description = t.Description,
        isActive = t.IsActive,
        queueIds = t.QueueIds,
        fieldCount = t.FieldCount,
        createdUtc = t.CreatedUtc,
        updatedUtc = t.UpdatedUtc,
    };

    internal static object MapDetail(FormTemplateDetail t) => new
    {
        id = t.Id,
        name = t.Name,
        description = t.Description,
        isActive = t.IsActive,
        queueIds = t.QueueIds,
        fieldCount = t.FieldCount,
        createdUtc = t.CreatedUtc,
        updatedUtc = t.UpdatedUtc,
        fields = t.Definition.Fields,
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

    public sealed record FormTemplateRequest(
        string? Name,
        string? Description,
        bool? IsActive,
        IReadOnlyList<Guid>? QueueIds,
        List<FormField>? Fields);
}
