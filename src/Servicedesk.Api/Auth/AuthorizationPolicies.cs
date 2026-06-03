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
            .RequireMfaComplete()
            .RequireRole("Admin"));

        options.AddPolicy(RequireAgent, p => p
            .AddAuthenticationSchemes(SessionAuthenticationHandler.SchemeName)
            .RequireAuthenticatedUser()
            .RequireMfaComplete()
            .RequireRole("Admin", "Agent"));

        options.AddPolicy(RequireCustomer, p => p
            .AddAuthenticationSchemes(SessionAuthenticationHandler.SchemeName)
            .RequireAuthenticatedUser()
            .RequireMfaComplete()
            .RequireRole("Admin", "Agent", "Customer"));

        return options;
    }

    /// Rejects a session that still owes its TOTP challenge
    /// (<see cref="SessionAuthenticationHandler.AmrPending"/>). This is the
    /// server-side enforcement of 2FA: until /2fa/verify upgrades the session,
    /// the password-only cookie cannot reach any role-gated endpoint. A
    /// blacklist (deny pending) rather than a whitelist so non-2FA local
    /// logins ("pwd") and M365 sessions ("ext") keep working unchanged.
    private static AuthorizationPolicyBuilder RequireMfaComplete(this AuthorizationPolicyBuilder builder)
        => builder.RequireAssertion(ctx =>
            ctx.User.FindFirst(SessionAuthenticationHandler.AmrClaimType)?.Value
                != SessionAuthenticationHandler.AmrPending);
}
