using Microsoft.AspNetCore.Mvc;
using Servicedesk.Api.Auth;
using Servicedesk.Infrastructure.Access;
using Servicedesk.Infrastructure.Audit;

namespace Servicedesk.Api.Access;

/// Admin-only endpoints for direct view-to-user assignments (bypassing groups).
public static class ViewAccessEndpoints
{
    public static IEndpointRouteBuilder MapViewAccessEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/admin/view-access")
            .WithTags("View Access")
            .RequireAuthorization(AuthorizationPolicies.RequireAdmin);

        group.MapGet("/{userId:guid}", async (
            Guid userId, IViewAccessService svc, CancellationToken ct) =>
        {
            var viewIds = await svc.GetDirectViewIdsAsync(userId, ct);
            return Results.Ok(new { viewIds });
        }).WithName("GetDirectViewAccess").WithOpenApi();

        group.MapPut("/{userId:guid}", async (
            Guid userId, [FromBody] SetDirectViewAccessRequest req,
            IViewAccessService svc, HttpContext http, IAuditLogger audit, CancellationToken ct) =>
        {
            await svc.SetDirectViewAccessAsync(userId, req.ViewIds, ct);

            var (actor, role) = ActorContext.Resolve(http);
            await audit.LogAsync(new AuditEvent(
                EventType: "view_access.direct_changed",
                Actor: actor,
                ActorRole: role,
                Target: userId.ToString(),
                ClientIp: http.Connection.RemoteIpAddress?.ToString(),
                UserAgent: http.Request.Headers.UserAgent.ToString(),
                Payload: new { viewIds = req.ViewIds }));

            return Results.NoContent();
        }).WithName("SetDirectViewAccess").WithOpenApi();

        return app;
    }

    public sealed record SetDirectViewAccessRequest(IReadOnlyList<Guid> ViewIds);
}
