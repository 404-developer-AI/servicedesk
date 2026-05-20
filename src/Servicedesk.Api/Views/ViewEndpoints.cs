using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Servicedesk.Api.Auth;
using Servicedesk.Infrastructure.Access;
using Servicedesk.Infrastructure.Persistence.Views;

namespace Servicedesk.Api.Views;

public static class ViewEndpoints
{
    public static IEndpointRouteBuilder MapViewEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/views")
            .WithTags("Views");

        // List: every caller (agent or admin) sees only views they have been
        // granted access to via a view group or direct assignment. Admins who
        // want everything in their personal sidebar add themselves like any
        // other agent; the management surface uses /all below.
        group.MapGet("/", async (HttpContext http, IViewAccessService viewAccess, CancellationToken ct) =>
        {
            var userId = Guid.Parse(http.User.FindFirst(ClaimTypes.NameIdentifier)!.Value);
            var role = http.User.FindFirst(ClaimTypes.Role)!.Value;
            return Results.Ok(await viewAccess.GetAccessibleViewsAsync(userId, role, ct));
        }).WithName("ListViews").WithOpenApi()
          .RequireAuthorization(AuthorizationPolicies.RequireAgent);

        // Admin-only: every view in the system, used by management surfaces
        // (Settings → Views, Settings → View groups) where admins need to see
        // and assign views regardless of personal access.
        group.MapGet("/all", async (IViewRepository repo, CancellationToken ct) =>
            Results.Ok(await repo.ListAllAsync(ct)))
          .WithName("ListAllViews").WithOpenApi()
          .RequireAuthorization(AuthorizationPolicies.RequireAdmin);

        // Get: validate view access. Returns 404 if no access (prevents enumeration).
        group.MapGet("/{id:guid}", async (
            Guid id, HttpContext http, IViewRepository repo, IViewAccessService viewAccess, CancellationToken ct) =>
        {
            var view = await repo.GetAsync(id, ct);
            if (view is null) return Results.NotFound();

            var userId = Guid.Parse(http.User.FindFirst(ClaimTypes.NameIdentifier)!.Value);
            var role = http.User.FindFirst(ClaimTypes.Role)!.Value;
            if (!await viewAccess.HasViewAccessAsync(userId, role, id, ct))
                return Results.NotFound();

            return Results.Ok(view);
        }).WithName("GetView").WithOpenApi()
          .RequireAuthorization(AuthorizationPolicies.RequireAgent);

        // CUD operations are admin-only. Views are managed centrally and
        // assigned to agents via view groups or direct assignment.
        group.MapPost("/", async (
            [FromBody] ViewRequest req, HttpContext http, IViewRepository repo, IViewAccessService viewAccess, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(req.Name))
                return Results.BadRequest(new { error = "Name is required." });
            if (req.SortOrder is { } so && (so < 0 || so > 100))
                return Results.BadRequest(new { error = "SortOrder must be between 0 and 100." });
            var userId = Guid.Parse(http.User.FindFirst(ClaimTypes.NameIdentifier)!.Value);
            var created = await repo.CreateAsync(userId, req.Name.Trim(), req.FiltersJson ?? "{}", req.Columns, req.SortOrder ?? 0, req.IsShared ?? false, req.DisplayConfigJson ?? "{}", ct);
            viewAccess.InvalidateAllViewCaches();
            return Results.Created($"/api/views/{created.Id}", created);
        }).WithName("CreateView").WithOpenApi()
          .RequireAuthorization(AuthorizationPolicies.RequireAdmin);

        group.MapPut("/{id:guid}", async (
            Guid id, [FromBody] ViewRequest req, IViewRepository repo, IViewAccessService viewAccess, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(req.Name))
                return Results.BadRequest(new { error = "Name is required." });
            if (req.SortOrder is { } so && (so < 0 || so > 100))
                return Results.BadRequest(new { error = "SortOrder must be between 0 and 100." });
            var updated = await repo.UpdateAsync(id, req.Name.Trim(), req.FiltersJson ?? "{}", req.Columns, req.SortOrder ?? 0, req.IsShared ?? false, req.DisplayConfigJson ?? "{}", ct);
            if (updated is not null) viewAccess.InvalidateAllViewCaches();
            return updated is null ? Results.NotFound() : Results.Ok(updated);
        }).WithName("UpdateView").WithOpenApi()
          .RequireAuthorization(AuthorizationPolicies.RequireAdmin);

        group.MapDelete("/{id:guid}", async (Guid id, IViewRepository repo, IViewAccessService viewAccess, CancellationToken ct) =>
        {
            var deleted = await repo.DeleteAsync(id, ct);
            if (deleted) viewAccess.InvalidateAllViewCaches();
            return deleted ? Results.NoContent() : Results.NotFound();
        }).WithName("DeleteView").WithOpenApi()
          .RequireAuthorization(AuthorizationPolicies.RequireAdmin);

        return app;
    }

    public sealed record ViewRequest(
        [property: Required] string? Name,
        string? FiltersJson,
        string? Columns,
        int? SortOrder,
        bool? IsShared,
        string? DisplayConfigJson);
}
