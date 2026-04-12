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

        return app;
    }
}
