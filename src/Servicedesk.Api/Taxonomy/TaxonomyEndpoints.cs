using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using Servicedesk.Api.Auth;
using Servicedesk.Domain.Taxonomy;
using Servicedesk.Infrastructure.Audit;
using Servicedesk.Infrastructure.Persistence.Taxonomy;

namespace Servicedesk.Api.Taxonomy;

/// Taxonomy CRUD for ticket metadata. Read endpoints (GET) are available
/// to all authenticated agents and admins so dropdowns, filters and the
/// new-ticket drawer work for everyone. Write endpoints (POST/PUT/DELETE)
/// are admin-only. Every write is audit-logged. System rows (seeded
/// defaults) can be renamed/re-colored but never deleted — the repository
/// enforces that invariant so it can't be bypassed here. Rows still
/// referenced by live tickets are also rejected for delete with a 409,
/// so admins can't accidentally orphan data.
public static class TaxonomyEndpoints
{
    public static IEndpointRouteBuilder MapTaxonomyEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/taxonomy")
            .WithTags("Taxonomy")
            .RequireAuthorization(AuthorizationPolicies.RequireAgent);

        MapQueues(group);
        MapPriorities(group);
        MapStatuses(group);
        MapCategories(group);

        return app;
    }

    // ---------- Queues ----------

    public sealed record QueueRequest(
        [property: Required] string Name,
        [property: Required] string Slug,
        string? Description,
        string? Color,
        string? Icon,
        int SortOrder,
        bool IsActive);

    private static void MapQueues(RouteGroupBuilder group)
    {
        group.MapGet("/queues", async (ITaxonomyRepository repo, CancellationToken ct) =>
            Results.Ok(await repo.ListQueuesAsync(ct)))
            .WithName("ListQueues").WithOpenApi();

        group.MapGet("/queues/{id:guid}", async (Guid id, ITaxonomyRepository repo, CancellationToken ct) =>
        {
            var q = await repo.GetQueueAsync(id, ct);
            return q is null ? Results.NotFound() : Results.Ok(q);
        }).WithName("GetQueue").WithOpenApi();

        group.MapPost("/queues", async (
            [FromBody] QueueRequest req, HttpContext http, ITaxonomyRepository repo, IAuditLogger audit, CancellationToken ct) =>
        {
            if (ValidateTaxonomyName(req.Name, req.Slug) is { } err) return err;
            var now = DateTime.UtcNow;
            var created = await repo.CreateQueueAsync(new Queue(
                Guid.Empty, req.Name.Trim(), req.Slug.Trim(), req.Description ?? "",
                Normalize(req.Color, "#7c7cff"), Normalize(req.Icon, "inbox"),
                req.SortOrder, req.IsActive, IsSystem: false, now, now), ct);
            await AuditWrite(audit, http, "taxonomy.queue.created", created.Id.ToString(), created);
            return Results.Created($"/api/taxonomy/queues/{created.Id}", created);
        }).WithName("CreateQueue").WithOpenApi()
          .RequireAuthorization(AuthorizationPolicies.RequireAdmin);

        group.MapPut("/queues/{id:guid}", async (
            Guid id, [FromBody] QueueRequest req, HttpContext http,
            ITaxonomyRepository repo, IAuditLogger audit, CancellationToken ct) =>
        {
            if (ValidateTaxonomyName(req.Name, req.Slug) is { } err) return err;
            var updated = await repo.UpdateQueueAsync(id, req.Name.Trim(), req.Slug.Trim(),
                req.Description ?? "", Normalize(req.Color, "#7c7cff"), Normalize(req.Icon, "inbox"),
                req.SortOrder, req.IsActive, ct);
            if (updated is null) return Results.NotFound();
            await AuditWrite(audit, http, "taxonomy.queue.updated", id.ToString(), updated);
            return Results.Ok(updated);
        }).WithName("UpdateQueue").WithOpenApi()
          .RequireAuthorization(AuthorizationPolicies.RequireAdmin);

        group.MapDelete("/queues/{id:guid}", async (
            Guid id, HttpContext http, ITaxonomyRepository repo, IAuditLogger audit, CancellationToken ct) =>
        {
            var result = await repo.DeleteQueueAsync(id, ct);
            return await DeleteResultToHttp(result, http, audit, "taxonomy.queue.deleted", id);
        }).WithName("DeleteQueue").WithOpenApi()
          .RequireAuthorization(AuthorizationPolicies.RequireAdmin);
    }

    // ---------- Priorities ----------

    public sealed record PriorityRequest(
        [property: Required] string Name,
        [property: Required] string Slug,
        int Level,
        string? Color,
        string? Icon,
        int SortOrder,
        bool IsActive,
        bool IsDefault);

    private static void MapPriorities(RouteGroupBuilder group)
    {
        group.MapGet("/priorities", async (ITaxonomyRepository repo, CancellationToken ct) =>
            Results.Ok(await repo.ListPrioritiesAsync(ct)))
            .WithName("ListPriorities").WithOpenApi();

        group.MapGet("/priorities/{id:guid}", async (Guid id, ITaxonomyRepository repo, CancellationToken ct) =>
        {
            var p = await repo.GetPriorityAsync(id, ct);
            return p is null ? Results.NotFound() : Results.Ok(p);
        }).WithName("GetPriority").WithOpenApi();

        group.MapPost("/priorities", async (
            [FromBody] PriorityRequest req, HttpContext http,
            ITaxonomyRepository repo, IAuditLogger audit, CancellationToken ct) =>
        {
            if (ValidateTaxonomyName(req.Name, req.Slug) is { } err) return err;
            var now = DateTime.UtcNow;
            var created = await repo.CreatePriorityAsync(new Priority(
                Guid.Empty, req.Name.Trim(), req.Slug.Trim(), req.Level,
                Normalize(req.Color, "#7c7cff"), Normalize(req.Icon, "flag"),
                req.SortOrder, req.IsActive, IsSystem: false, req.IsDefault, now, now), ct);
            await AuditWrite(audit, http, "taxonomy.priority.created", created.Id.ToString(), created);
            return Results.Created($"/api/taxonomy/priorities/{created.Id}", created);
        }).WithName("CreatePriority").WithOpenApi()
          .RequireAuthorization(AuthorizationPolicies.RequireAdmin);

        group.MapPut("/priorities/{id:guid}", async (
            Guid id, [FromBody] PriorityRequest req, HttpContext http,
            ITaxonomyRepository repo, IAuditLogger audit, CancellationToken ct) =>
        {
            if (ValidateTaxonomyName(req.Name, req.Slug) is { } err) return err;
            var updated = await repo.UpdatePriorityAsync(id, req.Name.Trim(), req.Slug.Trim(), req.Level,
                Normalize(req.Color, "#7c7cff"), Normalize(req.Icon, "flag"),
                req.SortOrder, req.IsActive, req.IsDefault, ct);
            if (updated is null) return Results.NotFound();
            await AuditWrite(audit, http, "taxonomy.priority.updated", id.ToString(), updated);
            return Results.Ok(updated);
        }).WithName("UpdatePriority").WithOpenApi()
          .RequireAuthorization(AuthorizationPolicies.RequireAdmin);

        group.MapDelete("/priorities/{id:guid}", async (
            Guid id, HttpContext http, ITaxonomyRepository repo, IAuditLogger audit, CancellationToken ct) =>
        {
            var result = await repo.DeletePriorityAsync(id, ct);
            return await DeleteResultToHttp(result, http, audit, "taxonomy.priority.deleted", id);
        }).WithName("DeletePriority").WithOpenApi()
          .RequireAuthorization(AuthorizationPolicies.RequireAdmin);
    }

    // ---------- Statuses ----------

    public sealed record StatusRequest(
        [property: Required] string Name,
        [property: Required] string Slug,
        [property: Required] string StateCategory,
        string? Color,
        string? Icon,
        int SortOrder,
        bool IsActive,
        bool IsDefault);

    private static readonly string[] AllowedStateCategories =
        ["New", "Open", "Pending", "Resolved", "Closed"];

    private static void MapStatuses(RouteGroupBuilder group)
    {
        group.MapGet("/statuses", async (ITaxonomyRepository repo, CancellationToken ct) =>
            Results.Ok(await repo.ListStatusesAsync(ct)))
            .WithName("ListStatuses").WithOpenApi();

        group.MapGet("/statuses/{id:guid}", async (Guid id, ITaxonomyRepository repo, CancellationToken ct) =>
        {
            var s = await repo.GetStatusAsync(id, ct);
            return s is null ? Results.NotFound() : Results.Ok(s);
        }).WithName("GetStatus").WithOpenApi();

        group.MapPost("/statuses", async (
            [FromBody] StatusRequest req, HttpContext http,
            ITaxonomyRepository repo, IAuditLogger audit, CancellationToken ct) =>
        {
            if (ValidateTaxonomyName(req.Name, req.Slug) is { } err) return err;
            if (!AllowedStateCategories.Contains(req.StateCategory))
                return Results.BadRequest(new { error = "Invalid stateCategory. Allowed: New, Open, Pending, Resolved, Closed." });
            var now = DateTime.UtcNow;
            var created = await repo.CreateStatusAsync(new Status(
                Guid.Empty, req.Name.Trim(), req.Slug.Trim(), req.StateCategory,
                Normalize(req.Color, "#7c7cff"), Normalize(req.Icon, "circle"),
                req.SortOrder, req.IsActive, IsSystem: false, req.IsDefault, now, now), ct);
            await AuditWrite(audit, http, "taxonomy.status.created", created.Id.ToString(), created);
            return Results.Created($"/api/taxonomy/statuses/{created.Id}", created);
        }).WithName("CreateStatus").WithOpenApi()
          .RequireAuthorization(AuthorizationPolicies.RequireAdmin);

        group.MapPut("/statuses/{id:guid}", async (
            Guid id, [FromBody] StatusRequest req, HttpContext http,
            ITaxonomyRepository repo, IAuditLogger audit, CancellationToken ct) =>
        {
            if (ValidateTaxonomyName(req.Name, req.Slug) is { } err) return err;
            if (!AllowedStateCategories.Contains(req.StateCategory))
                return Results.BadRequest(new { error = "Invalid stateCategory. Allowed: New, Open, Pending, Resolved, Closed." });
            var updated = await repo.UpdateStatusAsync(id, req.Name.Trim(), req.Slug.Trim(), req.StateCategory,
                Normalize(req.Color, "#7c7cff"), Normalize(req.Icon, "circle"),
                req.SortOrder, req.IsActive, req.IsDefault, ct);
            if (updated is null) return Results.NotFound();
            await AuditWrite(audit, http, "taxonomy.status.updated", id.ToString(), updated);
            return Results.Ok(updated);
        }).WithName("UpdateStatus").WithOpenApi()
          .RequireAuthorization(AuthorizationPolicies.RequireAdmin);

        group.MapDelete("/statuses/{id:guid}", async (
            Guid id, HttpContext http, ITaxonomyRepository repo, IAuditLogger audit, CancellationToken ct) =>
        {
            var result = await repo.DeleteStatusAsync(id, ct);
            return await DeleteResultToHttp(result, http, audit, "taxonomy.status.deleted", id);
        }).WithName("DeleteStatus").WithOpenApi()
          .RequireAuthorization(AuthorizationPolicies.RequireAdmin);
    }

    // ---------- Categories ----------

    public sealed record CategoryRequest(
        [property: Required] string Name,
        [property: Required] string Slug,
        Guid? ParentId,
        string? Description,
        int SortOrder,
        bool IsActive);

    private static void MapCategories(RouteGroupBuilder group)
    {
        group.MapGet("/categories", async (ITaxonomyRepository repo, CancellationToken ct) =>
            Results.Ok(await repo.ListCategoriesAsync(ct)))
            .WithName("ListCategories").WithOpenApi();

        group.MapGet("/categories/{id:guid}", async (Guid id, ITaxonomyRepository repo, CancellationToken ct) =>
        {
            var c = await repo.GetCategoryAsync(id, ct);
            return c is null ? Results.NotFound() : Results.Ok(c);
        }).WithName("GetCategory").WithOpenApi();

        group.MapPost("/categories", async (
            [FromBody] CategoryRequest req, HttpContext http,
            ITaxonomyRepository repo, IAuditLogger audit, CancellationToken ct) =>
        {
            if (ValidateTaxonomyName(req.Name, req.Slug) is { } err) return err;
            var now = DateTime.UtcNow;
            var created = await repo.CreateCategoryAsync(new Category(
                Guid.Empty, req.ParentId, req.Name.Trim(), req.Slug.Trim(),
                req.Description ?? "", req.SortOrder, req.IsActive, IsSystem: false, now, now), ct);
            await AuditWrite(audit, http, "taxonomy.category.created", created.Id.ToString(), created);
            return Results.Created($"/api/taxonomy/categories/{created.Id}", created);
        }).WithName("CreateCategory").WithOpenApi()
          .RequireAuthorization(AuthorizationPolicies.RequireAdmin);

        group.MapPut("/categories/{id:guid}", async (
            Guid id, [FromBody] CategoryRequest req, HttpContext http,
            ITaxonomyRepository repo, IAuditLogger audit, CancellationToken ct) =>
        {
            if (ValidateTaxonomyName(req.Name, req.Slug) is { } err) return err;
            if (req.ParentId == id)
                return Results.BadRequest(new { error = "A category cannot be its own parent." });
            var updated = await repo.UpdateCategoryAsync(id, req.ParentId, req.Name.Trim(), req.Slug.Trim(),
                req.Description ?? "", req.SortOrder, req.IsActive, ct);
            if (updated is null) return Results.NotFound();
            await AuditWrite(audit, http, "taxonomy.category.updated", id.ToString(), updated);
            return Results.Ok(updated);
        }).WithName("UpdateCategory").WithOpenApi()
          .RequireAuthorization(AuthorizationPolicies.RequireAdmin);

        group.MapDelete("/categories/{id:guid}", async (
            Guid id, HttpContext http, ITaxonomyRepository repo, IAuditLogger audit, CancellationToken ct) =>
        {
            var result = await repo.DeleteCategoryAsync(id, ct);
            return await DeleteResultToHttp(result, http, audit, "taxonomy.category.deleted", id);
        }).WithName("DeleteCategory").WithOpenApi()
          .RequireAuthorization(AuthorizationPolicies.RequireAdmin);
    }

    // ---------- Shared helpers ----------

    private static IResult? ValidateTaxonomyName(string? name, string? slug)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > 80)
            return Results.BadRequest(new { error = "Name is required and must be 80 characters or fewer." });
        if (string.IsNullOrWhiteSpace(slug) || slug.Trim().Length > 80)
            return Results.BadRequest(new { error = "Slug is required and must be 80 characters or fewer." });
        foreach (var ch in slug.Trim())
        {
            // Slugs are used in URLs, settings lookups and search filters —
            // keep them to ASCII word characters + dash.
            if (!(char.IsLetterOrDigit(ch) || ch == '-' || ch == '_'))
                return Results.BadRequest(new { error = "Slug may only contain letters, digits, dashes and underscores." });
        }
        return null;
    }

    private static string Normalize(string? value, string fallback) =>
        string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();

    private static async Task AuditWrite(IAuditLogger audit, HttpContext http, string eventType, string target, object payload)
    {
        var (actor, role) = ActorContext.Resolve(http);
        await audit.LogAsync(new AuditEvent(
            EventType: eventType,
            Actor: actor,
            ActorRole: role,
            Target: target,
            ClientIp: http.Connection.RemoteIpAddress?.ToString(),
            UserAgent: http.Request.Headers.UserAgent.ToString(),
            Payload: payload));
    }

    private static async Task<IResult> DeleteResultToHttp(
        DeleteResult result, HttpContext http, IAuditLogger audit, string eventType, Guid id)
    {
        switch (result)
        {
            case DeleteResult.Deleted:
                await AuditWrite(audit, http, eventType, id.ToString(), new { id });
                return Results.NoContent();
            case DeleteResult.NotFound:
                return Results.NotFound();
            case DeleteResult.SystemProtected:
                return Results.Conflict(new { error = "This is a system-protected or default row. Set another item as default first, or deactivate instead." });
            case DeleteResult.InUse:
                return Results.Conflict(new { error = "This row is still referenced by tickets. Reassign them first." });
            default:
                return Results.StatusCode(500);
        }
    }
}
