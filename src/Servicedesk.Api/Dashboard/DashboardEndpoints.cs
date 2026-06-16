using System.Security.Claims;
using Servicedesk.Api.Auth;
using Servicedesk.Api.Presence;
using Servicedesk.Infrastructure.Access;
using Servicedesk.Infrastructure.Dashboard;

namespace Servicedesk.Api.Dashboard;

public static class DashboardEndpoints
{
    public static IEndpointRouteBuilder MapDashboardEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/dashboard")
            .WithTags("Dashboard")
            .RequireAuthorization(AuthorizationPolicies.RequireAgent);

        // Initial snapshot for the AgentActivity dashboard tile. Returns
        // every Agent + Admin with an online flag and the tickets they
        // are currently viewing / have in their recent list. Live
        // updates afterwards arrive over the TicketPresenceHub via the
        // "AgentActivity" event broadcast to the
        // "agent-activity-broadcast" group. v0.0.44 — opened to Agents
        // (admin-grantable per user) since the same cross-agent presence
        // is already visible via the in-ticket presence chips.
        group.MapGet("/agent-activity", async (
            HttpContext http,
            IAgentActivityService service,
            IQueueAccessService queueAccess,
            CancellationToken ct) =>
        {
            var userId = Guid.Parse(http.User.FindFirst(ClaimTypes.NameIdentifier)!.Value);
            var role = http.User.FindFirst(ClaimTypes.Role)!.Value;
            var scope = await queueAccess.GetScopeAsync(userId, role, ct);

            var presence = TicketPresenceHub.GetAllAgentPresence();
            var snapshot = await service.BuildSnapshotAsync(presence, ct);

            // Mask each agent's tickets for the requesting user so a queue
            // they cannot access never leaks its subject/number through the
            // tile (the ticket-detail endpoint already enforces this).
            var masked = snapshot.Select(a => AgentActivityMasking.ForViewer(a, scope));
            return Results.Ok(new { agents = masked });
        }).WithName("DashboardAgentActivity").WithOpenApi();

        return app;
    }
}
