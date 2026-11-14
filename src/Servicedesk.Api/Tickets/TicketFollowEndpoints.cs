using Microsoft.AspNetCore.SignalR;
using Servicedesk.Api.Auth;
using Servicedesk.Api.Presence;
using Servicedesk.Infrastructure.Tickets.Following;

namespace Servicedesk.Api.Tickets;

/// v0.1.54 — following tickets: the sidebar "Following" block and the
/// Follow button in the ticket header. Staff only; following needs queue
/// access to the ticket, the list is filtered by queue access. A follow or
/// unfollow pushes FollowingChanged to the user's own other tabs.
public static class TicketFollowEndpoints
{
    public static IEndpointRouteBuilder MapTicketFollowEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/following", async (HttpContext http, ITicketFollowService svc, CancellationToken ct) =>
        {
            var items = await svc.ListAsync(TicketEndpoints.ResolveMutationActor(http), ct);
            return Results.Ok(new
            {
                count = items.Count,
                activeCount = items.Count(i => i.HasActivity),
                items = items.Select(i => new
                {
                    ticketId = i.TicketId,
                    ticketNumber = i.TicketNumber,
                    subject = i.Subject,
                    statusName = i.StatusName,
                    statusColor = i.StatusColor,
                    statusCategory = i.StatusCategory,
                    followedUtc = i.FollowedUtc,
                    lastSeenUtc = i.LastSeenUtc,
                    lastActivityUtc = i.LastActivityUtc,
                    hasActivity = i.HasActivity,
                }),
            });
        })
        .RequireAuthorization(AuthorizationPolicies.RequireAgent)
        .WithTags("Following").WithName("ListFollowedTickets").WithOpenApi();

        var group = app.MapGroup("/api/tickets/{ticketId:guid}/follow")
            .WithTags("Following")
            .RequireAuthorization(AuthorizationPolicies.RequireAgent);

        group.MapPut("", async (Guid ticketId, HttpContext http, ITicketFollowService svc,
            IHubContext<UserNotificationHub> hub, CancellationToken ct) =>
        {
            var actor = TicketEndpoints.ResolveMutationActor(http);
            try
            {
                await svc.FollowAsync(actor, ticketId, ct);
            }
            catch (TicketFollowRejectedException ex)
            {
                return ex.Code == TicketFollowRejectCode.NotFound
                    ? Results.NotFound()
                    : Results.Json(new { error = ex.Message, code = "limit_reached" }, statusCode: StatusCodes.Status409Conflict);
            }
            await PushChangedAsync(hub, actor.UserId, ct);
            return Results.NoContent();
        }).WithName("FollowTicket").WithOpenApi();

        group.MapDelete("", async (Guid ticketId, HttpContext http, ITicketFollowService svc,
            IHubContext<UserNotificationHub> hub, CancellationToken ct) =>
        {
            var actor = TicketEndpoints.ResolveMutationActor(http);
            if (await svc.UnfollowAsync(actor, ticketId, ct))
                await PushChangedAsync(hub, actor.UserId, ct);
            return Results.NoContent();
        }).WithName("UnfollowTicket").WithOpenApi();

        group.MapPost("/seen", async (Guid ticketId, HttpContext http, ITicketFollowService svc, CancellationToken ct) =>
        {
            await svc.MarkSeenAsync(TicketEndpoints.ResolveMutationActor(http), ticketId, ct);
            return Results.NoContent();
        }).WithName("MarkFollowedTicketSeen").WithOpenApi();

        return app;
    }

    private static Task PushChangedAsync(IHubContext<UserNotificationHub> hub, Guid userId, CancellationToken ct)
        => hub.Clients.Group($"user:{userId}").SendAsync("FollowingChanged", ct);
}
