using System.Security.Claims;
using Servicedesk.Api.Auth;
using Servicedesk.Infrastructure.Access;
using Servicedesk.Infrastructure.Persistence.Taxonomy;

namespace Servicedesk.Api.Access;

/// Agent-facing endpoint that returns only the queues the current user can
/// access. Admins get all active queues. Frontend uses this for dropdowns,
/// filters, and ticket creation — never the admin taxonomy endpoint.
public static class AgentQueueEndpoints
{
    public static IEndpointRouteBuilder MapAgentQueueEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/queues", async (
            HttpContext http, IQueueAccessService svc, ITaxonomyRepository taxonomy, CancellationToken ct) =>
        {
            var userId = Guid.Parse(http.User.FindFirst(ClaimTypes.NameIdentifier)!.Value);
            var role = http.User.FindFirst(ClaimTypes.Role)!.Value;

            var accessibleIds = await svc.GetAccessibleQueueIdsAsync(userId, role, ct);
            if (accessibleIds.Count == 0)
                return Results.Ok(Array.Empty<object>());

            // Return full queue objects so the frontend has name/color/icon for dropdowns
            var allQueues = await taxonomy.ListQueuesAsync(ct);
            var accessibleSet = accessibleIds.ToHashSet();
            var filtered = allQueues.Where(q => accessibleSet.Contains(q.Id)).ToList();

            return Results.Ok(filtered);
        })
        .WithTags("Queues")
        .WithName("ListAccessibleQueues")
        .WithOpenApi()
        .RequireAuthorization(AuthorizationPolicies.RequireAgent);

        return app;
    }
}
