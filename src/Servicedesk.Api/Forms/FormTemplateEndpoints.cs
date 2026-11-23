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
        }).WithName("ListFormTemplates");

        group.MapGet("/{id:guid}", async (Guid id, IFormTemplateRepository repo, CancellationToken ct) =>
        {
            var t = await repo.GetAsync(id, ct);
            return t is null ? Results.NotFound() : Results.Ok(MapDetail(t));
        }).WithName("GetFormTemplate");

        group.MapPost("/", async (
            [FromBody] FormTemplateRequest req, HttpContext http,
            IFormTemplateRepository repo, IFormSettingsReader settings,
            IAuditLogger audit, CancellationToken ct) =>
        {
            var (input, err) = await NormalizeAsync(req, settings, repo, ct);
            if (err is not null || input is null) return Results.BadRequest(new { error = err ?? "Invalid form." });
            if (input.IsActive && await repo.ActiveNameTakenAsync(input.Name, null, ct))
                return Results.Conflict(new { error = "An active form with this name already exists." });

            var id = await repo.CreateAsync(input, CurrentUserId(http), ct);
            await Audit(audit, http, "form_template.created", id,
                new { input.Name, fieldCount = input.Definition.Fields.Count, queueCount = input.QueueIds.Count, input.SendToBackoffice });
            var created = await repo.GetAsync(id, ct);
            return Results.Created($"/api/settings/form-templates/{id}", MapDetail(created!));
        }).WithName("CreateFormTemplate");

        group.MapPut("/{id:guid}", async (
            Guid id, [FromBody] FormTemplateRequest req, HttpContext http,
            IFormTemplateRepository repo, IFormSettingsReader settings,
            IAuditLogger audit, CancellationToken ct) =>
        {
            var (input, err) = await NormalizeAsync(req, settings, repo, ct);
            if (err is not null || input is null) return Results.BadRequest(new { error = err ?? "Invalid form." });
            if (input.BoPlanTemplateId == id)
                return Results.BadRequest(new { error = "'Plan with form' cannot be the form itself." });
            if (input.IsActive && await repo.ActiveNameTakenAsync(input.Name, id, ct))
                return Results.Conflict(new { error = "An active form with this name already exists." });

            if (!await repo.UpdateAsync(id, input, ct)) return Results.NotFound();
            await Audit(audit, http, "form_template.updated", id,
                new { input.Name, fieldCount = input.Definition.Fields.Count, input.IsActive, queueCount = input.QueueIds.Count, input.SendToBackoffice });
            var updated = await repo.GetAsync(id, ct);
            return Results.Ok(MapDetail(updated!));
        }).WithName("UpdateFormTemplate");

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
                new FormTemplateInput(name, src.Description, IsActive: false, src.QueueIds, def, src.SendToBackoffice,
                    src.BoPlanTemplateId, null, src.HeaderButtonLabel, src.HeaderButtonQueueIds, src.InPicker),
                CurrentUserId(http), ct);
            await Audit(audit, http, "form_template.duplicated", copyId, new { sourceId = id, name });
            var created = await repo.GetAsync(copyId, ct);
            return Results.Created($"/api/settings/form-templates/{copyId}", MapDetail(created!));
        }).WithName("DuplicateFormTemplate");

        group.MapDelete("/{id:guid}", async (
            Guid id, HttpContext http, IFormTemplateRepository repo, IAuditLogger audit, CancellationToken ct) =>
        {
            var existing = await repo.GetAsync(id, ct);
            if (existing is null) return Results.NotFound();
            await repo.DeleteAsync(id, ct);
            await Audit(audit, http, "form_template.deleted", id, new { existing.Name });
            return Results.NoContent();
        }).WithName("DeleteFormTemplate");

        return app;
    }

    private static Guid CurrentUserId(HttpContext http)
        => ActorContext.GetUserId(http);

    private static async Task<(FormTemplateInput? Input, string? Error)> NormalizeAsync(
        FormTemplateRequest req, IFormSettingsReader settings, IFormTemplateRepository templates, CancellationToken ct)
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

        // v0.1.61 — "Plan with form" only on a back-office template; the
        // target must exist (and is never the template itself — checked by
        // the caller that knows the id).
        var sendToBackoffice = req.SendToBackoffice ?? false;
        Guid? planId = sendToBackoffice && req.BoPlanTemplateId is { } p && p != Guid.Empty ? p : null;
        if (planId is { } pid && await templates.GetAsync(pid, ct) is null)
            return (null, "The form chosen under 'Plan with form' no longer exists.");

        // v0.1.61 — optional button next to the ticket title; empty = none.
        var headerButtonLabel = string.IsNullOrWhiteSpace(req.HeaderButtonLabel) ? null : req.HeaderButtonLabel.Trim();
        if (headerButtonLabel is not null)
        {
            if (headerButtonLabel.Length > FormLimits.HeaderButtonLabelMax)
                return (null, $"The button label must be at most {FormLimits.HeaderButtonLabelMax} characters.");
            if (headerButtonLabel.Any(char.IsControl))
                return (null, "The button label contains invalid characters.");
        }
        var buttonQueueIds = headerButtonLabel is null
            ? new List<Guid>()
            : (req.HeaderButtonQueueIds ?? Array.Empty<Guid>()).Where(q => q != Guid.Empty).Distinct().ToList();
        if (buttonQueueIds.Count > 500) return (null, "Too many queues.");
        if (headerButtonLabel is not null && buttonQueueIds.Count == 0)
            return (null, "Choose at least one queue for the ticket header button.");
        var inPicker = req.InPicker ?? true;

        var outlook = req.Outlook;
        if (outlook is not null)
        {
            err = outlook.Validate(def);
            if (err is not null) return (null, err);
            if (!outlook.Enabled && outlook.ToJson() == new FormOutlookMapping().ToJson()) outlook = null;
        }

        return (new FormTemplateInput(
            req.Name!.Trim(), (req.Description ?? string.Empty).Trim(),
            req.IsActive ?? true, queueIds, def, sendToBackoffice, planId, outlook, headerButtonLabel,
            buttonQueueIds, inPicker), null);
    }

    private static object MapSummary(FormTemplateSummary t) => new
    {
        id = t.Id,
        name = t.Name,
        description = t.Description,
        isActive = t.IsActive,
        queueIds = t.QueueIds,
        fieldCount = t.FieldCount,
        sendToBackoffice = t.SendToBackoffice,
        boPlanTemplateId = t.BoPlanTemplateId,
        outlookEnabled = t.OutlookEnabled,
        headerButtonLabel = t.HeaderButtonLabel,
        headerButtonQueueIds = t.HeaderButtonQueueIds ?? Array.Empty<Guid>(),
        inPicker = t.InPicker,
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
        sendToBackoffice = t.SendToBackoffice,
        boPlanTemplateId = t.BoPlanTemplateId,
        outlook = t.Outlook,
        headerButtonLabel = t.HeaderButtonLabel,
        headerButtonQueueIds = t.HeaderButtonQueueIds ?? Array.Empty<Guid>(),
        inPicker = t.InPicker,
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
        List<FormField>? Fields,
        bool? SendToBackoffice = null,
        Guid? BoPlanTemplateId = null,
        FormOutlookMapping? Outlook = null,
        string? HeaderButtonLabel = null,
        IReadOnlyList<Guid>? HeaderButtonQueueIds = null,
        bool? InPicker = null);
}
