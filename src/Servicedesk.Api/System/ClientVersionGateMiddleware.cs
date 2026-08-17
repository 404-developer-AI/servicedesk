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

        // Both values are attacker-controlled request data. Serilog writes
        // them as structured properties (no template interpolation), but we
        // still strip control characters and cap the length so a forged
        // header can never inject fake lines into a plain-text log sink.
        logger.LogWarning(
            "Rejected write from outdated client bundle {ClientVersion} (server {ServerVersion}) on {Method} {Path}",
            SanitizeForLog(clientVersion), _serverVersion, context.Request.Method, SanitizeForLog(path));

        context.Response.StatusCode = StatusCodes.Status426UpgradeRequired;
        await context.Response.WriteAsJsonAsync(new
        {
            error = "client_version_outdated",
            message = "A newer version of the application has been deployed. Reload the page to continue.",
            serverVersion = _serverVersion,
            clientVersion = SanitizeForLog(clientVersion),
        });
    }

    private const int MaxLoggedLength = 128;

    /// Drops control characters (CR/LF/ESC/…) and truncates, so untrusted
    /// request values can be logged/echoed without log-forging or terminal
    /// escape tricks. Header values are short by contract; paths that
    /// exceed the cap are cut with an ellipsis.
    internal static string SanitizeForLog(string value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        var buffer = new char[Math.Min(value.Length, MaxLoggedLength)];
        var n = 0;
        foreach (var c in value)
        {
            if (char.IsControl(c)) continue;
            buffer[n++] = c;
            if (n == buffer.Length) break;
        }
        var result = new string(buffer, 0, n);
        return value.Length > MaxLoggedLength ? result + "…" : result;
    }
}
