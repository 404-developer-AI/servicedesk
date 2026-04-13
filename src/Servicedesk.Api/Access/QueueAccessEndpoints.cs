using Microsoft.AspNetCore.Mvc;
using Servicedesk.Api.Auth;
using Servicedesk.Infrastructure.Access;
using Servicedesk.Infrastructure.Audit;

namespace Servicedesk.Api.Access;

/// Admin-only endpoints for managing which agents can access which queues.
public static class QueueAccessEndpoints
{
    public static IEndpointRouteBuilder MapQueueAccessEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/admin/queue-access")
            .WithTags("Queue Access")
            .RequireAuthorization(AuthorizationPolicies.RequireAdmin);

        group.MapGet("/{userId:guid}", async (
            Guid userId, IQueueAccessService svc, CancellationToken ct) =>
        {
            var queueIds = await svc.GetAccessibleQueueIdsAsync(userId, "Agent", ct);
            return Results.Ok(new { queueIds });
        }).WithName("GetQueueAccess").WithOpenApi();

        group.MapPut("/{userId:guid}", async (
            Guid userId, [FromBody] SetQueueAccessRequest req,
            IQueueAccessService svc, HttpContext http, IAuditLogger audit, CancellationToken ct) =>
        {
            await svc.SetQueueAccessAsync(userId, req.QueueIds, ct);

            var (actor, role) = ActorContext.Resolve(http);
            await audit.LogAsync(new AuditEvent(
                EventType: "queue_access.changed",
                Actor: actor,
                ActorRole: role,
                Target: userId.ToString(),
                ClientIp: http.Connection.RemoteIpAddress?.ToString(),
                UserAgent: http.Request.Headers.UserAgent.ToString(),
                Payload: new { queueIds = req.QueueIds }));

            return Results.NoContent();
        }).WithName("SetQueueAccess").WithOpenApi();

        group.MapGet("/by-queue/{queueId:guid}", async (
            Guid queueId, IQueueAccessService svc, CancellationToken ct) =>
        {
            var userIds = await svc.GetUsersForQueueAsync(queueId, ct);
            return Results.Ok(new { userIds });
        }).WithName("GetQueueAccessByQueue").WithOpenApi();

        return app;
    }

    public sealed record SetQueueAccessRequest(IReadOnlyList<Guid> QueueIds);
}
