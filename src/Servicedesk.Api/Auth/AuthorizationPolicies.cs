using Microsoft.AspNetCore.Authorization;

namespace Servicedesk.Api.Auth;

public static class AuthorizationPolicies
{
    public const string RequireAdmin = "RequireAdmin";
    public const string RequireAgent = "RequireAgent";
    public const string RequireCustomer = "RequireCustomer";

    public static AuthorizationOptions AddServicedeskPolicies(this AuthorizationOptions options)
    {
        options.AddPolicy(RequireAdmin, p => p
            .AddAuthenticationSchemes(SessionAuthenticationHandler.SchemeName)
            .RequireAuthenticatedUser()
            .RequireRole("Admin"));

        options.AddPolicy(RequireAgent, p => p
            .AddAuthenticationSchemes(SessionAuthenticationHandler.SchemeName)
            .RequireAuthenticatedUser()
            .RequireRole("Admin", "Agent"));

        options.AddPolicy(RequireCustomer, p => p
            .AddAuthenticationSchemes(SessionAuthenticationHandler.SchemeName)
            .RequireAuthenticatedUser()
            .RequireRole("Admin", "Agent", "Customer"));

        return options;
    }
}
