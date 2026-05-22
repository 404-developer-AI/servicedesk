using Servicedesk.Api.Auth;
using Servicedesk.Api.Presence;
using Servicedesk.Infrastructure.Dashboard;

namespace Servicedesk.Api.Dashboard;

public static class DashboardEndpoints
{
    public static IEndpointRouteBuilder MapDashboardEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/dashboard")
            .WithTags("Dashboard")
            .RequireAuthorization(AuthorizationPolicies.RequireAdmin);

        // Initial snapshot for the AgentActivity dashboard tile. Returns
        // every Agent + Admin with an online flag and the tickets they
        // are currently viewing / have in their recent list. Live
        // updates afterwards arrive over the TicketPresenceHub via the
        // "AgentActivity" event broadcast to the
        // "agent-activity-admins" group.
        group.MapGet("/agent-activity", async (
            IAgentActivityService service,
            CancellationToken ct) =>
        {
            var presence = TicketPresenceHub.GetAllAgentPresence();
            var snapshot = await service.BuildSnapshotAsync(presence, ct);
            return Results.Ok(new { agents = snapshot });
        }).WithName("DashboardAgentActivity").WithOpenApi();

        return app;
    }
}
