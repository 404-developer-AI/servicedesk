using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Servicedesk.Api.Auth;
using Servicedesk.Infrastructure.Audit;
using Servicedesk.Infrastructure.Triggers;

namespace Servicedesk.Api.Triggers;

/// Admin CRUD for trigger groups. Groups are a pure UX construct — the
/// evaluator and scheduler never look at them — so the endpoints below
/// stay narrowly focussed on the admin's drag-and-drop flow.
public static class TriggerGroupEndpoints
{
    public static IEndpointRouteBuilder MapTriggerGroupEndpoints(this IEndpointRouteBuilder app)
    {
        var admin = app.MapGroup("/api/admin/trigger-groups")
            .WithTags("Triggers")
            .RequireAuthorization(AuthorizationPolicies.RequireAdmin);

        admin.MapGet("/", async (ITriggerGroupRepository repo, CancellationToken ct) =>
        {
            var groups = await repo.ListAllAsync(ct);
            return Results.Ok(new { items = groups.Select(Project).ToArray() });
        }).WithName("ListTriggerGroups").WithOpenApi();

        admin.MapPost("/", async (
            [FromBody] TriggerGroupInput req, HttpContext http,
            ITriggerGroupRepository repo, IAuditLogger audit, CancellationToken ct) =>
        {
            var name = (req.Name ?? string.Empty).Trim();
            if (string.IsNullOrEmpty(name))
                return Results.BadRequest(new { error = "Name is required." });
            if (name.Length > 100)
                return Results.BadRequest(new { error = "Name must be 100 characters or fewer." });

            var row = await repo.CreateAsync(new NewTriggerGroup(name, NormalizeColor(req.Color)), ct);
            await LogAsync(audit, http, TriggerAuditEventTypes.GroupCreated, row.Id.ToString(), new { row.Name });
            return Results.Created($"/api/admin/trigger-groups/{row.Id}", Project(row));
        }).WithName("CreateTriggerGroup").WithOpenApi();

        admin.MapPut("/{id:guid}", async (
            Guid id, [FromBody] TriggerGroupInput req, HttpContext http,
            ITriggerGroupRepository repo, IAuditLogger audit, CancellationToken ct) =>
        {
            var name = (req.Name ?? string.Empty).Trim();
            if (string.IsNullOrEmpty(name))
                return Results.BadRequest(new { error = "Name is required." });
            if (name.Length > 100)
                return Results.BadRequest(new { error = "Name must be 100 characters or fewer." });

            var row = await repo.UpdateAsync(id, new UpdateTriggerGroup(name, NormalizeColor(req.Color)), ct);
            if (row is null) return Results.NotFound();
            await LogAsync(audit, http, TriggerAuditEventTypes.GroupUpdated, id.ToString(), new { row.Name });
            return Results.Ok(Project(row));
        }).WithName("UpdateTriggerGroup").WithOpenApi();

        admin.MapDelete("/{id:guid}", async (
            Guid id, HttpContext http,
            ITriggerGroupRepository repo, IAuditLogger audit, CancellationToken ct) =>
        {
            // FK `triggers.group_id` is ON DELETE SET NULL, so members
            // become Ungrouped instead of being lost.
            var ok = await repo.DeleteAsync(id, ct);
            if (!ok) return Results.NotFound();
            await LogAsync(audit, http, TriggerAuditEventTypes.GroupDeleted, id.ToString(), null);
            return Results.NoContent();
        }).WithName("DeleteTriggerGroup").WithOpenApi();

        admin.MapPost("/reorder", async (
            [FromBody] ReorderGroupsInput req, HttpContext http,
            ITriggerGroupRepository repo, IAuditLogger audit, CancellationToken ct) =>
        {
            if (req.Items is null || req.Items.Count == 0)
                return Results.NoContent();

            var placements = req.Items
                .Where(i => i.Id != Guid.Empty)
                .Select(i => new TriggerGroupPlacement(i.Id, i.SortOrder))
                .ToList();
            if (placements.Count == 0) return Results.NoContent();

            await repo.ReorderAsync(placements, ct);
            await LogAsync(audit, http, TriggerAuditEventTypes.GroupReordered, null, new { count = placements.Count });
            return Results.NoContent();
        }).WithName("ReorderTriggerGroups").WithOpenApi();

        return app;
    }

    private static object Project(TriggerGroupRow row) => new
    {
        id = row.Id,
        name = row.Name,
        color = row.Color,
        sortOrder = row.SortOrder,
        createdUtc = row.CreatedUtc,
        updatedUtc = row.UpdatedUtc,
    };

    private static string? NormalizeColor(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var trimmed = raw.Trim();
        return trimmed.Length > 20 ? trimmed[..20] : trimmed;
    }

    private static async Task LogAsync(IAuditLogger audit, HttpContext http, string eventType, string? target, object? payload)
    {
        var actor = http.User.FindFirst(ClaimTypes.Email)?.Value ?? "unknown";
        var role = http.User.FindFirst(ClaimTypes.Role)?.Value ?? "unknown";
        await audit.LogAsync(new AuditEvent(
            EventType: eventType,
            Actor: actor,
            ActorRole: role,
            Target: target,
            Payload: payload ?? new { }));
    }

    public sealed record TriggerGroupInput(
        [property: Required] string? Name,
        string? Color);

    public sealed record ReorderGroupItem(Guid Id, int SortOrder);
    public sealed record ReorderGroupsInput(IReadOnlyList<ReorderGroupItem>? Items);
}
