namespace Servicedesk.Api.Auth;

/// <summary>
/// Temporary role check for v0.0.3 endpoints that must be Admin-only but live
/// before real authentication exists. Reads the <c>X-Dev-Role</c> header that
/// the frontend dev role switcher sets. Replaced in v0.0.4 by an ASP.NET
/// authorization policy — this file is expected to be deleted then.
/// </summary>
public static class DevRoleGate
{
    public const string HeaderName = "X-Dev-Role";
    public const string Admin = "Admin";
    public const string Agent = "Agent";
    public const string Customer = "Customer";

    public static IResult? RequireAdmin(HttpContext ctx) => RequireRole(ctx, Admin);

    public static IResult? RequireRole(HttpContext ctx, params string[] allowed)
    {
        var role = ctx.Request.Headers[HeaderName].ToString();
        if (string.IsNullOrWhiteSpace(role) || Array.IndexOf(allowed, role) < 0)
        {
            return Results.StatusCode(StatusCodes.Status403Forbidden);
        }
        return null;
    }

    public static string CurrentRole(HttpContext ctx) =>
        ctx.Request.Headers[HeaderName].ToString() is { Length: > 0 } r ? r : "anon";
}
