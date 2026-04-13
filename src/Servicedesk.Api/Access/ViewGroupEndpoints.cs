using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using Servicedesk.Api.Auth;
using Servicedesk.Infrastructure.Access;
using Servicedesk.Infrastructure.Audit;
using Servicedesk.Infrastructure.Persistence.ViewGroups;

namespace Servicedesk.Api.Access;

/// Admin-only CRUD for view groups: bundles of views assigned to agents.
public static class ViewGroupEndpoints
{
    public static IEndpointRouteBuilder MapViewGroupEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/admin/view-groups")
            .WithTags("View Groups")
            .RequireAuthorization(AuthorizationPolicies.RequireAdmin);

        group.MapGet("/", async (IViewGroupRepository repo, CancellationToken ct) =>
        {
            return Results.Ok(await repo.ListAsync(ct));
        }).WithName("ListViewGroups").WithOpenApi();

        group.MapGet("/{id:guid}", async (Guid id, IViewGroupRepository repo, CancellationToken ct) =>
        {
            var detail = await repo.GetDetailAsync(id, ct);
            return detail is null ? Results.NotFound() : Results.Ok(detail);
        }).WithName("GetViewGroup").WithOpenApi();

        group.MapPost("/", async (
            [FromBody] ViewGroupRequest req, IViewGroupRepository repo,
            HttpContext http, IAuditLogger audit, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(req.Name))
                return Results.BadRequest(new { error = "Name is required." });

            var created = await repo.CreateAsync(req.Name.Trim(), req.Description ?? "", req.SortOrder ?? 0, ct);

            var (actor, role) = ActorContext.Resolve(http);
            await audit.LogAsync(new AuditEvent(
                EventType: "view_group.created",
                Actor: actor,
                ActorRole: role,
                Target: created.Id.ToString(),
                ClientIp: http.Connection.RemoteIpAddress?.ToString(),
                UserAgent: http.Request.Headers.UserAgent.ToString(),
                Payload: new { created.Name }));

            return Results.Created($"/api/admin/view-groups/{created.Id}", created);
        }).WithName("CreateViewGroup").WithOpenApi();

        group.MapPut("/{id:guid}", async (
            Guid id, [FromBody] ViewGroupRequest req, IViewGroupRepository repo,
            HttpContext http, IAuditLogger audit, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(req.Name))
                return Results.BadRequest(new { error = "Name is required." });

            var updated = await repo.UpdateAsync(id, req.Name.Trim(), req.Description ?? "", req.SortOrder ?? 0, ct);
            if (updated is null) return Results.NotFound();

            var (actor, role) = ActorContext.Resolve(http);
            await audit.LogAsync(new AuditEvent(
                EventType: "view_group.updated",
                Actor: actor,
                ActorRole: role,
                Target: id.ToString(),
                ClientIp: http.Connection.RemoteIpAddress?.ToString(),
                UserAgent: http.Request.Headers.UserAgent.ToString(),
                Payload: new { updated.Name }));

            return Results.Ok(updated);
        }).WithName("UpdateViewGroup").WithOpenApi();

        group.MapDelete("/{id:guid}", async (
            Guid id, IViewGroupRepository repo,
            HttpContext http, IAuditLogger audit, CancellationToken ct) =>
        {
            var deleted = await repo.DeleteAsync(id, ct);
            if (!deleted) return Results.NotFound();

            var (actor, role) = ActorContext.Resolve(http);
            await audit.LogAsync(new AuditEvent(
                EventType: "view_group.deleted",
                Actor: actor,
                ActorRole: role,
                Target: id.ToString(),
                ClientIp: http.Connection.RemoteIpAddress?.ToString(),
                UserAgent: http.Request.Headers.UserAgent.ToString(),
                Payload: new { }));

            return Results.NoContent();
        }).WithName("DeleteViewGroup").WithOpenApi();

        group.MapPut("/{id:guid}/members", async (
            Guid id, [FromBody] SetMembersRequest req,
            IViewGroupRepository repo, IViewAccessService viewAccess,
            HttpContext http, IAuditLogger audit, CancellationToken ct) =>
        {
            var existing = await repo.GetDetailAsync(id, ct);
            if (existing is null) return Results.NotFound();

            await repo.SetMembersAsync(id, req.UserIds, ct);

            // Invalidate view-access cache for all affected users (old + new members)
            var allUserIds = existing.Members.Select(m => m.UserId).Union(req.UserIds).Distinct();
            foreach (var uid in allUserIds) viewAccess.InvalidateCache(uid);

            var (actor, role) = ActorContext.Resolve(http);
            await audit.LogAsync(new AuditEvent(
                EventType: "view_group.members_changed",
                Actor: actor,
                ActorRole: role,
                Target: id.ToString(),
                ClientIp: http.Connection.RemoteIpAddress?.ToString(),
                UserAgent: http.Request.Headers.UserAgent.ToString(),
                Payload: new { userIds = req.UserIds }));

            return Results.NoContent();
        }).WithName("SetViewGroupMembers").WithOpenApi();

        group.MapPut("/{id:guid}/views", async (
            Guid id, [FromBody] SetViewsRequest req,
            IViewGroupRepository repo, IViewAccessService viewAccess,
            HttpContext http, IAuditLogger audit, CancellationToken ct) =>
        {
            var existing = await repo.GetDetailAsync(id, ct);
            if (existing is null) return Results.NotFound();

            await repo.SetViewsAsync(id, req.ViewIds, ct);

            // Invalidate view-access cache for all members of this group
            foreach (var m in existing.Members) viewAccess.InvalidateCache(m.UserId);

            var (actor, role) = ActorContext.Resolve(http);
            await audit.LogAsync(new AuditEvent(
                EventType: "view_group.views_changed",
                Actor: actor,
                ActorRole: role,
                Target: id.ToString(),
                ClientIp: http.Connection.RemoteIpAddress?.ToString(),
                UserAgent: http.Request.Headers.UserAgent.ToString(),
                Payload: new { viewIds = req.ViewIds }));

            return Results.NoContent();
        }).WithName("SetViewGroupViews").WithOpenApi();

        return app;
    }

    public sealed record ViewGroupRequest(
        [property: Required] string? Name,
        string? Description,
        int? SortOrder);

    public sealed record SetMembersRequest(IReadOnlyList<Guid> UserIds);
    public sealed record SetViewsRequest(IReadOnlyList<Guid> ViewIds);
}
