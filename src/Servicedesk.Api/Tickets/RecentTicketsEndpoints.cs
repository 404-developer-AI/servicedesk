using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Servicedesk.Api.Presence;
using Servicedesk.Infrastructure.Persistence.Tickets;

namespace Servicedesk.Api.Tickets;

/// User-scoped "recent tickets" sidebar list, persisted server-side so
/// the same user sees the same list across browsers and devices. Every
/// mutation also fires a `RecentTicketsUpdated` SignalR push to the
/// caller's own `user:{id}` group via UserNotificationHub, so the
/// sidebar in another open tab refreshes within ~100ms without a poll.
public static class RecentTicketsEndpoints
{
    public static IEndpointRouteBuilder MapRecentTicketsEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/me/recent-tickets")
            .WithTags("RecentTickets")
            .RequireAuthorization("RequireAgent");

        group.MapGet("/", async (
            HttpContext httpContext,
            IRecentTicketsService service,
            CancellationToken ct) =>
        {
            var userId = ResolveUserId(httpContext);
            if (userId is null) return Results.Unauthorized();
            var items = await service.ListForUserAsync(userId.Value, ct);
            return Results.Ok(new { items });
        }).WithName("RecentTicketsList").WithOpenApi();

        group.MapPost("/{ticketId:guid}", async (
            Guid ticketId,
            HttpContext httpContext,
            IRecentTicketsService service,
            IHubContext<UserNotificationHub> hub,
            CancellationToken ct) =>
        {
            var userId = ResolveUserId(httpContext);
            if (userId is null) return Results.Unauthorized();
            var changed = await service.AddAsync(userId.Value, ticketId, ct);
            if (changed)
            {
                await BroadcastAsync(hub, userId.Value, ct);
            }
            return Results.NoContent();
        }).WithName("RecentTicketsAdd").WithOpenApi();

        group.MapDelete("/{ticketId:guid}", async (
            Guid ticketId,
            HttpContext httpContext,
            IRecentTicketsService service,
            IHubContext<UserNotificationHub> hub,
            CancellationToken ct) =>
        {
            var userId = ResolveUserId(httpContext);
            if (userId is null) return Results.Unauthorized();
            var changed = await service.RemoveAsync(userId.Value, ticketId, ct);
            if (changed)
            {
                await BroadcastAsync(hub, userId.Value, ct);
            }
            return Results.NoContent();
        }).WithName("RecentTicketsRemove").WithOpenApi();

        group.MapPut("/order", async (
            [FromBody] ReorderRecentTicketsRequest request,
            HttpContext httpContext,
            IRecentTicketsService service,
            IHubContext<UserNotificationHub> hub,
            CancellationToken ct) =>
        {
            var userId = ResolveUserId(httpContext);
            if (userId is null) return Results.Unauthorized();
            await service.ReorderAsync(userId.Value, request.TicketIds ?? Array.Empty<Guid>(), ct);
            await BroadcastAsync(hub, userId.Value, ct);
            return Results.NoContent();
        }).WithName("RecentTicketsReorder").WithOpenApi();

        return app;
    }

    private static Task BroadcastAsync(IHubContext<UserNotificationHub> hub, Guid userId, CancellationToken ct) =>
        hub.Clients.Group($"user:{userId}").SendAsync("RecentTicketsUpdated", ct);

    private static Guid? ResolveUserId(HttpContext httpContext)
    {
        var raw = httpContext.User?.FindFirstValue(ClaimTypes.NameIdentifier);
        return Guid.TryParse(raw, out var id) ? id : null;
    }
}

public sealed record ReorderRecentTicketsRequest(IReadOnlyList<Guid>? TicketIds);
