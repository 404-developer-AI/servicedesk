using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Servicedesk.Infrastructure.Activity;
using Servicedesk.Infrastructure.Auth;

namespace Servicedesk.Api.Activity;

/// Public endpoints for the activity feed. Both routes require the
/// caller's <c>users.activity_feed_enabled</c> flag to be true — gated
/// in code (not via a policy) so admins without the flag are also
/// refused. RequireAgent enforces the basic non-customer baseline.
public static class ActivityEndpoints
{
    public static IEndpointRouteBuilder MapActivityEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/activity")
            .WithTags("Activity")
            .RequireAuthorization("RequireAgent");

        group.MapGet("/recent", async (
            HttpContext httpContext,
            IUserService users,
            IActivityFeedQuery query,
            [FromQuery] int? limit,
            CancellationToken ct) =>
        {
            var userId = ResolveUserId(httpContext);
            if (userId is null) return Results.Unauthorized();
            if (!await users.GetActivityFeedEnabledAsync(userId.Value, ct))
            {
                return Results.Forbid();
            }

            var clampedLimit = Math.Clamp(limit ?? 25, 1, 100);
            var rows = await query.ListRecentAsync(clampedLimit, ct);
            return Results.Ok(new { items = rows });
        }).WithName("ActivityRecent").WithOpenApi();

        group.MapGet("/", async (
            HttpContext httpContext,
            IUserService users,
            IActivityFeedQuery query,
            [FromQuery] Guid? agentId,
            [FromQuery] string? eventType,
            [FromQuery] DateTimeOffset? fromUtc,
            [FromQuery] DateTimeOffset? toUtc,
            [FromQuery] string? search,
            [FromQuery] long? cursor,
            [FromQuery] int? limit,
            CancellationToken ct) =>
        {
            var userId = ResolveUserId(httpContext);
            if (userId is null) return Results.Unauthorized();
            if (!await users.GetActivityFeedEnabledAsync(userId.Value, ct))
            {
                return Results.Forbid();
            }

            var filter = new ActivityFeedFilter(
                AgentId: agentId,
                EventType: string.IsNullOrWhiteSpace(eventType) ? null : eventType,
                FromUtc: fromUtc,
                ToUtc: toUtc,
                Search: search,
                CursorId: cursor,
                Limit: limit ?? 50);

            var page = await query.ListAsync(filter, ct);
            return Results.Ok(new
            {
                items = page.Items,
                nextCursor = page.NextCursor,
            });
        }).WithName("ActivityList").WithOpenApi();

        return app;
    }

    private static Guid? ResolveUserId(HttpContext httpContext)
    {
        var raw = httpContext.User?.FindFirstValue(ClaimTypes.NameIdentifier);
        return Guid.TryParse(raw, out var id) ? id : null;
    }
}
