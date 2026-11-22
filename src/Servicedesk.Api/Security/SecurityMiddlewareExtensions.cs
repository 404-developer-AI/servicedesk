namespace Servicedesk.Api.Security;

public static class SecurityMiddlewareExtensions
{
    public static IApplicationBuilder UseServicedeskSecurityHeaders(this IApplicationBuilder app) =>
        app.UseMiddleware<SecurityHeadersMiddleware>();

    public static IApplicationBuilder UseServicedeskContentSecurityPolicy(this IApplicationBuilder app) =>
        app.UseMiddleware<ContentSecurityPolicyMiddleware>();

    /// <c>UseRateLimiter()</c> plus a link to the rest of the pipeline that is
    /// cut once the host has stopped.
    ///
    /// RateLimitingMiddleware is not disposable and its limiters run
    /// replenishment timers for the life of the process; those timers keep the
    /// middleware — and through its next-delegate the whole pipeline, routing
    /// table and service provider — reachable after the host is disposed.
    /// With one host per process in production that is harmless, but the test
    /// suite boots hundreds of hosts in one process and kept every one of them
    /// in memory (the CI runner ran low on memory and the backend job slowed
    /// to an hour). Cutting the link leaves only the limiter itself behind;
    /// after shutdown no request reaches this point anyway.
    public static WebApplication UseServicedeskRateLimiter(this WebApplication app)
    {
        app.UseRateLimiter();
        var stopped = app.Lifetime.ApplicationStopped;
        app.Use(next =>
        {
            var link = new SeverablePipelineLink(next);
            stopped.Register(link.Sever);
            return link.InvokeAsync;
        });
        return app;
    }

    private sealed class SeverablePipelineLink(RequestDelegate next)
    {
        private RequestDelegate? _next = next;

        public void Sever() => Volatile.Write(ref _next, null);

        public Task InvokeAsync(HttpContext context)
        {
            var current = Volatile.Read(ref _next);
            if (current is null)
            {
                context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                return Task.CompletedTask;
            }
            return current(context);
        }
    }
}
