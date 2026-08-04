namespace Servicedesk.Api.System;

/// Hard stop for writes from an outdated SPA bundle. The frontend stamps
/// every API call with its build version (X-Client-Version, injected by a
/// global fetch wrapper so no call site can forget it); after a deploy an
/// old tab that missed the reload signal would otherwise keep POSTing
/// payloads shaped for the previous API contract — the server would accept
/// them and silently default any newly-added fields. Mutating requests with
/// a mismatched version are rejected with 426 Upgrade Required; the client
/// treats that as "reload now".
/// <para>
/// Reads always pass, so an old tab stays usable until the reload lands.
/// Requests without the header pass too: curl, CI health checks and
/// integrations are not version-locked — the gate only guards the one
/// caller that can actually go stale, the browser bundle. SignalR traffic
/// is exempt so an old client's hub reconnect (the very signal that
/// triggers its version check) is never blocked.
/// </para>
public sealed class ClientVersionGateMiddleware
{
    public const string HeaderName = "X-Client-Version";

    private static readonly HashSet<string> SafeMethods = new(StringComparer.OrdinalIgnoreCase)
    {
        "GET", "HEAD", "OPTIONS", "TRACE",
    };

    private readonly RequestDelegate _next;
    private readonly string _serverVersion;

    public ClientVersionGateMiddleware(RequestDelegate next, SystemInfo systemInfo)
    {
        _next = next;
        _serverVersion = systemInfo.Version;
    }

    public async Task InvokeAsync(HttpContext context, ILogger<ClientVersionGateMiddleware> logger)
    {
        if (SafeMethods.Contains(context.Request.Method))
        {
            await _next(context);
            return;
        }

        var path = context.Request.Path.Value ?? string.Empty;
        if (path.StartsWith("/hubs/", StringComparison.OrdinalIgnoreCase))
        {
            await _next(context);
            return;
        }

        var clientVersion = context.Request.Headers[HeaderName].ToString();
        if (string.IsNullOrEmpty(clientVersion) ||
            string.Equals(clientVersion, _serverVersion, StringComparison.Ordinal))
        {
            await _next(context);
            return;
        }

        logger.LogWarning(
            "Rejected write from outdated client bundle {ClientVersion} (server {ServerVersion}) on {Method} {Path}",
            clientVersion, _serverVersion, context.Request.Method, path);

        context.Response.StatusCode = StatusCodes.Status426UpgradeRequired;
        await context.Response.WriteAsJsonAsync(new
        {
            error = "client_version_outdated",
            message = "A newer version of the application has been deployed. Reload the page to continue.",
            serverVersion = _serverVersion,
            clientVersion,
        });
    }
}
