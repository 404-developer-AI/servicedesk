using Servicedesk.Api.Auth;
using Servicedesk.Infrastructure.Auth;

namespace Servicedesk.Api.Users;

public static class UserEndpoints
{
    public static IEndpointRouteBuilder MapUserEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/users")
            .WithTags("Users")
            .RequireAuthorization(AuthorizationPolicies.RequireAgent);

        group.MapGet("/", async (IUserService users, CancellationToken ct) =>
            Results.Ok(await users.ListAgentsAsync(ct)))
            .WithName("ListAgents").WithOpenApi();

        // Typeahead for @@-mentions in the post-editor (v0.0.12 stap 3). Agent+Admin
        // only — customers never receive a candidate list. `q` is ILIKE-matched on
        // email; empty `q` returns the top-N alphabetically so the popover has
        // something to show on first focus.
        group.MapGet("/agents/search", async (
            IUserService users,
            string? q,
            int? limit,
            CancellationToken ct) =>
        {
            var results = await users.SearchAgentsAsync(q, limit ?? 20, ct);
            return Results.Ok(results);
        }).WithName("SearchAgents").WithOpenApi();

        return app;
    }
}
