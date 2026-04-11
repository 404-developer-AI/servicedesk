namespace Servicedesk.Api.Security;

public static class SecurityMiddlewareExtensions
{
    public static IApplicationBuilder UseServicedeskSecurityHeaders(this IApplicationBuilder app) =>
        app.UseMiddleware<SecurityHeadersMiddleware>();

    public static IApplicationBuilder UseServicedeskContentSecurityPolicy(this IApplicationBuilder app) =>
        app.UseMiddleware<ContentSecurityPolicyMiddleware>();
}
